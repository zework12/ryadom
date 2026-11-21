using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Ryadom.Shared;

namespace Ryadom.Viewer;

/// <summary>Что умеет помощник-программа (Claude) во время сеанса.</summary>
public interface IAgentTarget
{
    (int Width, int Height) FrameSize { get; }
    byte[]? FramePng(double scale);
    void AgentMove(int x, int y);
    void AgentClick(int x, int y, string button, bool twice);
    void AgentScroll(int x, int y, int notches);
    void AgentKeys(string combo);
    void AgentText(string text);
    /// <summary>Команда agent-desktop на том компьютере; ответ — его JSON.</summary>
    Task<string> AgentDesktopAsync(string[] args);
    /// <summary>Фоновые задачи на том компьютере: {"op":"run","script":"…"} | list | out | kill.</summary>
    Task<string> AgentJobAsync(string requestJson);
    /// <summary>Мышь и клавиатура самого помощника-человека: true — идут на тот компьютер, false — нет.</summary>
    bool UserInput { get; set; }
}

/// <summary>
/// Вход для Claude на этом компьютере: картинка и ввод на тот компьютер без
/// окна «Рядом» — его можно свернуть и спокойно писать в другом месте.
/// Только 127.0.0.1 и только с ключом из %LOCALAPPDATA%\Ryadom\agent.json
/// (файл видит лишь этот пользователь Windows). Живёт, пока идёт сеанс.
/// Запросы: GET /status, GET /frame?scale=0.5 (PNG), POST /click?x=&amp;y=&amp;button=left|right&amp;double=1,
/// POST /move?x=&amp;y=, POST /scroll?x=&amp;y=&amp;n=-3, POST /key?combo=ctrl+v, POST /text (тело — текст),
/// POST /lock?on=1 (выключить мышь человека). Координаты — пиксели того экрана.
/// </summary>
public sealed class AgentServer : IDisposable
{
    public static readonly string KeyFile = Path.Combine(Log.Folder, "agent.json");

    private readonly IAgentTarget _target;
    private readonly TcpListener _listener;
    private readonly string _token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
    private readonly CancellationTokenSource _stop = new();

    public AgentServer(IAgentTarget target)
    {
        _target = target;
        TcpListener? listener = null;
        for (var port = 47930; port < 47940 && listener is null; port++)
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, port);
                l.Start();
                listener = l;
            }
            catch (SocketException) { }
        }
        _listener = listener ?? throw new InvalidOperationException("Нет свободного порта для Claude");
        var portUsed = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Directory.CreateDirectory(Log.Folder);
        File.WriteAllText(KeyFile, JsonSerializer.Serialize(new { port = portUsed, token = _token }));
        Log.Info($"Вход для Claude: 127.0.0.1:{portUsed}");
        _ = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        client.ReceiveTimeout = 10000;
        client.SendTimeout = 120000;
        var stream = client.GetStream();
        try
        {
            // Заголовки до пустой строки.
            var head = new StringBuilder();
            var buffer = new byte[1];
            while (head.Length < 8192)
            {
                if (await stream.ReadAsync(buffer) == 0) return;
                head.Append((char)buffer[0]);
                if (head.Length >= 4 && head.ToString(head.Length - 4, 4) == "\r\n\r\n") break;
            }
            var lines = head.ToString().Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length < 2) return;
            var method = parts[0];
            var url = new Uri("http://x" + parts[1]);
            var headers = lines.Skip(1).Where(l => l.Contains(':'))
                .ToDictionary(l => l[..l.IndexOf(':')].Trim().ToLowerInvariant(), l => l[(l.IndexOf(':') + 1)..].Trim());
            var body = "";
            if (headers.TryGetValue("content-length", out var lengthText) && int.TryParse(lengthText, out var length) && length is > 0 and < 1_000_000)
            {
                var data = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var n = await stream.ReadAsync(data.AsMemory(read));
                    if (n == 0) break;
                    read += n;
                }
                body = Encoding.UTF8.GetString(data, 0, read);
            }
            if (!headers.TryGetValue("x-token", out var token) || token != _token)
            {
                await Reply(stream, 403, "text/plain", "нет ключа"u8.ToArray());
                return;
            }
            var q = System.Web.HttpUtility.ParseQueryString(url.Query);
            int I(string k, int d = 0) => int.TryParse(q[k], out var v) ? v : d;

            switch ((method, url.AbsolutePath))
            {
                case ("GET", "/status"):
                {
                    var (w, h) = _target.FrameSize;
                    await Json(stream, new { width = w, height = h, userInput = _target.UserInput });
                    return;
                }
                case ("GET", "/frame"):
                {
                    var scale = double.TryParse(q["scale"], System.Globalization.CultureInfo.InvariantCulture, out var s) ? Math.Clamp(s, 0.1, 1) : 1;
                    var png = _target.FramePng(scale);
                    if (png is null) await Reply(stream, 503, "text/plain", "картинки ещё нет"u8.ToArray());
                    else await Reply(stream, 200, "image/png", png);
                    return;
                }
                case ("POST", "/click"):
                    _target.AgentClick(I("x"), I("y"), q["button"] ?? "left", q["double"] == "1");
                    break;
                case ("POST", "/move"):
                    _target.AgentMove(I("x"), I("y"));
                    break;
                case ("POST", "/scroll"):
                    _target.AgentScroll(I("x"), I("y"), I("n", -3));
                    break;
                case ("POST", "/key"):
                    _target.AgentKeys(q["combo"] ?? "");
                    break;
                case ("POST", "/text"):
                    _target.AgentText(body);
                    break;
                case ("POST", "/ad"):
                {
                    var args = JsonSerializer.Deserialize<string[]>(body) ?? [];
                    var result = await _target.AgentDesktopAsync(args);
                    await Reply(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(result));
                    return;
                }
                case ("POST", "/job"):
                {
                    var result = await _target.AgentJobAsync(body);
                    await Reply(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(result));
                    return;
                }
                case ("POST", "/lock"):
                    _target.UserInput = q["on"] != "1";
                    break;
                default:
                    await Reply(stream, 404, "text/plain", "нет такого"u8.ToArray());
                    return;
            }
            await Json(stream, new { ok = true });
        }
        catch (Exception error)
        {
            Log.Warn($"Вход для Claude: {error.Message}");
            try { await Reply(stream, 500, "text/plain", Encoding.UTF8.GetBytes(error.Message)); } catch (Exception) { }
        }
    }

    private static Task Json(NetworkStream stream, object value) =>
        Reply(stream, 200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value));

    private static async Task Reply(NetworkStream stream, int status, string type, byte[] body)
    {
        var head = $"HTTP/1.1 {status} X\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(body);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try { File.Delete(KeyFile); } catch (Exception) { }
    }
}
