using System.Collections.Concurrent;
using Google.Protobuf;
using LiveKit.Proto;
using Ryadom.Shared;

namespace Ryadom.Live.Ffi;

/// <summary>
/// Разговор с livekit_ffi.dll: запрос — protobuf FfiRequest, ответ —
/// FfiResponse сразу; долгие операции (подключение, публикация) отвечают
/// позже событием с тем же async_id. События приходят из потоков движка —
/// подписчики сами переносят их куда нужно.
/// </summary>
internal static unsafe class FfiClient
{
    // Ссылка на делегат живёт всё время работы: иначе сборщик мусора
    // уберёт его, и движок позовёт уже несуществующую функцию.
    private static readonly Native.EventCallback Callback = OnEvent;
    private static readonly ConcurrentDictionary<ulong, TaskCompletionSource<FfiEvent>> Pending = new();
    private static readonly Lock Gate = new();
    private static long _nextAsyncId;
    private static bool _initialized;

    /// <summary>Все события, кроме ответов на наши async-запросы.</summary>
    public static event Action<FfiEvent>? Event;

    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized) return;
            var version = typeof(FfiClient).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
            Native.Initialize(Callback, captureLogs: true, "ryadom-windows", version);
            _initialized = true;
        }
    }

    public static FfiResponse Request(FfiRequest request)
    {
        EnsureInitialized();
        var size = request.CalculateSize();
        var buffer = size <= 1024 ? stackalloc byte[size] : new byte[size];
        request.WriteTo(buffer);
        fixed (byte* data = buffer)
        {
            var handle = Native.Request(data, (nuint)size, out var response, out var length);
            try
            {
                return FfiResponse.Parser.ParseFrom(new ReadOnlySpan<byte>(response, (int)length));
            }
            finally
            {
                Native.DropHandle(handle);
            }
        }
    }

    public static ulong NextAsyncId() => (ulong)Interlocked.Increment(ref _nextAsyncId);

    /// <summary>Ждать событие-ответ на запрос с этим async_id. Регистрировать
    /// до отправки запроса: ответ может прийти раньше, чем Request вернётся.</summary>
    public static Task<FfiEvent> Expect(ulong asyncId, TimeSpan timeout, CancellationToken ct = default)
    {
        var waiter = new TaskCompletionSource<FfiEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending[asyncId] = waiter;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        cts.Token.Register(() =>
        {
            if (Pending.TryRemove(asyncId, out var stale)) stale.TrySetCanceled();
        });
        _ = waiter.Task.ContinueWith(_ => cts.Dispose(), TaskScheduler.Default);
        return waiter.Task;
    }

    private static void OnEvent(byte* data, nuint size)
    {
        FfiEvent ev;
        try
        {
            ev = FfiEvent.Parser.ParseFrom(new ReadOnlySpan<byte>(data, (int)size));
        }
        catch (InvalidProtocolBufferException error)
        {
            Log.Warn($"LiveKit: непонятное событие: {error.Message}");
            return;
        }

        switch (ev.MessageCase)
        {
            case FfiEvent.MessageOneofCase.Logs:
                foreach (var record in ev.Logs.Records)
                {
                    if (record.Level is LogLevel.LogError or LogLevel.LogWarn)
                        Log.Warn($"LiveKit [{record.Target}] {record.Message}");
                }
                return;
            case FfiEvent.MessageOneofCase.Panic:
                Log.Error($"LiveKit упал: {ev.Panic.Message}");
                break;
        }

        if (AsyncIdOf(ev) is { } id && Pending.TryRemove(id, out var waiter))
        {
            waiter.TrySetResult(ev);
            return;
        }
        try
        {
            Event?.Invoke(ev);
        }
        catch (Exception error)
        {
            // Исключение не должно уйти обратно в движок: он его не переживёт.
            Log.Error("Обработчик события LiveKit", error);
        }
    }

    private static ulong? AsyncIdOf(FfiEvent ev) => ev.MessageCase switch
    {
        FfiEvent.MessageOneofCase.Connect => ev.Connect.AsyncId,
        FfiEvent.MessageOneofCase.Disconnect => ev.Disconnect.AsyncId,
        FfiEvent.MessageOneofCase.Dispose => ev.Dispose.AsyncId,
        FfiEvent.MessageOneofCase.PublishTrack => ev.PublishTrack.AsyncId,
        FfiEvent.MessageOneofCase.UnpublishTrack => ev.UnpublishTrack.AsyncId,
        FfiEvent.MessageOneofCase.CaptureAudioFrame => ev.CaptureAudioFrame.AsyncId,
        FfiEvent.MessageOneofCase.GetStats => ev.GetStats.AsyncId,
        FfiEvent.MessageOneofCase.PublishData => ev.PublishData.AsyncId,
        _ => null,
    };
}
