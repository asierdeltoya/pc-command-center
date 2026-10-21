using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using PCCommandCenter.App.Services;
using PCCommandCenter.App.ViewModels;
using PCCommandCenter.Core;
using PCCommandCenter.Settings;

namespace PCCommandCenter.App.Views;

public partial class MainWindow : Window
{
    private const double SidebarExpanded = 240;
    private const double SidebarCollapsed = 72;

    private readonly MainViewModel _vm;
    private readonly ISettingsService _settings;
    private readonly ThemeService _theme;

    public event Action? ExitRequested;

    public MainWindow(MainViewModel vm, ISettingsService settings, ThemeService theme)
    {
        InitializeComponent();
        _vm = vm;
        _settings = settings;
        _theme = theme;
        DataContext = vm;

        var s = settings.Current;
        Width = s.WindowWidth;
        Height = s.WindowHeight;
        if (s.WindowMaximized) WindowState = WindowState.Maximized;

        SourceInitialized += (_, _) => _theme.ApplyWindowFrame(this);
        StateChanged += (_, _) => UpdateMaximizedState();
        _theme.ThemeChanged += () => _theme.ApplyWindowFrame(this);
        PreviewKeyDown += OnPreviewKeyDown;

        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.Palette.PropertyChanged += OnPalettePropertyChanged;
        ApplySidebar(vm.SidebarCollapsed, animate: false);
        ApplyWelcome();
    }

    // ───────────── Window chrome ─────────────

    private void UpdateMaximizedState()
    {
        // With a custom chrome, a maximized window extends past the screen edges by the resize border.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_settings.Current.MinimizeToTray && !((App)Application.Current).IsExiting)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        SaveWindowState();
        if (!((App)Application.Current).IsExiting) ExitRequested?.Invoke();
    }

    private void HideToTray()
    {
        SaveWindowState();
        Hide();
        _vm.OnWindowVisibilityChanged(false);
        if (!_settings.Current.TrayHintShown)
        {
            _settings.Update(s => s.TrayHintShown = true);
            _vm.Notifications.Notify(NotificationLevel.Info, Loc.T("PC Command Center is still running"),
                Loc.T("It keeps monitoring from the notification area. Right-click the tray icon to exit."));
        }
        AppLog.Info("Main window hidden to tray");
    }

    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = _settings.Current.WindowMaximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false; // bring to front reliably
        _vm.OnWindowVisibilityChanged(true);
    }

    public void SaveWindowState()
    {
        _settings.Update(s =>
        {
            s.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                s.WindowWidth = ActualWidth;
                s.WindowHeight = ActualHeight;
            }
        });
    }

    // ───────────── View model reactions ─────────────

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.CurrentPage):
                AnimatePage();
                break;
            case nameof(MainViewModel.SidebarCollapsed):
                ApplySidebar(_vm.SidebarCollapsed, animate: true);
                break;
            case nameof(MainViewModel.Welcome):
                ApplyWelcome();
                break;
        }
    }

    private void AnimatePage()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        PageShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }

    private void ApplySidebar(bool collapsed, bool animate)
    {
        var target = collapsed ? SidebarCollapsed : SidebarExpanded;
        if (animate)
            Sidebar.BeginAnimation(WidthProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else
            Sidebar.Width = target;
        var v = collapsed ? Visibility.Collapsed : Visibility.Visible;
        BrandText.Visibility = v;
        SearchButton.Visibility = v;
        StatusCard.Visibility = v;
        AdminChip.Opacity = AdminButton.Opacity = collapsed ? 0 : 1;
        AdminChip.IsHitTestVisible = AdminButton.IsHitTestVisible = !collapsed;
    }

    private void ApplyWelcome()
    {
        var stage = _vm.Welcome;
        WelcomeLayer.Visibility = stage == WelcomeStage.None ? Visibility.Collapsed : Visibility.Visible;
        WelcomeIntro.Visibility = stage == WelcomeStage.Intro ? Visibility.Visible : Visibility.Collapsed;
        WelcomeScanning.Visibility = stage == WelcomeStage.Scanning ? Visibility.Visible : Visibility.Collapsed;
        WelcomeReady.Visibility = stage == WelcomeStage.Ready ? Visibility.Visible : Visibility.Collapsed;
        if (stage != WelcomeStage.None)
            WelcomeLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(300)));
    }

    // ───────────── Command palette ─────────────

    private void OnPalettePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CommandPaletteViewModel.IsOpen) || !_vm.Palette.IsOpen) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PaletteScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        PaletteScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        PalettePanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        Dispatcher.BeginInvoke(() => { PaletteInput.Focus(); Keyboard.Focus(PaletteInput); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void PaletteInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: _vm.Palette.Move(1); PaletteList.ScrollIntoView(_vm.Palette.Selected); e.Handled = true; break;
            case Key.Up: _vm.Palette.Move(-1); PaletteList.ScrollIntoView(_vm.Palette.Selected); e.Handled = true; break;
            case Key.Enter: _vm.Palette.Run(null); e.Handled = true; break;
            case Key.Escape: _vm.Palette.Close(); e.Handled = true; break;
        }
    }

    private void PaletteList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && _vm.Palette.Selected != null) _vm.Palette.Run(null);
    }

    private void PaletteBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => _vm.Palette.Close();

    private void NotificationBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => _vm.NotificationsOpen = false;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (_vm.Dialogs.Current is { } d) { d.CancelCommand.Execute(null); e.Handled = true; }
        else if (_vm.Palette.IsOpen) { _vm.Palette.Close(); e.Handled = true; }
        else if (_vm.NotificationsOpen) { _vm.NotificationsOpen = false; e.Handled = true; }
    }
}
