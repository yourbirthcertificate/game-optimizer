using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using OptiGame.Core;
using OptiGame.Core.Hardware;
using OptiGame.Core.Optimize;
using OptiGame.Core.Library.ConfigDiscovery;
using OptiGame.Core.Recommend;

namespace OptiGame.App.ViewModels;

/// <summary>One advisory suggestion. It is never written by Apply.</summary>
public sealed record AdviceLine(string Name, string Change, string Reason);

/// <summary>A source behind a recommendation. Only https links are offered as clickable.</summary>
public sealed record EvidenceLine(string Source, string Detail, string? Url)
{
    public bool HasUrl => Uri.TryCreate(Url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;
    public string LinkText => HasUrl ? new Uri(Url!).Host : "";
}

/// <summary>A place the game's settings might live, with how sure we are and why.</summary>
public sealed record CandidateLine(string Path, string Kind, string Confidence, string Format, string Evidence);

public sealed partial class GameRow
{
    // ------------------------------------------------------------------ advice (advisory, never applied)

    public ObservableCollection<AdviceLine> AdviceLines { get; } = new();
    public ObservableCollection<EvidenceLine> EvidenceLines { get; } = new();
    string _adviceNotes = "";
    string _qualityLine = "";

    /// <summary>The effective step and the loaded estimate behind it, from GamePlan.Step and GamePlan.Recommendation.</summary>
    public string QualityLine { get => _qualityLine; private set => Set(ref _qualityLine, value); }
    /// <summary>Targets, assumptions, what was skipped and any fallback, one per line.</summary>
    public string AdviceNotes { get => _adviceNotes; private set => Set(ref _adviceNotes, value); }
    public bool HasAdvice => AdviceLines.Count > 0;
    public bool HasAdviceNotes => AdviceNotes.Length > 0;
    public bool HasEvidence => EvidenceLines.Count > 0;
    public bool ShowAdviceCard => _plan is not null && (HasAdvice || HasAdviceNotes || HasEvidence);

    void RebuildAdvice()
    {
        AdviceLines.Clear();
        EvidenceLines.Clear();
        var notes = new List<string>();
        if (_plan is { } p)
        {
            QualityLine = p.Recommendation is { } rec
                ? $"Plan step: {p.Step.Label()}. Estimate: {rec.Step.Label()}. {rec.KindLabel}."
                : $"Plan step: {p.Step.Label()}.";
            if (p.SettingAdvice is { } report)
            {
                foreach (var s in report.Settings)
                    AdviceLines.Add(new AdviceLine(s.Name, $"{s.CurrentValue} -> {s.SuggestedValue}", s.Reason));
                if (report.TargetFps is double fps)
                    notes.Add(string.Create(CultureInfo.InvariantCulture, $"Aimed at {fps:0.#} fps ({report.TargetSource})."));
                if (report.Resolution is { } r)
                    notes.Add(string.Create(CultureInfo.InvariantCulture, $"Resolution used: {r.Width} x {r.Height}."));
                notes.AddRange(report.Assumptions.Select(a => "Assumes: " + a));
                notes.AddRange(report.Skipped.Select(a => "Left out: " + a));
                if (!string.IsNullOrWhiteSpace(report.FallbackReason)) notes.Add("Fallback: " + report.FallbackReason);
            }
            foreach (var e in p.Evidence) EvidenceLines.Add(new EvidenceLine(e.Source, e.Detail, e.Url));
        }
        else QualityLine = "";
        AdviceNotes = string.Join(Environment.NewLine, notes);
        foreach (var n in new[] { nameof(HasAdvice), nameof(HasAdviceNotes), nameof(HasEvidence), nameof(ShowAdviceCard) }) Raise(n);
    }

    // ------------------------------------------------------------------ where the settings live (on demand)

    public ObservableCollection<CandidateLine> CandidateLines { get; } = new();
    public ObservableCollection<string> DiagnosticLines { get; } = new();
    CancellationTokenSource? _locationCts;
    bool _locating, _located;
    string _locationStatus = "";

    public bool Locating { get => _locating; private set { if (Set(ref _locating, value)) Raise(nameof(CanLocate)); } }
    public bool Located { get => _located; private set => Set(ref _located, value); }
    public bool CanLocate => !_locating;
    public string LocationStatus { get => _locationStatus; private set => Set(ref _locationStatus, value); }
    public bool HasCandidates => CandidateLines.Count > 0;
    public bool HasDiagnostics => DiagnosticLines.Count > 0;

    /// <summary>Stops a running lookup and clears its results, for example when another game is selected.</summary>
    public void CancelLocations()
    {
        _locationCts?.Cancel();
        _locationCts = null;
        Locating = false;
    }

    /// <summary>Runs InspectPlan off the UI thread. It reads local files only; nothing is written or fetched.</summary>
    public async Task FindLocationsAsync()
    {
        if (_vm.Session is not { } session || Locating) return;
        CancelLocations();
        var cts = _locationCts = new CancellationTokenSource();
        Locating = true;
        LocationStatus = "Looking…";
        CandidateLines.Clear();
        DiagnosticLines.Clear();
        try
        {
            var plan = await Task.Run(() => session.InspectPlan(Game, cancellationToken: cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var c in plan.SettingsCandidates)
                CandidateLines.Add(new CandidateLine(
                    PathDisplay.Short(c.Path), c.Kind.ToString(), ConfidenceText(c.Confidence), c.FormatHint ?? "unknown format",
                    string.Join(Environment.NewLine, c.Evidence.Select(e => $"{e.Source}: {e.Observation}"))));
            foreach (var d in plan.SettingsDiscovery?.Diagnostics ?? [])
                DiagnosticLines.Add($"{d.Message}" + (d.Path is null ? "" : $" ({PathDisplay.Short(d.Path)})"));
            LocationStatus = CandidateLines.Count == 0 ? "No likely settings location was found." : $"{CandidateLines.Count} possible location{(CandidateLines.Count == 1 ? "" : "s")}.";
            Located = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LocationStatus = "Could not read part of the disk: " + e.Message;
        }
        finally
        {
            if (ReferenceEquals(_locationCts, cts)) { _locationCts = null; Locating = false; }
            cts.Dispose();
            Raise(nameof(HasCandidates)); Raise(nameof(HasDiagnostics));
        }
    }

    static string ConfidenceText(ConfigConfidence c) => c switch
    {
        ConfigConfidence.Confirmed => "Confirmed",
        ConfigConfidence.Present => "File found, not confirmed",
        _ => "A lead only",
    };
}
