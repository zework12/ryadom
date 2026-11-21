using System.Windows;
using System.Windows.Controls;

namespace Ryadom.Views;

/// <summary>Панель сеанса. Одна и та же стоит полосой над картинкой и во всплывающем окне полноэкранного режима.</summary>
public partial class SessionToolbar : UserControl
{
    public SessionToolbar()
    {
        InitializeComponent();
        // Пока меню открыто, кнопка не ловит щелчок — иначе щелчок по ней закрыл бы меню и тут же открыл снова.
        KeysPopup.Opened += (_, _) => KeysToggle.IsHitTestVisible = false;
        KeysPopup.Closed += (_, _) => KeysToggle.IsHitTestVisible = true;
    }

    /// <summary>Открыт какой-нибудь список или меню — выезжающую панель прятать нельзя.</summary>
    public bool HasOpenDropdown =>
        KeysPopup.IsOpen || FpsCombo.IsDropDownOpen || QualityCombo.IsDropDownOpen || MonitorCombo.IsDropDownOpen;

    public void CloseDropdowns()
    {
        KeysToggle.IsChecked = false;
        FpsCombo.IsDropDownOpen = QualityCombo.IsDropDownOpen = MonitorCombo.IsDropDownOpen = false;
    }

    /// <summary>Сколько места есть по ширине: чем уже, тем меньше подписей (значки и подсказки остаются).</summary>
    public void SetAvailableWidth(double width)
    {
        // Пороги подобраны по превью с запасом на длинное имя компьютера.
        var level = width >= 1300 ? 0 : width >= 1220 ? 1 : width >= 1100 ? 2 : width >= 990 ? 3 : 4;
        ResolutionText.Visibility = Show(level <= 0);
        QualityCombo.Tag = level <= 1 ? "Качество" : null;
        PeerText.MaxWidth = level <= 1 ? 200 : level == 2 ? 140 : level == 3 ? 120 : 96;
        StatsHost.Visibility = Show(level <= 2);
        DisconnectLabel.Visibility = Show(level <= 3);
        KeysLabel.Visibility = Show(level <= 3);
        ControlLabel.Visibility = Show(level <= 3);
    }

    private static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    private void OnKeysItemClick(object sender, RoutedEventArgs e) => KeysToggle.IsChecked = false;
}
