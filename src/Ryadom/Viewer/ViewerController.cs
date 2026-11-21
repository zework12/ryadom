using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using LiveKit.Proto;
using Ryadom.Live;
using Ryadom.Net;
using Ryadom.Shared;
using Ryadom.ViewModels;

namespace Ryadom.Viewer;

/// <summary>
/// Сторона «Я помогаю»: код → комната → ждём «Разрешить» → картинка,
/// мышь, клавиши, общий буфер обмена.
/// </summary>
public sealed class ViewerController : IAsyncDisposable, IAgentTarget
{
    private static readonly string NameFile = Path.Combine(Log.Folder, "name.txt");

    private readonly JoinViewModel _join;
    private readonly Action<object> _show;
    private readonly Action _back;
    private readonly Dispatcher _ui;
    private CancellationTokenSource? _attempt;

    private LiveRoom? _room;
    private string? _host;
    private SessionViewModel? _session;
    private VideoView? _video;
    private KeyboardGrab? _keyboard;
    private ClipboardSync? _clipboard;
    private AgentServer? _agent;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, (TaskCompletionSource<string> Done, Dictionary<int, string> Parts)> _adPending = new();
    private long _adNext;
    private DispatcherTimer? _stats;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _lastRendered;
    private bool _videoFocused;
    private bool _ending;

    public ViewerController(JoinViewModel join, Action<object> show, Action back)
    {
        _join = join;
        _show = show;
        _back = back;
        _ui = Application.Current.Dispatcher;
        try { _join.Name = File.Exists(NameFile) ? File.ReadAllText(NameFile).Trim() : ""; } catch (IOException) { }
        if (string.IsNullOrEmpty(_join.Name)) _join.Name = Environment.UserName;
        _join.ConnectCommand.Bind(_ => _ = ConnectAsync(), _ => Digits(_join.Code).Length == 9 && _join.State is not (JoinState.Connecting or JoinState.WaitingApproval));
        _join.CancelCommand.Bind(_ => _ = CancelAsync());
        _join.BackCommand.Bind(_ => { _ = CancelAsync(); _back(); });
    }

    private static string Digits(string text) => new(text.Where(char.IsDigit).ToArray());

    private async Task ConnectAsync()
    {
        await LeaveRoomAsync();
        _attempt = new CancellationTokenSource();
        var ct = _attempt.Token;
        _join.ErrorText = null;
        _join.State = JoinState.Connecting;
        _join.ConnectCommand.Refresh();
        var name = string.IsNullOrWhiteSpace(_join.Name) ? "Помощник" : _join.Name.Trim();
        try { Directory.CreateDirectory(Log.Folder); File.WriteAllText(NameFile, name); } catch (IOException) { }
        try
        {
            var grant = await Api.JoinAsync(Digits(_join.Code), name, ct);
            var room = await Connector.ConnectAsync(grant.Urls, grant.Token, subscribe: true, ct);
            _room = room;
            _host = room.Others.Keys.FirstOrDefault(k => k == "host");
            if (_host is null)
            {
                await LeaveRoomAsync();
                Fail("Человек сейчас не в сети: приложение «Рядом» у него закрыто.");
                return;
            }
            room.DataReceived += OnData;
            room.ParticipantLeft += identity => { if (identity == _host) _ui.BeginInvoke(() => _ = EndAsync("Человек закрыл «Рядом».")); };
            room.LinkChanged += link => _ui.BeginInvoke(() => OnLink(link));
            room.QualityChanged += q => _ui.BeginInvoke(() => { if (_session is not null) _session.Quality = Map(q); });
            unsafe { room.FrameReceived += OnFrame; }
            _join.State = JoinState.WaitingApproval;
            room.SendControl(new Control
            {
                T = "hello",
                Name = name,
                Fps = 60,
                Quality = "normal",
                Monitor = 0,
            }, _host);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Log.Error("Подключение", error);
            await LeaveRoomAsync();
            Fail(error is ApiException ? error.Message : "Не получилось подключиться. Проверьте интернет.");
        }
        finally
        {
            _join.ConnectCommand.Refresh();
        }
    }

    private void Fail(string text)
    {
        _join.ErrorText = text;
        _join.State = JoinState.Error;
        _join.ConnectCommand.Refresh();
    }

    private async Task CancelAsync()
    {
        _attempt?.Cancel();
        await LeaveRoomAsync();
        if (_join.State is JoinState.Connecting or JoinState.WaitingApproval) _join.State = JoinState.Idle;
        _join.ConnectCommand.Refresh();
    }

    // ── Сообщения ──────────────────────────────────────────────

    private void OnData(string from, string topic, ReadOnlySpan<byte> data)
    {
        if (from != _host) return;
        if (topic == Protocol.InputTopic)
        {
            // Курсор человека — прямо в окно картинки, без очереди окна.
            if (Protocol.TryDecodeCursor(data, out var fx, out var fy, out var visible, out var shape))
                _video?.SetCursor(fx, fy, visible, shape);
            return;
        }
        if (topic != Protocol.ControlTopic) return;
        var message = Protocol.DecodeControl(data);
        if (message is null) return;
        if (message.T == "cursor" && message.Id is { } shapeId && message.W is { } cw && message.H is { } ch && message.Text is not null)
        {
            try { _video?.AddCursorShape((ushort)shapeId, cw, ch, message.Hx ?? 0, message.Hy ?? 0, Protocol.UnpackPixels(message.Text)); }
            catch (Exception error) when (error is FormatException or InvalidDataException) { }
            return;
        }
        if (message.T == "adr" && message.Id is { } adId && message.Part is { } part && message.Parts is { } parts && message.Text is not null)
        {
            if (_adPending.TryGetValue(adId, out var entry) && parts is > 0 and < 10_000 && part >= 0 && part < parts)
            {
                lock (entry.Parts)
                {
                    entry.Parts[part] = message.Text;
                    if (entry.Parts.Count == parts)
                    {
                        var all = string.Concat(Enumerable.Range(0, parts).Select(i => entry.Parts[i]));
                        try { entry.Done.TrySetResult(System.Text.Encoding.UTF8.GetString(Protocol.UnpackPixels(all))); }
                        catch (Exception error) { entry.Done.TrySetException(error); }
                    }
                }
            }
            return;
        }
        if (message.T == "pong" && message.Id is { } sent)
        {
            var rtt = (int)(_clock.ElapsedMilliseconds - sent);
            _ui.BeginInvoke(() => { if (_session is not null) _session.PingMs = rtt; });
            return;
        }
        _ui.BeginInvoke(() => OnControl(message));
    }

    private void OnControl(Control message)
    {
        switch (message.T)
        {
            case "accept":
                StartSession(message.Name);
                break;
            case "deny":
                _ = LeaveRoomAsync();
                _join.State = JoinState.Denied;
                _join.ConnectCommand.Refresh();
                break;
            case "bye":
                _ = EndAsync("Человек отключил помощь.");
                break;
            case "monitors" when _session is not null && message.Monitors is not null:
                var list = message.Monitors.Select(m => new MonitorOption(m.Index, m.Title, m.Width, m.Height)).ToList();
                _session.PropertyChanged -= OnSessionChanged;
                _session.Monitors = list;
                _session.SelectedMonitor = list.FirstOrDefault(m => m.Index == message.Monitor) ?? list.FirstOrDefault();
                _session.PropertyChanged += OnSessionChanged;
                break;
            case "error" when message.Text is not null:
                Log.Warn($"У человека: {message.Text}");
                Notify("У человека не запускается показ экрана: " + message.Text);
                break;
            case "diag" when message.Text is not null:
                try
                {
                    var text = System.Text.Encoding.UTF8.GetString(Protocol.UnpackPixels(message.Text));
                    Log.Info("Журнал человека:\n" + text);
                }
                catch (Exception error) when (error is FormatException or InvalidDataException) { }
                break;
            case "clip" when message.Text is not null:
                _clipboard?.SetFromRemote(message.Text);
                break;
        }
    }

    // ── Сеанс ──────────────────────────────────────────────────

    private void StartSession(string? peerName)
    {
        if (_room is null || _session is not null) return;
        _session = new SessionViewModel { PeerName = string.IsNullOrWhiteSpace(peerName) ? "Компьютер" : peerName };
        _video = new VideoView();
        _video.Input += (data, reliable) => _room?.Send(data, Protocol.InputTopic, reliable, _host);
        _video.FocusChanged += focused =>
        {
            _videoFocused = focused;
            UpdateKeyboard();
            if (focused) SendLocks();
        };
        _video.FirstFrame += () => _ui.BeginInvoke(() => { if (_session is not null) _session.HasVideo = true; });
        _session.Video = _video;
        _keyboard = new KeyboardGrab(data => _room?.Send(data, Protocol.InputTopic, reliable: true, _host))
        {
            Root = () => _video is { } v ? GetAncestor(v.Hwnd, 2) : IntPtr.Zero,
        };
        _clipboard = new ClipboardSync(text =>
        {
            _room?.SendControl(new Control { T = "clip", Text = text }, _host);
            Notify("Скопированный текст передан");
        });
        _session.ToggleFullscreenCommand.Bind(_ => _session.IsFullscreen = !_session.IsFullscreen);
        _session.DisconnectCommand.Bind(_ => _ = EndAsync(null, sendBye: true));
        _session.SendKeysCommand.Bind(p =>
        {
            if (p is string combo) _room?.SendControl(new Control { T = "keys", Combo = combo }, _host);
        });
        _session.PropertyChanged += OnSessionChanged;
        _stats = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Stats(), _ui);
        _stats.Start();
        try { _agent = new AgentServer(this); }
        catch (Exception error) { Log.Warn($"Вход для Claude не открылся: {error.Message}"); }
        _join.State = JoinState.Idle;
        _show(_session);
        Log.Info("Сеанс начат");
        _ = WatchFirstFrameAsync(_session);
    }

    /// <summary>Картинки нет 10 с — сказать об этом и забрать журнал человека (для разбора).</summary>
    private async Task WatchFirstFrameAsync(SessionViewModel session)
    {
        await Task.Delay(TimeSpan.FromSeconds(10));
        if (_session != session || session.HasVideo || _room is null || _host is null) return;
        Log.Warn("Картинки нет 10 с после разрешения");
        _room.SendControl(new Control { T = "diag" }, _host);
        Notify("Картинки пока нет — проверяю связь…");
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_session is null || _room is null) return;
        switch (e.PropertyName)
        {
            case nameof(SessionViewModel.SelectedFps) or nameof(SessionViewModel.SelectedQuality) or nameof(SessionViewModel.SelectedMonitor):
                _session.HasVideo = _video?.Rendered > 0;
                _room.SendControl(new Control
                {
                    T = "settings",
                    Fps = _session.SelectedFps.Fps,
                    Quality = _session.SelectedQuality.Key,
                    Monitor = _session.SelectedMonitor?.Index ?? 0,
                }, _host);
                break;
            case nameof(SessionViewModel.ControlEnabled):
                if (_video is not null) _video.ControlEnabled = _session.ControlEnabled;
                if (!_session.ControlEnabled) _room.Send(Protocol.EncodeReleaseAll(), Protocol.InputTopic, true, _host);
                UpdateKeyboard();
                break;
        }
    }

    /// <summary>Caps Lock и Num Lock у человека — как у помощника.</summary>
    private void SendLocks()
    {
        if (_room is null || _host is null || _session?.ControlEnabled != true) return;
        _room.SendControl(new Control { T = "locks", Caps = (GetKeyState(0x14) & 1) != 0, Num = (GetKeyState(0x90) & 1) != 0 }, _host);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetKeyState(int vk);

    // ── Вход для Claude ────────────────────────────────────────

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    public (int Width, int Height) FrameSize => _video is { } v ? (v.SourceWidth, v.SourceHeight) : (0, 0);

    public byte[]? FramePng(double scale)
    {
        var video = _video;
        if (video is null) return null;
        var pixels = video.Snapshot(out var w, out var h);
        if (pixels is null) return null;
        System.Windows.Media.Imaging.BitmapSource image = System.Windows.Media.Imaging.BitmapSource.Create(
            w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, w * 4);
        if (scale < 0.999)
            image = new System.Windows.Media.Imaging.TransformedBitmap(image, new System.Windows.Media.ScaleTransform(scale, scale));
        image.Freeze();
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private void SendInput(byte[] data, bool reliable) => _room?.Send(data, Protocol.InputTopic, reliable, _host);

    private void MoveTo(int x, int y)
    {
        var (w, h) = FrameSize;
        if (w <= 1 || h <= 1) return;
        SendInput(Protocol.EncodeMove(x / (double)(w - 1), y / (double)(h - 1)), reliable: true);
    }

    public void AgentMove(int x, int y) => MoveTo(x, y);

    public void AgentClick(int x, int y, string button, bool twice)
    {
        var b = button switch { "right" => Protocol.Right, "middle" => Protocol.Middle, _ => Protocol.Left };
        MoveTo(x, y);
        Thread.Sleep(30);
        for (var i = 0; i < (twice ? 2 : 1); i++)
        {
            SendInput(Protocol.EncodeButton(b, true), reliable: true);
            Thread.Sleep(40);
            SendInput(Protocol.EncodeButton(b, false), reliable: true);
            Thread.Sleep(60);
        }
    }

    public void AgentScroll(int x, int y, int notches)
    {
        MoveTo(x, y);
        SendInput(Protocol.EncodeWheel(notches * 120, false), reliable: true);
    }

    /// <summary>Сочетание по именам: «ctrl+shift+esc», «enter», «win+r», «alt+f4», «a».</summary>
    public void AgentKeys(string combo)
    {
        var keys = combo.ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(KeyScan).ToList();
        if (keys.Count == 0 || keys.Any(k => k.Scan == 0)) throw new ArgumentException($"Не знаю клавишу в «{combo}»");
        foreach (var k in keys) { SendInput(Protocol.EncodeKey(k.Scan, false, k.Ext), true); Thread.Sleep(20); }
        foreach (var k in Enumerable.Reverse(keys)) { SendInput(Protocol.EncodeKey(k.Scan, true, k.Ext), true); Thread.Sleep(20); }
    }

    /// <summary>Текст — через общий буфер обмена и Ctrl+V: так верно и по-русски, и по-английски.</summary>
    public void AgentText(string text)
    {
        if (_room is null || _host is null || text.Length == 0) return;
        _room.SendControl(new Control { T = "clip", Text = text }, _host);
        Thread.Sleep(250);
        AgentKeys("ctrl+v");
    }

    public Task<string> AgentDesktopAsync(string[] args) =>
        RequestAsync("ad", System.Text.Json.JsonSerializer.Serialize(args));

    public Task<string> AgentJobAsync(string requestJson) => RequestAsync("job", requestJson);

    /// <summary>Запрос человеку с ответом кусками («adr»).</summary>
    private async Task<string> RequestAsync(string type, string payload)
    {
        if (_room is null || _host is null) return """{"ok":false,"error":{"code":"RYADOM","message":"нет сеанса"}}""";
        var id = Interlocked.Increment(ref _adNext);
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _adPending[id] = (done, new Dictionary<int, string>());
        try
        {
            _room.SendControl(new Control { T = type, Id = id, Text = payload }, _host);
            var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(100)));
            return finished == done.Task
                ? await done.Task
                : """{"ok":false,"error":{"code":"RYADOM","message":"тот компьютер не ответил (старая версия «Рядом»?)"}}""";
        }
        finally
        {
            _adPending.TryRemove(id, out _);
        }
    }

    public bool UserInput
    {
        get => _session?.ControlEnabled ?? false;
        set => _ui.Invoke(() =>
        {
            if (_session is null) return;
            _session.ControlEnabled = value;
            Notify(value ? "Твоя мышь и клавиатура снова управляют" : "Управляет Claude — твоя мышь и клавиатура отключены");
        });
    }

    private static (ushort Scan, bool Ext) KeyScan(string name)
    {
        if (name.Length == 1)
        {
            const string row1 = "qwertyuiop", row2 = "asdfghjkl", row3 = "zxcvbnm", digits = "1234567890";
            var c = name[0];
            if (row1.IndexOf(c) is var a and >= 0) return ((ushort)(0x10 + a), false);
            if (row2.IndexOf(c) is var b and >= 0) return ((ushort)(0x1E + b), false);
            if (row3.IndexOf(c) is var d and >= 0) return ((ushort)(0x2C + d), false);
            if (digits.IndexOf(c) is var e and >= 0) return ((ushort)(0x02 + e), false);
        }
        if (name.Length is 2 or 3 && name[0] == 'f' && int.TryParse(name[1..], out var f) && f is >= 1 and <= 12)
            return ((ushort)(f <= 10 ? 0x3A + f : 0x57 + f - 11), false);
        return name switch
        {
            "ctrl" or "control" => (0x1D, false),
            "shift" => (0x2A, false),
            "alt" => (0x38, false),
            "win" => (0x5B, true),
            "enter" => (0x1C, false),
            "esc" or "escape" => (0x01, false),
            "tab" => (0x0F, false),
            "space" => (0x39, false),
            "backspace" => (0x0E, false),
            "delete" or "del" => (0x53, true),
            "up" => (0x48, true),
            "down" => (0x50, true),
            "left" => (0x4B, true),
            "right" => (0x4D, true),
            "home" => (0x47, true),
            "end" => (0x4F, true),
            "pageup" => (0x49, true),
            "pagedown" => (0x51, true),
            _ => (0, false),
        };
    }

    private void UpdateKeyboard()
    {
        if (_keyboard is not null) _keyboard.Active = _videoFocused && (_session?.ControlEnabled ?? false);
    }

    private unsafe void OnFrame(byte* bgra, int width, int height, int stride) => _video?.Push(bgra, width, height, stride);

    private void Stats()
    {
        if (_session is null || _video is null || _room is null) return;
        var rendered = _video.Rendered;
        _session.ActualFps = rendered - _lastRendered;
        _lastRendered = rendered;
        if (_video.SourceWidth > 0) _session.ResolutionText = $"{_video.SourceWidth}×{_video.SourceHeight}";
        _room.SendControl(new Control { T = "ping", Id = _clock.ElapsedMilliseconds }, _host);
    }

    private async void Notify(string text)
    {
        if (_session is null) return;
        _session.Notice = text;
        await Task.Delay(2500);
        if (_session is not null && _session.Notice == text) _session.Notice = null;
    }

    private void OnLink(RoomLink link)
    {
        if (_session is null)
        {
            if (link == RoomLink.Lost && _join.State == JoinState.WaitingApproval) { _ = LeaveRoomAsync(); Fail("Связь пропала. Попробуйте ещё раз."); }
            return;
        }
        _session.IsReconnecting = link == RoomLink.Reconnecting;
        if (link == RoomLink.Lost) _ = EndAsync("Связь пропала. Подключитесь снова тем же кодом.");
    }

    private static LinkQuality Map(ConnectionQuality q) => q switch
    {
        ConnectionQuality.QualityExcellent => LinkQuality.Excellent,
        ConnectionQuality.QualityGood => LinkQuality.Good,
        ConnectionQuality.QualityPoor => LinkQuality.Poor,
        _ => LinkQuality.Lost,
    };

    /// <summary>Конец сеанса: назад к вводу кода (код остаётся в поле).</summary>
    private async Task EndAsync(string? reason, bool sendBye = false)
    {
        if (_ending) return;
        _ending = true;
        try
        {
            if (sendBye && _host is not null) _room?.SendControl(new Control { T = "bye" }, _host);
            await LeaveRoomAsync();
            _join.State = reason is null ? JoinState.Idle : JoinState.Error;
            _join.ErrorText = reason;
            _join.ConnectCommand.Refresh();
            _show(_join);
        }
        finally
        {
            _ending = false;
        }
    }

    private async Task LeaveRoomAsync()
    {
        _stats?.Stop();
        _stats = null;
        _agent?.Dispose();
        _agent = null;
        _keyboard?.Dispose();
        _keyboard = null;
        _clipboard?.Dispose();
        _clipboard = null;
        if (_session is not null)
        {
            _session.PropertyChanged -= OnSessionChanged;
            _session.IsFullscreen = false;
            _session.Video = null;
            _session = null;
        }
        _video?.Dispose();
        _video = null;
        _lastRendered = 0;
        var room = _room;
        _room = null;
        _host = null;
        if (room is not null)
        {
            unsafe { room.FrameReceived -= OnFrame; }
            await room.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null && _host is not null) _room?.SendControl(new Control { T = "bye" }, _host);
        _attempt?.Cancel();
        await LeaveRoomAsync();
    }
}
