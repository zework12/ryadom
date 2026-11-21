using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Ryadom.ViewModels;
using Ryadom.Views;

namespace Ryadom.Preview;

/// <summary>Рисует все экраны во всех состояниях в PNG, не показывая ни одного окна.
/// Всё, что в приложении живёт во всплывающих окнах (панель полноэкранного режима, списки, сообщения),
/// рисуется отдельно и накладывается на картинку в том месте, где оно появится.</summary>
public static class PreviewRenderer
{
    private const string Viewer = "Миша";
    private const string Peer = "НОУТБУК-ДОМ";

    public static void Render(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir, "*.png")) File.Delete(old);

        // ── Первый экран ──
        Save(dir, "01-start", Shell(Start(), 960, 640));
        Save(dir, "01-start-1920", Shell(Start(), 1920, 1032, maximized: true));
        Save(dir, "01-start-min-150", Shell(Start(), 800, 560, 1.5));
        Save(dir, "01-start-uninstall-question", Shell(Start(true), 960, 640));
        Save(dir, "01-start-uninstall-question-min-150", Shell(Start(true), 800, 560, 1.5));

        // ── «Мне нужна помощь» ──
        Save(dir, "02-host-preparing", Shell(Host(HostState.Preparing), 960, 640));
        Save(dir, "03-host-waiting", Shell(Host(HostState.Waiting), 960, 640));
        Save(dir, "03-host-waiting-copied-admin", Shell(Host(HostState.Waiting, h => { h.CodeCopied = true; h.IsElevated = true; }), 960, 640));
        Save(dir, "03-host-waiting-1920", Shell(Host(HostState.Waiting), 1920, 1032, maximized: true));
        Save(dir, "03-host-waiting-min-150", Shell(Host(HostState.Waiting), 800, 560, 1.5));
        Save(dir, "04-host-asking", Shell(Host(HostState.Asking), 960, 640));
        Save(dir, "04-host-asking-long-name", Shell(Host(HostState.Asking, h => h.ViewerName = "Александр Константинович"), 960, 640));
        Save(dir, "04-host-asking-min-150", Shell(Host(HostState.Asking), 800, 560, 1.5));
        Save(dir, "05-host-connected", Shell(Host(HostState.Connected), 960, 640));
        Save(dir, "05-host-connected-min-150", Shell(Host(HostState.Connected), 800, 560, 1.5));
        Save(dir, "06-host-reconnecting", Shell(Host(HostState.Reconnecting), 960, 640));
        Save(dir, "07-host-error", Shell(Host(HostState.Error), 960, 640));

        // ── «Я помогаю» ──
        Save(dir, "10-join-empty", Shell(Join(JoinState.Idle, "", ""), 960, 640));
        Save(dir, "11-join-filled", Shell(Join(JoinState.Idle), 960, 640));
        Save(dir, "11-join-filled-1920", Shell(Join(JoinState.Idle), 1920, 1032, maximized: true));
        Save(dir, "11-join-filled-min-150", Shell(Join(JoinState.Idle), 800, 560, 1.5));
        Save(dir, "12-join-connecting", Shell(Join(JoinState.Connecting), 960, 640));
        Save(dir, "13-join-waiting-approval", Shell(Join(JoinState.WaitingApproval), 960, 640));
        Save(dir, "14-join-denied", Shell(Join(JoinState.Denied), 960, 640));
        Save(dir, "15-join-error", Shell(Join(JoinState.Error), 960, 640));
        Save(dir, "15-join-error-min-150", Shell(Join(JoinState.Error), 800, 560, 1.5));

        // ── Сеанс ──
        Save(dir, "20-session-1920", Shell(Session(), 1920, 1032, maximized: true));
        Save(dir, "20-session-1280-150", Shell(Session(), 1280, 680, 1.5, maximized: true));
        Save(dir, "21-session-waiting-picture", Shell(Session(s => { s.HasVideo = false; s.Video = null; }), 1366, 728, maximized: true));
        Save(dir, "22-session-1100", Shell(Session(), 1100, 700));
        Save(dir, "22-session-960", Shell(Session(), 960, 640));
        Save(dir, "22-session-800-min", Shell(Session(), 800, 560));
        Save(dir, "23-session-one-monitor-view-only", Shell(Session(s =>
        {
            s.Monitors = [new MonitorOption(0, "Экран 1", 1920, 1080)];
            s.ControlEnabled = false;
            s.Quality = LinkQuality.Excellent;
        }), 1366, 728, maximized: true));
        Save(dir, "24-session-reconnecting", Shell(Session(s => { s.IsReconnecting = true; s.Quality = LinkQuality.Lost; s.ActualFps = 0; }), 1366, 728, maximized: true));
        Save(dir, "25-session-poor-link-notice", SessionScene(Session(s => { s.Quality = LinkQuality.Poor; s.ActualFps = 17; s.PingMs = 240; }),
            1366, 728, notice: "Текст из буфера отправлен"));
        Save(dir, "26-session-fullscreen-bar", SessionScene(Session(s => s.IsFullscreen = true), 1920, 1080, fullscreen: true));
        Save(dir, "26-session-fullscreen-hidden", SessionScene(Session(s => s.IsFullscreen = true), 1920, 1080, fullscreen: true, bar: false));
        Save(dir, "26-session-fullscreen-bar-1280-150", SessionScene(Session(s => s.IsFullscreen = true), 1280, 720, fullscreen: true, scale: 1.5));
        Save(dir, "27-session-list-fps", SessionScene(Session(), 1366, 728, open: "FpsCombo"));
        Save(dir, "27-session-list-quality", SessionScene(Session(), 1366, 728, open: "QualityCombo"));
        Save(dir, "27-session-list-monitor", SessionScene(Session(), 1366, 728, open: "MonitorCombo"));
        Save(dir, "28-session-keys-menu", SessionScene(Session(), 1366, 728, open: "KeysMenu"));

        // ── Установщик ──
        Save(dir, "50-setup-install", Shell(Setup(SetupState.Choose), 960, 640));
        Save(dir, "50-setup-install-min-150", Shell(Setup(SetupState.Choose), 800, 560, 1.5));
        Save(dir, "51-setup-installed", Shell(Setup(SetupState.Choose, true), 960, 640));
        Save(dir, "51-setup-installed-1920", Shell(Setup(SetupState.Choose, true), 1920, 1032, maximized: true));
        Save(dir, "51-setup-installed-min-150", Shell(Setup(SetupState.Choose, true), 800, 560, 1.5));
        Save(dir, "52-setup-confirm-uninstall", Shell(Setup(SetupState.ConfirmUninstall, true), 960, 640));
        Save(dir, "53-setup-working-install", Shell(Setup(SetupState.Working), 960, 640));
        Save(dir, "53-setup-working-uninstall", Shell(Setup(SetupState.Working, true, s => s.StatusText = "Удаляем…"), 960, 640));
        Save(dir, "54-setup-uninstalled", Shell(Setup(SetupState.Uninstalled), 960, 640));
        Save(dir, "55-setup-error", Shell(Setup(SetupState.Error), 960, 640));

        // ── У того, кому помогают: рамка и плашка поверх рабочего стола ──
        Save(dir, "30-host-desktop-frame-and-pill", HostDesktop(1920, 1080, 1.0, false));
        Save(dir, "30-host-desktop-light-150", HostDesktop(1280, 720, 1.5, true));
        Save(dir, "31-pill", PillOnly(2.0));

        // ── Значок ──
        WriteIcon(System.IO.Path.Combine(dir, "icon.ico"));
        Save(dir, "40-icon-sizes", IconSheet());
    }

    // ───────────────────────── Модели с выдуманными данными ─────────────────────────

    private static void Live(params RelayCommand[] commands)
    {
        foreach (var c in commands) c.Bind(_ => { });
    }

    private static StartViewModel Start(bool confirmingUninstall = false)
    {
        var vm = new StartViewModel { ConfirmingUninstall = confirmingUninstall };
        Live(vm.NeedHelpCommand, vm.HelpOtherCommand, vm.AskUninstallCommand, vm.ConfirmUninstallCommand, vm.CancelUninstallCommand);
        return vm;
    }

    private static SetupViewModel Setup(SetupState state, bool installed = false, Action<SetupViewModel>? tune = null)
    {
        var vm = new SetupViewModel
        {
            State = state,
            IsInstalled = installed,
            InstalledVersion = installed ? "1.1.3" : null,
            ThisVersion = "1.2.0",
            StatusText = "Устанавливаем…",
            ErrorText = "Не удалось записать файлы программы. Закройте «Рядом», если оно открыто, и попробуйте ещё раз.",
        };
        Live(vm.InstallCommand, vm.OpenCommand, vm.ReinstallCommand, vm.UninstallCommand, vm.ConfirmUninstallCommand, vm.CancelCommand, vm.CloseCommand);
        tune?.Invoke(vm);
        return vm;
    }

    private static HostViewModel Host(HostState state, Action<HostViewModel>? tune = null)
    {
        var vm = new HostViewModel
        {
            State = state,
            Code = "482 915 367",
            ViewerName = Viewer,
            DurationText = "12:34",
            ErrorText = "Нет связи с интернетом. Проверьте подключение и попробуйте ещё раз.",
        };
        Live(vm.CopyCodeCommand, vm.AllowCommand, vm.DenyCommand, vm.DisconnectCommand, vm.RetryCommand, vm.BackCommand);
        tune?.Invoke(vm);
        return vm;
    }

    private static JoinViewModel Join(JoinState state, string code = "482 915 367", string name = Viewer)
    {
        var vm = new JoinViewModel
        {
            Code = code,
            Name = name,
            State = state,
            ErrorText = "Код не подошёл. Проверьте цифры — возможно, код уже устарел.",
        };
        Live(vm.CancelCommand, vm.BackCommand);
        if (code.Length > 0) Live(vm.ConnectCommand); // без кода кнопка неактивна — так видно и это состояние
        return vm;
    }

    private static SessionViewModel Session(Action<SessionViewModel>? tune = null)
    {
        var vm = new SessionViewModel
        {
            PeerName = Peer,
            HasVideo = true,
            Video = FakeScreen(false, 1920, 1080),
            ActualFps = 58.6,
            PingMs = 24,
            ResolutionText = "1920×1080",
            Quality = LinkQuality.Good,
        };
        vm.Monitors = [new MonitorOption(0, "Экран 1", 1920, 1080), new MonitorOption(1, "Экран 2", 2560, 1440)];
        vm.SelectedMonitor = vm.Monitors[0];
        Live(vm.ToggleFullscreenCommand, vm.DisconnectCommand, vm.SendKeysCommand);
        tune?.Invoke(vm);
        return vm;
    }

    // ───────────────────────── Сцены ─────────────────────────

    private sealed record Shot(FrameworkElement Root, double Width, double Height, double Scale, List<(BitmapSource Image, Rect Place)> Overlays);

    /// <summary>Главное окно без самого окна: то же содержимое, что показывает MainWindow.</summary>
    private static Shot Shell(object page, double width, double height, double scale = 1.0, bool maximized = false, bool fullscreen = false)
    {
        var shell = new MainShell { DataContext = new MainViewModel(page) { Version = "1.0.0" } };
        shell.SetPage(page);
        if (fullscreen) shell.SetFullscreen(true);
        else shell.SetMaximized(maximized, default);
        Layout(shell, width, height, scale);
        if (Find<SessionView>(shell) is { } session)
        {
            session.PreviewLayout();
            Layout(shell, width, height, scale);
        }
        return new Shot(shell, width, height, scale, []);
    }

    /// <summary>Сеанс вместе с тем, что всплывает: панель полноэкранного режима, сообщение, открытый список.</summary>
    private static Shot SessionScene(SessionViewModel vm, double width, double height, double scale = 1.0,
        bool fullscreen = false, bool bar = true, string? notice = null, string? open = null)
    {
        var shot = Shell(vm, width, height, scale, maximized: !fullscreen, fullscreen: fullscreen);
        var view = Find<SessionView>(shot.Root)!;
        var toolbar = fullscreen ? view.PreviewBarToolbar : view.PreviewToolbar;
        var toolbarOrigin = new Point(0, 0);

        if (fullscreen && bar)
        {
            var (image, size) = Snap(view.PreviewBar, scale);
            var place = new Rect(Math.Round((width - size.Width) / 2), 0, size.Width, size.Height);
            shot.Overlays.Add((image, place));
            toolbarOrigin = toolbar.TranslatePoint(new Point(0, 0), view.PreviewBar);
            toolbarOrigin.Offset(place.X, place.Y);
        }
        else if (!fullscreen)
        {
            toolbarOrigin = toolbar.TranslatePoint(new Point(0, 0), shot.Root);
        }

        if (open is not null && toolbar.FindName(open) is FrameworkElement target)
        {
            FrameworkElement? anchor = target, drop = null;
            if (target is ComboBox combo)
            {
                combo.ApplyTemplate();
                drop = (combo.Template.FindName("PART_Popup", combo) as Popup)?.Child as FrameworkElement;
            }
            else
            {
                anchor = toolbar.FindName("KeysToggle") as FrameworkElement;
                drop = target;
            }
            if (anchor is not null && drop is not null)
            {
                var (image, size) = Snap(drop, scale);
                var at = anchor.TranslatePoint(new Point(0, anchor.ActualHeight), toolbar);
                shot.Overlays.Add((image, new Rect(toolbarOrigin.X + at.X - 12, toolbarOrigin.Y + at.Y, size.Width, size.Height)));
            }
        }

        if (notice is not null)
        {
            view.PreviewSetNotice(notice);
            var (image, size) = Snap(view.PreviewNotice, scale);
            var area = view.PreviewVideoArea;
            var origin = area.TranslatePoint(new Point(0, 0), shot.Root);
            shot.Overlays.Add((image, new Rect(
                Math.Round(origin.X + (area.ActualWidth - size.Width) / 2),
                Math.Round(origin.Y + area.ActualHeight - size.Height - 8), size.Width, size.Height)));
        }

        return shot;
    }

    /// <summary>Рабочий стол того, кому помогают: рамка по краю и плашка сверху.</summary>
    private static Shot HostDesktop(double width, double height, double scale, bool light)
    {
        var root = new Grid();
        root.Children.Add(FakeScreen(light, width, height));
        root.Children.Add(ScreenFrameWindow.CreateFrame());
        var pill = new ControlPill { DataContext = Host(HostState.Connected), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        root.Children.Add(pill);
        Layout(root, width, height, scale);
        return new Shot(root, width, height, scale, []);
    }

    private static Shot PillOnly(double scale)
    {
        var root = new Grid { Background = new SolidColorBrush(Color.FromRgb(0xC9, 0xD3, 0xDF)) };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new ControlPill { DataContext = Host(HostState.Connected), HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new ControlPill { DataContext = Host(HostState.Connected, h => { h.ViewerName = "Александр Константинович"; h.DurationText = "1:02:45"; }), HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new ControlPill { DataContext = Host(HostState.Connected, h => h.ViewerName = null), HorizontalAlignment = HorizontalAlignment.Center });
        root.Children.Add(stack);
        Layout(root, 720, 280, scale);
        return new Shot(root, 720, 280, scale, []);
    }

    private static Shot IconSheet()
    {
        var logo = (ImageSource)Application.Current.FindResource("Logo");
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var (column, back) in new[] { (0, Color.FromRgb(0xF4, 0xF5, 0xF7)), (1, Color.FromRgb(0x15, 0x17, 0x1B)) })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            foreach (var size in new[] { 16, 24, 32, 48, 128 })
                row.Children.Add(new Image { Source = logo, Width = size, Height = size, Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Bottom });
            var cell = new Border { Background = new SolidColorBrush(back), Child = row };
            Grid.SetColumn(cell, column);
            root.Children.Add(cell);
        }
        Layout(root, 800, 200, 1.0);
        return new Shot(root, 800, 200, 1.0, []);
    }

    // ───────────────────────── Нарисованный «чужой экран» ─────────────────────────

    /// <summary>Простая картинка рабочего стола вместо настоящего видео.</summary>
    private static FrameworkElement FakeScreen(bool light, double width, double height)
    {
        var canvas = new Grid { Width = width, Height = height };
        canvas.Children.Add(new Rectangle
        {
            Fill = light
                ? new LinearGradientBrush(Color.FromRgb(0xDD, 0xE8, 0xF3), Color.FromRgb(0xF3, 0xEE, 0xE6), 35)
                : new LinearGradientBrush(Color.FromRgb(0x1F, 0x3B, 0x5C), Color.FromRgb(0x3A, 0x2A, 0x55), 35),
        });

        var window = new Border
        {
            Width = width * 0.56, Height = height * 0.62, CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(width * 0.12, height * 0.14, 0, 0),
            Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), BorderThickness = new Thickness(1),
        };
        var body = new StackPanel();
        body.Children.Add(new Border { Height = 36, Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF2, 0xF5)), CornerRadius = new CornerRadius(8, 8, 0, 0) });
        var widths = new[] { 0.42, 0.78, 0.7, 0.74, 0.36, 0.66, 0.72, 0.5 };
        for (var i = 0; i < widths.Length; i++)
        {
            body.Children.Add(new Border
            {
                Height = i == 0 ? 22 : 12, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left,
                Width = width * 0.56 * widths[i] * 0.9, Margin = new Thickness(32, i == 0 ? 32 : 20, 0, 0),
                Background = new SolidColorBrush(i == 0 ? Color.FromRgb(0xB8, 0xC0, 0xCC) : Color.FromRgb(0xDD, 0xE1, 0xE7)),
            });
        }
        window.Child = body;
        canvas.Children.Add(window);

        var taskbar = new Border
        {
            Height = 48, VerticalAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(light ? Color.FromArgb(0xE6, 0xF3, 0xF4, 0xF6) : Color.FromArgb(0xE6, 0x1C, 0x20, 0x28)),
        };
        var icons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var color in new[] { "#3B82F6", "#F59E0B", "#10B981", "#EF4444", "#8B5CF6", "#64748B" })
            icons.Children.Add(new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(6), Margin = new Thickness(8, 0, 8, 0), Background = (Brush)new BrushConverter().ConvertFromString(color)! });
        taskbar.Child = icons;
        canvas.Children.Add(taskbar);

        return new Viewbox { Child = canvas, Stretch = Stretch.Uniform };
    }

    // ───────────────────────── Рисование в память ─────────────────────────

    private static void Layout(FrameworkElement element, double width, double height, double scale)
    {
        VisualTreeHelper.SetRootDpi(element, new DpiScale(scale, scale));
        for (var pass = 0; pass < 2; pass++)
        {
            element.Measure(new Size(width, height));
            element.Arrange(new Rect(0, 0, width, height));
            element.UpdateLayout();
            // Дать сработать привязкам и шаблонам, которые ждут своей очереди.
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>Отдельный кусок (содержимое всплывающего окна) своего размера.</summary>
    private static (BitmapSource Image, Size Size) Snap(FrameworkElement element, double scale)
    {
        VisualTreeHelper.SetRootDpi(element, new DpiScale(scale, scale));
        var size = new Size();
        for (var pass = 0; pass < 2; pass++)
        {
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            size = new Size(Math.Ceiling(element.DesiredSize.Width), Math.Ceiling(element.DesiredSize.Height));
            element.Arrange(new Rect(new Point(0, 0), size));
            element.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        return (Bitmap(element, size.Width, size.Height, scale), size);
    }

    private static RenderTargetBitmap Bitmap(Visual visual, double width, double height, double scale)
    {
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(width * scale)), Math.Max(1, (int)Math.Ceiling(height * scale)),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Save(string dir, string name, Shot shot)
    {
        BitmapSource image = Bitmap(shot.Root, shot.Width, shot.Height, shot.Scale);
        if (shot.Overlays.Count > 0)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(image, new Rect(0, 0, shot.Width, shot.Height));
                foreach (var (overlay, place) in shot.Overlays) dc.DrawImage(overlay, place);
            }
            image = Bitmap(visual, shot.Width, shot.Height, shot.Scale);
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(System.IO.Path.Combine(dir, name + ".png"));
        encoder.Save(file);
    }

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T hit) return hit;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find<T>(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    // ───────────────────────── Значок приложения ─────────────────────────

    /// <summary>Собирает icon.ico из того же знака, что в полосе заголовка: 16, 24, 32, 48 и 256.</summary>
    private static void WriteIcon(string path)
    {
        var drawing = (Drawing)Application.Current.FindResource("Logo.Drawing");
        int[] sizes = [16, 24, 32, 48, 256];
        var images = new List<byte[]>();
        foreach (var size in sizes)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(size / 64.0, size / 64.0));
                dc.DrawDrawing(drawing);
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            images.Add(size >= 256 ? Png(bitmap) : Dib(bitmap, size));
        }

        using var file = File.Create(path);
        using var w = new BinaryWriter(file);
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }
        foreach (var image in images) w.Write(image);
    }

    private static byte[] Png(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Картинка в виде, который понимают все версии Windows: 32 бита, строки снизу вверх, плюс маска.</summary>
    private static byte[] Dib(BitmapSource bitmap, int size)
    {
        var straight = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var stride = size * 4;
        var pixels = new byte[stride * size];
        straight.CopyPixels(pixels, stride, 0);

        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(40); w.Write(size); w.Write(size * 2);
        w.Write((short)1); w.Write((short)32);
        w.Write(0); w.Write(pixels.Length);
        w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        for (var y = size - 1; y >= 0; y--) w.Write(pixels, y * stride, stride);
        var maskStride = (size + 31) / 32 * 4;
        w.Write(new byte[maskStride * size]);
        w.Flush();
        return stream.ToArray();
    }
}
