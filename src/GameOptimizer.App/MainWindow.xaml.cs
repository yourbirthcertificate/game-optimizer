using System.IO;
﻿using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using GameOptimizer.Core.Hardware;
using GameOptimizer.Core.Library;
using GameOptimizer.Core.Optimize;
using GameOptimizer.Core.Recommend;
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

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _replanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _replanTimer.Tick += async (_, _) => { _replanTimer.Stop(); await ReplanAllAsync(); };
        _vm.GlobalChanged += () => { _replanTimer.Stop(); _replanTimer.Start(); };
        _vm.RowChanged += async row => await ReplanRowsAsync(new[] { row });
        GlobalSlider.SizeChanged += (_, _) => LayoutTicks();
        Loaded += async (_, _) => await LoadAsync();
    }

    async Task LoadAsync()
    {
        _vm.Busy = true;
        _vm.Status = "Reading hardware and scanning games...";
        var errors = new List<string>();
        var (hw, scan, rules) = await Task.Run(() => (HardwareDetector.Detect(), LibraryScanner.Scan(), RuleSet.Load(errors)));
        _vm.Load(hw, scan, rules);
        _view = CollectionViewSource.GetDefaultView(_vm.Games);
        _view.Filter = o => !_vm.OnlySupported || o is GameRow { Plan: null } || ((GameRow)o).IsSupported;
        LayoutTicks();
        await ReplanAllAsync();
        var notes = scan.Notes.Concat(errors).ToList();
        _vm.Busy = false;

        // Per-game recommendations come from published requirements (cached for a week), so they
        // arrive after the list is shown. Only games the tool can change are looked up.
        var supported = _vm.Games.Where(r => r.IsSupported).Select(r => r.Game).ToList();
        var progress = new Progress<(int done, int total)>(p => _vm.Status = $"Reading published requirements {p.done}/{p.total}...");
        var recs = await Task.Run(() => Recommender.RecommendAllAsync(hw, supported, progress));
        _vm.SetRecommendations(recs);
        await ReplanAllAsync();
        if (notes.Count > 0) _vm.Status += "  " + string.Join("  ", notes);
    }

    /// <summary>Plans are only reads, so they run off the UI thread. Older results are dropped.</summary>
    Task ReplanAllAsync() => ReplanRowsAsync(_vm.Games.ToList(), all: true);

    async Task ReplanRowsAsync(IReadOnlyList<GameRow> rows, bool all = false)
    {
        if (_vm.Hardware is not { } hw) return;
        int version = all ? ++_planVersion : _planVersion;
        var jobs = rows.Select(r => (row: r, step: (QualityStep)r.Step)).ToList();
        var rules = _vm.Rules;
        var results = await Task.Run(() =>
        {
            var images = ProcessGuard.RunningImages();
            return jobs.Select(j =>
            {
                GamePlan plan;
                try { plan = Planner.Plan(j.row.Game, hw, j.step, rules); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    plan = new GamePlan { Game = j.row.Game, Step = j.step, Status = PlanStatus.Skipped };
                    plan.Skipped.Add("Could not read settings: " + e.Message);
                }
                if (plan.HasChanges && ProcessGuard.RunningFrom(j.row.Game.InstallDir, images) is not null) plan.Status = PlanStatus.Running;
                return (j.row, j.step, plan);
            }).ToList();
        });
        if (version != _planVersion) return;
        foreach (var (row, step, plan) in results)
            if (row.Step == (int)step) row.Plan = plan;
        _view?.Refresh();
        int ready = _vm.Games.Count(g => g.CanOptimize);
        if (!_vm.Busy) _vm.Status = ready == 0 ? "Every supported game is already set for its slider step." : $"{ready} game(s) can be optimized.";
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
                Text = OffsetTicks[i],
                TextAlignment = TextAlignment.Center,
                FontSize = 12,
                Foreground = mid ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted"),
                FontWeight = mid ? FontWeights.SemiBold : FontWeights.Normal,
            };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double x = thumbHalf + i * (w - 2 * thumbHalf) / (OffsetTicks.Length - 1);
            Canvas.SetLeft(tb, Math.Clamp(x - tb.DesiredSize.Width / 2, -8, w - tb.DesiredSize.Width + 8));
            GlobalTicks.Children.Add(tb);
        }
    }

    void UseRecommended_Click(object sender, RoutedEventArgs e) => _vm.GlobalOffset = 0;

    void ResetRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is GameRow row) _vm.ResetRow(row);
    }

    void Filter_Changed(object sender, RoutedEventArgs e) => _view?.Refresh();

    async void Rescan_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    async void PreviewRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GameRow row) return;
        await ReplanRowsAsync(new[] { row });
        if (row.Plan is { } plan) await ShowPreviewAndMaybeApply(new[] { plan });
    }

    async void ApplyRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GameRow row) return;
        await ReplanRowsAsync(new[] { row });
        if (row.Plan is { Status: PlanStatus.Ready } plan) await ApplyAsync(new[] { plan }, $"{row.Name}: {plan.Step.Label()}");
        else MessageBox.Show(this, row.StatusText, row.Name);
    }

    async void OptimizeAll_Click(object sender, RoutedEventArgs e)
    {
        _vm.Busy = true;
        _vm.Status = "Checking every game...";
        await ReplanAllAsync();
        _vm.Busy = false;
        var plans = _vm.Games.Select(g => g.Plan).OfType<GamePlan>()
            .Where(p => p.Status is PlanStatus.Ready or PlanStatus.Running).ToList();
        if (plans.Count == 0)
        {
            MessageBox.Show(this, "Every supported game is already set for its slider step.", "Nothing to change");
            return;
        }
        await ShowPreviewAndMaybeApply(plans);
    }

    async Task ShowPreviewAndMaybeApply(IReadOnlyList<GamePlan> plans)
    {
        var dlg = new PreviewWindow(plans) { Owner = this };
        if (dlg.ShowDialog() == true)
            await ApplyAsync(plans.Where(p => p.Status == PlanStatus.Ready).ToList(),
                             plans.Count == 1 ? $"{plans[0].Game.Name}: {plans[0].Step.Label()}" : $"Optimize all ({plans.Count} games)");
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
        if (res.RunId is not null) lines.Add($"\nBackups are in run {res.RunId}. \"Restore last run\" puts everything back.");
        MessageBox.Show(this, string.Join("\n", lines), "Optimize", MessageBoxButton.OK,
                        res.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        await ReplanAllAsync();
    }

    async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var run = Applier.Runs().FirstOrDefault();
        if (run is null) { MessageBox.Show(this, "There are no backups yet.", "Restore"); return; }
        var games = string.Join(", ", run.Entries.Select(x => x.GameName).Distinct());
        var ok = MessageBox.Show(this, $"Put back the settings from before the run on {run.Created:g}?\n\n{run.Summary}\nGames: {games}",
                                 "Restore last run", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;
        var res = await Task.Run(() => Applier.Restore(run));
        var lines = res.Applied.Select(a => "Restored: " + a)
            .Concat(res.Refused.Select(r => "Not restored: " + r))
            .Concat(res.Errors.Select(x => "Note: " + x));
        MessageBox.Show(this, string.Join("\n", lines), "Restore");
        await ReplanAllAsync();
    }
}
