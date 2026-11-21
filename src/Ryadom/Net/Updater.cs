using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Ryadom.Shared;

namespace Ryadom.Net;

/// <summary>
/// Своё место и обновления. Каждая версия живёт в своей папке
/// %LOCALAPPDATA%\Ryadom\app\&lt;версия&gt;\Ryadom.exe, ярлыки «Рядом» в «Пуске»
/// и на рабочем столе ведут на самую новую. Запущенный файл не трогается
/// никогда: программа из одного файла дочитывает себя с диска на ходу, и
/// подмена посреди работы ломала бы её. Новая версия скачивается тихо
/// (сверка SHA-256) и начинает работать со следующего запуска.
/// </summary>
public static class Updater
{
    public static readonly string AppFolder = Path.Combine(Log.Folder, "app");

    public static Version Current => typeof(Updater).Assembly.GetName().Version ?? new Version(1, 0, 0);

    public static string VersionExe(Version v) => Path.Combine(AppFolder, v.ToString(3), "Ryadom.exe");

    /// <summary>Самая новая установленная версия (папка с готовым Ryadom.exe).</summary>
    public static (Version Version, string Exe)? Newest()
    {
        if (!Directory.Exists(AppFolder)) return null;
        (Version, string)? best = null;
        foreach (var dir in Directory.GetDirectories(AppFolder))
        {
            if (!Version.TryParse(Path.GetFileName(dir), out var v)) continue;
            var exe = Path.Combine(dir, "Ryadom.exe");
            if (File.Exists(exe) && (best is null || v > best.Value.Item1)) best = (v, exe);
        }
        return best;
    }

    public static bool IsDevBuild(string exe) => File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "Ryadom.dll"));

    /// <summary>Положить себя на своё место, ярлыки — на самую новую версию, старое убрать.</summary>
    public static void EnsureInstalled()
    {
        var self = Environment.ProcessPath;
        if (self is null || !self.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || IsDevBuild(self)) return;
        var firstInstall = Newest() is null;
        try
        {
            var mine = VersionExe(Current);
            if (!File.Exists(mine))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
                File.Copy(self, mine + ".part", overwrite: true);
                File.Move(mine + ".part", mine, overwrite: true);
            }
        }
        catch (Exception error)
        {
            Log.Warn($"Установка на место: {error.Message}");
        }
        PointShortcuts(firstInstall);
        Cleanup(self);
    }

    /// <summary>Ярлыки — на самую новую версию: в «Пуске» всегда, на рабочем
    /// столе — при первой установке или если ярлык там уже есть (удалённый не возвращаем).</summary>
    public static void PointShortcuts(bool createDesktop)
    {
        var target = Newest()?.Exe;
        if (target is null) return;
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        try { Shortcut(programs, target); }
        catch (Exception error) { Log.Warn($"Ярлык в «Пуске»: {error.Message}"); }
        if (createDesktop || File.Exists(Path.Combine(desktop, "Рядом.lnk")))
        {
            try { Shortcut(desktop, target); }
            catch (Exception error) { Log.Warn($"Ярлык на рабочем столе: {error.Message}"); }
        }
        Installer.Register(target);
    }

    /// <summary>Убрать старые версии (кроме запущенной сейчас) и файлы прежнего устройства папки.</summary>
    private static void Cleanup(string self)
    {
        var newest = Newest();
        if (newest is null) return;
        foreach (var dir in Directory.GetDirectories(AppFolder))
        {
            if (!Version.TryParse(Path.GetFileName(dir), out var v) || v >= newest.Value.Version) continue;
            if (self.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { }
        }
        foreach (var name in new[] { "Ryadom.exe", "Ryadom.exe.old", "Ryadom.exe.new" })
        {
            var legacy = Path.Combine(AppFolder, name);
            if (!string.Equals(legacy, self, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(legacy); } catch (Exception) { }
            }
        }
    }

    /// <summary>Ярлык «Рядом» в папке. Через IShellLink напрямую — без WScript
    /// (его могут отключить политики и антивирусы).</summary>
    public static void Shortcut(string folder, string target)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Рядом.lnk");
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            link.SetDescription("Рядом — удалённая помощь");
            link.SetIconLocation(target, 0);
            ((IPersistFile)link).Save(path, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
        Log.Info($"Ярлык: {path}");
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int max, IntPtr data, int flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short key);
        void SetHotkey(short key);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid id);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string file, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }

    /// <summary>Проверить и тихо скачать новую версию в её папку.</summary>
    public static async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            var have = Newest();
            if (have is null) return; // запуск не из установки (разработка)
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            VersionInfo? info = null;
            string? root = null;
            foreach (var b in Api.Bases)
            {
                try
                {
                    info = await http.GetFromJsonAsync<VersionInfo>(b + "/version.json", ct);
                    root = b;
                    break;
                }
                catch (Exception) when (!ct.IsCancellationRequested) { }
            }
            if (info?.Version is null || info.Sha256 is null || root is null) return;
            if (!Version.TryParse(info.Version, out var latest) || latest <= have.Value.Version) return;

            var target = VersionExe(latest);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var part = target + ".part";
            await using (var stream = await http.GetStreamAsync(root + "/Ryadom.exe", ct))
            await using (var file = File.Create(part))
                await stream.CopyToAsync(file, ct);
            string hash;
            await using (var file = File.OpenRead(part))
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
            if (!string.Equals(hash, info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                Log.Warn("Обновление не совпало по SHA-256 — выброшено");
                return;
            }
            File.Move(part, target, overwrite: true);
            PointShortcuts(createDesktop: false);
            Log.Info($"Скачана версия {info.Version} — заработает со следующего запуска");
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Log.Warn($"Обновление: {error.Message}");
        }
    }

    private sealed class VersionInfo
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    }
}
