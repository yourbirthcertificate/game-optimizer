using System.Globalization;
using System.Text;
using OptiGame.Core.Optimize;
using static OptiGame.Core.Optimize.Planner;

namespace OptiGame.Core.Formats;

internal sealed class KvSettingsHandler : IRuleFormatHandler
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly UTF8Encoding ReadEncoding = new(false, true);

    public KvSettingsDocument Read(byte[] bytes)
    {
        var text = ReadEncoding.GetString(bytes);
        return new(text, KvNode.Parse(text));
    }

    ISettingsDocument IFormatHandler.Read(byte[] bytes) => Read(bytes);

    // Older backups can restore an unambiguous line even if unrelated text no longer parses as a tree.
    public ISettingsDocument ReadForUndo(byte[] bytes) => new KvSettingsDocument(ReadEncoding.GetString(bytes), null);

    public void EditRule(GamePlan plan, OptimizeRequest req, FileEdit edit, IReadOnlyList<RuleValue> values)
    {
        var document = Read(edit.Original);
        foreach (var rv in values)
        {
            var val = rv.Value is bool b ? (b ? "1" : "0") : rv.Value is double d ? d.ToString("0.######", Inv) : Convert.ToString(rv.Value, Inv)!;
            var locator = "kv|" + rv.Key;
            var old = document.GetRaw(locator);
            if (old is null) { plan.Skipped.Add($"Kept: {rv.Key} (not in {Path.GetFileName(edit.Path)}, or listed twice)"); continue; }
            if (old == val || !Want(plan, req, rv.Id, rv.Category, rv.Key, old, val)) continue;
            if (!document.TrySet(locator, val)) continue;
            edit.Changes.Add(new(rv.Key, old, val) { Id = rv.Id, Category = rv.Category, Locator = locator, OldRaw = old, NewRaw = val });
        }
        edit.Updated = document.ToBytes();
    }
}

internal sealed class KvSettingsDocument(string text, KvNode? root) : ISettingsDocument
{
    string _text = text;
    public KvNode? Root { get; } = root;

    public string? GetRaw(string locator) => KvText.Get(_text, locator["kv|".Length..]);
    public bool TrySet(string locator, string value) => KvText.TrySet(ref _text, locator["kv|".Length..], value, out _);

    public void RestoreRaw(string locator, string? value)
    {
        if (!TrySet(locator, value ?? "")) throw new FormatException($"{locator["kv|".Length..]} could not be set");
    }

    public byte[] ToBytes() => Encoding.UTF8.GetBytes(_text);
}
