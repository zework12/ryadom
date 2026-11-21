using System.Diagnostics;
using Microsoft.Win32;
using Ryadom.Shared;

namespace Ryadom.Net;

/// <summary>
/// Установка, переустановка и удаление — без прав администратора, всё в
/// профиле человека: %LOCALAPPDATA%\Ryadom, ярлыки «Рядом» в «Пуске» и на
/// рабочем столе, строка в «Установленных приложениях» Windows.
/// </summary>
public static class Installer
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Ryadom";

    public static string ProgramsLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Рядом.lnk");
    public static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Рядом.lnk");

    /// <summary>Запущены из установленной папки (а не из «Загрузок»).</summary>
    public static bool RunningInstalled
    {
        get
        {
            var self = Environment.ProcessPath;
            return self is not null && self.StartsWith(Updater.AppFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Установить (или поставить начисто) версию этого файла и открыть её.</summary>
    public static void Install(bool clean)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден файл программы");
        if (clean)
        {
            StopOthers();
            try { if (Directory.Exists(Updater.AppFolder)) Directory.Delete(Updater.AppFolder, recursive: true); }
            catch (Exception error) { Log.Warn($"Старая установка не удалилась целиком: {error.Message}"); }
        }
        var target = Updater.VersionExe(Updater.Current);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(self, target + ".part", overwrite: true);
            File.Move(target + ".part", target, overwrite: true);
        }
        Updater.PointShortcuts(createDesktop: true);
        Log.Info($"Установлено: {target}");
    }

    /// <summary>Открыть установленную программу.</summary>
    public static void Launch()
    {
        var exe = Updater.Newest()?.Exe ?? throw new InvalidOperationException("Программа не установлена");
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
    }

    /// <summary>Удалить всё. Если сами запущены из папки программы — папку
    /// дочищает отдельная команда через пару секунд после выхода.</summary>
    public static void Uninstall()
    {
        StopOthers();
        foreach (var link in new[] { ProgramsLink, DesktopLink })
        {
            try { if (File.Exists(link)) File.Delete(link); }
            catch (Exception error) { Log.Warn($"Ярлык не удалился: {error.Message}"); }
        }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); }
        catch (Exception error) { Log.Warn($"Запись в «Приложениях»: {error.Message}"); }

        try { Directory.Delete(Log.Folder, recursive: true); }
        catch (Exception) { }
        // Свой запущенный файл удалить нельзя — папку дочистим после выхода (FinishOnExit).
        _deleteOnExit = Directory.Exists(Log.Folder);
    }

    private static bool _deleteOnExit;

    /// <summary>Звать при выходе из программы: если было удаление, а папка
    /// ещё держится (свой файл), стереть её, как только процесс закроется.</summary>
    public static void FinishOnExit()
    {
        if (!_deleteOnExit) return;
        var folder = Log.Folder;
        // До минуты попыток раз в секунду — пока Windows не отпустит файл.
        var script = $"/c for /L %i in (1,1,60) do @(if exist \"{folder}\" (rmdir /s /q \"{folder}\" 2>nul & ping -n 2 127.0.0.1 >nul))";
        Process.Start(new ProcessStartInfo("cmd.exe", script)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    /// <summary>Строка «Рядом» в «Установленных приложениях» — с кнопкой «Удалить».</summary>
    public static void Register(string exe)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
            key.SetValue("DisplayName", "Рядом");
            key.SetValue("DisplayVersion", Updater.Newest()?.Version.ToString(3) ?? Updater.Current.ToString(3));
            key.SetValue("Publisher", "Рядом");
            key.SetValue("DisplayIcon", exe + ",0");
            key.SetValue("InstallLocation", Updater.AppFolder);
            key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
        }
        catch (Exception error)
        {
            Log.Warn($"Запись в «Приложениях»: {error.Message}");
        }
    }

    /// <summary>Закрыть другие запущенные копии «Рядом» из папки программы.</summary>
    private static void StopOthers()
    {
        foreach (var process in Process.GetProcessesByName("Ryadom"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is null || !path.StartsWith(Updater.AppFolder, StringComparison.OrdinalIgnoreCase)) continue;
                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch (Exception error)
                {
                    Log.Warn($"Не закрылась копия {process.Id}: {error.Message}");
                }
            }
        }
    }
}
