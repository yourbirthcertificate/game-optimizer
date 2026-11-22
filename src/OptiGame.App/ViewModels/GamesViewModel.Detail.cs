using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OptiGame.Core;
using OptiGame.Core.Art;
using OptiGame.Core.Hardware;
using OptiGame.Core.Optimize;
using OptiGame.Core.Safety;

namespace OptiGame.App.ViewModels;

/// <summary>One line of the detail panel's "Changes" list: a setting, its old and new value, and a tick box.</summary>
public sealed class ChangeItem : Observable
{
    readonly GameRow _row;
    bool _included;
    public ChangeItem(GameRow row, string id, string text, bool included, string? why)
    {
        _row = row; Id = id; Text = text; _included = included; Why = why;
    }
    public string Id { get; }
    public string Text { get; }
    public string? Why { get; }
    /// <summary>Kept by the profile or saved as "always keep": the tick box cannot change it here.</summary>
    public bool CanToggle => Why is null || !(Why.StartsWith("kept by profile") || Why == "kept for this game");
    public bool SavedKeep => Why == "kept for this game";
    public bool ProfileKeep => Why is not null && Why.StartsWith("kept by profile");
    public string? KeepText => ProfileKeep ? null : SavedKeep ? "Stop keeping" : "Always keep";
    public string? Tip => Why is null ? "Untick to leave this setting as it is, this time only" : Why;
    public bool Included
    {
        get => _included;
        set { if (Set(ref _included, value)) _row.ToggleChange(Id, value); }
    }
}

public sealed partial class GameRow
{
    // ------------------------------------------------------------------ art

    ImageSource? _art;
    ImageSource? _smallIconOverride;
    string _artSource = "";
    int _coverRequest;

    public ImageSource? Art
    {
        get => _art;
        private set
        {
            if (!Set(ref _art, value)) return;
            Raise(nameof(HasArt));
            Raise(nameof(ShowInitials));
            RefreshSmallIcon();
        }
    }

    public ImageSource? SmallIcon => _smallIconOverride ?? Art;
    public bool HasSmallIcon => SmallIcon is not null;
    public bool ShowSmallInitials => SmallIcon is null;
    public Stretch SmallIconStretch => _smallIconOverride is not null || IsIcon ? Stretch.Uniform : Stretch.UniformToFill;
    public bool HasArt => _art is not null;
    public bool ShowInitials => _art is null;
    public string ArtSource { get => _artSource; private set => Set(ref _artSource, value); }

    void RefreshSmallIcon()
    {
        Raise(nameof(SmallIcon));
        Raise(nameof(HasSmallIcon));
        Raise(nameof(ShowSmallInitials));
        Raise(nameof(SmallIconStretch));
    }

    public string Initials
    {
        get
        {
            var words = Name.Split(new[] { ' ', '-', ':', '/', '.', '_' }, StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).ToList();
            return words.Count == 0 ? "?" : string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
        }
    }

    /// <summary>A flat tile colour derived from the name, used behind the initials when there is no image.</summary>
    public Brush Tile
    {
        get
        {
            int h = 17; foreach (var c in Name) h = unchecked(h * 31 + c);
            var palette = new[] { "#1F6F8B", "#5B3FA0", "#B4581F", "#8A1F4D", "#2F7D4F", "#3C3F4A", "#7A5A1E", "#0F6E56", "#993C1D", "#534AB7" };
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(palette[(h & 0x7fffffff) % palette.Length])!;
            b.Freeze();
            return b;
        }
    }

    /// <summary>Loads the art: your image, the cache, Steam's cache, then (if allowed) the online chain, then the exe icon.</summary>
    public async Task LoadArtAsync(bool network, string? sgdbKey)
    {
        var request = ++_coverRequest;
        _smallIconOverride = ReadImage(ArtService.UserPath(Game, ArtSlot.SmallIcon), ArtSlot.SmallIcon);
        RefreshSmallIcon();

        ArtResult? res = null;
        try { res = await ArtService.GetAsync(Game, network, sgdbKey); }
        catch (Exception e) when (e is IOException or System.Net.Http.HttpRequestException or UnauthorizedAccessException) { }
        if (request != _coverRequest) return;
        if (res is not null && ReadImage(res.Path, ArtSlot.Cover) is { } cover)
        {
            ShowCover(cover, res.Source);
            return;
        }
        foreach (var exe in Exes)
            if (ExeIcon(exe) is { } icon)
            {
                ShowCover(icon, "the game's own icon", isIcon: true);
                return;
            }
        ShowCover(null, "");
    }

    public bool IsIcon { get; private set; }

    void ShowCover(ImageSource? image, string source, bool isIcon = false)
    {
        IsIcon = isIcon;
        Art = image;
        ArtSource = source;
        Raise(nameof(IsIcon));
        Raise(nameof(SmallIconStretch));
    }

    static ImageSource? ReadImage(string file, ArtSlot slot) =>
        ArtService.ReadUserImage(file, slot) is { } bytes ? DecodeImage(bytes, slot) : null;

    static ImageSource? DecodeImage(byte[] bytes, ArtSlot slot)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;      // read once, so the file is not locked
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = slot == ArtSlot.SmallIcon ? 96 : 640;
            using var stream = new MemoryStream(bytes, writable: false);
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or InvalidOperationException or FileFormatException or ArgumentException)
        { return null; }
    }

    static ImageSource? ExeIcon(string exe)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            if (icon is null) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch (Exception e) when (e is IOException or ArgumentException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { return null; }
    }

    public bool ChooseArt(string file, ArtSlot slot)
    {
        var bytes = ArtService.ReadUserImage(file, slot);
        if (bytes is null || DecodeImage(bytes, slot) is not { } image ||
            !ArtService.SetUserImage(Game, slot, bytes)) return false;

        if (slot == ArtSlot.SmallIcon)
        {
            _smallIconOverride = image;
            RefreshSmallIcon();
        }
        else
        {
            _coverRequest++;
            ShowCover(image, "your image");
        }
        return true;
    }

    public bool ResetSmallIcon()
    {
        if (!ArtService.ClearUserSmallIcon(Game)) return false;
        _smallIconOverride = null;
        RefreshSmallIcon();
        return true;
    }

    // ------------------------------------------------------------------ quality control

    public static IReadOnlyList<string> StepNames { get; } = Enum.GetValues<QualityStep>().Select(s => s.Label()).ToList();

    /// <summary>"Recommended" under the recommended stop, nothing under the others.</summary>
    public IReadOnlyList<string> RecMarkers => Enumerable.Range(0, 6).Select(i => i == (int)Recommended ? "▲ Recommended" : "").ToList();
    public string QualityNote => IsCustom && !AtRecommended
        ? $"You chose {StepLabel}. Recommended is {Recommended.Label()}."
        : "Using the recommendation.";
    public string RecommendedLine => _rec is null ? "Looking up this game's published requirements…" : $"Recommended {Recommended.Label()}: {_rec.Chip}. Hover for the assumptions.";

    // ------------------------------------------------------------------ changes (inline preview)

    public HashSet<string> TempExcluded { get; } = new();
    public ObservableCollection<ChangeItem> ChangeItems { get; } = new();

    void RebuildChanges()
    {
        ChangeItems.Clear();
        if (_plan is { } p)
        {
            foreach (var f in p.Files) foreach (var c in f.Changes) ChangeItems.Add(new ChangeItem(this, c.Id, c.ToString(), true, null));
            foreach (var r in p.Registry) ChangeItems.Add(new ChangeItem(this, r.Id, r.Display, true, null));
            foreach (var x in p.Excluded) ChangeItems.Add(new ChangeItem(this, x.Id, $"{x.Setting}: {x.Old ?? "(not set)"} (would be {x.New})", false, x.Why));
        }
        foreach (var n in new[] { nameof(ChangesTitle), nameof(HasChangeItems), nameof(NoChangesText), nameof(ApplyText), nameof(CanApply), nameof(ShowRunningBanner),
                                  nameof(RunningText), nameof(ChangedFile), nameof(HasSkipReason), nameof(NotControlledVisible), nameof(StepSummary), nameof(StatusDot), nameof(RowStatus),
                                  nameof(QualityNote), nameof(RecommendedLine), nameof(RecMarkers), nameof(IsSkipped), nameof(SkipDetailText) })
            Raise(n);
    }

    public int IncludedCount => _plan is { } p ? p.ChangeCount : 0;
    public string ChangesTitle => _plan is null ? "Changes" : ChangeItems.Count == 0 ? "Changes" : $"Changes ({IncludedCount} of {ChangeItems.Count})";
    public bool HasChangeItems => ChangeItems.Count > 0;
    public string NoChangesText => _plan?.Status == PlanStatus.Skipped ? "" : $"Already set for {StepLabel}. Nothing to write.";
    public string? ChangedFile => _plan?.Files.FirstOrDefault()?.Path is { } f ? PathDisplay.Short(f) : (_plan?.Registry.Count > 0 ? "HKEY_CURRENT_USER" + (char)92 + _plan.Registry[0].SubKey : null);
    public bool ShowRunningBanner => _plan?.Status == PlanStatus.Running;
    public string RunningText => $"{Name} is running. Close it to apply changes: its settings are open in the game.";
    public bool IsSkipped => _plan?.Status == PlanStatus.Skipped;
    /// <summary>Why a skipped game was skipped beyond the one-line status: what was looked at, and where its settings probably are.</summary>
    public string SkipDetailText => _plan?.Status == PlanStatus.Skipped ? string.Join(Environment.NewLine, (_plan.Skipped.Count > 0 ? _plan.Skipped : new()).Where(x => x.Length > 0).Distinct()) : "";
    public bool HasSkipReason => IsSkipped;
    public bool NotControlledVisible => HasNotControlled;
    public string StepSummary => IsSkipped ? "Skipped" : StepLabel;

    public string ApplyText => _plan?.Status switch
    {
        PlanStatus.Running => "Close the game first",
        PlanStatus.Ready => $"Apply {_plan.ChangeCount} change{(_plan.ChangeCount == 1 ? "" : "s")}",
        _ => "Nothing to apply",
    };
    public bool CanApply => _plan?.Status == PlanStatus.Ready;

    /// <summary>"ok" / "set" / "warn" / "skip" for the list row's status dot.</summary>
    public string StatusDot => _plan?.Status switch
    {
        PlanStatus.Ready => "ready", PlanStatus.Running => "warn", PlanStatus.NothingToChange => "set", PlanStatus.Skipped => "skip", _ => "set",
    };
    public string RowStatus => _plan?.Status switch
    {
        PlanStatus.Ready => $"{_plan.ChangeCount} changes", PlanStatus.Running => "Running", PlanStatus.NothingToChange => "Already set", PlanStatus.Skipped => "Skipped", _ => "Checking…",
    };

    public void ToggleChange(string id, bool include)
    {
        if (include) TempExcluded.Remove(id); else TempExcluded.Add(id);
        _vm.RowChanged(this);
    }

    /// <summary>Saves (or removes) "always keep this setting for this game", like the old preview window did.</summary>
    public void AlwaysKeep(ChangeItem item)
    {
        var g = _vm.Session!.Prefs.Game(Game.Id);
        bool keep = !item.SavedKeep;
        if (keep && !g.Excluded.Contains(item.Id)) g.Excluded.Add(item.Id);
        if (!keep) g.Excluded.Remove(item.Id);
        _vm.Session.Prefs.Save();
        TempExcluded.Remove(item.Id);
        RaiseSettings();
        _vm.RowChanged(this);
    }

    // ------------------------------------------------------------------ last run and undo

    BackupRun? _lastRun;
    public BackupRun? LastRun { get => _lastRun; set { if (Set(ref _lastRun, value)) { Raise(nameof(LastRunText)); Raise(nameof(CanUndo)); } } }
    public string LastRunText => _lastRun is null ? "No earlier runs for this game." : $"Optimized {_lastRun.Created:d MMM, HH:mm} · {_lastRun.Summary}";
    public bool CanUndo => _lastRun is not null;
}

public sealed partial class MainViewModel
{
    GameRow? _selected;
    /// <summary>The game shown in the detail panel.</summary>
    public GameRow? Selected { get => _selected; set { if (Set(ref _selected, value)) { Raise(nameof(HasSelected)); Raise(nameof(NoSelected)); } } }
    public bool HasSelected => _selected is not null;
    public bool NoSelected => _selected is null;

    public bool AnySelected => SelectedCount > 0;
    public bool BatchVisible => SelectedCount >= 2;

    // segmented filter: Can change / Skipped / All
    static readonly string[] Segments = { "Games it can change", "Skipped", "All games" };
    public int FilterIndex
    {
        get => Math.Max(0, Array.IndexOf(Segments, _statusFilter));
        set { if (value >= 0 && value < Segments.Length) StatusFilter = Segments[value]; Raise(); }
    }
    /// <summary>One line for the collapsed "Defaults for all games" strip.</summary>
    public string DefaultsSummary => Session is null ? "" : $"{GlobalLabel} · {Session.Prefs.DefaultProfile.Name} profile · {(Session.Prefs.GamingDisplay is null ? $"Main display {Session.Hw.Primary.ActiveModeText}" : Session.Prefs.GamingDisplay)}";
    public string CanChangeLabel => $"Can change {Games.Count(r => r.Plan is null || r.IsSupported)}";
    public string SkippedLabel => $"Skipped {Games.Count(r => r.IsSkipped)}";
    public string AllLabel => $"All {Games.Count}";
    void RaiseCounts() { Raise(nameof(CanChangeLabel)); Raise(nameof(SkippedLabel)); Raise(nameof(AllLabel)); Raise(nameof(FilterIndex)); Raise(nameof(DefaultsSummary)); }

    /// <summary>Art for every game, three at a time, once the exes are known (the exe icon is the last resort).</summary>
    public async Task LoadAllArtAsync()
    {
        var rows = Games.ToList();
        bool net = Live.Settings.FetchArt;
        var key = Live.Store.Data.SteamGridDbKey;
        using var gate = new SemaphoreSlim(3);
        await Task.WhenAll(rows.Select(async r =>
        {
            await gate.WaitAsync();
            try { await r.LoadArtAsync(net, key); } finally { gate.Release(); }
        }));
    }

    /// <summary>Each game's most recent optimize run, from the backups folder.</summary>
    public async Task RefreshRunsAsync()
    {
        var runs = await Task.Run(Applier.Runs);
        foreach (var r in Games)
            r.LastRun = runs.FirstOrDefault(x => x.Kind == "optimize" && x.GameNames.Contains(r.Name, StringComparer.OrdinalIgnoreCase));
    }
}
