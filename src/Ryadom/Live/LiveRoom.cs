using System.Collections.Concurrent;
using LiveKit.Proto;
using Ryadom.Live.Ffi;
using Ryadom.Shared;

namespace Ryadom.Live;

public enum RoomLink
{
    Connected,
    /// <summary>LiveKit чинит связь сам.</summary>
    Reconnecting,
    /// <summary>Сдался — нужен вход заново.</summary>
    Lost,
}

/// <summary>
/// Комната LiveKit на двоих: человек публикует экран, помощник смотрит;
/// мышь, клавиши и служебные сообщения — каналом данных.
/// События приходят из потоков движка.
/// </summary>
public sealed class LiveRoom : IAsyncDisposable
{
    private readonly FfiHandle _room;
    private readonly FfiHandle _local;
    private readonly ConcurrentDictionary<ulong, (FfiHandle Stream, FfiHandle Track)> _videoStreams = new();
    private readonly ConcurrentDictionary<ulong, byte> _closed = new();
    private readonly ConcurrentDictionary<string, string> _names = new();
    private bool _disposed;

    private LiveRoom(ConnectCallback.Types.Result result)
    {
        _room = new FfiHandle(result.Room.Handle.Id);
        _local = new FfiHandle(result.LocalParticipant.Handle.Id);
        LocalIdentity = result.LocalParticipant.Info.Identity;
    }

    public string LocalIdentity { get; }

    /// <summary>Кто сейчас в комнате, кроме нас: identity → имя.</summary>
    public IReadOnlyDictionary<string, string> Others => _names;

    public event Action<string, string>? ParticipantJoined;
    public event Action<string>? ParticipantLeft;
    /// <summary>(от кого, тема, данные). Данные действительны только во время вызова.</summary>
    public event Action<string, string, ReadOnlySpan<byte>>? DataReceived;
    public event Action<RoomLink>? LinkChanged;
    internal event Action<ConnectionQuality>? QualityChanged;
    /// <summary>Кадр чужого экрана, BGRA. Указатель живёт только во время вызова.</summary>
    public event FrameHandler? FrameReceived;
    /// <summary>Чужой экран появился (true) или пропал (false).</summary>
    public event Action<bool>? RemoteScreenChanged;

    public unsafe delegate void FrameHandler(byte* bgra, int width, int height, int stride);

    public static async Task<LiveRoom> ConnectAsync(string url, string token, bool subscribe, CancellationToken ct = default)
    {
        FfiClient.EnsureInitialized();
        var asyncId = FfiClient.NextAsyncId();
        var answer = FfiClient.Expect(asyncId, TimeSpan.FromSeconds(25), ct);
        FfiClient.Request(new FfiRequest
        {
            Connect = new ConnectRequest
            {
                Url = url,
                Token = token,
                RequestAsyncId = asyncId,
                Options = new RoomOptions
                {
                    AutoSubscribe = subscribe,
                    // Картинка нужна в полном размере всегда: окно помощника
                    // может быть меньше экрана, но мелкий текст должен читаться.
                    AdaptiveStream = false,
                    Dynacast = false,
                    JoinRetries = 3,
                    ConnectTimeoutMs = 8000,
                },
            },
        });
        var reply = (await answer).Connect;
        if (reply.MessageCase == ConnectCallback.MessageOneofCase.Error)
            throw new InvalidOperationException($"Не удалось подключиться: {reply.Error}");

        var room = new LiveRoom(reply.Result);
        FfiClient.Event += room.OnEvent;
        foreach (var participant in reply.Result.Participants)
        {
            var info = participant.Participant.Info;
            room._names[info.Identity] = info.Name;
            new FfiHandle(participant.Participant.Handle.Id).Dispose();
            foreach (var publication in participant.Publications)
            {
                if (publication.Info.Source == TrackSource.SourceScreenshare) room.RemoteScreenChanged?.Invoke(true);
                new FfiHandle(publication.Handle.Id).Dispose();
            }
        }
        FfiClient.Request(new FfiRequest { ReadyForRoomEvent = new ReadyForRoomEventRequest { RoomHandle = room._room.Id } });
        Log.Info($"В комнате как {room.LocalIdentity}, других: {room._names.Count}");
        return room;
    }

    // ── Данные ─────────────────────────────────────────────────

    /// <summary>Отправить данные. reliable=false — без повторов (движение мыши).</summary>
    public unsafe void Send(ReadOnlySpan<byte> data, string topic, bool reliable, string? to = null)
    {
        if (_disposed) return;
        fixed (byte* p = data)
        {
            var request = new PublishDataRequest
            {
                LocalParticipantHandle = _local.Id,
                DataPtr = (ulong)p,
                DataLen = (ulong)data.Length,
                Reliable = reliable,
                Topic = topic,
            };
            if (to is not null) request.DestinationIdentities.Add(to);
            try { FfiClient.Request(new FfiRequest { PublishData = request }); }
            catch (Exception error) { Log.Warn($"Данные не ушли: {error.Message}"); }
        }
    }

    public void SendControl(Control message, string? to = null) =>
        Send(Protocol.EncodeControl(message), Protocol.ControlTopic, reliable: true, to);

    // ── Свой экран ─────────────────────────────────────────────

    public async Task<ScreenTrack> PublishScreenAsync(int width, int height, int fps, ulong bitrate, CancellationToken ct = default)
    {
        var source = new FfiHandle(FfiClient.Request(new FfiRequest
        {
            NewVideoSource = new NewVideoSourceRequest
            {
                Type = VideoSourceType.VideoSourceNative,
                Resolution = new VideoSourceResolution { Width = (uint)width, Height = (uint)height },
                IsScreencast = true,
            },
        }).NewVideoSource.Source.Handle.Id);
        var track = new FfiHandle(FfiClient.Request(new FfiRequest
        {
            CreateVideoTrack = new CreateVideoTrackRequest { Name = "screen", SourceHandle = source.Id },
        }).CreateVideoTrack.Track.Handle.Id);

        var asyncId = FfiClient.NextAsyncId();
        var answer = FfiClient.Expect(asyncId, TimeSpan.FromSeconds(15), ct);
        FfiClient.Request(new FfiRequest
        {
            PublishTrack = new PublishTrackRequest
            {
                LocalParticipantHandle = _local.Id,
                TrackHandle = track.Id,
                RequestAsyncId = asyncId,
                Options = new TrackPublishOptions
                {
                    Source = TrackSource.SourceScreenshare,
                    // H264 — его аппаратно сжимают и разжимают почти все видеокарты.
                    VideoCodec = VideoCodec.H264,
                    // Смотрит один человек — лишние слои только жгут процессор.
                    Simulcast = false,
                    VideoEncoding = new VideoEncoding { MaxBitrate = bitrate, MaxFramerate = fps },
                },
            },
        });
        var reply = (await answer).PublishTrack;
        if (reply.MessageCase == PublishTrackCallback.MessageOneofCase.Error)
        {
            track.Dispose();
            source.Dispose();
            throw new InvalidOperationException($"Экран не опубликовался: {reply.Error}");
        }
        var sid = reply.Publication.Info.Sid;
        new FfiHandle(reply.Publication.Handle.Id).Dispose();
        Log.Info($"Экран опубликован: {width}×{height}, {fps} к/с, {bitrate / 1000} кбит/с");
        return new ScreenTrack(this, source, track, sid);
    }

    internal async Task UnpublishAsync(string sid)
    {
        if (_disposed) return;
        try
        {
            var asyncId = FfiClient.NextAsyncId();
            var answer = FfiClient.Expect(asyncId, TimeSpan.FromSeconds(5));
            FfiClient.Request(new FfiRequest
            {
                UnpublishTrack = new UnpublishTrackRequest
                {
                    LocalParticipantHandle = _local.Id,
                    TrackSid = sid,
                    StopOnUnpublish = true,
                    RequestAsyncId = asyncId,
                },
            });
            await answer;
        }
        catch (OperationCanceledException)
        {
            Log.Warn("Снятие экрана без ответа");
        }
    }

    // ── События ────────────────────────────────────────────────

    private void OnEvent(FfiEvent ev)
    {
        switch (ev.MessageCase)
        {
            case FfiEvent.MessageOneofCase.VideoStreamEvent:
                OnVideo(ev.VideoStreamEvent);
                return;
            case FfiEvent.MessageOneofCase.RoomEvent when ev.RoomEvent.RoomHandle == _room.Id:
                OnRoom(ev.RoomEvent);
                return;
        }
    }

    private void OnRoom(RoomEvent ev)
    {
        switch (ev.MessageCase)
        {
            case RoomEvent.MessageOneofCase.ParticipantConnected:
            {
                var info = ev.ParticipantConnected.Info;
                new FfiHandle(info.Handle.Id).Dispose();
                _names[info.Info.Identity] = info.Info.Name;
                ParticipantJoined?.Invoke(info.Info.Identity, info.Info.Name);
                break;
            }
            case RoomEvent.MessageOneofCase.ParticipantDisconnected:
                _names.TryRemove(ev.ParticipantDisconnected.ParticipantIdentity, out _);
                ParticipantLeft?.Invoke(ev.ParticipantDisconnected.ParticipantIdentity);
                break;
            case RoomEvent.MessageOneofCase.TrackPublished:
                Log.Info($"{ev.TrackPublished.ParticipantIdentity} опубликовал {ev.TrackPublished.Publication.Info.Source}");
                if (ev.TrackPublished.Publication.Info.Source == TrackSource.SourceScreenshare) RemoteScreenChanged?.Invoke(true);
                new FfiHandle(ev.TrackPublished.Publication.Handle.Id).Dispose();
                break;
            case RoomEvent.MessageOneofCase.TrackUnpublished:
                RemoteScreenChanged?.Invoke(false);
                break;
            case RoomEvent.MessageOneofCase.TrackSubscribed:
                OnTrackSubscribed(ev.TrackSubscribed);
                break;
            case RoomEvent.MessageOneofCase.TrackSubscriptionFailed:
                Log.Warn($"Не подписались на {ev.TrackSubscriptionFailed.ParticipantIdentity}: {ev.TrackSubscriptionFailed.Error}");
                break;
            case RoomEvent.MessageOneofCase.ConnectionStateChanged:
                Log.Info($"Связь: {ev.ConnectionStateChanged.State}");
                break;
            case RoomEvent.MessageOneofCase.DataPacketReceived:
                OnData(ev.DataPacketReceived);
                break;
            case RoomEvent.MessageOneofCase.ConnectionQualityChanged
                when ev.ConnectionQualityChanged.ParticipantIdentity == LocalIdentity:
                QualityChanged?.Invoke(ev.ConnectionQualityChanged.Quality);
                break;
            case RoomEvent.MessageOneofCase.Reconnecting:
                LinkChanged?.Invoke(RoomLink.Reconnecting);
                break;
            case RoomEvent.MessageOneofCase.Reconnected:
                LinkChanged?.Invoke(RoomLink.Connected);
                break;
            case RoomEvent.MessageOneofCase.Disconnected:
                if (_disposed) break;
                Log.Info($"Комната закрыта: {ev.Disconnected.Reason}");
                LinkChanged?.Invoke(RoomLink.Lost);
                break;
        }
    }

    private unsafe void OnData(DataPacketReceived packet)
    {
        if (packet.ValueCase != DataPacketReceived.ValueOneofCase.User) return;
        var user = packet.User;
        using var handle = new FfiHandle(user.Data.Handle.Id);
        var span = new ReadOnlySpan<byte>((void*)user.Data.Data.DataPtr, (int)user.Data.Data.DataLen);
        try { DataReceived?.Invoke(packet.ParticipantIdentity, user.Topic ?? "", span); }
        catch (Exception error) { Log.Error("Обработка данных", error); }
    }

    private void OnTrackSubscribed(TrackSubscribed subscribed)
    {
        var track = new FfiHandle(subscribed.Track.Handle.Id);
        if (subscribed.Track.Info.Kind != TrackKind.KindVideo)
        {
            track.Dispose();
            return;
        }
        var stream = FfiClient.Request(new FfiRequest
        {
            NewVideoStream = new NewVideoStreamRequest
            {
                TrackHandle = track.Id,
                Type = VideoStreamType.VideoStreamNative,
                Format = VideoBufferType.Bgra,
                NormalizeStride = true,
            },
        }).NewVideoStream.Stream;
        _videoStreams[stream.Handle.Id] = (new FfiHandle(stream.Handle.Id), track);
        Log.Info("Подписались на экран");
    }

    private unsafe void OnVideo(VideoStreamEvent ev)
    {
        if (!_videoStreams.ContainsKey(ev.StreamHandle))
        {
            if (_closed.ContainsKey(ev.StreamHandle) && ev.MessageCase == VideoStreamEvent.MessageOneofCase.FrameReceived)
                new FfiHandle(ev.FrameReceived.Buffer.Handle.Id).Dispose();
            return;
        }
        if (ev.MessageCase == VideoStreamEvent.MessageOneofCase.Eos)
        {
            CloseStream(ev.StreamHandle);
            return;
        }
        var buffer = ev.FrameReceived.Buffer;
        using var handle = new FfiHandle(buffer.Handle.Id);
        var info = buffer.Info;
        if (info.Type != VideoBufferType.Bgra) return;
        var stride = info.HasStride ? (int)info.Stride : (int)info.Width * 4;
        try { FrameReceived?.Invoke((byte*)info.DataPtr, (int)info.Width, (int)info.Height, stride); }
        catch (Exception error) { Log.Error("Кадр не нарисовался", error); }
    }

    private void CloseStream(ulong handle)
    {
        _closed[handle] = 0;
        if (_videoStreams.TryRemove(handle, out var video))
        {
            video.Stream.Dispose();
            video.Track.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        FfiClient.Event -= OnEvent;
        try
        {
            var asyncId = FfiClient.NextAsyncId();
            var answer = FfiClient.Expect(asyncId, TimeSpan.FromSeconds(5));
            FfiClient.Request(new FfiRequest { Disconnect = new DisconnectRequest { RoomHandle = _room.Id, RequestAsyncId = asyncId } });
            await answer;
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException)
        {
            Log.Warn($"Выход из комнаты без ответа: {error.Message}");
        }
        foreach (var key in _videoStreams.Keys.ToList()) CloseStream(key);
        _local.Dispose();
        _room.Dispose();
    }
}

/// <summary>Свой экран в комнате. Кадры — NV12 (готовит видеокарта) или BGRA (запасной путь).</summary>
public sealed class ScreenTrack : IAsyncDisposable
{
    private readonly LiveRoom _room;
    private readonly FfiHandle _source;
    private readonly FfiHandle _track;
    private readonly string _sid;
    private bool _disposed;

    internal ScreenTrack(LiveRoom room, FfiHandle source, FfiHandle track, string sid)
    {
        _room = room;
        _source = source;
        _track = track;
        _sid = sid;
    }

    /// <summary>Кадр NV12: плоскость Y (stride yStride, height строк), за ней UV.
    /// Движок копирует данные во время вызова.</summary>
    public unsafe void PushNv12(byte* y, int yStride, byte* uv, int uvStride, int width, int height, long timestampUs)
    {
        if (_disposed) return;
        var info = new VideoBufferInfo
        {
            Type = VideoBufferType.Nv12,
            Width = (uint)width,
            Height = (uint)height,
            DataPtr = (ulong)y,
        };
        info.Components.Add(new VideoBufferInfo.Types.ComponentInfo { DataPtr = (ulong)y, Stride = (uint)yStride, Size = (uint)(yStride * height) });
        info.Components.Add(new VideoBufferInfo.Types.ComponentInfo { DataPtr = (ulong)uv, Stride = (uint)uvStride, Size = (uint)(uvStride * ((height + 1) / 2)) });
        Capture(info, timestampUs);
    }

    public unsafe void PushBgra(byte* bgra, int width, int height, int stride, long timestampUs)
    {
        if (_disposed) return;
        Capture(new VideoBufferInfo
        {
            Type = VideoBufferType.Bgra,
            Width = (uint)width,
            Height = (uint)height,
            DataPtr = (ulong)bgra,
            Stride = (uint)stride,
        }, timestampUs);
    }

    private void Capture(VideoBufferInfo info, long timestampUs) =>
        FfiClient.Request(new FfiRequest
        {
            CaptureVideoFrame = new CaptureVideoFrameRequest
            {
                SourceHandle = _source.Id,
                TimestampUs = timestampUs,
                Rotation = VideoRotation._0,
                Buffer = info,
            },
        });

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _room.UnpublishAsync(_sid);
        _track.Dispose();
        _source.Dispose();
    }
}
