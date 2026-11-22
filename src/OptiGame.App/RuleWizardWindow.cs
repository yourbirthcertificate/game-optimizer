using System.IO;
using System.Windows;
using System.Windows.Controls;
using OptiGame.Core.Library;
using OptiGame.Core.Optimize;
using OptiGame.Core.Rules;
using OptiGame.Core.Safety;

namespace OptiGame.App;

/// <summary>
/// Guided rule creation: pick a settings file (or registry key), snapshot it, change ONE setting in
/// the game, snapshot again, pick the keys that changed, give their values, check, save, preview.
/// Snapshots only read. Two snapshots show two values, so the wizard never invents the other steps.
/// </summary>
public sealed class RuleWizardWindow : Dialog
{
    readonly OptimizerSession _s;
    readonly List<GameInfo> _games;
    readonly ComboBox _game;
    readonly TextBox _target = Ui.Box("", 560);
    readonly ComboBox _format = Ui.Combo(Snapshot.Formats, 1, 110);
    readonly StackPanel _body = new();
    readonly TextBlock _stepText = Ui.Text("", 13, bold: true, top: 10);
    Snapshot? _before;
    List<(SnapDiff diff, CheckBox pick, TextBox label, ComboBox cat, TextBox values)> _rows = new();
    string? _json;
    GameRule? _rule;

    public RuleWizardWindow(OptimizerSession s, GameInfo? game, Window owner) : base("Rule wizard", 1000, 760, owner)
    {
        _s = s;
        _games = s.Scan.Games;
        _game = Ui.Combo(_games.Select(g => g.Name), Math.Max(0, game is null ? 0 : _games.FindIndex(g => g.Id == game.Id)), 320);
        _game.SelectionChanged += (_, _) => Suggest();
        var browse = Ui.Button("Browse...", (_, _) => Browse());
        var top = Ui.Stack(
            Ui.Text("Create a rule for a game the tool cannot change on its own. Only reading happens until you save the rule, and the rule only writes after a preview.", 12, brush: "Muted"),
            Ui.Row(Ui.Text("Game ", brush: "Muted"), _game),
            Ui.Row(Ui.Text("Settings file or HKCU/Software/... ", brush: "Muted"), _target, browse),
            Ui.Row(Ui.Text("Format ", brush: "Muted"), _format, Ui.Button("1. Take the 'before' snapshot", (_, _) => Before(), primary: true)),
            _stepText, _body);
        Content = Ui.Frame(top, Ui.Footer(new Button { Content = "Close", IsCancel = true }));
        Suggest();
    }

    GameInfo Game => _games[Math.Max(0, _game.SelectedIndex)];

    void Suggest()
    {
        var g = Game;
        string? t = null;
        if (g.Engine == GameEngine.Unity && g.EngineDetail is not null) t = "HKCU/Software/" + g.EngineDetail.Replace('\\', '/');
        else if (g.Engine == GameEngine.Unreal) t = Planner.FindUnrealSettings(g, null);
        if (t is not null) { _target.Text = t; SetFormat(t); }
    }

    void SetFormat(string t)
    {
        var f = Snapshot.GuessFormat(t);
        if (f is not null) _format.SelectedItem = f;
    }

    void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Settings files|*.ini;*.cfg;*.json;*.sav;*.txt;*.vcfg|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        _target.Text = dlg.FileName;
        SetFormat(dlg.FileName);
    }

    string Format => _format.SelectedItem as string ?? "ini";

    void Before()
    {
        try { _before = Snapshot.Capture(Format, _target.Text.Trim()); }
        catch (Exception e) when (e is IOException or FormatException or NotSupportedException or ArgumentException or UnauthorizedAccessException)
        { Ui.Info(this, "Cannot read it: " + e.Message, "Rule wizard"); return; }
        _body.Children.Clear();
        _stepText.Text = $"Before snapshot: {_before.Values.Count} values read.";
        foreach (var u in _before.Unreadable.Take(6)) _body.Children.Add(Ui.Text("Not usable: " + u, 12, brush: "Muted"));
        _body.Children.Add(Ui.Text("2. Start the game, change ONE setting in its own menu, save, and close the game completely. Then:", 13, top: 10));
        _body.Children.Add(Ui.Row(Ui.Button("Take the 'after' snapshot", (_, _) => After(), primary: true)));
    }

    void After()
    {
        if (_before is null) return;
        if (ProcessGuard.RunningFrom(Game.InstallDir) is { } exe) { Ui.Info(this, $"{Path.GetFileName(exe)} is still running. Close the game first.", "Rule wizard"); return; }
        Snapshot after;
        try { after = Snapshot.Capture(Format, _target.Text.Trim()); }
        catch (Exception e) when (e is IOException or FormatException or NotSupportedException or ArgumentException) { Ui.Info(this, "Cannot read it: " + e.Message); return; }
        var diffs = Snapshot.Diff(_before, after);
        _body.Children.Clear();
        _rows.Clear();
        if (diffs.Count == 0)
        {
            _stepText.Text = "Nothing changed in that file. The setting may be saved somewhere else; try another file.";
            return;
        }
        _stepText.Text = $"3. {diffs.Count} difference(s). Tick the keys that belong to the setting you changed, and give their values.";
        _body.Children.Add(Ui.Text("Values: one value is used at every slider step; six values separated by | set Low | Medium | Balanced | High | Very high | Ultra. " +
                                   "Two snapshots only show two values, so the other steps are up to you; nothing is guessed. Values can also be variables such as $fpsCap or $vsync.", 12, brush: "Muted"));
        foreach (var d in diffs.Where(d => d.After is not null))
        {
            var pick = new CheckBox { IsChecked = diffs.Count == 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var label = Ui.Box("", 140);
            var cat = Ui.Combo(new[] { "(no category)" }.Concat(Categories.Keepable), 0, 130);
            var values = Ui.Box(d.After!.Display, 220);
            _rows.Add((d, pick, label, cat, values));
            _body.Children.Add(Ui.Card(Ui.Stack(
                Ui.Row(pick, Ui.Text($"{d.Key} ({d.After.Type}): {d.Before?.Display ?? "(none)"} -> {d.After.Display}", 12, mono: true)),
                Ui.Row(Ui.Text("Label ", brush: "Muted"), label, Ui.Text("Category ", brush: "Muted"), cat, Ui.Text("Values ", brush: "Muted"), values)), 4));
        }
        foreach (var d in diffs.Where(d => d.After is null)) _body.Children.Add(Ui.Text($"{d.Key} was removed; a rule cannot set it.", 12, brush: "Muted"));
        _body.Children.Add(Ui.Row(Ui.Button("4. Check the rule", (_, _) => Build(), primary: true)));
    }

    void Build()
    {
        var keys = _rows.Where(r => r.pick.IsChecked == true).Select(r => new DraftKey
        {
            Key = r.diff.Key, Type = r.diff.After!.Type,
            Label = r.label.Text.Trim().Length > 0 ? r.label.Text.Trim() : null,
            Category = r.cat.SelectedIndex > 0 ? r.cat.SelectedItem as string : null,
            Values = r.values.Text.Split('|').Select(v => v.Trim()).ToList(),
        }).ToList();
        if (keys.Count == 0) { Ui.Info(this, "Tick at least one key."); return; }
        try
        {
            var (json, rule, problems) = RuleBuilder.Build(Game, Format, _target.Text.Trim(), keys);
            _json = json; _rule = rule;
            var box = Ui.Stack(Ui.Text("The rule:", 13, bold: true), Ui.Text(json, 11, mono: true));
            foreach (var p in problems) box.Children.Add(Ui.Text(p.ToString(), 12, brush: p.Severity == RuleProblemSeverity.Error ? "Warn" : "Muted"));
            bool ok = problems.All(p => p.Severity != RuleProblemSeverity.Error);
            if (ok) box.Children.Add(Ui.Row(Ui.Button("5. Save to my rules and preview", (_, _) => Save(), primary: true)));
            _body.Children.Add(Ui.Card(box));
        }
        catch (FormatException e) { Ui.Info(this, "The rule is not valid yet:\n" + e.Message, "Rule wizard"); }
    }

    void Save()
    {
        if (_json is null || _rule is null) return;
        string path;
        try { path = RuleBuilder.Save(_json, _rule.Id); }
        catch (IOException e)
        {
            if (!Ui.Confirm(this, e.Message + "\n\nReplace it?", "Rule exists")) return;
            path = RuleBuilder.Save(_json, _rule.Id, overwrite: true);
        }
        _s.Rescan();
        var g = _s.Find(Game.Id) ?? Game;
        var plan = _s.PlanFor(g, runningImages: ProcessGuard.RunningImages());
        var box = Ui.Stack(Ui.Text($"Saved {path}. Preview with the new rule (nothing was written to the game):", 13, bold: true));
        foreach (var f in plan.Files) foreach (var c in f.Changes) box.Children.Add(Ui.Text("   " + c, 12, mono: true));
        foreach (var r in plan.Registry) box.Children.Add(Ui.Text("   " + r.Display, 12, mono: true));
        foreach (var x in plan.Skipped) box.Children.Add(Ui.Text("   Left alone: " + x, 12, brush: "Muted"));
        if (!plan.HasChanges) box.Children.Add(Ui.Text("   No changes at the current step (the game already has these values).", 12, brush: "Muted"));
        _body.Children.Add(Ui.Card(box));
        Saved = true;
    }

    /// <summary>True once a rule was saved, so the caller rescans.</summary>
    public bool Saved { get; private set; }
}
