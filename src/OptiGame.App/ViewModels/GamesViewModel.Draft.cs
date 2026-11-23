using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using OptiGame.Core;
using OptiGame.Core.Hardware;
using OptiGame.Core.Optimize;
using OptiGame.Core.Rules;

namespace OptiGame.App.ViewModels;

/// <summary>One drafted setting: the value seen at the chosen step, and what the other steps do.</summary>
public sealed record DraftLine(string Key, string Slots);

public sealed partial class GameRow
{
    // ------------------------------------------------------------------ rule draft from two snapshots (a preview, then an inactive file)

    string _draftBefore = "", _draftAfter = "", _draftKeys = "", _draftStatus = "";
    int _draftStep = (int)QualityStep.Balanced;
    RuleDraft? _draft;
    string? _savedPath;

    public ObservableCollection<DraftLine> DraftLines { get; } = new();
    public ObservableCollection<string> DraftNotes { get; } = new();

    public string DraftBefore { get => _draftBefore; set { if (Set(ref _draftBefore, value)) DraftInputsChanged(); } }
    public string DraftAfter { get => _draftAfter; set { if (Set(ref _draftAfter, value)) DraftInputsChanged(); } }
    /// <summary>Setting keys to include, separated by commas. Empty means the one setting that changed.</summary>
    public string DraftKeys { get => _draftKeys; set { if (Set(ref _draftKeys, value)) DraftInputsChanged(); } }
    /// <summary>The quality step the "after" capture was taken at.</summary>
    public int DraftStep { get => _draftStep; set { if (Set(ref _draftStep, value)) DraftInputsChanged(); } }
    public string DraftStatus { get => _draftStatus; private set => Set(ref _draftStatus, value); }

    public bool CanPreviewDraft => DraftBefore.Length > 0 && DraftAfter.Length > 0;
    public bool HasDraft => _draft is not null;
    public bool CanSaveDraft => _draft is not null && _savedPath is null;
    public bool HasDraftNotes => DraftNotes.Count > 0;
    public string DraftStepText => $"Observed at {((QualityStep)_draftStep).Label()}. Other steps keep the setting as it is.";

    void DraftInputsChanged()
    {
        _draft = null; _savedPath = null;
        DraftLines.Clear(); DraftNotes.Clear();
        DraftStatus = "";
        foreach (var n in new[] { nameof(CanPreviewDraft), nameof(HasDraft), nameof(CanSaveDraft), nameof(HasDraftNotes), nameof(DraftStepText) }) Raise(n);
    }

    /// <summary>Builds the draft from the two snapshot files. Reads local files only; nothing is saved.</summary>
    public async Task PreviewDraftAsync()
    {
        DraftInputsChanged();
        var (before, after, step) = (DraftBefore, DraftAfter, (QualityStep)DraftStep);
        var keys = DraftKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        try
        {
            var draft = await Task.Run(() =>
                SnapshotRuleDraft.Build(Game, SnapshotRuleDraft.ReadSnapshot(File.ReadAllText(before)),
                    SnapshotRuleDraft.ReadSnapshot(File.ReadAllText(after)), step, keys));
            var rule = RuleSet.TryParse(draft.Json, out _);
            foreach (var (key, value) in rule?.Files.SelectMany(f => f.Set) ?? [])
                DraftLines.Add(new DraftLine(key, SlotsText(value)));
            foreach (var s in draft.Skipped) DraftNotes.Add("Skipped: " + s);
            foreach (var p in draft.Problems) DraftNotes.Add(p.ToString());
            _draft = draft;
            DraftStatus = "Draft ready to review. It is not saved yet and does nothing until you enable it.";
        }
        catch (Exception e) when (e is FormatException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            DraftStatus = e.Message.Replace(Environment.NewLine, " ");
        }
        foreach (var n in new[] { nameof(HasDraft), nameof(CanSaveDraft), nameof(HasDraftNotes), nameof(DraftStepText) }) Raise(n);
    }

    /// <summary>Saves the previewed draft as an inactive file. It is never loaded as a rule until the user moves it.</summary>
    public async Task SaveDraftAsync()
    {
        if (_draft is not { } draft || _savedPath is not null) return;
        try
        {
            var path = await Task.Run(() => SnapshotRuleDraft.Save(draft));
            _savedPath = path;
            DraftStatus = "Saved as an inactive draft: " + PathDisplay.Short(path) + ". Review it, then move it into your rules folder to use it.";
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
        {
            DraftStatus = "Not saved: " + e.Message;
        }
        Raise(nameof(CanSaveDraft));
    }

    /// <summary>"Low: keep · Medium: keep · High: 3 …": empty slots keep the setting.</summary>
    static string SlotsText(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return value.GetRawText();
        var steps = Enum.GetValues<QualityStep>();
        var parts = new List<string>();
        var slots = value.EnumerateArray().ToArray();
        for (int i = 0; i < slots.Length; i++)
            parts.Add($"{(i < steps.Length ? steps[i].Label() : "?")}: {(slots[i].ValueKind == JsonValueKind.Null ? "keep" : slots[i].GetRawText())}");
        return string.Join("  ·  ", parts);
    }
}
