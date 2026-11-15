using System.Security.Cryptography;
using System.Text.Json;
using GameOptimizer.Core.Optimize;
using Microsoft.Win32;

namespace GameOptimizer.Core.Safety;

public sealed class BackupEntry
{
    public string GameId { get; set; } = "";
    public string GameName { get; set; } = "";
    public string InstallDir { get; set; } = "";
    /// <summary>"file" or "registry".</summary>
    public string Kind { get; set; } = "";
    public string? Path { get; set; }
    public string? BackupFile { get; set; }
    /// <summary>SHA-256 of what we wrote, to tell whether the game changed the file since.</summary>
    public string? WrittenSha256 { get; set; }
    public string? SubKey { get; set; }
    public string? ValueName { get; set; }
    public bool ValueExisted { get; set; }
    public string? ValueKind { get; set; }
    public JsonElement? ValueData { get; set; }
}

public sealed class BackupRun
{
    public string Id { get; set; } = "";
    public DateTime Created { get; set; }
    public string Summary { get; set; } = "";
    public List<BackupEntry> Entries { get; set; } = new();
    public string Dir => System.IO.Path.Combine(AppPaths.BackupsDir, Id);
}

public sealed class ApplyResult
{
    public string? RunId { get; set; }
    public List<string> Applied { get; } = new();
    public List<string> Refused { get; } = new();
    public List<string> Errors { get; } = new();
}

/// <summary>Writes plans to disk and the registry with a backup first, and restores those backups.</summary>
public static class Applier
{
    public const int KeepRuns = 20;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static ApplyResult Apply(IEnumerable<GamePlan> plans, string summary)
    {
        var result = new ApplyResult();
        var ready = plans.Where(p => p.HasChanges).ToList();
        if (ready.Count == 0) return result;

        var run = new BackupRun { Id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"), Created = DateTime.Now, Summary = summary };
        Directory.CreateDirectory(System.IO.Path.Combine(run.Dir, "files"));
        var images = ProcessGuard.RunningImages();
        int n = 0;

        foreach (var plan in ready)
        {
            var g = plan.Game;
            // Checked right before writing: never touch a game's files while it runs.
            if (ProcessGuard.RunningFrom(g.InstallDir, images) is { } exe)
            {
                plan.Status = PlanStatus.Running;
                result.Refused.Add($"{g.Name}: running ({System.IO.Path.GetFileName(exe)}). Close it and try again.");
                continue;
            }
            // Files must still hold exactly what we planned against; otherwise the plan is stale.
            var stale = plan.Files.FirstOrDefault(f => !File.Exists(f.Path) || !File.ReadAllBytes(f.Path).AsSpan().SequenceEqual(f.Original));
            if (stale is not null)
            {
                result.Refused.Add($"{g.Name}: {System.IO.Path.GetFileName(stale.Path)} changed since the preview. Preview again.");
                continue;
            }

            var entries = new List<BackupEntry>();
            try
            {
                foreach (var f in plan.Files)
                {
                    var bak = System.IO.Path.Combine("files", $"{++n:D3}-{System.IO.Path.GetFileName(f.Path)}");
                    File.WriteAllBytes(System.IO.Path.Combine(run.Dir, bak), f.Original);
                    entries.Add(new BackupEntry
                    {
                        GameId = g.Id, GameName = g.Name, InstallDir = g.InstallDir, Kind = "file", Path = f.Path,
                        BackupFile = bak, WrittenSha256 = Sha(f.Updated),
                    });
                }
                foreach (var r in plan.Registry)
                {
                    using var k = Registry.CurrentUser.OpenSubKey(r.SubKey);
                    var cur = k?.GetValue(r.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    entries.Add(new BackupEntry
                    {
                        GameId = g.Id, GameName = g.Name, InstallDir = g.InstallDir, Kind = "registry", SubKey = r.SubKey,
                        ValueName = r.ValueName, ValueExisted = cur is not null,
                        ValueKind = cur is null ? null : k!.GetValueKind(r.ValueName).ToString(),
                        ValueData = cur is null ? null : JsonSerializer.SerializeToElement(cur),
                    });
                }
                // Manifest first, then writes: a crash mid-write still leaves a restorable backup.
                run.Entries.AddRange(entries);
                SaveManifest(run);

                foreach (var f in plan.Files) AtomicWrite(f.Path, f.Updated);
                foreach (var r in plan.Registry)
                {
                    using var k = Registry.CurrentUser.OpenSubKey(r.SubKey, writable: true) ?? throw new IOException($"Registry key {r.SubKey} disappeared");
                    k.SetValue(r.ValueName, r.NewValue, r.Kind);
                }
                result.Applied.Add($"{g.Name}: {plan.ChangeCount} change(s)");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                result.Errors.Add($"{g.Name}: {e.Message}. Restore run {run.Id} to undo any partial change.");
            }
        }

        if (run.Entries.Count == 0) { TryDelete(run.Dir); return result; }
        result.RunId = run.Id;
        Prune();
        return result;
    }

    static void AtomicWrite(string path, byte[] data)
    {
        var tmp = path + ".gameopt-tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }

    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    static void SaveManifest(BackupRun run) =>
        File.WriteAllText(System.IO.Path.Combine(run.Dir, "manifest.json"), JsonSerializer.Serialize(run, Json));

    public static List<BackupRun> Runs()
    {
        var list = new List<BackupRun>();
        if (!Directory.Exists(AppPaths.BackupsDir)) return list;
        foreach (var d in Directory.GetDirectories(AppPaths.BackupsDir))
        {
            var m = System.IO.Path.Combine(d, "manifest.json");
            try
            {
                if (File.Exists(m) && JsonSerializer.Deserialize<BackupRun>(File.ReadAllText(m)) is { } r) list.Add(r);
            }
            catch (JsonException) { }
        }
        return list.OrderByDescending(r => r.Id, StringComparer.Ordinal).ToList();
    }

    static void Prune()
    {
        foreach (var old in Runs().Skip(KeepRuns)) TryDelete(old.Dir);
    }

    static void TryDelete(string dir) { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    /// <summary>Put back everything a run changed, optionally for one game only (by id or name).</summary>
    public static ApplyResult Restore(BackupRun run, string? game = null)
    {
        var result = new ApplyResult { RunId = run.Id };
        var images = ProcessGuard.RunningImages();
        var entries = run.Entries.Where(e => game is null || e.GameId.Equals(game, StringComparison.OrdinalIgnoreCase) ||
                                             e.GameName.Contains(game, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var grp in entries.GroupBy(e => e.GameId))
        {
            var first = grp.First();
            if (ProcessGuard.RunningFrom(first.InstallDir, images) is { } exe)
            { result.Refused.Add($"{first.GameName}: running ({System.IO.Path.GetFileName(exe)}). Close it and try again."); continue; }
            try
            {
                foreach (var e in grp)
                {
                    if (e.Kind == "file")
                    {
                        var bytes = File.ReadAllBytes(System.IO.Path.Combine(run.Dir, e.BackupFile!));
                        if (File.Exists(e.Path) && Sha(File.ReadAllBytes(e.Path!)) != e.WrittenSha256)
                            result.Errors.Add($"{first.GameName}: {System.IO.Path.GetFileName(e.Path)} was changed after the optimize (probably by the game). Restored the backup anyway.");
                        AtomicWrite(e.Path!, bytes);
                    }
                    else
                    {
                        using var k = Registry.CurrentUser.CreateSubKey(e.SubKey!, writable: true);
                        if (!e.ValueExisted) { k.DeleteValue(e.ValueName!, throwOnMissingValue: false); continue; }
                        var kind = Enum.Parse<RegistryValueKind>(e.ValueKind!);
                        object val = kind switch
                        {
                            RegistryValueKind.DWord => e.ValueData!.Value.GetInt32(),
                            RegistryValueKind.QWord => e.ValueData!.Value.GetInt64(),
                            RegistryValueKind.Binary => e.ValueData!.Value.GetBytesFromBase64(),
                            RegistryValueKind.MultiString => e.ValueData!.Value.EnumerateArray().Select(x => x.GetString()!).ToArray(),
                            _ => e.ValueData!.Value.GetString()!,
                        };
                        k.SetValue(e.ValueName, val, kind);
                    }
                }
                result.Applied.Add($"{first.GameName}: restored {grp.Count()} item(s)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                result.Errors.Add($"{first.GameName}: {ex.Message}");
            }
        }
        return result;
    }
}
