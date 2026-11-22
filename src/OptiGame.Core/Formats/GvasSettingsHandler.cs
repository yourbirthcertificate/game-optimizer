using System.Text.Json;
using OptiGame.Core.Optimize;
using static OptiGame.Core.Optimize.Planner;

namespace OptiGame.Core.Formats;

internal sealed class GvasSettingsHandler : IRuleFormatHandler
{
    public GvasSettingsDocument Read(byte[] bytes) => new(GvasFile.Load(bytes));
    ISettingsDocument IFormatHandler.Read(byte[] bytes) => Read(bytes);

    public void EditRule(GamePlan plan, OptimizeRequest req, FileEdit edit, IReadOnlyList<RuleValue> values)
    {
        var document = Read(edit.Original);
        foreach (var rv in values)
        {
            var p = document.Content.Find(rv.Key);
            if (p is null) { plan.Skipped.Add($"Kept: {rv.Key} (not in {Path.GetFileName(edit.Path)})"); continue; }
            var old = p.Get();
            if (old is null) { plan.Skipped.Add($"Kept: {rv.Key} ({p.Type} values are not editable)"); continue; }
            object target = (rv.Value, old) switch
            {
                (int i, double) => (double)i,
                (double d, int) => (int)Math.Round(d), // the save stores whole numbers; fractional caps are rounded here
                _ => rv.Value,
            };
            if (Show(old) == Show(target)) continue;
            if (!Want(plan, req, rv.Id, rv.Category, rv.Key, Show(old), Show(target))) continue;
            if (!p.TrySet(target, out var err)) { plan.Skipped.Add($"Kept: {err}"); continue; }
            edit.Changes.Add(new(rv.Key, Show(old), Show(target))
            { Id = rv.Id, Category = rv.Category, Locator = "gvas|" + p.Name, OldRaw = GvasSettingsDocument.ToRaw(old), NewRaw = GvasSettingsDocument.ToRaw(target) });
        }
        edit.Updated = document.ToBytes();
    }
}

internal sealed class GvasSettingsDocument(GvasFile content) : ISettingsDocument
{
    public GvasFile Content { get; } = content;

    public string? GetRaw(string locator) =>
        Property(locator)?.Get() is { } value ? ToRaw(value) : null;

    public bool TrySet(string locator, string value) => SetRaw(locator, value, out _);

    public void RestoreRaw(string locator, string? value)
    {
        if (!SetRaw(locator, value!, out var error)) throw new FormatException(error);
    }

    bool SetRaw(string locator, string value, out string? error)
    {
        var property = Property(locator);
        if (property?.Get() is not { } current)
        {
            error = "The recorded GVAS property could not be restored safely.";
            return false;
        }
        return property.TrySet(FromRaw(value, current), out error);
    }

    GvasProperty? Property(string locator) => Content.Find(locator["gvas|".Length..]);

    public byte[] ToBytes()
    {
        var bytes = Content.Save();
        GvasFile.Load(bytes); // the new file must parse and round-trip too
        return bytes;
    }

    public static string ToRaw(object value) => JsonSerializer.Serialize(value);

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
}
