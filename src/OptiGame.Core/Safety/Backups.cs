using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OptiGame.Core.Formats;
using OptiGame.Core.Optimize;
using Microsoft.Win32;

namespace OptiGame.Core.Safety;

/// <summary>One key the optimizer changed, as recorded in a backup run (enough to undo just that key).</summary>
public sealed class RecordedChange
{
    public string Id { get; set; } = "";
    public string Setting { get; set; } = "";
    public string Locator { get; set; } = "";
    public string? OldRaw { get; set; }
    public string? NewRaw { get; set; }
}

public sealed class BackupEntry
{
    public string GameId { get; set; } = "";
    public string GameName { get; set; } = "";
    public string InstallDir { get; set; } = "";
    /// <summary>"file" or "registry".</summary>
    public string Kind { get; set; } = "";
    public string? Path { get; set; }
    public string? BackupFile { get; set; }
    /// <summary>SHA-256 of what was written, to tell whether the file changed since.</summary>
    public string? WrittenSha256 { get; set; }
    public string? Format { get; set; }
    /// <summary>Keys changed in this file (version 2 runs). Older runs only support a full restore.</summary>
    public List<RecordedChange> Changes { get; set; } = new();
    public string? SubKey { get; set; }
    public string? ValueName { get; set; }
    public string? Setting { get; set; }
    public bool ValueExisted { get; set; }
    public string? ValueKind { get; set; }
    public JsonElement? ValueData { get; set; }
    /// <summary>The registry value that was written, to detect later edits.</summary>
    public JsonElement? WrittenData { get; set; }
}

public sealed class BackupRun
{
    public int Version { get; set; } = 2;
    public string Id { get; set; } = "";
    public DateTime Created { get; set; }
    public string Summary { get; set; } = "";
    /// <summary>"optimize" or "restore" (a snapshot taken before a restore, so the restore can be undone).</summary>
    public string Kind { get; set; } = "optimize";
    public List<BackupEntry> Entries { get; set; } = new();
    public string Dir => System.IO.Path.Combine(AppPaths.BackupsDir, Id);
    public IEnumerable<string> GameNames => Entries.Select(e => e.GameName).Distinct();
}

public sealed class ApplyResult
{
    public string? RunId { get; set; }
    public List<string> Applied { get; } = new();
    public List<string> Refused { get; } = new();
    public List<string> Errors { get; } = new();
}

// ---------------------------------------------------------------------- restore planning

public enum RestoreMode
{
    /// <summary>Put back only the keys the optimizer changed; later edits to other keys stay.</summary>
    UndoChanges,
    /// <summary>Write the whole backed-up file or value, replacing everything changed since.</summary>
    FullBackup,
}

public enum KeyState { Revert, AlreadyOriginal, Conflict }

public sealed record KeyUndo(string Setting, string? Current, string? Original, string? Written, KeyState State)
{
    public string Text => State switch
    {
        KeyState.Revert => $"{Setting}: {Written ?? "(not set)"} -> {Original ?? "(removed)"}",
        KeyState.AlreadyOriginal => $"{Setting}: already {Original ?? "(not set)"}",
        _ => $"{Setting}: changed again to {Current ?? "(missing)"} since the optimize (it wrote {Written}); left alone",
    };
}

public sealed class RestoreItem
{
    public required BackupEntry Entry { get; init; }
    public string Title => Entry.Kind == "file" ? Entry.Path! : $@"HKCU\{Entry.SubKey}\{Entry.ValueName}";
    /// <summary>Current contents at planning time (null: the file is missing). Checked again before writing.</summary>
    public byte[]? CurrentBytes { get; init; }
    public object? CurrentValue { get; init; }
    public bool ChangedSinceOptimize { get; init; }
    /// <summary>Key-level undo is possible (recorded keys and a format that can be edited safely).</summary>
    public bool CanUndoChanges { get; init; }
    public string? WhyNoUndo { get; init; }
    public List<KeyUndo> Keys { get; init; } = new();
    public byte[]? UndoBytes { get; init; }
    public byte[]? BackupBytes { get; init; }
    public int ConflictCount => Keys.Count(k => k.State == KeyState.Conflict);
}

public sealed class RestorePlan
{
    public required BackupRun Run { get; init; }
    public string? Game { get; init; }
    public List<RestoreItem> Items { get; } = new();
    public bool AnyConflicts => Items.Any(i => i.ConflictCount > 0);
    public bool AllUndoable => Items.All(i => i.CanUndoChanges);
}

/// <summary>Writes plans to disk and the registry with a backup first, and restores those backups.</summary>
public static class Applier
{
    public const int KeepRuns = 20;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static ApplyResult Apply(IEnumerable<GamePlan> plans, string summary)
    {
        var result = new ApplyResult();
        var ready = plans.Where(p => p.HasChanges).ToList();
        if (ready.Count == 0) return result;

        var run = NewRun(summary, "optimize");
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
            var staleReg = plan.Registry.FirstOrDefault(r => !Same(ReadValue(r.SubKey, r.ValueName), r.OldValue));
            if (staleReg is not null)
            {
                result.Refused.Add($"{g.Name}: {staleReg.Setting} changed since the preview. Preview again.");
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
                        GameId = g.Id, GameName = g.Name, InstallDir = g.InstallDir, Kind = "file", Path = f.Path, Format = f.Format,
                        BackupFile = bak, WrittenSha256 = Sha(f.Updated),
                        Changes = f.Changes.Select(c => new RecordedChange { Id = c.Id, Setting = c.Setting, Locator = c.Locator, OldRaw = c.OldRaw, NewRaw = c.NewRaw }).ToList(),
                    });
                }
                foreach (var r in plan.Registry)
                    entries.Add(RegistryEntry(g.Id, g.Name, g.InstallDir, r.SubKey, r.ValueName, r.Setting, r.NewValue));
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
        return Finish(run, result);
    }

    static BackupRun NewRun(string summary, string kind)
    {
        var run = new BackupRun { Id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", Inv), Created = DateTime.Now, Summary = summary, Kind = kind };
        while (Directory.Exists(run.Dir)) run.Id += "x";
        Directory.CreateDirectory(System.IO.Path.Combine(run.Dir, "files"));
        return run;
    }

    static ApplyResult Finish(BackupRun run, ApplyResult result)
    {
        if (run.Entries.Count == 0) { TryDelete(run.Dir); return result; }
        result.RunId = run.Id;
        Prune();
        return result;
    }

    static BackupEntry RegistryEntry(string gameId, string name, string installDir, string sub, string valueName, string setting, object? written)
    {
        using var k = Registry.CurrentUser.OpenSubKey(sub);
        var cur = k?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return new BackupEntry
        {
            GameId = gameId, GameName = name, InstallDir = installDir, Kind = "registry", SubKey = sub, ValueName = valueName, Setting = setting,
            ValueExisted = cur is not null,
            ValueKind = cur is null ? null : k!.GetValueKind(valueName).ToString(),
            ValueData = cur is null ? null : JsonSerializer.SerializeToElement(cur),
            WrittenData = written is null ? null : JsonSerializer.SerializeToElement(written),
        };
    }

    static object? ReadValue(string sub, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(sub);
        return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    static string? Text(object? v) => v switch
    {
        null => null,
        byte[] b => Convert.ToBase64String(b),
        string[] a => string.Join("\n", a),
        JsonElement e => e.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Array => string.Join("\n", e.EnumerateArray().Select(x => x.ToString())),
            _ => e.GetRawText(),
        },
        _ => Convert.ToString(v, Inv),
    };

    static bool Same(object? a, object? b) => Text(a) == Text(b);

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
                if (File.Exists(m) && JsonSerializer.Deserialize<BackupRun>(File.ReadAllText(m)) is { } r)
                {
                    if (r.Version < 2 && r.Kind is null or "") r.Kind = "optimize";
                    list.Add(r);
                }
            }
            catch (Exception e) when (e is JsonException or IOException) { }
        }
        return list.OrderByDescending(r => r.Id, StringComparer.Ordinal).ToList();
    }

    static void Prune()
    {
        foreach (var old in Runs().Skip(KeepRuns)) TryDelete(old.Dir);
    }

    static void TryDelete(string dir) { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    // ------------------------------------------------------------------ restore

    static bool Matches(BackupEntry e, string? game) => game is null || e.GameId.Equals(game, StringComparison.OrdinalIgnoreCase) ||
                                                        e.GameName.Contains(game, StringComparison.OrdinalIgnoreCase);

    /// <summary>Works out what a restore would do, without writing anything.</summary>
    public static RestorePlan PlanRestore(BackupRun run, string? game = null)
    {
        var plan = new RestorePlan { Run = run, Game = game };
        foreach (var e in run.Entries.Where(e => Matches(e, game)))
            plan.Items.Add(e.Kind == "file" ? PlanFile(run, e) : PlanRegistry(e));
        return plan;
    }

    static RestoreItem PlanFile(BackupRun run, BackupEntry e)
    {
        byte[]? backup = null;
        var bakPath = System.IO.Path.Combine(run.Dir, e.BackupFile ?? "");
        if (File.Exists(bakPath)) backup = File.ReadAllBytes(bakPath);
        byte[]? current = File.Exists(e.Path) ? File.ReadAllBytes(e.Path!) : null;
        bool changed = current is null || Sha(current) != e.WrittenSha256;

        if (backup is null)
            return new RestoreItem { Entry = e, CurrentBytes = current, ChangedSinceOptimize = changed, WhyNoUndo = "the backup file is missing" };
        if (current is null)
            return new RestoreItem { Entry = e, CurrentBytes = null, ChangedSinceOptimize = true, BackupBytes = backup, WhyNoUndo = "the file no longer exists; only the full backup can be put back" };
        if (!changed)
            // Nothing touched the file since: undoing our keys gives exactly the backup.
            return new RestoreItem
            {
                Entry = e, CurrentBytes = current, BackupBytes = backup, CanUndoChanges = true, UndoBytes = backup,
                Keys = e.Changes.Select(c => new KeyUndo(c.Setting, c.NewRaw, c.OldRaw, c.NewRaw, KeyState.Revert)).ToList(),
            };
        if (e.Changes.Count == 0)
            return new RestoreItem
            {
                Entry = e, CurrentBytes = current, BackupBytes = backup, ChangedSinceOptimize = true,
                WhyNoUndo = "this run is from an older version that did not record individual keys",
            };
        try
        {
            var (bytes, keys) = UndoKeys(e.Format ?? "", current, e.Changes);
            return new RestoreItem { Entry = e, CurrentBytes = current, BackupBytes = backup, ChangedSinceOptimize = true, CanUndoChanges = true, UndoBytes = bytes, Keys = keys };
        }
        catch (Exception ex) when (ex is FormatException or JsonException or NotSupportedException or InvalidOperationException)
        {
            return new RestoreItem
            {
                Entry = e, CurrentBytes = current, BackupBytes = backup, ChangedSinceOptimize = true,
                WhyNoUndo = $"the file changed since and cannot be merged safely ({ex.Message})",
            };
        }
    }

    static RestoreItem PlanRegistry(BackupEntry e)
    {
        var cur = ReadValue(e.SubKey!, e.ValueName!);
        string? curT = Text(cur), written = Text(e.WrittenData), original = e.ValueExisted ? Text(e.ValueData) : null;
        var state = curT == written ? KeyState.Revert : curT == original ? KeyState.AlreadyOriginal : KeyState.Conflict;
        bool legacy = e.WrittenData is null; // version 1 runs did not record the written value
        if (legacy) state = curT == original ? KeyState.AlreadyOriginal : KeyState.Revert;
        return new RestoreItem
        {
            Entry = e, CurrentValue = cur, ChangedSinceOptimize = !legacy && state == KeyState.Conflict, CanUndoChanges = !legacy || state != KeyState.Conflict,
            WhyNoUndo = legacy ? "older run: the value written was not recorded" : null,
            Keys = { new KeyUndo(e.Setting ?? e.ValueName!, curT, original, written ?? curT, state) },
        };
    }

    /// <summary>Puts back only the recorded keys whose current value is still what the optimizer wrote.</summary>
    internal static (byte[] bytes, List<KeyUndo> keys) UndoKeys(string format, byte[] current, List<RecordedChange> changes)
    {
        var keys = new List<KeyUndo>();
        KeyState Decide(string setting, string? cur, RecordedChange c)
        {
            var s = cur == c.NewRaw ? KeyState.Revert : cur == c.OldRaw ? KeyState.AlreadyOriginal : KeyState.Conflict;
            keys.Add(new KeyUndo(setting, cur, c.OldRaw, c.NewRaw, s));
            return s;
        }
        if (FormatHandlers.Find(format) is { } handler)
        {
            var document = handler.Read(current);
            foreach (var change in changes)
            {
                if (Decide(change.Setting, document.GetRaw(change.Locator), change) != KeyState.Revert) continue;
                if (change.OldRaw is null || !document.TrySet(change.Locator, change.OldRaw))
                    throw new FormatException("The recorded setting could not be restored safely.");
            }
            return (document.ToBytes(), keys);
        }

        switch (format)
        {
            case "ini":
            {
                var ini = IniDocument.Parse(current);
                foreach (var c in changes)
                {
                    var parts = c.Locator.Split('|');
                    if (parts is not ["ini", var sec, var key]) throw new FormatException($"bad locator {c.Locator}");
                    if (Decide(c.Setting, ini.Get(sec, key), c) != KeyState.Revert) continue;
                    if (c.OldRaw is null) ini.Remove(sec, key);
                    else if (!ini.Set(sec, key, c.OldRaw, false, out var err)) throw new FormatException(err);
                }
                return (ini.ToBytes(), keys);
            }
            case "kv":
            {
                var text = new UTF8Encoding(false, true).GetString(current);
                foreach (var c in changes)
                {
                    var key = c.Locator["kv|".Length..];
                    if (Decide(c.Setting, KvText.Get(text, key), c) != KeyState.Revert) continue;
                    if (!KvText.TrySet(ref text, key, c.OldRaw ?? "", out _)) throw new FormatException($"{key} could not be set");
                }
                return (new UTF8Encoding(false).GetBytes(text), keys);
            }
            case "json":
            {
                var root = JsonNode.Parse(current, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                           ?? throw new FormatException("empty JSON");
                foreach (var c in changes)
                {
                    var parts = c.Locator["json|".Length..].Split('.');
                    JsonNode? parent = root;
                    foreach (var p in parts[..^1]) parent = parent?[p];
                    var obj = parent as JsonObject;
                    var cur = obj is not null && obj.TryGetPropertyValue(parts[^1], out var node) ? node?.ToJsonString() : null;
                    if (Decide(c.Setting, cur, c) != KeyState.Revert) continue;
                    obj![parts[^1]] = c.OldRaw is null ? null : JsonNode.Parse(c.OldRaw);
                }
                return (Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = current.Contains((byte)'\n') })), keys);
            }
            case "red":
            {
                // REDengine UserSettings.json: put back the recorded options in place (value, and a list's index), nothing else.
                var red = RedSettingsFile.Parse(current);
                foreach (var c in changes)
                {
                    var parts = c.Locator.Split('|');
                    if (parts is not ["red", var group, var name]) throw new FormatException($"bad locator {c.Locator}");
                    if (Decide(c.Setting, red.GetRaw(group, name), c) != KeyState.Revert) continue;
                    string err = "";
                    if (c.OldRaw is null || !red.TrySet(group, name, c.OldRaw, out err)) throw new FormatException(err.Length > 0 ? err : $"{name} could not be restored");
                }
                return (red.ToBytes(), keys);
            }
            case "gvas":
            {
                var sav = GvasFile.Load(current);
                foreach (var c in changes)
                {
                    var p = sav.Find(c.Locator["gvas|".Length..]);
                    var curVal = p?.Get();
                    var cur = curVal is null ? null : Planner.GvasRaw(curVal);
                    if (Decide(c.Setting, cur, c) != KeyState.Revert) continue;
                    if (!p!.TrySet(FromRaw(c.OldRaw!, curVal!), out var err)) throw new FormatException(err);
                }
                var bytes = sav.Save();
                GvasFile.Load(bytes);
                return (bytes, keys);
            }
            default: throw new NotSupportedException($"key-level undo is not available for {format} files");
        }
    }

    /// <summary>Typed value from its JSON text, shaped like the property's current value.</summary>
    static object FromRaw(string raw, object like)
    {
        using var d = JsonDocument.Parse(raw);
        var e = d.RootElement;
        return like switch
        {
            int => e.GetInt32(),
            long => e.GetInt64(),
            double => e.GetDouble(),
            bool => e.GetBoolean(),
            string => e.GetString()!,
            int[] => e.EnumerateArray().Select(x => x.GetInt32()).ToArray(),
            _ => throw new NotSupportedException($"cannot restore a {like.GetType().Name} value"),
        };
    }

    /// <summary>
    /// Carries out a restore plan. Re-checks running games and that nothing changed since the plan, backs
    /// up the current state first (so the restore itself can be undone), then writes. In UndoChanges
    /// mode, items that cannot be merged are left alone and reported; conflicting keys are never overwritten.
    /// </summary>
    public static ApplyResult Restore(RestorePlan plan, RestoreMode mode)
    {
        var result = new ApplyResult();
        var images = ProcessGuard.RunningImages();
        var safety = NewRun($"Before restoring run {plan.Run.Id}{(plan.Game is null ? "" : $" ({plan.Game})")}", "restore");
        int n = 0;

        foreach (var grp in plan.Items.GroupBy(i => i.Entry.GameId))
        {
            var first = grp.First().Entry;
            if (ProcessGuard.RunningFrom(first.InstallDir, images) is { } exe)
            { result.Refused.Add($"{first.GameName}: running ({System.IO.Path.GetFileName(exe)}). Close it and try again."); continue; }

            var writes = new List<(RestoreItem item, byte[]? bytes)>();
            foreach (var item in grp)
            {
                if (mode == RestoreMode.UndoChanges && !item.CanUndoChanges)
                { result.Refused.Add($"{first.GameName}: {System.IO.Path.GetFileName(item.Title)} kept: {item.WhyNoUndo}. Use a full restore to replace it."); continue; }
                if (item.Entry.Kind == "file")
                {
                    var now = File.Exists(item.Entry.Path) ? File.ReadAllBytes(item.Entry.Path!) : null;
                    bool same = now is null ? item.CurrentBytes is null : item.CurrentBytes is not null && now.AsSpan().SequenceEqual(item.CurrentBytes);
                    if (!same) { result.Refused.Add($"{first.GameName}: {System.IO.Path.GetFileName(item.Title)} changed since the restore preview. Preview again."); continue; }
                    var bytes = mode == RestoreMode.UndoChanges ? item.UndoBytes : item.BackupBytes;
                    if (bytes is null) { result.Refused.Add($"{first.GameName}: {System.IO.Path.GetFileName(item.Title)}: {item.WhyNoUndo}"); continue; }
                    if (now is not null && bytes.AsSpan().SequenceEqual(now)) continue; // nothing to do
                    writes.Add((item, bytes));
                }
                else
                {
                    if (!Same(ReadValue(item.Entry.SubKey!, item.Entry.ValueName!), item.CurrentValue))
                    { result.Refused.Add($"{first.GameName}: {item.Entry.Setting} changed since the restore preview. Preview again."); continue; }
                    var k = item.Keys[0];
                    if (k.State == KeyState.AlreadyOriginal) continue;
                    if (k.State == KeyState.Conflict && mode == RestoreMode.UndoChanges) continue; // left alone, shown in the preview
                    writes.Add((item, null));
                }
            }
            if (writes.Count == 0) continue;

            try
            {
                foreach (var (item, bytes) in writes)
                {
                    if (item.Entry.Kind == "file")
                    {
                        if (item.CurrentBytes is not null)
                        {
                            var bak = System.IO.Path.Combine("files", $"{++n:D3}-{System.IO.Path.GetFileName(item.Entry.Path)}");
                            File.WriteAllBytes(System.IO.Path.Combine(safety.Dir, bak), item.CurrentBytes);
                            safety.Entries.Add(new BackupEntry
                            {
                                GameId = first.GameId, GameName = first.GameName, InstallDir = first.InstallDir, Kind = "file",
                                Path = item.Entry.Path, Format = item.Entry.Format, BackupFile = bak, WrittenSha256 = Sha(bytes!),
                                // Undoing this restore later means flipping each reverted key back.
                                Changes = mode == RestoreMode.UndoChanges
                                    ? item.Entry.Changes.Where(c => item.Keys.Any(k => k.Setting == c.Setting && k.State == KeyState.Revert))
                                        .Select(c => new RecordedChange { Id = c.Id, Setting = c.Setting, Locator = c.Locator, OldRaw = c.NewRaw, NewRaw = c.OldRaw }).ToList()
                                    : new(),
                            });
                        }
                    }
                    else
                    {
                        object? restored = item.Entry.ValueExisted ? RegistryValue(item.Entry) : null;
                        safety.Entries.Add(RegistryEntry(first.GameId, first.GameName, first.InstallDir, item.Entry.SubKey!, item.Entry.ValueName!, item.Entry.Setting ?? item.Entry.ValueName!, restored));
                    }
                }
                SaveManifest(safety);
                foreach (var (item, bytes) in writes)
                {
                    if (item.Entry.Kind == "file") AtomicWrite(item.Entry.Path!, bytes!);
                    else
                    {
                        using var key = Registry.CurrentUser.CreateSubKey(item.Entry.SubKey!, writable: true);
                        if (!item.Entry.ValueExisted) key.DeleteValue(item.Entry.ValueName!, throwOnMissingValue: false);
                        else key.SetValue(item.Entry.ValueName, RegistryValue(item.Entry), Enum.Parse<RegistryValueKind>(item.Entry.ValueKind!));
                    }
                }
                result.Applied.Add($"{first.GameName}: restored {writes.Count} item(s){(mode == RestoreMode.UndoChanges ? " (optimizer changes only)" : " (full backup)")}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                result.Errors.Add($"{first.GameName}: {ex.Message}. The state before this restore is saved in run {safety.Id}.");
            }
        }
        return Finish(safety, result);
    }

    static object RegistryValue(BackupEntry e)
    {
        var kind = Enum.Parse<RegistryValueKind>(e.ValueKind!);
        var v = e.ValueData!.Value;
        return kind switch
        {
            RegistryValueKind.DWord => v.GetInt32(),
            RegistryValueKind.QWord => v.GetInt64(),
            RegistryValueKind.Binary => v.GetBytesFromBase64(),
            RegistryValueKind.MultiString => v.EnumerateArray().Select(x => x.GetString()!).ToArray(),
            _ => v.GetString()!,
        };
    }

    /// <summary>Old entry point: full restore of a run (or one game), with the usual checks.</summary>
    public static ApplyResult Restore(BackupRun run, string? game = null) => Restore(PlanRestore(run, game), RestoreMode.FullBackup);
}
