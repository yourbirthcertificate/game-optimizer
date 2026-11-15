using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameOptimizer.Core.Hardware;
using GameOptimizer.Core.Optimize;
using GameOptimizer.Core.Safety;

namespace GameOptimizer.App;

public sealed class InvertBool : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => v is bool b ? !b : v;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => v is bool b ? !b : v;
}

public partial class MainWindow : Window
{
    readonly MainViewModel _vm = new();
    readonly DispatcherTimer _replanTimer;
    ICollectionView? _view;
    int _planVersion;
    OptimizerSession? S => _vm.Session;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _replanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _replanTimer.Tick += async (_, _) => { _replanTimer.Stop(); await ReplanAllAsync(); };
        _vm.GlobalChanged += () => { _replanTimer.Stop(); _replanTimer.Start(); };
        _vm.RowChangedEvent += async row => await ReplanRowsAsync(new[] { row });
        _vm.FilterChanged += RefreshView;
        _vm.RecommendationNeededEvent += async row => await RecommendAsync(new[] { row });
        GlobalSlider.SizeChanged += (_, _) => LayoutTicks();
        Loaded += async (_, _) => await LoadAsync();
        InputBindings.Add(new KeyBinding(new Relay(async () => await LoadAsync()), Key.F5, ModifierKeys.None));
        // Display changes (a monitor plugged in, a new refresh rate) refresh what the app shows. Nothing is changed in Windows.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        Closed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
    }

    sealed class Relay(Func<Task> run) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? p) => true;
        public async void Execute(object? p) => await run();
    }

    void OnDisplaysChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(async () =>
    {
        if (S is null) return;
        S.RefreshDisplays();
        _vm.RebuildChoices();
        foreach (var r in _vm.Games) r.SyncStep();
        _vm.Status = "Display settings changed; active modes and automatic caps updated.";
        await ReplanAllAsync();
    });

    async Task LoadAsync()
    {
        _vm.Busy = true;
        _vm.Status = "Reading hardware and scanning games...";
        var session = await Task.Run(() => OptimizerSession.Load());
        _vm.Load(session);
        _view = CollectionViewSource.GetDefaultView(_vm.Games);
        _view.Filter = o => o is GameRow r && _vm.Matches(r);
        LayoutTicks();
        await ReplanAllAsync();
        _vm.Busy = false;
        var supported = _vm.Games.Where(r => r.IsSupported).ToList();
        await RecommendAsync(supported);
        if (session.Notes.Count > 0) _vm.Status += "  " + string.Join("  ", session.Notes);
    }

    async Task RecommendAsync(IReadOnlyList<GameRow> rows)
    {
        if (S is null || rows.Count == 0) return;
        var games = rows.Select(r => r.Game).ToList();
        var progress = new Progress<(int done, int total)>(p => _vm.Status = $"Reading published requirements {p.done}/{p.total}...");
        var session = S;
        await Task.Run(() => session.LoadRecommendationsAsync(games, progress));
        foreach (var r in rows) if (session.Recs.TryGetValue(r.Game.Id, out var rec)) r.SetRecommendation(rec);
        await ReplanRowsAsync(rows);
    }

    Task ReplanAllAsync() => ReplanRowsAsync(_vm.Games.ToList(), all: true);

    async Task ReplanRowsAsync(IReadOnlyList<GameRow> rows, bool all = false)
    {
        if (S is null) return;
        int version = all ? ++_planVersion : _planVersion;
        var games = rows.Select(r => r.Game).ToList();
        var session = S;
        var plans = await Task.Run(() =>
        {
            var images = ProcessGuard.RunningImages();
            return games.Select(g => session.PlanFor(g, runningImages: images)).ToList();
        });
        if (version != _planVersion) return;
        for (int i = 0; i < rows.Count; i++) rows[i].Plan = plans[i];
        _vm.CountsChanged();
        RefreshView();
        int ready = _vm.Games.Count(g => g.CanOptimize);
        if (!_vm.Busy) _vm.Status = ready == 0 ? "Every supported game is already set for its choices." : $"{ready} game(s) have changes waiting.";
    }

    void RefreshView()
    {
        _view?.Refresh();
        int shown = _view?.Cast<object>().Count() ?? 0;
        ShownText.Text = $"Showing {shown} of {_vm.Games.Count}";
    }

    static readonly string[] OffsetTicks = { "Fastest", "-2", "-1", "Recommended", "+1", "+2", "Best looking" };

    void LayoutTicks()
    {
        GlobalTicks.Children.Clear();
        double w = GlobalSlider.ActualWidth;
        if (w <= 0) return;
        const double thumbHalf = 5.5;
        for (int i = 0; i < OffsetTicks.Length; i++)
        {
            bool mid = i == 3;
            var tb = new TextBlock
            {
                Text = OffsetTicks[i], FontSize = 12,
                Foreground = mid ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted"),
                FontWeight = mid ? FontWeights.SemiBold : FontWeights.Normal,
            };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double x = thumbHalf + i * (w - 2 * thumbHalf) / (OffsetTicks.Length - 1);
            Canvas.SetLeft(tb, Math.Clamp(x - tb.DesiredSize.Width / 2, -8, w - tb.DesiredSize.Width + 8));
            GlobalTicks.Children.Add(tb);
        }
    }

    static GameRow? RowOf(object sender) => (sender as FrameworkElement)?.Tag as GameRow;

    void UseRecommended_Click(object sender, RoutedEventArgs e) => _vm.GlobalOffset = 0;
    void ResetRow_Click(object sender, RoutedEventArgs e) => RowOf(sender)?.ResetStep();
    void ClearKept_Click(object sender, RoutedEventArgs e) => RowOf(sender)?.ClearExclusions();
    void ResetChoices_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } r && Ui.Confirm(this, $"Remove every choice made for {r.Name} (step, profile, frame cap, display, kept settings, linked app id)?", "Reset choices"))
            r.ResetAllChoices();
    }

    void SelectShown_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _view?.Cast<GameRow>().ToList() ?? new List<GameRow>()) r.IsSelected = true;
    }
    void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _vm.Games) r.IsSelected = false;
    }

    void Link_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string url && url.StartsWith("https://", StringComparison.Ordinal)) Open(url);
    }

    void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { Ui.Info(this, "Could not open it: " + ex.Message); }
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender)?.Plan?.SettingsFolder is { } f && Directory.Exists(f)) Open(f);
    }

    async void AddRule_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } r || S is null) return;
        var dlg = new RuleWizardWindow(S, r.Game, this);
        dlg.ShowDialog();
        if (dlg.Saved) await LoadAsync();
    }

    void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { Game.SteamAppId: { } id } r) return;
        Open($"steam://rungameid/{id}");
        Ui.Info(this, $"Steam is starting {r.Name}. Let it reach the main menu, close it, then press F5 (Library > Rescan).", "Launch once");
    }

    // ------------------------------------------------------------------ previews and applying

    async void PreviewRow_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } r) await PreviewAndMaybeApply(new[] { r }, r.Name);
    }

    async void ApplyRow_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } r || S is null) return;
        await ReplanRowsAsync(new[] { r });
        if (r.Plan is { Status: PlanStatus.Ready } plan) await ApplyAsync(new[] { plan }, $"{r.Name}: {plan.Step.Label()}");
        else Ui.Info(this, r.StatusText, r.Name);
    }

    async void PreviewSelected_Click(object sender, RoutedEventArgs e) => await SelectedAction();
    async void ApplySelected_Click(object sender, RoutedEventArgs e) => await SelectedAction();

    async Task SelectedAction()
    {
        var rows = _vm.Games.Where(g => g.IsSelected).ToList();
        if (rows.Count == 0) { Ui.Info(this, "Tick the games you want first (the box at the left of each row, or Select shown)."); return; }
        await PreviewAndMaybeApply(rows, $"{rows.Count} selected game(s)");
    }

    async void OptimizeAll_Click(object sender, RoutedEventArgs e)
    {
        var rows = _vm.Games.Where(g => g.IsSupported).ToList();
        await PreviewAndMaybeApply(rows, $"all {rows.Count} supported games");
    }

    async Task PreviewAndMaybeApply(IReadOnlyList<GameRow> rows, string scope)
    {
        if (S is null) return;
        _vm.Busy = true;
        _vm.Status = "Checking " + scope + "...";
        await ReplanRowsAsync(rows);
        _vm.Busy = false;
        var plans = rows.Select(r => r.Plan).OfType<GamePlan>().Where(p => p.Status != PlanStatus.Skipped).ToList();
        if (plans.Count == 0) { Ui.Info(this, $"Nothing in {scope} can be changed.", "Preview"); return; }
        var dlg = new PreviewWindow(S, plans, scope, this);
        if (dlg.ShowDialog() == true && dlg.FinalPlans.Count > 0)
            await ApplyAsync(dlg.FinalPlans, plans.Count == 1 ? $"{plans[0].Game.Name}: {plans[0].Step.Label()}" : $"Optimize {scope}");
        await ReplanRowsAsync(rows);
    }

    async Task ApplyAsync(IReadOnlyList<GamePlan> plans, string summary)
    {
        if (plans.Count == 0) return;
        _vm.Busy = true;
        _vm.Status = "Backing up and writing settings...";
        var res = await Task.Run(() => Applier.Apply(plans, summary));
        _vm.Busy = false;
        var lines = new List<string>();
        lines.AddRange(res.Applied.Select(a => "Done: " + a));
        lines.AddRange(res.Refused.Select(r => "Not changed: " + r));
        lines.AddRange(res.Errors.Select(x => "Problem: " + x));
        if (res.RunId is not null) lines.Add($"\nBackups are in run {res.RunId}. Tools > History and restore can undo it.");
        MessageBox.Show(this, string.Join("\n", lines), "Optimize", MessageBoxButton.OK, res.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        await ReplanAllAsync();
    }

    // ------------------------------------------------------------------ menus

    async void Rescan_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (S is null) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a game's install folder" };
        if (dlg.ShowDialog(this) != true) return;
        var path = dlg.FolderName;
        if (S.Prefs.ManualGames.Any(m => Core.Library.LibraryScanner.NormalizeDir(m.Path) == Core.Library.LibraryScanner.NormalizeDir(path)))
        { Ui.Info(this, "That folder is already in the list."); return; }
        S.Prefs.ManualGames.Add(new ManualGame { Path = path });
        S.Prefs.Save();
        await LoadAsync();
    }

    async void ManageFolders_Click(object sender, RoutedEventArgs e)
    {
        if (S is null) return;
        if (new FoldersWindow(S.Prefs, this).ShowDialog() == true) await LoadAsync();
    }

    async void History_Click(object sender, RoutedEventArgs e)
    {
        new HistoryWindow(this).ShowDialog();
        await ReplanAllAsync();
    }

    async void Benchmarks_Click(object sender, RoutedEventArgs e)
    {
        if (S is null) return;
        new BenchmarksWindow(S, this).ShowDialog();
        await RecommendAsync(_vm.Games.Where(r => r.IsSupported).ToList());
    }

    async void Wizard_Click(object sender, RoutedEventArgs e)
    {
        if (S is null) return;
        var dlg = new RuleWizardWindow(S, null, this);
        dlg.ShowDialog();
        if (dlg.Saved) await LoadAsync();
    }

    void ValidateRules_Click(object sender, RoutedEventArgs e)
    {
        var errors = new List<string>();
        var rules = RuleSet.Load(errors);
        var text = $"{rules.Count} rule(s) loaded: {string.Join(", ", rules.Select(r => r.Id))}.\nYour rules folder: {RuleSet.UserRulesDir}";
        if (errors.Count > 0) text += "\n\nProblems:\n" + string.Join("\n", errors);
        Ui.Info(this, text, "Rule files");
    }

    async void Profiles_Click(object sender, RoutedEventArgs e)
    {
        if (S is null) return;
        new ProfilesWindow(S, this).ShowDialog();
        _vm.RebuildChoices();
        foreach (var r in _vm.Games) r.SyncStep();
        await ReplanAllAsync();
    }

    void Export_Click(object sender, RoutedEventArgs e)
    {
        if (S is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "gameoptimizer-preferences.json", Filter = "JSON|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, PrefsTransfer.Export(S.Prefs));
        Ui.Info(this, "Exported profiles, choices, kept settings and your rules. Game saves, backups and benchmarks are not included.", "Export");
    }

    async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (S is null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        if (new ImportWindow(S, File.ReadAllText(dlg.FileName), this).ShowDialog() == true) await LoadAsync();
    }
}
