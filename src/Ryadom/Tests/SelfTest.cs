using System.Diagnostics;
using System.Text;
using Ryadom.Host;
using Ryadom.Live;
using Ryadom.Net;
using Ryadom.Shared;

namespace Ryadom.Tests;

/// <summary>
/// Проверка без окон и без настоящего ввода: обе стороны в одном процессе
/// через настоящий сервер. `Ryadom.exe --selftest out.txt [кадры] [качество] [synthetic|screen] [секунды]`.
/// synthetic — движущаяся картинка с заданной частотой (меряет кодек и сеть);
/// screen — настоящий захват экрана (проверяет перевод в NV12 видеокартой).
/// Кадры никуда не сохраняются — считаются только числа.
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(string[] args)
    {
        var i = Array.IndexOf(args, "--selftest");
        var output = args[i + 1];
        var fps = i + 2 < args.Length ? int.Parse(args[i + 2]) : 60;
        var quality = i + 3 < args.Length ? args[i + 3] : "normal";
        var mode = i + 4 < args.Length ? args[i + 4] : "synthetic";
        var seconds = i + 5 < args.Length ? int.Parse(args[i + 5]) : 10;
        var report = new StringBuilder();
        void Say(string line) { report.AppendLine(line); Log.Info("selftest: " + line); }
        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 60)).Token;

        try
        {
            var grant = await Api.HostAsync("selftest", ct);
            Say($"код получен ({grant.Code.Length} цифр)");
            await using var host = await Connector.ConnectAsync(grant.Urls, grant.Token, subscribe: false, ct);
            var join = await Api.JoinAsync(grant.Code, "selftest-viewer", ct);
            await using var viewer = await Connector.ConnectAsync(join.Urls, join.Token, subscribe: true, ct);
            Say($"обе стороны в комнате; помощник видит: {string.Join(",", viewer.Others.Keys)}");

            // Данные: помощник → человек.
            var inputs = 0;
            var controlTexts = new List<string>();
            host.DataReceived += (from, topic, data) =>
            {
                if (topic == Protocol.InputTopic) Interlocked.Increment(ref inputs);
                else if (topic == Protocol.ControlTopic)
                {
                    var m = Protocol.DecodeControl(data);
                    if (m?.T == "ping") host.SendControl(new Control { T = "pong", Id = m.Id }, from);
                    else if (m is not null) lock (controlTexts) controlTexts.Add(m.T);
                }
            };
            var rtts = new List<long>();
            var cursorPackets = 0;
            var shapes = new List<string>();
            var clock = Stopwatch.StartNew();
            viewer.DataReceived += (_, topic, data) =>
            {
                if (topic == Protocol.InputTopic)
                {
                    if (Protocol.TryDecodeCursor(data, out var _cx, out var _cy, out var _cv, out var _cs)) Interlocked.Increment(ref cursorPackets);
                    return;
                }
                var m = Protocol.DecodeControl(data);
                if (m?.T == "pong" && m.Id is { } sent) lock (rtts) rtts.Add(clock.ElapsedMilliseconds - sent);
                if (m?.T == "cursor" && m.Text is not null)
                {
                    var px = Protocol.UnpackPixels(m.Text);
                    var opaque = 0;
                    for (var k = 3; k < px.Length; k += 4) if (px[k] > 0) opaque++;
                    lock (shapes) shapes.Add($"#{m.Id} {m.W}×{m.H} точка {m.Hx},{m.Hy}, видимых точек {opaque}, сжато {m.Text.Length} симв.");
                }
            };

            // Картинка.
            var arrivals = new List<long>();
            var probe = new FrameProbe(clock, arrivals);
            probe.Attach(viewer);

            var monitor = Monitors.All()[0];
            var maxHeight = Quality.MaxHeight(quality);
            ScreenSource? capture = null;
            int width, height;
            if (mode == "screen")
            {
                capture = new ScreenSource(monitor, fps, maxHeight);
                (width, height) = (capture.Width, capture.Height);
            }
            else
            {
                (width, height) = ScreenSource.FitSize(monitor.Bounds.Width, monitor.Bounds.Height, maxHeight);
            }
            var bitrate = Quality.Bitrate(quality, width, height, fps);
            await using var track = await host.PublishScreenAsync(width, height, fps, bitrate, ct);
            Say($"экран опубликован {width}×{height} @ {fps}, {bitrate / 1000} кбит/с, режим {mode}");

            var pushed = new int[1];
            var stop = false;
            Task producer;
            CursorWatcher? cursorWatch = null;
            if (capture is not null)
            {
                cursorWatch = new CursorWatcher(monitor.Bounds,
                    data => host.Send(data, Protocol.InputTopic, reliable: false, viewer.LocalIdentity),
                    shape => host.SendControl(shape, viewer.LocalIdentity));
                capture.Target = track;
                producer = Task.CompletedTask;
            }
            else
            {
                producer = Task.Run(() => Synthetic(track, width, height, fps, () => stop, pushed));
            }

            // Мышь и пинг, пока идёт картинка.
            var sender = Task.Run(async () =>
            {
                var n = 0;
                while (!stop)
                {
                    viewer.Send(Protocol.EncodeMove((n % 100) / 100.0, 0.5), Protocol.InputTopic, reliable: false, "host");
                    if (n % 25 == 0) viewer.SendControl(new Control { T = "ping", Id = clock.ElapsedMilliseconds }, "host");
                    n++;
                    await Task.Delay(8);
                }
            });

            await Task.Delay(TimeSpan.FromSeconds(3), ct); // разгон кодека
            var f0 = probe.Frames;
            var t0 = clock.ElapsedMilliseconds;
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
            var f1 = probe.Frames;
            var t1 = clock.ElapsedMilliseconds;
            stop = true;
            await producer;
            await sender;
            capture?.Dispose();
            cursorWatch?.Dispose();
            if (cursorWatch is not null)
            {
                Say($"курсор: пакетов положения {cursorPackets}");
                lock (shapes) foreach (var line in shapes) Say("форма курсора " + line);
            }

            long[] gaps;
            lock (arrivals) gaps = arrivals.Where(a => a >= t0).Zip(arrivals.Where(a => a >= t0).Skip(1), (a, b) => b - a).OrderBy(g => g).ToArray();
            Say($"принято кадров: {f1 - f0} за {(t1 - t0) / 1000.0:F1} с = {(f1 - f0) * 1000.0 / (t1 - t0):F1} к/с; размер {probe.Width}×{probe.Height}; яркость середины {probe.Brightness}");
            if (gaps.Length > 0) Say($"промежутки между кадрами: медиана {gaps[gaps.Length / 2]} мс, 95% {gaps[(int)(gaps.Length * 0.95)]} мс, макс {gaps[^1]} мс");
            if (capture is not null) Say($"захват отдал кадров: {capture.Pushed}");
            else Say($"отправлено кадров: {pushed[0]}");
            Say($"пакетов мыши дошло: {inputs}");
            lock (rtts) if (rtts.Count > 0) Say($"пинг через сервер: мин {rtts.Min()} мс, медиана {rtts.OrderBy(r => r).ElementAt(rtts.Count / 2)} мс, пакетов {rtts.Count}");
            await File.WriteAllTextAsync(output, report.ToString());
            return 0;
        }
        catch (Exception error)
        {
            Say("ОШИБКА: " + error);
            await File.WriteAllTextAsync(output, report.ToString());
            return 1;
        }
    }

    /// <summary>Движущаяся картинка NV12: полосы и бегущий квадрат — кодеку есть что сжимать.</summary>
    private static unsafe void Synthetic(ScreenTrack track, int width, int height, int fps, Func<bool> stop, int[] pushed)
    {
        var y = new byte[width * height];
        var uv = new byte[width * height / 2];
        Array.Fill(uv, (byte)128);
        var clock = Stopwatch.StartNew();
        var frame = 0;
        while (!stop())
        {
            var shift = frame * 4;
            for (var row = 0; row < height; row++)
            {
                var line = row * width;
                for (var col = 0; col < width; col++) y[line + col] = (byte)(((col + shift) / 32 % 2 == 0) ? 60 : 180);
            }
            var bx = (frame * 13) % Math.Max(1, width - 200);
            var by = (frame * 7) % Math.Max(1, height - 200);
            for (var row = by; row < by + 200; row++)
                Array.Fill(y, (byte)250, row * width + bx, 200);
            fixed (byte* py = y)
            fixed (byte* puv = uv)
                track.PushNv12(py, width, puv, width, width, height, clock.ElapsedTicks * 1_000_000 / Stopwatch.Frequency);
            pushed[0]++;
            frame++;
            var next = frame * 1000.0 / fps;
            var wait = next - clock.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
        }
    }

    private sealed class FrameProbe(Stopwatch clock, List<long> arrivals)
    {
        public int Frames;
        public int Width, Height;
        public long Brightness;

        public unsafe void Attach(LiveRoom room) => room.FrameReceived += OnFrame;

        private unsafe void OnFrame(byte* bgra, int w, int h, int stride)
        {
            Interlocked.Increment(ref Frames);
            Width = w;
            Height = h;
            Brightness = bgra[(h / 2) * stride + (w / 2) * 4 + 1];
            lock (arrivals) arrivals.Add(clock.ElapsedMilliseconds);
        }
    }
}
