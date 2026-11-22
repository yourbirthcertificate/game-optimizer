using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OptiGame.App.Pages;
using OptiGame.App.ViewModels;
using OptiGame.Core.Live;

namespace OptiGame.App;

public partial class MainWindow : Window
{
    readonly AppVm _vm;
    readonly Tray? _tray;
    readonly UserControl[] _pages;
    readonly GamesPage _games;
    bool _exiting, _trayHintShown;

    public MainWindow(AppVm vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Chrome.Attach(this);

        // Fit and centre inside the work area (CenterScreen can push tall windows off-screen on scaled displays).
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(Width, wa.Width - 24); Height = Math.Min(Height, wa.Height - 24);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = wa.Left + (wa.Width - Width) / 2; Top = wa.Top + (wa.Height - Height) / 2;

        _games = new GamesPage(new MainViewModel(vm));
        _pages = new UserControl[] { new DashboardPage(vm), new AppsPage(vm), _games, new SettingsPage(vm, _games, ExitApp) };
        foreach (var p in _pages) { p.Visibility = Visibility.Collapsed; Pages.Children.Add(p); }
        _pages[0].Visibility = Visibility.Visible;

        _tray = new Tray(vm.Settings.AutoBoost);
        _tray.OpenRequested += ShowFromTray;
        _tray.BoostRequested += async () => await vm.BoostOrRestoreAsync();
        _tray.AutoToggled += on => { if (vm.Settings.AutoBoost != on) vm.Settings.AutoBoost = on; };
        _tray.ExitRequested += ExitApp;
        vm.Settings.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(LiveSettings.AutoBoost)) _tray.SetAuto(vm.Settings.AutoBoost); };
        vm.ToastRequested += (t, s) => _tray.Toast(t, s);
        vm.NavigateRequested += i => Nav.SelectedIndex = i;

        IsVisibleChanged += (_, _) => UpdateVisibility();
        StateChanged += (_, _) => UpdateVisibility();
        InputBindings.Add(new KeyBinding(new Relay(() => _games.Rescan_Click(null, null)), Key.F5, ModifierKeys.None));
    }

    sealed class Relay(Action run) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? p) => true;
        public void Execute(object? p) => run();
    }

    /// <summary>Scans the game library and checks for an update. Runs even when the window starts hidden in the tray.</summary>
    public async Task StartBackgroundWorkAsync()
    {
        await _games.LoadAsync();
        await _vm.CheckForUpdateAsync(manual: false);
    }

    // ------------------------------------------------------------------ tray / lifetime
    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate(); Topmost = true; Topmost = false; Focus();
    }

    public void ExitApp()
    {
        _exiting = true;
        _tray?.Dispose();
        _vm.Shutdown();
        Application.Current.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && _vm.Settings.KeepInTray)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown) { _trayHintShown = true; _tray?.Toast("OptiGame+ is still running", "It stays in the tray so it can boost your games automatically. Right-click the tray icon to exit."); }
            return;
        }
        if (!_exiting) { _exiting = true; _tray?.Dispose(); _vm.Shutdown(); Application.Current.Shutdown(); }
        base.OnClosing(e);
    }

    void UpdateVisibility()
    {
        bool vis = IsVisible && WindowState != WindowState.Minimized;
        if (vis && !_vm.WindowVisible) { _vm.WindowVisible = true; _ = _vm.RefreshAsync(); }
        else _vm.WindowVisible = vis;
    }

    // ------------------------------------------------------------------ navigation
    void Nav_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_pages is null) return;
        int i = Nav.SelectedIndex;
        for (int k = 0; k < _pages.Length; k++) _pages[k].Visibility = k == i ? Visibility.Visible : Visibility.Collapsed;
    }

    void DismissMessage_Click(object sender, RoutedEventArgs e) => _vm.DismissMessage();
}
