using System.Text;

namespace Ryadom.Shared;

/// <summary>Журнал в %LOCALAPPDATA%\Ryadom\log.txt — чтобы разбирать неполадки
/// у человека на том конце. Больше 2 МБ — начинаем заново.</summary>
public static class Log
{
    private static readonly Lock Gate = new();
    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ryadom");
    private static readonly string FilePath = Path.Combine(Folder, "log.txt");

    static Log()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 2_000_000) File.Delete(FilePath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Последние строки журнала (для «почему нет картинки» у помощника).</summary>
    public static string Tail(int lines)
    {
        lock (Gate)
        {
            try
            {
                var all = File.ReadAllLines(FilePath, Encoding.UTF8);
                return string.Join('\n', all.Where(l => !l.StartsWith("   at ")).TakeLast(lines));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return "журнал не прочитать: " + error.Message;
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? error = null) =>
        Write("ERR ", error is null ? message : $"{message}: {error}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}";
        System.Diagnostics.Debug.Write(line);
        lock (Gate)
        {
            try { File.AppendAllText(FilePath, line, Encoding.UTF8); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
