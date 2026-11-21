using System.Diagnostics;
using System.Security.Cryptography;
using Ryadom.Shared;

namespace Ryadom.Host;

/// <summary>
/// agent-desktop (Apache-2.0, vercel-labs) у человека: помощник-программа
/// читает окна по дереву доступности и нажимает кнопки без картинки.
/// Файл зашит в Ryadom.exe и достаётся при первом запросе в
/// %LOCALAPPDATA%\Ryadom\agent-desktop\. Запускается только с аргументами
/// (без командной строки) и только по просьбе разрешённого помощника.
/// </summary>
public static class DesktopAgent
{
    private const string Sha256 = "a7ef5e539fb97287a35eb9ed49d3803b4d6bd705bc4106eb28617229f9be679e";
    private static readonly string Folder = Path.Combine(Log.Folder, "agent-desktop");
    private static readonly string Exe = Path.Combine(Folder, "agent-desktop.exe");
    private static readonly Lock Gate = new();

    private static string Ensure()
    {
        lock (Gate)
        {
            if (File.Exists(Exe) && Hash(Exe) == Sha256) return Exe;
            Directory.CreateDirectory(Folder);
            using var source = typeof(DesktopAgent).Assembly.GetManifestResourceStream("agent-desktop.exe")
                               ?? throw new InvalidOperationException("agent-desktop не встроен в эту версию");
            var part = Exe + ".part";
            using (var file = File.Create(part)) source.CopyTo(file);
            if (Hash(part) != Sha256) { File.Delete(part); throw new InvalidOperationException("agent-desktop повреждён"); }
            File.Move(part, Exe, overwrite: true);
            Log.Info("agent-desktop распакован");
            return Exe;
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Выполнить одну команду agent-desktop. Ответ — его JSON (или наш JSON с ошибкой).</summary>
    public static async Task<string> RunAsync(IReadOnlyList<string> args, TimeSpan timeout)
    {
        try
        {
            var exe = Ensure();
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                WorkingDirectory = Folder,
            };
            foreach (var a in args) info.ArgumentList.Add(a);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("agent-desktop не запустился");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeout);
            try { await process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                return Fail("agent-desktop не ответил вовремя");
            }
            var text = (await output).Trim();
            if (text.Length > 0) return text;
            return Fail($"agent-desktop без ответа (код {process.ExitCode}): {(await error).Trim()}");
        }
        catch (Exception error)
        {
            Log.Warn($"agent-desktop: {error.Message}");
            return Fail(error.Message);
        }
    }

    private static string Fail(string message) =>
        System.Text.Json.JsonSerializer.Serialize(new { ok = false, error = new { code = "RYADOM", message } });
}
