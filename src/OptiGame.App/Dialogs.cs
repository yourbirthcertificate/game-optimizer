using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OptiGame.Core.Bench;
using OptiGame.Core.Hardware;
using OptiGame.Core.Library;
using OptiGame.Core.Optimize;
using OptiGame.Core.Rules;
using OptiGame.Core.Safety;

namespace OptiGame.App;

/// <summary>Base for the code-built dialogs: themed window with scrolling content and a footer.</summary>
public abstract class Dialog : Window
{
    protected Dialog(string title, double w, double h, Window owner)
    {
        Title = title; Width = w; Height = h; MinWidth = 520; MinHeight = 360; Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"); FontSize = 13;
        Ui.Style(this);
    }
    protected static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
}

// ---------------------------------------------------------------------- history and restore

/// <summary>Every run, its changes, and restores with a preview, conflicts and a choice of mode.</summary>
public sealed class HistoryWindow : Dialog
{
    readonly ListBox _runs = new() { Width = 300, Margin = new Thickness(0, 0, 12, 0) };
    readonly StackPanel _detail = new();
    readonly ComboBox _game = Ui.Combo(Array.Empty<string>(), 0, 220);
    readonly RadioButton _undo = new() { Content = "Undo the optimizer's changes only (keeps later edits)", IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
    readonly RadioButton _full = new() { Content = "Put the full backup back" };
    readonly Button _restore;
    RestorePlan? _plan;

    public HistoryWindow(Window owner) : base("History and restore", 1050, 720, owner)
    {
        _restore = Ui.Button("Restore", (_, _) => DoRestore(), primary: true);
        _restore.IsEnabled = false;
        _runs.SelectionChanged += (_, _) => ShowRun();
        _game.SelectionChanged += (_, _) => Invalidate();
        _undo.Checked += (_, _) => Invalidate();
        _full.Checked += (_, _) => Invalidate();
        var right = new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var grid = new DockPanel { Margin = new Thickness(16) };
        var footer = Ui.Footer(new Button { Content = "Close", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) }, Ui.Button("Preview restore", (_, _) => PreviewRestore()), _restore);
        DockPanel.SetDock(footer, Dock.Bottom);
        grid.Children.Add(footer);
        var options = Ui.Row(Ui.Text("Game ", brush: "Muted"), _game, _undo, _full);
        DockPanel.SetDock(options, Dock.Bottom);
        grid.Children.Add(options);
        DockPanel.SetDock(_runs, Dock.Left);
        grid.Children.Add(_runs);
        grid.Children.Add(right);
        Content = grid;
        LoadRuns();
    }

    void LoadRuns()
    {
        _runs.Items.Clear();
        foreach (var r in Applier.Runs())
            _runs.Items.Add(new ListBoxItem
            {
                Tag = r, Content = Ui.Stack(Ui.Text($"{r.Created:g}", bold: true), Ui.Text((r.Kind == "restore" ? "Before a restore: " : "") + r.Summary, 12, brush: "Muted"),
                                            Ui.Text(string.Join(", ", r.GameNames), 11, brush: "Muted")),
            });
        if (_runs.Items.Count > 0) _runs.SelectedIndex = 0;
        else _detail.Children.Add(Ui.Text("No runs yet. Every optimize and restore is listed here.", brush: "Muted"));
    }

    BackupRun? Run => (_runs.SelectedItem as ListBoxItem)?.Tag as BackupRun;

    void ShowRun()
    {
        _detail.Children.Clear();
        _plan = null; _restore.IsEnabled = false;
        if (Run is not { } run) return;
        _game.Items.Clear();
        _game.Items.Add("Every game in this run");
        foreach (var g in run.GameNames) _game.Items.Add(g);
        _game.SelectedIndex = 0;
        _detail.Children.Add(Ui.Text($"Run {run.Id}: {run.Summary}", 14, bold: true));
        foreach (var e in run.Entries)
        {
            var box = Ui.Stack(Ui.Text($"{e.GameName}: {(e.Kind == "file" ? e.Path : $@"HKCU\{e.SubKey}\{e.ValueName}")}", 12, brush: "Muted"));
            foreach (var c in e.Changes) box.Children.Add(Ui.Text($"   {c.Setting}: {c.OldRaw ?? "(not set)"} -> {c.NewRaw}", 12, mono: true));
            if (e.Kind == "registry") box.Children.Add(Ui.Text($"   {e.Setting}: {(e.ValueExisted ? e.ValueData?.ToString() : "(not set)")} -> {e.WrittenData?.ToString() ?? "?"}", 12, mono: true));
            if (e.Kind == "file" && e.Changes.Count == 0) box.Children.Add(Ui.Text("   (older run: individual keys were not recorded; only a full restore is possible once the file changes)", 12, brush: "Muted"));
            _detail.Children.Add(Ui.Card(box, 6));
        }
    }

    void Invalidate() { _plan = null; _restore.IsEnabled = false; }

    string? GameFilter => _game.SelectedIndex > 0 ? _game.SelectedItem as string : null;
    RestoreMode Mode => _full.IsChecked == true ? RestoreMode.FullBackup : RestoreMode.UndoChanges;

    void PreviewRestore()
    {
        if (Run is not { } run) return;
        _plan = Applier.PlanRestore(run, GameFilter);
        _detail.Children.Clear();
        _detail.Children.Add(Ui.Text(Mode == RestoreMode.UndoChanges
            ? "Restore preview: only the keys the optimizer changed are put back. Keys changed again since are conflicts and stay as they are."
            : "Restore preview: each file or value is replaced by its backup. Anything changed since is replaced too (it is saved first, so this can be undone).", 13, bold: true));
        int work = 0;
        foreach (var item in _plan.Items)
        {
            var box = Ui.Stack(Ui.Text($"{item.Entry.GameName}: {item.Title}{(item.ChangedSinceOptimize ? "  (changed since the optimize)" : "")}", 12, brush: "Muted"));
            if (Mode == RestoreMode.FullBackup)
            {
                bool ok = item.Entry.Kind != "file" || item.BackupBytes is not null;
                box.Children.Add(Ui.Text(ok ? "   whole backup put back" : "   cannot restore: " + item.WhyNoUndo, 12, brush: ok ? "Text" : "Warn"));
                if (ok) work++;
            }
            else if (!item.CanUndoChanges) box.Children.Add(Ui.Text($"   left as it is: {item.WhyNoUndo}. Choose the full backup to replace it.", 12, brush: "Warn"));
            else
            {
                if (item.Keys.Count == 0) box.Children.Add(Ui.Text("   unchanged since the optimize: the backup is put back exactly", 12));
                foreach (var k in item.Keys)
                    box.Children.Add(Ui.Text("   " + k.Text, 12, mono: true, brush: k.State == KeyState.Conflict ? "Warn" : k.State == KeyState.AlreadyOriginal ? "Muted" : "Text"));
                work += item.Keys.Count(k => k.State == KeyState.Revert) + (item.Keys.Count == 0 ? 1 : 0);
            }
            _detail.Children.Add(Ui.Card(box, 6));
        }
        if (Mode == RestoreMode.UndoChanges && _plan.AnyConflicts)
            _detail.Children.Add(Ui.Text("Conflicts are never overwritten in this mode. To replace them, choose the full backup and preview again.", 12, brush: "Warn"));
        _restore.IsEnabled = work > 0;
        _restore.Content = work > 0 ? "Restore" : "Nothing to restore";
    }

    void DoRestore()
    {
        if (_plan is null) return;
        var res = Applier.Restore(_plan, Mode);
        var lines = res.Applied.Select(a => "Restored: " + a).Concat(res.Refused.Select(r => "Not done: " + r)).Concat(res.Errors.Select(e => "Problem: " + e)).ToList();
        if (res.RunId is not null) lines.Add($"\nThe state before this restore is saved as run {res.RunId}; restoring that run undoes this.");
        if (lines.Count == 0) lines.Add("Nothing needed restoring.");
        Ui.Info(this, string.Join("\n", lines), "Restore");
        LoadRuns();
    }
}

// ---------------------------------------------------------------------- profiles

/// <summary>Create, duplicate, rename, edit and delete profiles. Built-in ones are read-only.</summary>
public sealed class ProfilesWindow : Dialog
{
    readonly OptimizerSession _s;
    readonly ListBox _list = new() { Width = 220, Margin = new Thickness(0, 0, 12, 0) };
    readonly StackPanel _edit = new();
    bool _loading;

    public ProfilesWindow(OptimizerSession s, Window owner) : base("Profiles", 900, 640, owner)
    {
        _s = s;
        _list.SelectionChanged += (_, _) => ShowProfile();
        var root = new DockPanel { Margin = new Thickness(16) };
        var footer = Ui.Footer(Ui.Button("New", (_, _) => New(null)), Ui.Button("Duplicate", (_, _) => New(Selected)),
                               Ui.Button("Delete", (_, _) => Delete()), Ui.Button("Use as default", (_, _) => MakeDefault()),
                               new Button { Content = "Close", IsCancel = true });
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        DockPanel.SetDock(_list, Dock.Left);
        root.Children.Add(_list);
        root.Children.Add(new ScrollViewer { Content = _edit, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Fill(_s.Prefs.DefaultProfileId);
    }

    Profile? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as Profile;

    void Fill(string? select)
    {
        _list.Items.Clear();
        foreach (var p in _s.Prefs.AllProfiles)
        {
            var item = new ListBoxItem { Tag = p, Content = Ui.Stack(Ui.Text(p.Name, bold: true), Ui.Text((p.BuiltIn ? "built-in" : "yours") + (p.Id == _s.Prefs.DefaultProfileId ? " · default" : ""), 11, brush: "Muted")) };
            _list.Items.Add(item);
            if (p.Id == select) _list.SelectedItem = item;
        }
    }

    void New(Profile? from)
    {
        var name = from is null ? "My profile" : from.Name + " copy";
        var p = _s.Prefs.CreateProfile(name, from);
        _s.Prefs.Save();
        Fill(p.Id);
    }

    void Delete()
    {
        if (Selected is not { BuiltIn: false } p) { Ui.Info(this, "Built-in profiles cannot be deleted."); return; }
        if (!Ui.Confirm(this, $"Delete {p.Name}? Games using it go back to the default profile.", "Delete profile")) return;
        _s.Prefs.DeleteProfile(p.Id);
        _s.Prefs.Save();
        Fill(_s.Prefs.DefaultProfileId);
    }

    void MakeDefault()
    {
        if (Selected is not { } p) return;
        _s.Prefs.DefaultProfileId = p.Id;
        _s.Prefs.Save();
        Fill(p.Id);
    }

    void Save() { if (!_loading) _s.Prefs.Save(); ShowSummary(); }

    readonly TextBlock _summary = Ui.Text("", 12, brush: "Muted", top: 10);

    void ShowSummary()
    {
        if (Selected is { } p)
            _summary.Text = string.Join("\n", SettingsParser.Describe(p, _s.Hw.Primary)) +
                            $"\nUsed by {_s.Prefs.Games.Count(g => g.Value.ProfileId == p.Id)} game(s){(p.Id == _s.Prefs.DefaultProfileId ? " and every game without its own profile" : "")}.";
    }

    void ShowProfile()
    {
        _edit.Children.Clear();
        if (Selected is not { } p) return;
        _loading = true;
        bool ro = p.BuiltIn;
        var name = Ui.Box(p.Name, 260);
        name.IsEnabled = !ro;
        name.LostFocus += (_, _) => { if (!ro && name.Text.Trim().Length > 0) { _s.Prefs.RenameProfile(p.Id, name.Text); Save(); } };
        _edit.Children.Add(Ui.Row(Ui.Text("Name ", brush: "Muted"), name));
        if (p.Description.Length > 0) _edit.Children.Add(Ui.Text(p.Description, 12, brush: "Muted", top: 6));
        if (ro) _edit.Children.Add(Ui.Text("Built-in profiles cannot be changed. Duplicate one to make an editable copy.", 12, brush: "Warn", top: 6));

        string[] qLabels = { "Recommended -3", "Recommended -2", "Recommended -1", "Recommended", "Recommended +1", "Recommended +2", "Recommended +3",
                             "Always Low", "Always Medium", "Always Balanced", "Always High", "Always Very high", "Always Ultra" };
        var q = p.Quality ?? new QualityPref();
        var quality = Ui.Combo(qLabels, q.Mode == QualityMode.Fixed ? 7 + (int)q.Step : q.Offset + 3, 200);
        quality.SelectionChanged += (_, _) =>
        {
            int i = quality.SelectedIndex;
            p.Quality = i < 7 ? new QualityPref { Mode = QualityMode.Offset, Offset = i - 3 } : new QualityPref { Mode = QualityMode.Fixed, Step = (QualityStep)(i - 7) };
            Save();
        };

        var fpsKeys = new[] { "keep", "auto", "uncapped", "30", "60", "90", "120", "144", "custom" };
        var fpsLabels = new[] { "Keep the game's", $"Automatic ({_s.Hw.Primary.AutoFpsCap} fps on the main display)", "No cap, where supported", "30 fps", "60 fps", "90 fps", "120 fps", "144 fps", "Custom" };
        var f = p.Fps ?? new FpsPolicy();
        int fi = f.Mode switch { FpsMode.Keep => 0, FpsMode.Auto => 1, FpsMode.Uncapped => 2, _ => Array.IndexOf(fpsKeys, f.Value?.ToString(Inv) ?? "") is var k and > 2 ? k : 8 };
        var fps = Ui.Combo(fpsLabels, fi, 300);
        var custom = Ui.Box(f.Mode == FpsMode.Custom ? f.Value?.ToString("0.##", Inv) ?? "" : "", 60);
        void SetFps()
        {
            var key = fpsKeys[fps.SelectedIndex];
            if (key == "custom") { if (double.TryParse(custom.Text, NumberStyles.Float, Inv, out var v) && v is >= 10 and <= 1000) p.Fps = new FpsPolicy { Mode = FpsMode.Custom, Value = v }; }
            else SettingsParser.Apply(p, "fps=" + key);
            Save();
        }
        fps.SelectionChanged += (_, _) => SetFps();
        custom.LostFocus += (_, _) => SetFps();

        var resKeys = new[] { "keep", "native", "current" };
        var res = Ui.Combo(new[] { "Keep the game's", "Display's native", "Display's current" }, Array.IndexOf(resKeys, (p.Resolution?.Mode ?? ResolutionMode.Native).ToString().ToLowerInvariant()) is var ri and >= 0 ? ri : 1, 200);
        res.SelectionChanged += (_, _) => { SettingsParser.Apply(p, "resolution=" + resKeys[res.SelectedIndex]); Save(); };
        var modes = Enum.GetValues<DisplayModeChoice>();
        var mode = Ui.Combo(new[] { "Keep the game's", "Engine's usual fullscreen", "Exclusive fullscreen", "Borderless", "Windowed" }, (int)(p.DisplayMode ?? DisplayModeChoice.Recommended), 200);
        mode.SelectionChanged += (_, _) => { p.DisplayMode = modes[mode.SelectedIndex]; Save(); };
        var vsync = Ui.Combo(new[] { "Keep the game's", "On", "Off" }, (int)(p.VSync ?? VSyncChoice.Off), 160);
        vsync.SelectionChanged += (_, _) => { p.VSync = (VSyncChoice)vsync.SelectedIndex; Save(); };
        var displays = new List<(string label, string? key)> { ("Gaming display", null) };
        displays.AddRange(_s.Hw.Displays.Select(d => (d.Label, (string?)d.DeviceName)));
        var disp = Ui.Combo(displays.Select(d => d.label), Math.Max(0, displays.FindIndex(d => d.key == p.Display)), 300);
        disp.SelectionChanged += (_, _) => { p.Display = displays[disp.SelectedIndex].key; Save(); };

        foreach (var c in new FrameworkElement[] { quality, fps, custom, res, mode, vsync, disp }) c.IsEnabled = !ro;
        _edit.Children.Add(Ui.Row(Ui.Text("Quality ", brush: "Muted"), quality));
        _edit.Children.Add(Ui.Row(Ui.Text("Frame cap ", brush: "Muted"), fps, custom, Ui.Text("fps", brush: "Muted")));
        _edit.Children.Add(Ui.Row(Ui.Text("Resolution ", brush: "Muted"), res, Ui.Text("Display mode ", brush: "Muted"), mode));
        _edit.Children.Add(Ui.Row(Ui.Text("VSync ", brush: "Muted"), vsync, Ui.Text("Display ", brush: "Muted"), disp));
        var keepRow = Ui.Row(Ui.Text("Always keep: ", brush: "Muted"));
        foreach (var cat in Categories.Keepable)
        {
            var cb = new CheckBox { Content = Categories.Label(cat), IsChecked = p.Preserve?.Contains(cat) == true, Margin = new Thickness(0, 0, 10, 0), IsEnabled = !ro };
            void OnKeep()
            {
                p.Preserve ??= new();
                if (cb.IsChecked == true) { if (!p.Preserve.Contains(cat)) p.Preserve.Add(cat); } else p.Preserve.Remove(cat);
                Save();
            }
            cb.Checked += (_, _) => OnKeep();
            cb.Unchecked += (_, _) => OnKeep();
            keepRow.Children.Add(cb);
        }
        _edit.Children.Add(keepRow);
        _edit.Children.Add(Ui.Text("A profile lists settings. It does not promise a frame rate, temperature or noise level; those depend on the game and the hardware.", 12, brush: "Muted", top: 10));
        _edit.Children.Add(_summary);
        _loading = false;
        ShowSummary();
    }
}

// ---------------------------------------------------------------------- manually added folders

public sealed class FoldersWindow : Dialog
{
    readonly Prefs _prefs;
    readonly StackPanel _list = new();
    bool _changed;

    public FoldersWindow(Prefs prefs, Window owner) : base("Added game folders", 760, 460, owner)
    {
        _prefs = prefs;
        Content = Ui.Frame(Ui.Stack(Ui.Text("Folders you added by hand. Removing one only takes it off this list; no files are deleted.", 12, brush: "Muted"), _list),
            Ui.Footer(Ui.Button("Add folder...", (_, _) => Add()), Ui.Button("Done", (_, _) => DialogResult = _changed, primary: true)));
        Render();
    }

    void Render()
    {
        _list.Children.Clear();
        if (_prefs.ManualGames.Count == 0) _list.Children.Add(Ui.Text("None yet.", brush: "Muted", top: 8));
        foreach (var m in _prefs.ManualGames.ToList())
        {
            var name = Ui.Box(m.Name ?? "", 200);
            name.LostFocus += (_, _) => { m.Name = name.Text.Trim().Length == 0 ? null : name.Text.Trim(); _prefs.Save(); _changed = true; };
            var row = Ui.Row(Ui.Button("Remove", (_, _) => { _prefs.ManualGames.Remove(m); _prefs.Save(); _changed = true; Render(); }),
                             name, Ui.Text(m.Path + (Directory.Exists(m.Path) ? "" : "  (not reachable)"), 12, brush: Directory.Exists(m.Path) ? "Text" : "Warn"));
            _list.Children.Add(row);
        }
    }

    void Add()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a game's install folder" };
        if (dlg.ShowDialog(this) != true) return;
        if (_prefs.ManualGames.Any(m => LibraryScanner.NormalizeDir(m.Path) == LibraryScanner.NormalizeDir(dlg.FolderName))) return;
        _prefs.ManualGames.Add(new ManualGame { Path = dlg.FolderName });
        _prefs.Save();
        _changed = true;
        Render();
    }
}

// ---------------------------------------------------------------------- import

public sealed class ImportWindow : Dialog
{
    readonly OptimizerSession _s;
    readonly string _json;
    readonly RadioButton _merge = new() { Content = "Merge with mine", IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
    readonly RadioButton _replace = new() { Content = "Replace mine", Margin = new Thickness(0, 0, 16, 0) };
    readonly CheckBox _take = new() { Content = "When both differ, use the imported one" };
    readonly StackPanel _out = new();
    readonly Dictionary<string, TextBox> _maps = new();
    readonly Button _apply;
    ImportPlan? _plan;

    public ImportWindow(OptimizerSession s, string json, Window owner) : base("Import preferences", 860, 640, owner)
    {
        _s = s; _json = json;
        _apply = Ui.Button("Import", (_, _) => Apply(), primary: true);
        foreach (var c in new UIElement[] { _merge, _replace, _take }) (c as System.Windows.Controls.Primitives.ButtonBase)!.Click += (_, _) => Analyze();
        Content = Ui.Frame(Ui.Stack(Ui.Row(_merge, _replace, _take), _out),
            Ui.Footer(Ui.Button("Check again", (_, _) => Analyze()), new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) }, _apply));
        Analyze();
    }

    void Analyze()
    {
        var maps = _maps.Where(kv => kv.Value.Text.Trim().Length > 0).ToDictionary(kv => kv.Key, kv => kv.Value.Text.Trim(), StringComparer.OrdinalIgnoreCase);
        _plan = PrefsTransfer.Analyze(_json, _s.Prefs, _replace.IsChecked == true ? ImportMode.Replace : ImportMode.Merge,
            _take.IsChecked == true ? ConflictPolicy.TakeImported : ConflictPolicy.KeepMine, maps, displayExists: d => _s.Hw.FindDisplay(d) is not null);
        _out.Children.Clear();
        void Section(string title, IEnumerable<string> items, string brush = "Text")
        {
            var l = items.ToList();
            if (l.Count == 0) return;
            _out.Children.Add(Ui.Text(title, 13, bold: true, top: 10));
            foreach (var i in l) _out.Children.Add(Ui.Text("   " + i, 12, brush: brush));
        }
        Section("Problems", _plan.Errors, "Warn");
        Section("Added", _plan.Additions);
        Section("Replaced", _plan.Replacements);
        Section("Conflicts", _plan.Conflicts, "Warn");
        Section("Skipped", _plan.Skipped, "Muted");
        if (_plan.NeedsRemap.Count > 0)
        {
            _out.Children.Add(Ui.Text("Folders that do not exist on this PC. Give their location here, or leave them out by removing them from the file:", 13, bold: true, top: 10));
            foreach (var p in _plan.NeedsRemap)
            {
                if (!_maps.TryGetValue(p, out var box)) _maps[p] = box = Ui.Box("", 360);
                _out.Children.Add(Ui.Row(Ui.Text(p + "  ->  ", 12, wrap: false), box));
            }
        }
        if (_plan.Additions.Count + _plan.Replacements.Count + _plan.Conflicts.Count + _plan.Errors.Count == 0)
            _out.Children.Add(Ui.Text("Nothing new in this file.", brush: "Muted", top: 10));
        _apply.IsEnabled = _plan.CanApply;
    }

    void Apply()
    {
        if (_plan is not { CanApply: true }) return;
        PrefsTransfer.Apply(_plan);
        Ui.Info(this, "Imported. Your previous preferences file was kept as prefs.json.before-import.", "Import");
        DialogResult = true;
    }
}

// ---------------------------------------------------------------------- benchmarks

/// <summary>Import frame-time captures, see the numbers and a graph, and compare two runs.</summary>
public sealed class BenchmarksWindow : Dialog
{
    readonly OptimizerSession _s;
    readonly ComboBox _game;
    readonly ComboBox _label = Ui.Combo(new[] { "before", "after", "other" }, 0, 100);
    readonly TextBox _notes = Ui.Box("", 280);
    readonly ListBox _results = new() { Height = 170, Margin = new Thickness(0, 6, 0, 0) };
    readonly StackPanel _detail = new();
    readonly ComboBox _a = Ui.Combo(Array.Empty<string>(), 0, 330), _b = Ui.Combo(Array.Empty<string>(), 0, 330);
    readonly List<GameInfo> _games;
    List<BenchResult> _list = new();

    public BenchmarksWindow(OptimizerSession s, Window owner) : base("Benchmarks", 1000, 760, owner)
    {
        _s = s;
        _games = s.Scan.Games;
        _game = Ui.Combo(_games.Select(g => g.Name), 0, 300);
        _game.SelectionChanged += (_, _) => Reload();
        _results.SelectionChanged += (_, _) => ShowSelected();
        var help = Ui.Text("Import a frame-time CSV: either one column named frametime_ms, or a PresentMon/CapFrameX export (MsBetweenPresents). " +
                           "Average FPS = frames / time. 1% low = 1000 / the 99th-percentile frame time. Results stay on this PC and never change a recommendation.", 12, brush: "Muted");
        var top = Ui.Stack(help,
            Ui.Row(Ui.Text("Game ", brush: "Muted"), _game),
            Ui.Row(Ui.Text("Label ", brush: "Muted"), _label, Ui.Text("Scene or route notes ", brush: "Muted"), _notes, Ui.Button("Import CSV...", (_, _) => Import(), primary: true)),
            _results,
            Ui.Row(Ui.Button("Mark as preferred", (_, _) => Prefer()), Ui.Button("Delete", (_, _) => Delete())),
            Ui.Row(Ui.Text("Compare ", brush: "Muted"), _a, Ui.Text(" with ", brush: "Muted"), _b, Ui.Button("Compare", (_, _) => Compare())),
            _detail);
        Content = Ui.Frame(top, Ui.Footer(new Button { Content = "Close", IsCancel = true }));
        Reload();
    }

    GameInfo? Game => _game.SelectedIndex >= 0 && _game.SelectedIndex < _games.Count ? _games[_game.SelectedIndex] : null;

    void Reload()
    {
        _list = Game is { } g ? BenchmarkStore.All().Where(r => r.GameId == g.Id).ToList() : new();
        _results.Items.Clear();
        _a.Items.Clear(); _b.Items.Clear();
        foreach (var r in _list)
        {
            var text = $"{r.Imported:g}  {r.Label,-7} {r.Summary}{(r.Preferred ? "  [preferred]" : "")}";
            _results.Items.Add(new ListBoxItem { Tag = r, Content = Ui.Text(text, 12, mono: true, wrap: false) });
            _a.Items.Add($"{r.Imported:g} {r.Label}");
            _b.Items.Add($"{r.Imported:g} {r.Label}");
        }
        if (_list.Count > 1) { _a.SelectedIndex = _list.Count - 1; _b.SelectedIndex = 0; }
        _detail.Children.Clear();
        if (_list.Count == 0) _detail.Children.Add(Ui.Text("No results for this game yet.", brush: "Muted", top: 8));
        else _results.SelectedIndex = 0;
    }

    BenchResult? Selected => (_results.SelectedItem as ListBoxItem)?.Tag as BenchResult;

    void Import()
    {
        if (Game is not { } g) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "CSV|*.csv|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        var req = _s.RequestFor(g);
        try
        {
            var r = BenchmarkStore.Import(dlg.FileName, g.Id, g.Name, _label.SelectedItem as string ?? "", _s.HardwareSummary, req.ProfileName, req.Step.Label(),
                req.Resolution is var (w, h) ? $"{w}x{h}" : "kept", req.FpsText, _notes.Text.Trim().Length > 0 ? _notes.Text.Trim() : null);
            Ui.Info(this, $"Imported ({r.SourceFormat}): {r.Summary}.\nThe current profile, step, resolution and cap were recorded with it; check they match what you ran.", "Benchmark");
        }
        catch (Exception e) when (e is FormatException or IOException) { Ui.Info(this, "Not imported: " + e.Message, "Benchmark"); }
        Reload();
    }

    void ShowSelected()
    {
        _detail.Children.Clear();
        if (Selected is not { } r) return;
        var m = r.Metrics;
        _detail.Children.Add(Ui.Text(string.Create(Inv, $"{r.Label}: {m.AverageFps:0.#} fps average · {m.OnePercentLowFps:0.#} fps 1% low (99th percentile {m.P99FrameTimeMs:0.##} ms) · worst frame {m.MaxFrameTimeMs:0.##} ms · {m.Frames} frames in {m.DurationSeconds:0.#} s"), 13, bold: true, top: 8));
        _detail.Children.Add(Ui.Text($"Settings: {r.ProfileName} / {r.Step}, {r.Resolution}, cap {r.FpsCap}. Hardware: {r.Hardware}. From {r.SourceFile} ({r.SourceFormat}).{(r.Notes is null ? "" : " Notes: " + r.Notes)}", 12, brush: "Muted"));
        _detail.Children.Add(Graph(r.FrameTimesMs));
    }

    static UIElement Graph(List<double> ms)
    {
        const double W = 900, H = 160;
        var canvas = new Canvas { Width = W, Height = H, Background = Ui.B("PanelHi"), Margin = new Thickness(0, 8, 0, 0), ClipToBounds = true };
        if (ms.Count < 2) return canvas;
        double max = Math.Max(1, ms.OrderBy(x => x).ElementAt((int)(ms.Count * 0.995)) * 1.2);
        var line = new Polyline { Stroke = Ui.B("Accent"), StrokeThickness = 1 };
        int step = Math.Max(1, ms.Count / (int)W);
        for (int i = 0; i < ms.Count; i += step)
        {
            double v = ms.Skip(i).Take(step).Max();
            line.Points.Add(new Point(i / (double)(ms.Count - 1) * W, H - Math.Min(v, max) / max * H));
        }
        canvas.Children.Add(line);
        foreach (var t in new[] { 16.67, 33.33 }.Where(t => t < max))
        {
            double y = H - t / max * H;
            canvas.Children.Add(new Line { X1 = 0, X2 = W, Y1 = y, Y2 = y, Stroke = Ui.B("Line"), StrokeDashArray = new DoubleCollection { 4, 4 } });
            var lbl = Ui.Text(string.Create(Inv, $"{t:0.#} ms ({1000 / t:0} fps)"), 10, brush: "Muted");
            Canvas.SetLeft(lbl, 4); Canvas.SetTop(lbl, y - 14);
            canvas.Children.Add(lbl);
        }
        return Ui.Stack(Ui.Text("Frame time over the capture (higher = slower frame; spikes are stutters)", 11, brush: "Muted", top: 8), canvas);
    }

    void Prefer()
    {
        if (Selected is not { } r) return;
        BenchmarkStore.SetPreferred(r.Id, !r.Preferred);
        Reload();
    }

    void Delete()
    {
        if (Selected is not { } r || !Ui.Confirm(this, "Delete this result? The original CSV file is not touched.", "Delete")) return;
        BenchmarkStore.Delete(r.Id);
        Reload();
    }

    void Compare()
    {
        if (_a.SelectedIndex < 0 || _b.SelectedIndex < 0 || _a.SelectedIndex == _b.SelectedIndex) { Ui.Info(this, "Pick two different results."); return; }
        var c = BenchmarkStore.Compare(_list[_a.SelectedIndex], _list[_b.SelectedIndex]);
        _detail.Children.Clear();
        _detail.Children.Add(Ui.Text($"{c.A.Label} ({c.A.Imported:g}) -> {c.B.Label} ({c.B.Imported:g})", 13, bold: true, top: 8));
        foreach (var d in c.Differences) _detail.Children.Add(Ui.Text("   " + d, 12, mono: true));
        foreach (var w in c.Warnings) _detail.Children.Add(Ui.Text("   Note: " + w, 12, brush: "Warn"));
        _detail.Children.Add(Ui.Text("Two measured runs; they do not predict other scenes or settings.", 12, brush: "Muted", top: 4));
    }
}
