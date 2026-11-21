using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Ryadom.Live;
using Ryadom.Net;
using Ryadom.Shared;
using Ryadom.ViewModels;
using Ryadom.Views;

namespace Ryadom.Host;

/// <summary>
/// Сторона «Мне нужна помощь». Код → комната → помощник стучится →
/// человек нажимает «Разрешить» → только тогда экран уходит в сеть и
/// принимаются мышь и клавиши. Пока управляют — рамка по краю экрана и
/// плашка «Отключить» (помощник их не видит).
/// </summary>
public sealed class HostController : IAsyncDisposable
{
    private readonly HostViewModel _vm;
    private readonly Dispatcher _ui;
    private readonly Action _bringToFront;
    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly DispatcherTimer _clock;

    private LiveRoom? _room;
    private HostGrant? _grant;
    private string? _pending;        // кто стучится
    private string? _active;         // кому разрешено
    private Control _settings = new() { Fps = 60, Quality = "normal", Monitor = 0 };
    private ScreenSource? _capture;
    private ScreenTrack? _track;
    private InputInjector? _injector;
    private CursorWatcher? _cursor;
    private readonly PillGuard _pillGuard = new();
    private readonly DispatcherTimer _pillTimer;
    private ClipboardSync? _clipboard;
    private ScreenFrameWindow? _frame;
    private ControlPillWindow? _pill;
    private MonitorDesc? _monitor;
    private DateTime _since;
    private bool _disposed;

    public HostController(HostViewModel vm, Action bringToFront, Action back)
    {
        _vm = vm;
        _ui = Application.Current.Dispatcher;
        _bringToFront = bringToFront;
        _vm.IsElevated = Elevation.IsElevated;
        _vm.AllowCommand.Bind(_ => _ = AllowAsync());
        _vm.DenyCommand.Bind(_ => Deny());
        _vm.DisconnectCommand.Bind(_ => _ = StopHelpAsync(sendBye: true));
        _vm.RetryCommand.Bind(_ => _ = StartAsync());
        _vm.BackCommand.Bind(_ => back());
        _vm.CopyCodeCommand.Bind(_ => CopyCode());
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => Tick(), _ui);
        _pillTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Normal, (_, _) => _pillGuard.Check(), _ui);
    }

    public async Task StartAsync()
    {
        if (_disposed) return;
        _vm.State = HostState.Preparing;
        _vm.ErrorText = null;
        try
        {
            _grant = await Api.HostAsync(Environment.MachineName, _life.Token);
            _vm.Code = FormatCode(_grant.Code);
            await ConnectRoomAsync(_grant.Urls, _grant.Token);
            _vm.State = HostState.Waiting;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Log.Error("Не получилось начать", error);
            _vm.ErrorText = error is ApiException ? error.Message : "Не получилось подключиться. Проверьте интернет.";
            _vm.State = HostState.Error;
        }
    }

    public static string FormatCode(string code) =>
        code.Length == 9 ? $"{code[..3]} {code[3..6]} {code[6..]}" : code;

    private async Task ConnectRoomAsync(string[] urls, string token)
    {
        var room = await Connector.ConnectAsync(urls, token, subscribe: false, _life.Token);
        room.ParticipantJoined += (identity, name) => _ui.BeginInvoke(() => OnJoined(identity, name));
        room.ParticipantLeft += identity => _ui.BeginInvoke(() => OnLeft(identity));
        room.DataReceived += OnData;
        room.LinkChanged += link => _ui.BeginInvoke(() => OnLink(link));
        _room = room;
        // Помощник мог войти раньше нас (переподключение) — спросить сразу.
        foreach (var (identity, name) in room.Others) _ = _ui.BeginInvoke(() => OnJoined(identity, name));
    }

    // ── Помощник ───────────────────────────────────────────────

    private void OnJoined(string identity, string name)
    {
        if (_room is null || _disposed) return;
        if (identity == _active)
        {
            // Помощник переподключился (у LiveKit полный перезапуск связи) — помощь продолжается.
            _activeLeftAt = null;
            _lastViewerSeen = DateTime.Now;
            Log.Info("Помощник вернулся после обрыва");
            _ = RestartCaptureAsync();
            return;
        }
        if (_active is not null || (_pending is not null && _pending != identity))
        {
            // Уже помогают — второго не пускаем.
            _room.SendControl(new Control { T = "deny" }, identity);
            return;
        }
        _pending = identity;
        _vm.ViewerName = string.IsNullOrWhiteSpace(name) ? "Помощник" : name;
        _vm.State = HostState.Asking;
        _bringToFront();
    }

    private void OnLeft(string identity)
    {
        if (identity == _pending)
        {
            _pending = null;
            if (_vm.State == HostState.Asking) _vm.State = HostState.Waiting;
        }
        if (identity == _active)
        {
            // Не сразу: при перезапуске связи LiveKit человек «выходит» и тут же входит снова.
            _activeLeftAt ??= DateTime.Now;
            Log.Info("Помощник пропал — ждём 30 с");
        }
    }

    private DateTime? _activeLeftAt;
    private DateTime _lastViewerSeen = DateTime.Now;

    private async Task AllowAsync()
    {
        if (_room is null || _pending is null) return;
        _active = _pending;
        _pending = null;
        _activeLeftAt = null;
        _lastViewerSeen = DateTime.Now;
        _since = DateTime.Now;
        _vm.DurationText = "00:00";
        _vm.State = HostState.Connected;
        _clock.Start();
        _room.SendControl(new Control { T = "accept", Name = Environment.MachineName }, _active);
        _clipboard = new ClipboardSync(text => _room?.SendControl(new Control { T = "clip", Text = text }, _active));
        Log.Info("Помощь разрешена");
        await RestartCaptureAsync();
    }

    private void Deny()
    {
        if (_room is null || _pending is null) return;
        _room.SendControl(new Control { T = "deny" }, _pending);
        _pending = null;
        _vm.State = HostState.Waiting;
    }

    /// <summary>Закончить помощь: экран снять, ввод отпустить, рамку убрать. Код остаётся.</summary>
    private async Task StopHelpAsync(bool sendBye)
    {
        var who = _active;
        if (who is null) return;
        _active = null;
        if (sendBye) _room?.SendControl(new Control { T = "bye" }, who);
        _clock.Stop();
        _injector?.ReleaseAll();
        _injector = null;
        _cursor?.Dispose();
        _cursor = null;
        _clipboard?.Dispose();
        _clipboard = null;
        await StopCaptureAsync();
        CloseOverlays();
        if (!_disposed) _vm.State = HostState.Waiting;
        Log.Info("Помощь закончена");
    }

    // ── Данные от помощника ────────────────────────────────────

    private void OnData(string from, string topic, ReadOnlySpan<byte> data)
    {
        if (topic == Protocol.InputTopic)
        {
            // Мышь — прямо отсюда, без очереди окна: так быстрее.
            if (from == _active && !Tests.AutoDrive.NoInput) _injector?.Handle(data);
            return;
        }
        if (topic != Protocol.ControlTopic) return;
        var message = Protocol.DecodeControl(data);
        if (message is null) return;
        if (from == _active) _lastViewerSeen = DateTime.Now;
        if (message.T == "ping")
        {
            _room?.SendControl(new Control { T = "pong", Id = message.Id }, from);
            return;
        }
        _ui.BeginInvoke(() => OnControl(from, message));
    }

    private void OnControl(string from, Control message)
    {
        switch (message.T)
        {
            case "hello" when from == _pending || from == _active:
                if (!string.IsNullOrWhiteSpace(message.Name)) _vm.ViewerName = message.Name;
                ApplySettings(message, restart: false);
                break;
            case "settings" when from == _active:
                ApplySettings(message, restart: true);
                break;
            case "clip" when from == _active && message.Text is not null:
                _clipboard?.SetFromRemote(message.Text);
                break;
            case "keys" when from == _active && message.Combo is not null && !Tests.AutoDrive.NoInput:
                var combo = message.Combo;
                _ = Task.Run(() => _injector?.Combo(combo));
                break;
            case "locks" when from == _active && !Tests.AutoDrive.NoInput:
                var (caps, num) = (message.Caps, message.Num);
                _ = Task.Run(() => _injector?.SetLocks(caps, num));
                break;
            case "diag" when from == _active:
                var text = $"Windows {Environment.OSVersion.Version}, админ: {Elevation.IsElevated}, захват поддерживается: {ScreenSource.IsSupported}, экранов: {Monitors.All().Count}\n" + Log.Tail(60);
                _room?.SendControl(new Control { T = "diag", Text = Protocol.PackPixels(System.Text.Encoding.UTF8.GetBytes(text)) }, _active);
                break;
            case "ad" when from == _active && message.Id is { } adId && message.Text is not null:
                _ = RunDesktopAgentAsync(from, adId, message.Text);
                break;
            case "job" when from == _active && message.Id is { } jobId && message.Text is not null:
                var request = message.Text;
                _ = Task.Run(() => SendChunked(from, jobId, RemoteJobs.Handle(request)));
                break;
            case "bye" when from == _active:
                _ = StopHelpAsync(sendBye: false);
                break;
        }
    }

    /// <summary>Команда agent-desktop от помощника → ответ кусками (канал данных
    /// не любит большие сообщения, а снимок дерева окна бывает сотни КБ).</summary>
    private async Task RunDesktopAgentAsync(string to, long id, string argsJson)
    {
        string[] args;
        try { args = System.Text.Json.JsonSerializer.Deserialize<string[]>(argsJson) ?? []; }
        catch (System.Text.Json.JsonException) { args = []; }
        var result = await DesktopAgent.RunAsync(args, TimeSpan.FromSeconds(90));
        SendChunked(to, id, result);
    }

    private void SendChunked(string to, long id, string result)
    {
        var packed = Protocol.PackPixels(System.Text.Encoding.UTF8.GetBytes(result));
        var chunk = int.TryParse(Environment.GetEnvironmentVariable("RYADOM_AD_CHUNK"), out var c) && c > 100 ? c : 12_000;
        var parts = Math.Max(1, (packed.Length + chunk - 1) / chunk);
        for (var i = 0; i < parts; i++)
        {
            var piece = packed.Substring(i * chunk, Math.Min(chunk, packed.Length - i * chunk));
            _room?.SendControl(new Control { T = "adr", Id = id, Part = i, Parts = parts, Text = piece }, to);
        }
    }

    private void ApplySettings(Control message, bool restart)
    {
        var next = new Control
        {
            Fps = Quality.ClampFps(message.Fps ?? _settings.Fps),
            Quality = message.Quality ?? _settings.Quality,
            Monitor = message.Monitor ?? _settings.Monitor,
        };
        var changed = next.Fps != _settings.Fps || next.Quality != _settings.Quality || next.Monitor != _settings.Monitor;
        _settings = next;
        if (restart && changed) _ = RestartCaptureAsync();
    }

    // ── Захват ─────────────────────────────────────────────────

    private async Task RestartCaptureAsync()
    {
        await _captureGate.WaitAsync();
        try
        {
            await StopCaptureCoreAsync();
            if (_room is null || _active is null) return;
            var monitors = Monitors.All();
            var index = Math.Clamp(_settings.Monitor ?? 0, 0, monitors.Count - 1);
            _monitor = monitors[index];
            var fps = Quality.ClampFps(_settings.Fps);
            var capture = new ScreenSource(_monitor, fps, Quality.MaxHeight(_settings.Quality));
            capture.Ended += () => _ui.BeginInvoke(() => _ = RestartCaptureAsync());
            _track = await _room.PublishScreenAsync(capture.Width, capture.Height, fps,
                Quality.Bitrate(_settings.Quality, capture.Width, capture.Height, fps));
            capture.Target = _track;
            _capture = capture;
            if (_injector is null) _injector = new InputInjector(_monitor.Bounds) { Pill = _pillGuard };
            else _injector.SetMonitor(_monitor.Bounds);
            var viewer = _active;
            if (_cursor is null)
            {
                var room = _room;
                _cursor = new CursorWatcher(_monitor.Bounds,
                    data => room.Send(data, Protocol.InputTopic, reliable: false, viewer),
                    shape => room.SendControl(shape, viewer));
            }
            else _cursor.SetMonitor(_monitor.Bounds);
            _room.SendControl(new Control
            {
                T = "monitors",
                Monitor = index,
                Monitors = monitors.Select(m => new MonitorInfo { Index = m.Index, Title = m.Title, Width = m.Bounds.Width, Height = m.Bounds.Height }).ToList(),
            }, _active);
            ShowOverlays(_monitor.Bounds);
        }
        catch (Exception error)
        {
            Log.Error("Экран не показывается", error);
            _vm.ErrorText = "Не получилось показать экран.";
            if (_active is not null) _room?.SendControl(new Control { T = "error", Text = error.Message }, _active);
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private async Task StopCaptureAsync()
    {
        await _captureGate.WaitAsync();
        try { await StopCaptureCoreAsync(); }
        finally { _captureGate.Release(); }
    }

    private async Task StopCaptureCoreAsync()
    {
        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            capture.Target = null;
            capture.Dispose();
        }
        var track = _track;
        _track = null;
        if (track is not null) await track.DisposeAsync();
    }

    private void ShowOverlays(Int32Rect bounds)
    {
        try
        {
            if (_frame is null) { _frame = new ScreenFrameWindow(bounds); _frame.Show(); }
            else _frame.MoveTo(bounds);
            if (_pill is null)
            {
                _pill = new ControlPillWindow(_vm, bounds);
                _pill.Show();
                _pillGuard.Attach(new System.Windows.Interop.WindowInteropHelper(_pill).Handle);
                _pillTimer.Start();
            }
            else _pill.MoveTo(bounds);
        }
        catch (Exception error)
        {
            Log.Error("Рамка не показалась", error);
        }
    }

    private void CloseOverlays()
    {
        _frame?.Close();
        _frame = null;
        _pillTimer.Stop();
        _pillGuard.Attach(IntPtr.Zero);
        _pill?.Close();
        _pill = null;
    }

    // ── Прочее ─────────────────────────────────────────────────

    private void OnLink(RoomLink link)
    {
        if (_disposed) return;
        switch (link)
        {
            case RoomLink.Reconnecting:
                _vm.State = HostState.Reconnecting;
                break;
            case RoomLink.Connected:
                _vm.State = _active is not null ? HostState.Connected : _pending is not null ? HostState.Asking : HostState.Waiting;
                break;
            case RoomLink.Lost:
                _ = ReconnectAsync();
                break;
        }
    }

    /// <summary>LiveKit сдался — входим заново с тем же кодом.</summary>
    private async Task ReconnectAsync()
    {
        await StopHelpAsync(sendBye: false);
        _vm.State = HostState.Reconnecting;
        var old = _room;
        _room = null;
        if (old is not null) await old.DisposeAsync();
        for (var attempt = 0; !_disposed; attempt++)
        {
            try
            {
                var grant = await Api.RenewAsync(_grant!.Code, _grant.HostKey, _life.Token);
                await ConnectRoomAsync(grant.Urls, grant.Token);
                _vm.State = HostState.Waiting;
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (ApiException error) when (error.Message.Contains("устарел"))
            {
                await StartAsync();
                return;
            }
            catch (Exception error)
            {
                Log.Warn($"Переподключение: {error.Message}");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(15, 2 + attempt * 2)), _life.Token).ContinueWith(_ => { });
            }
        }
    }

    private void Tick()
    {
        // Помощник ушёл и не вернулся, или от него давно ни звука (пинг раз в секунду) — помощь окончена.
        if (_active is not null && ((_activeLeftAt is { } left && DateTime.Now - left > TimeSpan.FromSeconds(30))
                                    || DateTime.Now - _lastViewerSeen > TimeSpan.FromSeconds(45)))
        {
            Log.Info("Помощник не вернулся — помощь закончена");
            _activeLeftAt = null;
            _ = StopHelpAsync(sendBye: false);
            return;
        }
        var span = DateTime.Now - _since;
        _vm.DurationText = span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"mm\:ss");
        // Плашка и рамка — поверх окон, которые сами лезут наверх.
        if (_frame is not null) Interop.WindowHelpers.BringTopmost(_frame);
        if (_pill is not null) Interop.WindowHelpers.BringTopmost(_pill);
    }

    private async void CopyCode()
    {
        try
        {
            Clipboard.SetText(_vm.Code.Replace(" ", ""));
            _vm.CodeCopied = true;
            await Task.Delay(2000);
            _vm.CodeCopied = false;
        }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopHelpAsync(sendBye: true);
        _disposed = true;
        _life.Cancel();
        _clock.Stop();
        CloseOverlays();
        if (_room is not null) await _room.DisposeAsync();
        _room = null;
    }
}

public static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Перезапуститься с правами администратора (спросит Windows). false — человек отказался.</summary>
    public static bool RestartElevated(string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
