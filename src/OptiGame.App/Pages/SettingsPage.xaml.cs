using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using OptiGame.App.ViewModels;
using OptiGame.Core.Vendors;

namespace OptiGame.App.Pages;

public partial class SettingsPage : UserControl
{
    readonly AppVm _vm;
    readonly GamesPage _games;
    readonly Action _exit;

    public SettingsPage(AppVm vm, GamesPage games, Action exitApp)
    {
        _vm = vm; _games = games; _exit = exitApp;
        InitializeComponent();
        DataContext = new SettingsContext(vm, games.Vm);
    }

    static T? Ctx<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;
    Window Owner => Window.GetWindow(this)!;

    void RemoveKeep_Click(object sender, RoutedEventArgs e) { if (Ctx<KeepItem>(sender) is { } k) _vm.RemoveKeep(k); }
    void ResetLearning_Click(object sender, RoutedEventArgs e) => _vm.ResetLearning();

    void RestartAdmin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--elevated") { Verb = "runas", UseShellExecute = true });
            _exit();
        }
        catch (Win32Exception) { /* user declined the UAC prompt */ }
    }

    // vendor apps
    async void OpenVendor_Click(object sender, RoutedEventArgs e) => await _vm.OpenVendorAsync();
    async void OpenVendorApp_Click(object sender, RoutedEventArgs e) { if (Ctx<VendorApp>(sender) is { } a) await _vm.OpenVendorAsync(a); }

    void ChooseVendor_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose your laptop's control app", Filter = "Programs and shortcuts (*.exe;*.lnk)|*.exe;*.lnk", CheckFileExists = true };
        if (dlg.ShowDialog(Owner) == true) _vm.ChooseVendorApp(dlg.FileName);
    }
    void ForgetVendor_Click(object sender, RoutedEventArgs e) => _vm.ForgetVendorApp();

    // Game Spec Check
    void ChooseSpecCheck_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose GameSpecCheck.exe (or its run.py)", Filter = "Game Spec Check (*.exe;run.py)|*.exe;run.py", CheckFileExists = true };
        if (dlg.ShowDialog(Owner) != true) return;
        _vm.Store.Data.SpecCheckPath = dlg.FileName; _vm.Store.Save();
        _games.Vm.RefreshSpecCheck();
        ((SettingsContext)DataContext).Refresh();
    }

    // updates
    async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await _vm.CheckForUpdateAsync(manual: true);
    void InstallUpdate_Click(object sender, RoutedEventArgs e) => _vm.ShowUpdateWindow();

    // tools
    void History_Click(object sender, RoutedEventArgs e) => _games.History_Click(sender, e);
    void Benchmarks_Click(object sender, RoutedEventArgs e) => _games.Benchmarks_Click(sender, e);
    void Profiles_Click(object sender, RoutedEventArgs e) => _games.Profiles_Click(sender, e);
    void Wizard_Click(object sender, RoutedEventArgs e) => _games.Wizard_Click(sender, e);
    void ValidateRules_Click(object sender, RoutedEventArgs e) => _games.ValidateRules_Click(sender, e);
    void Export_Click(object sender, RoutedEventArgs e) => _games.Export_Click(sender, e);
    void Import_Click(object sender, RoutedEventArgs e) => _games.Import_Click(sender, e);
}

/// <summary>The settings page binds to the live view model; this adds the few items that come from the optimizer side.</summary>
public sealed class SettingsContext : System.ComponentModel.INotifyPropertyChanged
{
    readonly AppVm _live; readonly MainViewModel _games;
    public SettingsContext(AppVm live, MainViewModel games)
    {
        _live = live; _games = games;
        live.PropertyChanged += (_, e) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(e.PropertyName));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpecCheckText)));

    // everything else forwards to the live view model
    public OptiGame.Core.Live.LiveSettings Settings => _live.Settings;
    public bool StartWithWindows { get => _live.StartWithWindows; set => _live.StartWithWindows = value; }
    public string AdminText => _live.AdminText;
    public bool IsAdmin => _live.IsAdmin;
    public string VendorHeadline => _live.VendorHeadline;
    public string VendorTipText => _live.VendorTipText;
    public string VendorOpenText => _live.VendorOpenText;
    public bool HasVendor => _live.HasVendor;
    public System.Collections.ObjectModel.ObservableCollection<VendorApp> VendorApps => _live.VendorApps;
    public string SteamGridKey
    {
        get => _live.Store.Data.SteamGridDbKey ?? "";
        set { _live.Store.Data.SteamGridDbKey = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); _live.Store.Save(); }
    }
    public string VersionText => _live.VersionText;
    public string UpdateStatus => _live.UpdateStatus;
    public bool UpdateAvailable => _live.UpdateAvailable;
    public bool CanCheckUpdate => _live.CanCheckUpdate;
    public string InstallUpdateText => _live.InstallUpdateText;
    public string LearnedSummary => _live.LearnedSummary;
    public System.Collections.ObjectModel.ObservableCollection<KeepItem> KeepItems => _live.KeepItems;
    public string ModelLine => _live.ModelLine;
    public OptiGame.Core.Hardware.HardwareInfo Profile => _live.Profile;
    public string RamLine => _live.RamLine;
    public string GpuLine => _live.GpuLine;
    public string TuneNote => _live.TuneNote;
    public string SpecCheckText => _games.SpecCheck is { } s
        ? $"Found: {OptiGame.Core.PathDisplay.Short(s.Path)}" + (s.Version.Major > 0 ? $" (version {s.Version})" : "")
        : "Not found. Put it next to OptiGame+, in a game-spec-check folder beside it, or choose it here.";
}
