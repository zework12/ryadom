using System.Windows;

namespace Ryadom.ViewModels;

// ─────────────────────────────────────────────────────────────────────────────
// Модели экранов. Их ведут контроллеры (Host/HostController, Viewer/ViewerController):
// ставят свойства и подставляют действия в команды. Окна только показывают
// и вызывают команды. Все свойства можно ставить руками — так строится превью.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Окно целиком: какой экран сейчас открыт.</summary>
public sealed class MainViewModel : Observable
{
    private object _page;

    public MainViewModel(object page) => _page = page;

    /// <summary>StartViewModel / HostViewModel / JoinViewModel / SessionViewModel.</summary>
    public object Page { get => _page; set => Set(ref _page, value); }

    public string Version { get; init; } = "1.0.0";
}

/// <summary>Первый экран: две большие кнопки.</summary>
public sealed class StartViewModel : Observable
{
    /// <summary>«Мне нужна помощь» — показать свой экран.</summary>
    public RelayCommand NeedHelpCommand { get; } = new();

    /// <summary>«Я помогаю» — подключиться к другому.</summary>
    public RelayCommand HelpOtherCommand { get; } = new();

    private bool _confirmingUninstall;

    /// <summary>Показан вопрос «Удалить „Рядом“ с этого компьютера?» (поверх первого экрана).</summary>
    public bool ConfirmingUninstall { get => _confirmingUninstall; set => Set(ref _confirmingUninstall, value); }

    /// <summary>Неприметная ссылка «Удалить программу» внизу первого экрана — открывает вопрос.</summary>
    public RelayCommand AskUninstallCommand { get; } = new();

    /// <summary>«Удалить» в вопросе: программа удаляется и закрывается.</summary>
    public RelayCommand ConfirmUninstallCommand { get; } = new();

    /// <summary>«Отмена» в вопросе.</summary>
    public RelayCommand CancelUninstallCommand { get; } = new();
}

public enum SetupState
{
    /// <summary>Выбор: «Установить» (не стоит) или «Открыть / Переустановить / Удалить» (уже стоит).</summary>
    Choose,
    /// <summary>Вопрос «Удалить „Рядом“?» — «Удалить» / «Отмена».</summary>
    ConfirmUninstall,
    /// <summary>Идёт установка или удаление (StatusText — что именно).</summary>
    Working,
    /// <summary>Удалено — «Готово, „Рядом“ удалено» и «Закрыть».</summary>
    Uninstalled,
    Error,
}

/// <summary>
/// Установщик — тот же файл, скачанный по ссылке. Показывается вместо первого
/// экрана, когда программу запустили не из установленной папки, или с ключом
/// --uninstall (из «Установленных приложений» Windows). После установки и
/// переустановки сам открывает установленную программу и закрывается.
/// </summary>
public sealed class SetupViewModel : Observable
{
    private SetupState _state = SetupState.Choose;
    private bool _isInstalled;
    private string? _installedVersion;
    private string _thisVersion = "1.0.0";
    private string _statusText = "";
    private string? _errorText;

    public SetupState State { get => _state; set => Set(ref _state, value); }

    /// <summary>Программа уже стоит на этом компьютере.</summary>
    public bool IsInstalled { get => _isInstalled; set => Set(ref _isInstalled, value); }

    /// <summary>Какая версия стоит («1.1.3»), null — не стоит.</summary>
    public string? InstalledVersion { get => _installedVersion; set => Set(ref _installedVersion, value); }

    /// <summary>Версия этого установщика.</summary>
    public string ThisVersion { get => _thisVersion; set => Set(ref _thisVersion, value); }

    /// <summary>«Устанавливаем…», «Удаляем…».</summary>
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    public string? ErrorText { get => _errorText; set => Set(ref _errorText, value); }

    /// <summary>Главная кнопка, когда не установлено.</summary>
    public RelayCommand InstallCommand { get; } = new();

    /// <summary>Когда установлено: открыть установленную (главная кнопка).</summary>
    public RelayCommand OpenCommand { get; } = new();

    /// <summary>Когда установлено: поставить заново начисто (версия этого файла).</summary>
    public RelayCommand ReinstallCommand { get; } = new();

    /// <summary>Когда установлено: перейти к вопросу об удалении.</summary>
    public RelayCommand UninstallCommand { get; } = new();

    /// <summary>«Удалить» в вопросе.</summary>
    public RelayCommand ConfirmUninstallCommand { get; } = new();

    /// <summary>«Отмена» в вопросе — назад к выбору.</summary>
    public RelayCommand CancelCommand { get; } = new();

    /// <summary>Закрыть установщик (после удаления, при ошибке).</summary>
    public RelayCommand CloseCommand { get; } = new();
}

public enum HostState
{
    /// <summary>Получаем код у сервера.</summary>
    Preparing,
    /// <summary>Код готов, ждём помощника.</summary>
    Waiting,
    /// <summary>Помощник постучался — спрашиваем «Разрешить?».</summary>
    Asking,
    /// <summary>Помощник видит экран и управляет.</summary>
    Connected,
    /// <summary>Связь с сервером пропала, LiveKit чинит сам.</summary>
    Reconnecting,
    /// <summary>Не получилось (нет интернета, сервер недоступен).</summary>
    Error,
}

/// <summary>Сторона «Мне нужна помощь».</summary>
public sealed class HostViewModel : Observable
{
    private HostState _state = HostState.Preparing;
    private string _code = "";
    private string? _viewerName;
    private string? _errorText;
    private bool _codeCopied;
    private bool _isElevated;
    private string _durationText = "";

    public HostState State { get => _state; set => Set(ref _state, value); }

    /// <summary>Код для помощника, уже с пробелами: «123 456 789».</summary>
    public string Code { get => _code; set => Set(ref _code, value); }

    /// <summary>Как назвался помощник (во время Asking/Connected).</summary>
    public string? ViewerName { get => _viewerName; set => Set(ref _viewerName, value); }

    public string? ErrorText { get => _errorText; set => Set(ref _errorText, value); }

    /// <summary>true на ~2 с после «Скопировать».</summary>
    public bool CodeCopied { get => _codeCopied; set => Set(ref _codeCopied, value); }

    /// <summary>Запущено с правами администратора. false — окна «с щитом»
    /// (настройки системы, установщики) помощник двигать не сможет.</summary>
    public bool IsElevated { get => _isElevated; set => Set(ref _isElevated, value); }

    /// <summary>Сколько уже идёт помощь: «12:34».</summary>
    public string DurationText { get => _durationText; set => Set(ref _durationText, value); }

    public RelayCommand CopyCodeCommand { get; } = new();
    public RelayCommand AllowCommand { get; } = new();
    public RelayCommand DenyCommand { get; } = new();
    /// <summary>Прекратить помощь (помощник отключается, код остаётся).</summary>
    public RelayCommand DisconnectCommand { get; } = new();
    public RelayCommand RetryCommand { get; } = new();
    /// <summary>Назад на первый экран (код пропадает).</summary>
    public RelayCommand BackCommand { get; } = new();
}

public enum JoinState
{
    Idle,
    Connecting,
    /// <summary>Подключились, ждём, пока человек нажмёт «Разрешить».</summary>
    WaitingApproval,
    /// <summary>Человек нажал «Отклонить».</summary>
    Denied,
    Error,
}

/// <summary>Сторона «Я помогаю»: ввод кода.</summary>
public sealed class JoinViewModel : Observable
{
    private string _code = "";
    private string _name = "";
    private JoinState _state = JoinState.Idle;
    private string? _errorText;

    /// <summary>Код как ввели (пробелы и дефисы допустимы — контроллер их уберёт).</summary>
    public string Code { get => _code; set { if (Set(ref _code, value)) ConnectCommand.Refresh(); } }

    /// <summary>Своё имя — его увидит человек в вопросе «Разрешить?». Запоминается.</summary>
    public string Name { get => _name; set { if (Set(ref _name, value)) ConnectCommand.Refresh(); } }

    public JoinState State { get => _state; set => Set(ref _state, value); }

    public string? ErrorText { get => _errorText; set => Set(ref _errorText, value); }

    public RelayCommand ConnectCommand { get; } = new();
    public RelayCommand CancelCommand { get; } = new();
    public RelayCommand BackCommand { get; } = new();
}

public enum LinkQuality
{
    Excellent,
    Good,
    Poor,
    Lost,
}

public sealed record FpsOption(int Fps, string Title);

public sealed record QualityOption(string Key, string Title, string Hint);

public sealed record MonitorOption(int Index, string Title, int Width, int Height);

/// <summary>Идёт помощь: картинка чужого экрана и панель.</summary>
public sealed class SessionViewModel : Observable
{
    private string _peerName = "";
    private FrameworkElement? _video;
    private bool _hasVideo;
    private FpsOption _selectedFps;
    private QualityOption _selectedQuality;
    private IReadOnlyList<MonitorOption> _monitors = [];
    private MonitorOption? _selectedMonitor;
    private double _actualFps;
    private int _pingMs;
    private string _resolutionText = "";
    private LinkQuality _quality = LinkQuality.Good;
    private bool _isReconnecting;
    private bool _isFullscreen;
    private bool _controlEnabled = true;
    private string? _notice;

    public SessionViewModel()
    {
        _selectedFps = FpsOptions[1];
        _selectedQuality = Qualities[1];
    }

    /// <summary>Кому помогаем (имя компьютера человека).</summary>
    public string PeerName { get => _peerName; set => Set(ref _peerName, value); }

    /// <summary>Сама картинка (окно Direct3D внутри WPF). Поверх неё в WPF ничего
    /// нарисовать нельзя — только рядом или во всплывающем окне (Popup).
    /// null в превью.</summary>
    public FrameworkElement? Video { get => _video; set => Set(ref _video, value); }

    /// <summary>Хотя бы один кадр уже пришёл. Пока false — показать «Ждём картинку…»
    /// (на месте Video, само Video при этом скрыть).</summary>
    public bool HasVideo { get => _hasVideo; set => Set(ref _hasVideo, value); }

    public IReadOnlyList<FpsOption> FpsOptions { get; } =
    [
        new(30, "30 кадров"),
        new(60, "60 кадров"),
        new(90, "90 кадров"),
    ];

    public FpsOption SelectedFps { get => _selectedFps; set => Set(ref _selectedFps, value); }

    public IReadOnlyList<QualityOption> Qualities { get; } =
    [
        new("low", "Экономно", "720p — для слабого интернета"),
        new("normal", "Обычно", "1080p"),
        new("max", "Максимум", "Как на экране, без сжатия размера"),
    ];

    public QualityOption SelectedQuality { get => _selectedQuality; set => Set(ref _selectedQuality, value); }

    /// <summary>Экраны человека; если один — переключатель не показывать.</summary>
    public IReadOnlyList<MonitorOption> Monitors { get => _monitors; set => Set(ref _monitors, value); }

    public MonitorOption? SelectedMonitor { get => _selectedMonitor; set => Set(ref _selectedMonitor, value); }

    /// <summary>Сколько кадров в секунду реально приходит.</summary>
    public double ActualFps { get => _actualFps; set => Set(ref _actualFps, value); }

    /// <summary>Задержка туда-обратно через сервер, мс.</summary>
    public int PingMs { get => _pingMs; set => Set(ref _pingMs, value); }

    /// <summary>«1920×1080».</summary>
    public string ResolutionText { get => _resolutionText; set => Set(ref _resolutionText, value); }

    public LinkQuality Quality { get => _quality; set => Set(ref _quality, value); }

    /// <summary>Связь прервалась, чиним (картинка застыла).</summary>
    public bool IsReconnecting { get => _isReconnecting; set => Set(ref _isReconnecting, value); }

    /// <summary>Во весь экран: окно без рамки на весь монитор, панель прячется
    /// и выезжает, если подвести мышь к верхнему краю.</summary>
    public bool IsFullscreen { get => _isFullscreen; set => Set(ref _isFullscreen, value); }

    /// <summary>false — только смотреть (мышь и клавиши не уходят).</summary>
    public bool ControlEnabled { get => _controlEnabled; set => Set(ref _controlEnabled, value); }

    /// <summary>Короткое сообщение на пару секунд («Текст из буфера отправлен»). null — нет.</summary>
    public string? Notice { get => _notice; set => Set(ref _notice, value); }

    public RelayCommand ToggleFullscreenCommand { get; } = new();
    public RelayCommand DisconnectCommand { get; } = new();

    /// <summary>Отправить сочетание клавиш. Параметр — строка: "taskmgr" (Ctrl+Shift+Esc),
    /// "run" (Win+R), "win" (Win), "alttab" (Alt+Tab), "lang" (смена раскладки Alt+Shift).</summary>
    public RelayCommand SendKeysCommand { get; } = new();
}
