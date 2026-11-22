using System.Windows;
using System.Windows.Controls;
using OptiGame.Core.Hardware;
using OptiGame.Core.Optimize;
using OptiGame.Core.Safety;

namespace OptiGame.App;

/// <summary>
/// Every change, grouped by game, with a tick box each. Unticking leaves a change out of this run;
/// "Always keep" saves it for the game. Either way the game is planned again, so what is shown is
/// exactly what will be written.
/// </summary>
public sealed class PreviewWindow : Window
{
    readonly OptimizerSession _s;
    readonly string _scope;
    readonly List<GamePlan> _plans;
    readonly Dictionary<string, HashSet<string>> _temp = new();
    readonly StackPanel _list = new();
    readonly TextBlock _summary = Ui.Text("", 13, bold: true);
    readonly Button _apply;
    IReadOnlyList<string> _images = ProcessGuard.RunningImages();

    public List<GamePlan> FinalPlans { get; private set; } = new();

    public PreviewWindow(OptimizerSession s, List<GamePlan> plans, string scope, Window owner)
    {
        _s = s; _scope = scope; _plans = plans.ToList();
        Title = "Preview changes"; Width = 900; Height = 700; MinWidth = 560; MinHeight = 360;
        Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI"); FontSize = 13;
        Ui.Style(this);

        _apply = Ui.Button("Apply", (_, _) => Done(), primary: true);
        _apply.IsDefault = true;
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var buttons = Ui.Footer(Ui.Button("Tick everything again", (_, _) => { _temp.Clear(); ReplanAll(); }),
                                new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) }, _apply);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(Ui.Text("Each file and registry value is backed up first. Running games are skipped. A frame cap limits how fast a game renders; it does not make the hardware reach that rate.", 12, brush: "Muted"));
        var body = Ui.Stack(_summary, _list);
        Content = Ui.Frame(body, footer);
        Render();
    }

    void Done()
    {
        // Plans are rebuilt one last time so nothing stale is written.
        _images = ProcessGuard.RunningImages();
        FinalPlans = _plans.Select(p => _s.PlanFor(p.Game, _temp.GetValueOrDefault(p.Game.Id), null, _images))
                           .Where(p => p.Status == PlanStatus.Ready).ToList();
        DialogResult = true;
    }

    void Replan(OptiGame.Core.Library.GameInfo g)
    {
        int i = _plans.FindIndex(p => p.Game.Id == g.Id);
        _plans[i] = _s.PlanFor(g, _temp.GetValueOrDefault(g.Id), null, _images);
        Render();
    }

    void ReplanAll()
    {
        for (int i = 0; i < _plans.Count; i++) _plans[i] = _s.PlanFor(_plans[i].Game, _temp.GetValueOrDefault(_plans[i].Game.Id), null, _images);
        Render();
    }

    void Toggle(GamePlan p, string id, bool include)
    {
        var set = _temp.TryGetValue(p.Game.Id, out var x) ? x : _temp[p.Game.Id] = new();
        if (include) set.Remove(id); else set.Add(id);
        Replan(p.Game);
    }

    void AlwaysKeep(GamePlan p, string id, bool keep)
    {
        var g = _s.Prefs.Game(p.Game.Id);
        if (keep && !g.Excluded.Contains(id)) g.Excluded.Add(id);
        if (!keep) g.Excluded.Remove(id);
        _s.Prefs.Save();
        _temp.GetValueOrDefault(p.Game.Id)?.Remove(id);
        Replan(p.Game);
    }

    void Render()
    {
        _list.Children.Clear();
        int ready = _plans.Count(p => p.Status == PlanStatus.Ready), changes = _plans.Where(p => p.Status == PlanStatus.Ready).Sum(p => p.ChangeCount);
        _summary.Text = $"Preview of {_scope}: {ready} game(s) with {changes} change(s) to write";
        _summary.Margin = new Thickness(0, 0, 0, 10);
        _apply.Content = ready == 0 ? "Nothing to apply" : $"Apply {changes} change(s) to {ready} game(s)";
        _apply.IsEnabled = ready > 0;

        foreach (var p in _plans)
        {
            var box = new StackPanel();
            var req = p.Request;
            box.Children.Add(Ui.Text($"{p.Game.Name}  ·  {p.Step.Label()}  ·  {p.StatusText}", 14, bold: true, brush: p.Status == PlanStatus.Running ? "Warn" : "Text"));
            if (req is not null)
                box.Children.Add(Ui.Text($"Profile {req.ProfileName} · {req.StepSource} · display {req.Display.ActiveModeText} · frame cap {req.FpsText} · " +
                                         $"resolution {(req.Resolution is var (w, h) ? $"{w}x{h}" : "kept")} · VSync {req.VSync.ToString().ToLowerInvariant()}", 11, brush: "Muted"));
            foreach (var f in p.Files)
            {
                box.Children.Add(Ui.Text(f.Path, 11, brush: "Muted", top: 6));
                foreach (var c in f.Changes) box.Children.Add(ChangeRow(p, c.Id, c.ToString(), included: true, why: null));
            }
            if (p.Registry.Count > 0)
            {
                box.Children.Add(Ui.Text(@"HKEY_CURRENT_USER\" + p.Registry[0].SubKey, 11, brush: "Muted", top: 6));
                foreach (var r in p.Registry) box.Children.Add(ChangeRow(p, r.Id, r.Display, included: true, why: null));
            }
            if (p.Excluded.Count > 0)
            {
                box.Children.Add(Ui.Text("Left as they are:", 11, brush: "Muted", top: 6));
                foreach (var x in p.Excluded) box.Children.Add(ChangeRow(p, x.Id, $"{x.Setting}: {x.Old ?? "(not set)"} (would be {x.New})", included: false, why: x.Why));
            }
            foreach (var n in p.NotControlled) box.Children.Add(Ui.Text("Not controlled: " + n, 12, brush: "Muted", top: 2));
            foreach (var s in p.Skipped) box.Children.Add(Ui.Text("Left alone: " + s, 12, brush: "Muted", top: 2));
            foreach (var w in p.Warnings) box.Children.Add(Ui.Text("Warning: " + w, 12, brush: "Warn", top: 2));
            _list.Children.Add(Ui.Card(box));
        }
    }

    UIElement ChangeRow(GamePlan p, string id, string text, bool included, string? why)
    {
        var row = new DockPanel { Margin = new Thickness(8, 2, 0, 0) };
        bool profileKept = why is not null && why.StartsWith("kept by profile");
        bool savedKept = why == "kept for this game";
        var cb = new CheckBox
        {
            IsChecked = included, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = !profileKept && !savedKept, ToolTip = included ? "Untick to leave this setting as it is, this time only" : why,
        };
        System.Windows.Automation.AutomationProperties.SetName(cb, text);
        // Checked/Unchecked fire for mouse, keyboard and assistive tools alike (Click does not for the last).
        cb.Checked += (_, _) => Toggle(p, id, true);
        cb.Unchecked += (_, _) => Toggle(p, id, false);
        row.Children.Add(cb);
        var link = new Button { Style = (Style)Application.Current.FindResource("Link"), Margin = new Thickness(8, 0, 0, 0) };
        if (savedKept)
        {
            link.Content = new TextBlock { Text = "Stop keeping", Foreground = Ui.B("Custom"), FontSize = 11 };
            link.Click += (_, _) => AlwaysKeep(p, id, false);
        }
        else if (!profileKept)
        {
            link.Content = new TextBlock { Text = "Always keep", Foreground = Ui.B("Custom"), FontSize = 11 };
            link.ToolTip = "Never change this setting for this game (saved; undo it here or in the game's settings)";
            link.Click += (_, _) => AlwaysKeep(p, id, true);
        }
        if (link.Content is not null) { DockPanel.SetDock(link, Dock.Right); row.Children.Add(link); }
        row.Children.Add(Ui.Text(text + (why is null ? "" : $"   ({why})"), 12, brush: included ? "Text" : "Muted", mono: true));
        return row;
    }
}
