using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using OptiGame.Core.Library;

namespace OptiGame.Core.SpecCheck;

/// <summary>
/// Opens Game Spec Check (the separate Python tool that answers "can this PC run it?") for one game.
/// It has no game argument on its command line, but its page opens a game from the address:
/// http://127.0.0.1:PORT/#steam/APPID, or #pcgw/TITLE for a game with no Steam id. So the launcher starts
/// it when it is not running (hidden, no browser), waits for its ping, then opens that address.
/// Nothing is ported or copied: the two tools only share the graphics card table and the lookup cache.
/// </summary>
public static class SpecCheckLauncher
{
    public const int DefaultPort = 8765;
    public const int PortTries = 10;     // Game Spec Check moves to the next port when one is taken
    public const string AppId = "game-spec-check";

    public sealed record Install(string Path, bool IsExe, Version Version, DateTime Written);

    /// <summary>The page address for a game: its Steam app id when known, else its PCGamingWiki title.</summary>
    public static string GameUrl(int port, GameInfo game, string? linkedSteamId = null)
    {
        var id = linkedSteamId ?? game.SteamAppId;
        var baseUrl = $"http://127.0.0.1:{port}/";
        return id is { Length: > 0 } && id.All(char.IsAsciiDigit)
            ? $"{baseUrl}#steam/{id}"
            : $"{baseUrl}#pcgw/{Uri.EscapeDataString(game.Name)}";
    }

    // ------------------------------------------------------------------ finding it

    /// <summary>
    /// Candidates are the path you chose, a GameSpecCheck.exe next to this app, and any
    /// game-spec-check* folder beside this app or beside any parent folder (with a dist exe or run.py).
    /// The newest version wins (file version, then write time), so an old build folder never beats a newer one.
    /// </summary>
    public static Install? Locate(string? chosenPath = null, string? appDir = null, Func<string, bool>? exists = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(chosenPath)) candidates.Add(chosenPath!);
        appDir ??= AppContext.BaseDirectory;
        candidates.Add(System.IO.Path.Combine(appDir, "GameSpecCheck.exe"));
        try
        {
            for (var dir = new DirectoryInfo(appDir); dir is not null; dir = dir.Parent)
            {
                foreach (var sub in SafeDirs(dir.FullName))
                {
                    var name = System.IO.Path.GetFileName(sub);
                    if (!name.StartsWith("game-spec-check", StringComparison.OrdinalIgnoreCase)) continue;
                    candidates.Add(System.IO.Path.Combine(sub, "dist", "GameSpecCheck.exe"));
                    candidates.Add(System.IO.Path.Combine(sub, "run.py"));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        exists ??= File.Exists;
        Install? best = null;
        foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase).Where(exists))
        {
            bool exe = c.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            Version v = new(0, 0);
            DateTime w = DateTime.MinValue;
            try
            {
                w = File.GetLastWriteTimeUtc(c);
                if (exe && FileVersionInfo.GetVersionInfo(c).FileVersion is { } fv && Version.TryParse(fv.Split(' ')[0], out var parsed)) v = parsed;
                else if (!exe && ReadSourceVersion(c) is { } sv) v = sv;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            var cand = new Install(c, exe, v, w);
            if (best is null || cand.Version > best.Version || (cand.Version == best.Version && (cand.IsExe && !best.IsExe || cand.Written > best.Written)))
                best = cand;
        }
        return best;
    }

    static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir, "game-spec-check*"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    /// <summary>Version from specbench/__init__.py next to run.py, so a source checkout is ranked like an exe.</summary>
    static Version? ReadSourceVersion(string runPy)
    {
        var init = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(runPy) ?? "", "specbench", "__init__.py");
        if (!File.Exists(init)) return null;
        foreach (var line in File.ReadLines(init).Take(20))
            if (line.StartsWith("__version__") && line.Split('"') is [_, var v, ..] && Version.TryParse(v, out var ver)) return ver;
        return null;
    }

    // ------------------------------------------------------------------ running it

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(1.5) };

    /// <summary>The port of a copy that is already answering, or null.</summary>
    public static async Task<int?> FindRunningAsync(int startPort = DefaultPort, int tries = PortTries, CancellationToken ct = default)
    {
        for (int p = startPort; p < startPort + tries; p++)
            if (await IsSpecCheckAsync(p, ct)) return p;
        return null;
    }

    public static async Task<bool> IsSpecCheckAsync(int port, CancellationToken ct = default)
    {
        try
        {
            using var r = await Http.GetAsync($"http://127.0.0.1:{port}/api/ping", ct);
            if (!r.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("app", out var a) && a.GetString() == AppId;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or OperationCanceledException) { return false; }
    }

    /// <summary>Starts a copy with no console window and no browser tab; returns the process so a caller may stop what it started.</summary>
    public static Process? Start(Install install)
    {
        var psi = install.IsExe
            ? new ProcessStartInfo(install.Path, "--no-browser")
            : new ProcessStartInfo(PythonExe(), $"\"{install.Path}\" --no-browser");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = System.IO.Path.GetDirectoryName(install.Path) ?? "";
        try { return Process.Start(psi); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    static string PythonExe()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            foreach (var n in new[] { "pythonw.exe", "python.exe" })
                try { var f = System.IO.Path.Combine(dir.Trim('"'), n); if (File.Exists(f)) return f; } catch (ArgumentException) { }
        return "python.exe";
    }

    public sealed record OpenResult(bool Ok, string Message, string? Url = null, Process? Started = null);

    /// <summary>Makes sure Game Spec Check is up, then opens the game's page in the default browser.</summary>
    public static async Task<OpenResult> OpenForAsync(GameInfo game, string? linkedSteamId, string? chosenPath, bool openBrowser = true, CancellationToken ct = default)
    {
        Process? started = null;
        int? port = await FindRunningAsync(ct: ct);
        if (port is null)
        {
            var install = Locate(chosenPath);
            if (install is null) return new OpenResult(false, "Game Spec Check was not found. Put it next to OptiGame+ or choose it in Settings.");
            started = Start(install);
            if (started is null) return new OpenResult(false, "Game Spec Check could not be started.");
            for (int i = 0; i < 40 && port is null; i++)      // up to about 12 seconds
            {
                await Task.Delay(300, ct);
                port = await FindRunningAsync(ct: ct);
                if (started.HasExited && port is null) return new OpenResult(false, "Game Spec Check closed right after starting.");
            }
            if (port is null) return new OpenResult(false, "Game Spec Check did not answer in time.", Started: started);
        }
        var url = GameUrl(port.Value, game, linkedSteamId);
        if (openBrowser) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return new OpenResult(true, started is null ? "Opened in Game Spec Check." : "Started Game Spec Check and opened the page.", url, started);
    }
}
