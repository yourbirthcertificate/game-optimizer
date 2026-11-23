using System.Globalization;
using System.Text.Json;

namespace OptiGame.Core.Bench;

/// <summary>
/// Measured frame times and the numbers derived from them. Definitions:
/// - Average FPS = frames / total captured time (1000 * N / sum of frame times in ms). Not the mean of per-frame FPS.
/// - 1% low FPS = 1000 / the 99th-percentile frame time (nearest rank): 99% of frames were delivered at least this fast.
/// - Duration = sum of frame times.
/// </summary>
public sealed class BenchMetrics
{
    public int Frames { get; init; }
    public double DurationSeconds { get; init; }
    public double AverageFps { get; init; }
    public double OnePercentLowFps { get; init; }
    public double P99FrameTimeMs { get; init; }
    public double MaxFrameTimeMs { get; init; }

    public static BenchMetrics From(IReadOnlyList<double> frameTimesMs)
    {
        if (frameTimesMs.Count == 0) throw new FormatException("no frames");
        double sum = frameTimesMs.Sum();
        var sorted = frameTimesMs.OrderBy(x => x).ToArray();
        int rank = (int)Math.Ceiling(0.99 * sorted.Length); // nearest-rank percentile, 1-based
        double p99 = sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
        return new BenchMetrics
        {
            Frames = sorted.Length,
            DurationSeconds = sum / 1000,
            AverageFps = 1000 * sorted.Length / sum,
            P99FrameTimeMs = p99,
            OnePercentLowFps = 1000 / p99,
            MaxFrameTimeMs = sorted[^1],
        };
    }
}

/// <summary>
/// Reads frame-time captures. Two formats:
/// 1. GameOptimizer CSV: a header row with a column named frametime_ms (other columns are ignored), one row per frame.
/// 2. PresentMon CSV (also written by CapFrameX and others): column MsBetweenPresents, or FrameTime /
///    MsBetweenDisplayChange in newer versions. All values are milliseconds.
/// </summary>
public static class FrameTimeCsv
{
    public const int MinFrames = 100;
    public const double MaxFrameMs = 5000;
    static readonly string[] Columns = { "frametime_ms", "MsBetweenPresents", "FrameTime", "MsBetweenDisplayChange" };

    public static (List<double> frames, string format) Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("//")).ToList();
        if (lines.Count < 2) throw new FormatException("The file has no frame rows. Expected a header row and one row per frame.");
        char sep = lines[0].Contains(',') ? ',' : lines[0].Contains(';') ? ';' : lines[0].Contains('\t') ? '\t' : ',';
        var header = lines[0].Split(sep).Select(h => h.Trim().Trim('"')).ToList();
        int col = -1;
        string? name = null;
        foreach (var c in Columns)
        {
            col = header.FindIndex(h => h.Equals(c, StringComparison.OrdinalIgnoreCase));
            if (col >= 0) { name = c; break; }
        }
        if (col < 0)
        {
            var guess = header.FirstOrDefault(h => h.Contains("fps", StringComparison.OrdinalIgnoreCase));
            throw new FormatException(guess is not null
                ? $"Found a '{guess}' column, but frame rates per row cannot be turned into frame times reliably. Export frame times in milliseconds (a frametime_ms or MsBetweenPresents column)."
                : $"No frame-time column. Expected one of: {string.Join(", ", Columns)} (milliseconds). Found: {string.Join(", ", header.Take(12))}");
        }
        var frames = new List<double>();
        for (int i = 1; i < lines.Count; i++)
        {
            var cells = lines[i].Split(sep);
            if (col >= cells.Length) throw new FormatException($"Row {i + 1} has {cells.Length} columns; the frame-time column is number {col + 1}.");
            var cell = cells[col].Trim().Trim('"');
            if (!double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) || double.IsNaN(ms) || double.IsInfinity(ms))
                throw new FormatException($"Row {i + 1}: '{cell}' is not a number (use a dot for decimals).");
            if (ms <= 0) throw new FormatException($"Row {i + 1}: frame time {cell} ms is not positive.");
            if (ms > MaxFrameMs) throw new FormatException($"Row {i + 1}: frame time {cell} ms is over {MaxFrameMs} ms. Is the column in seconds or microseconds instead of milliseconds?");
            frames.Add(ms);
        }
        if (frames.Count < MinFrames) throw new FormatException($"Only {frames.Count} frames; at least {MinFrames} are needed for a meaningful 1% low.");
        if (frames.Average() < 0.05) throw new FormatException("Frame times average under 0.05 ms; the column is probably in seconds, not milliseconds.");
        return (frames, name == "frametime_ms" ? "GameOptimizer CSV" : $"PresentMon CSV ({name})");
    }
}

/// <summary>A stored, measured result with the context it was taken in.</summary>
public sealed class BenchResult
{
    public string Id { get; set; } = "";
    public string GameId { get; set; } = "";
    public string GameName { get; set; } = "";
    public DateTime Imported { get; set; }
    public DateTime? Captured { get; set; }
    /// <summary>"before", "after" or free text.</summary>
    public string Label { get; set; } = "";
    public string? ProfileName { get; set; }
    public string? Step { get; set; }
    public string? Resolution { get; set; }
    public string? FpsCap { get; set; }
    public string Hardware { get; set; } = "";
    public string? Notes { get; set; }
    public BenchmarkCaptureContext? CaptureContext { get; set; }
    public string SourceFormat { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public bool Preferred { get; set; }
    public BenchMetrics Metrics { get; set; } = new();
    /// <summary>Frame times (ms), kept for the graph. Downsampled to at most 20000 points.</summary>
    public List<double> FrameTimesMs { get; set; } = new();

    public string Summary => string.Create(CultureInfo.InvariantCulture,
        $"{Metrics.AverageFps:0.#} fps average, {Metrics.OnePercentLowFps:0.#} fps 1% low, {Metrics.DurationSeconds:0.#} s");
}

public sealed record BenchComparison(BenchResult A, BenchResult B, List<string> Differences, List<string> Warnings);

/// <summary>Local storage of results in %LOCALAPPDATA%\GameOpt\benchmarks. Nothing is uploaded.</summary>
public static class BenchmarkStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    public static string Dir => Path.Combine(AppPaths.DataDir, "benchmarks");

    public static BenchResult Import(string csvPath, string gameId, string gameName, string label, string hardware,
        string? profile = null, string? step = null, string? resolution = null, string? fpsCap = null, string? notes = null,
        BenchmarkCaptureContext? captureContext = null)
    {
        if (captureContext is not null && !captureContext.IsComplete())
            throw new ArgumentException("The benchmark capture context is incomplete.", nameof(captureContext));
        var (frames, fmt) = FrameTimeCsv.Parse(File.ReadAllText(csvPath));
        var r = new BenchResult
        {
            Id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture),
            GameId = gameId, GameName = gameName, Imported = DateTime.Now, Captured = File.GetLastWriteTime(csvPath),
            Label = label, ProfileName = profile, Step = step, Resolution = resolution, FpsCap = fpsCap, Hardware = hardware, Notes = notes,
            SourceFormat = fmt, SourceFile = Path.GetFileName(csvPath), CaptureContext = captureContext,
            Metrics = BenchMetrics.From(frames),
            FrameTimesMs = Downsample(frames, 20000),
        };
        Save(r);
        return r;
    }

    static List<double> Downsample(List<double> f, int max)
    {
        if (f.Count <= max) return f;
        int step = (int)Math.Ceiling(f.Count / (double)max);
        var o = new List<double>();
        for (int i = 0; i < f.Count; i += step) o.Add(f.GetRange(i, Math.Min(step, f.Count - i)).Max()); // keep spikes visible
        return o;
    }

    public static void Save(BenchResult r)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, r.Id + ".json"), JsonSerializer.Serialize(r, Json));
    }

    public static List<BenchResult> All()
    {
        var list = new List<BenchResult>();
        if (!Directory.Exists(Dir)) return list;
        foreach (var f in Directory.EnumerateFiles(Dir, "*.json"))
            try { if (JsonSerializer.Deserialize<BenchResult>(File.ReadAllText(f)) is { } r) list.Add(r); }
            catch (Exception e) when (e is JsonException or IOException) { }
        return list.OrderByDescending(r => r.Imported).ToList();
    }

    public static BenchResult? Find(string id) => All().FirstOrDefault(r => r.Id == id);

    public static void Delete(string id)
    {
        var p = Path.Combine(Dir, id + ".json");
        if (File.Exists(p)) File.Delete(p);
    }

    /// <summary>Marks one result as the preferred one for its game (and unmarks the others).</summary>
    public static void SetPreferred(string id, bool preferred = true)
    {
        var r = Find(id) ?? throw new ArgumentException($"No benchmark '{id}'.");
        if (preferred)
            foreach (var other in All().Where(o => o.GameId == r.GameId && o.Preferred && o.Id != id)) { other.Preferred = false; Save(other); }
        r.Preferred = preferred;
        Save(r);
    }

    public static BenchResult? PreferredFor(string gameId) => All().FirstOrDefault(r => r.GameId == gameId && r.Preferred);

    /// <summary>Before/after comparison. Warns when the captures are not like for like.</summary>
    public static BenchComparison Compare(BenchResult a, BenchResult b)
    {
        var inv = CultureInfo.InvariantCulture;
        string Pct(double x, double y) => y == 0 ? "" : string.Create(inv, $" ({(x - y) / y * 100:+0.#;-0.#}%)");
        var diff = new List<string>
        {
            string.Create(inv, $"Average FPS: {a.Metrics.AverageFps:0.#} -> {b.Metrics.AverageFps:0.#}{Pct(b.Metrics.AverageFps, a.Metrics.AverageFps)}"),
            string.Create(inv, $"1% low FPS: {a.Metrics.OnePercentLowFps:0.#} -> {b.Metrics.OnePercentLowFps:0.#}{Pct(b.Metrics.OnePercentLowFps, a.Metrics.OnePercentLowFps)}"),
            string.Create(inv, $"99th percentile frame time: {a.Metrics.P99FrameTimeMs:0.##} ms -> {b.Metrics.P99FrameTimeMs:0.##} ms"),
            string.Create(inv, $"Duration: {a.Metrics.DurationSeconds:0.#} s -> {b.Metrics.DurationSeconds:0.#} s"),
        };
        var warn = new List<string>();
        if (a.GameId != b.GameId) warn.Add($"Different games ({a.GameName} and {b.GameName}); the numbers are not comparable.");
        double d1 = a.Metrics.DurationSeconds, d2 = b.Metrics.DurationSeconds;
        if (Math.Max(d1, d2) / Math.Max(0.001, Math.Min(d1, d2)) > 1.25)
            warn.Add(string.Create(inv, $"Durations differ ({d1:0.#} s and {d2:0.#} s). Longer runs include more scenes; compare the same route for the same time."));
        if (a.Resolution != b.Resolution) warn.Add($"Resolutions differ ({a.Resolution ?? "?"} and {b.Resolution ?? "?"}).");
        if (a.FpsCap != b.FpsCap) warn.Add($"Frame caps differ ({a.FpsCap ?? "?"} and {b.FpsCap ?? "?"}); a cap limits the average by design.");
        if (a.Hardware != b.Hardware) warn.Add("The hardware description differs between the two captures.");
        if (a.Step != b.Step || a.ProfileName != b.ProfileName)
            diff.Add($"Settings: {a.ProfileName ?? "?"}/{a.Step ?? "?"} -> {b.ProfileName ?? "?"}/{b.Step ?? "?"}");
        return new BenchComparison(a, b, diff, warn);
    }
}
