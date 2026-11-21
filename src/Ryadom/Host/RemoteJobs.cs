using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ryadom.Shared;

namespace Ryadom.Host;

/// <summary>
/// Фоновые задачи у человека по просьбе помощника: команда PowerShell
/// запускается скрыто, её вывод пишется в файл, задача живёт и после
/// отключения помощника (и даже если «Рядом» закрыть). Папка задач —
/// %LOCALAPPDATA%\Ryadom\jobs: &lt;id&gt;.json (что и когда), .log (вывод),
/// .exit (код завершения, появляется в конце).
/// </summary>
public static class RemoteJobs
{
    private static readonly string Folder = Path.Combine(Log.Folder, "jobs");
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    private sealed class Meta
    {
        public string Id { get; set; } = "";
        public string Script { get; set; } = "";
        public int Pid { get; set; }
        public DateTime Started { get; set; }
        public DateTime ProcessStart { get; set; }
    }

    /// <summary>Разобрать запрос помощника и ответить JSON.</summary>
    public static string Handle(string requestJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            var r = doc.RootElement;
            var op = r.TryGetProperty("op", out var o) ? o.GetString() : null;
            string? S(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int I(string name, int d) => r.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : d;
            return op switch
            {
                "run" => Run(S("script") ?? throw new ArgumentException("нет script"), S("dir")),
                "list" => List(),
                "out" => Output(S("id") ?? "", I("tail", 20_000)),
                "kill" => Kill(S("id") ?? ""),
                _ => Fail("неизвестная операция: run | list | out | kill"),
            };
        }
        catch (Exception error)
        {
            return Fail(error.Message);
        }
    }

    private static string Run(string script, string? dir)
    {
        Directory.CreateDirectory(Folder);
        var id = DateTime.Now.ToString("MMdd-HHmmss") + "-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(2));
        var log = Path.Combine(Folder, id + ".log");
        var exit = Path.Combine(Folder, id + ".exit");
        // Сама команда — отдельным файлом: тогда `exit N` в ней завершает только её, а код попадает в .exit.
        var body = Path.Combine(Folder, id + ".ps1");
        File.WriteAllText(body, script, new UTF8Encoding(true));
        // Обёртка: весь вывод (и ошибки) — в файл по мере появления, код — в .exit.
        var wrapper = new StringBuilder()
            .AppendLine("$ErrorActionPreference = 'Continue'")
            .AppendLine("[Console]::OutputEncoding = [Text.Encoding]::UTF8")
            .AppendLine("$OutputEncoding = [Text.Encoding]::UTF8")
            .AppendLine("$code = 0")
            .AppendLine("$u8 = New-Object Text.UTF8Encoding $false")
            .AppendLine("try {")
            .AppendLine($"  & '{body.Replace("'", "''")}' *>&1 | ForEach-Object {{ [IO.File]::AppendAllText('{log.Replace("'", "''")}', ($_ | Out-String -Width 4096), $u8) }}")
            .AppendLine("  if ($LASTEXITCODE) { $code = $LASTEXITCODE } elseif (-not $?) { $code = 1 }")
            .AppendLine("} catch {")
            .AppendLine($"  [IO.File]::AppendAllText('{log.Replace("'", "''")}', \"ОШИБКА: $_`n\", $u8); $code = 1")
            .AppendLine("}")
            .AppendLine($"[IO.File]::WriteAllText('{exit.Replace("'", "''")}', \"$code\")")
            .ToString();
        File.WriteAllText(log, "", Utf8);
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Directory.Exists(dir) ? dir! : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapper)) })
            info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("PowerShell не запустился");
        var meta = new Meta { Id = id, Script = script, Pid = process.Id, Started = DateTime.Now, ProcessStart = process.StartTime };
        File.WriteAllText(Path.Combine(Folder, id + ".json"), JsonSerializer.Serialize(meta, Json));
        Log.Info($"Фоновая задача {id} запущена помощником");
        return JsonSerializer.Serialize(new { ok = true, id, pid = process.Id }, Json);
    }

    private static object Describe(Meta m)
    {
        var exitFile = Path.Combine(Folder, m.Id + ".exit");
        int? exit = File.Exists(exitFile) && int.TryParse(File.ReadAllText(exitFile).Trim(), out var c) ? c : null;
        var running = exit is null && Alive(m);
        var log = Path.Combine(Folder, m.Id + ".log");
        return new
        {
            id = m.Id,
            state = running ? "running" : exit is null ? "stopped" : "done",
            exit,
            started = m.Started.ToString("yyyy-MM-dd HH:mm:ss"),
            outputBytes = File.Exists(log) ? new FileInfo(log).Length : 0,
            script = m.Script.Length > 120 ? m.Script[..120] + "…" : m.Script,
        };
    }

    private static bool Alive(Meta m)
    {
        try
        {
            using var p = Process.GetProcessById(m.Pid);
            return !p.HasExited && Math.Abs((p.StartTime - m.ProcessStart).TotalSeconds) < 2;
        }
        catch (Exception) { return false; }
    }

    private static IEnumerable<Meta> All() =>
        Directory.Exists(Folder)
            ? Directory.GetFiles(Folder, "*.json").Select(f =>
              {
                  try { return JsonSerializer.Deserialize<Meta>(File.ReadAllText(f), Json); }
                  catch (Exception) { return null; }
              }).Where(m => m is not null).Select(m => m!).OrderBy(m => m.Started)
            : [];

    private static string List() =>
        JsonSerializer.Serialize(new { ok = true, jobs = All().TakeLast(30).Select(Describe).ToList() }, Json);

    private static Meta Find(string id) =>
        All().FirstOrDefault(m => m.Id == id) ?? throw new ArgumentException($"нет задачи {id}");

    private static string Output(string id, int tail)
    {
        var meta = Find(id);
        var log = Path.Combine(Folder, id + ".log");
        var text = "";
        if (File.Exists(log))
        {
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var take = (int)Math.Min(length, Math.Clamp(tail, 100, 2_000_000));
            stream.Seek(length - take, SeekOrigin.Begin);
            var buffer = new byte[take];
            stream.ReadExactly(buffer);
            text = Encoding.UTF8.GetString(buffer);
        }
        return JsonSerializer.Serialize(new { ok = true, job = Describe(meta), output = text }, Json);
    }

    private static string Kill(string id)
    {
        var meta = Find(id);
        if (Alive(meta))
        {
            using var p = Process.GetProcessById(meta.Pid);
            p.Kill(entireProcessTree: true);
            Log.Info($"Фоновая задача {id} остановлена помощником");
        }
        return JsonSerializer.Serialize(new { ok = true, job = Describe(meta) }, Json);
    }

    private static string Fail(string message) => JsonSerializer.Serialize(new { ok = false, error = message }, Json);
}
