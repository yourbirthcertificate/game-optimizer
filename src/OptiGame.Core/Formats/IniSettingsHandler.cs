using System.Globalization;
using OptiGame.Core.Optimize;
using static OptiGame.Core.Optimize.Planner;

namespace OptiGame.Core.Formats;

internal sealed class IniSettingsHandler : IRuleFormatHandler
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public IniSettingsDocument Read(byte[] bytes) => new(IniDocument.Parse(bytes));
    ISettingsDocument IFormatHandler.Read(byte[] bytes) => Read(bytes);

    public void EditRule(GamePlan plan, OptimizeRequest req, FileEdit edit, IReadOnlyList<RuleValue> values)
    {
        var ini = Read(edit.Original).Content;
        foreach (var rv in values)
        {
            int slash = rv.Key.LastIndexOf('/');
            if (slash <= 0) { plan.Warnings.Add($"Rule ini key '{rv.Key}' needs the form Section/Key"); continue; }
            string section = rv.Key[..slash], key = rv.Key[(slash + 1)..];
            var old = ini.Get(section, key);
            if (old is null) { plan.Skipped.Add($"Kept: {key} (not in {Path.GetFileName(edit.Path)})"); continue; }
            var val = IniValue(rv.Value, old);
            if (old == val) continue;
            if (!Want(plan, req, rv.Id, rv.Category, key, old, val)) continue;
            if (ini.Set(section, key, val, addIfMissing: false, out var err))
                edit.Changes.Add(new(key, old, val) { Id = rv.Id, Category = rv.Category, Locator = $"ini|{section}|{key}", OldRaw = old, NewRaw = val });
            else plan.Skip(SkipKind.Ambiguous, $"Kept: {err}");
        }
        edit.Updated = ini.ToBytes();
    }

    /// <summary>Formats a value the way the existing INI value is written.</summary>
    static string IniValue(object v, string old) => v switch
    {
        bool b when old is "True" or "False" => b ? "True" : "False",
        bool b when old is "true" or "false" => b ? "true" : "false",
        bool b => b ? "1" : "0",
        int i when old.Contains('.') => i.ToString("0.000000", Inv),
        double d when !old.Contains('.') && d == Math.Floor(d) => d.ToString("0", Inv),
        double d => d.ToString(old.Contains('.') ? "0.000000" : "0.######", Inv),
        int[] a => string.Join(old.Contains('x') ? "x" : ",", a),
        _ => Convert.ToString(v, Inv) ?? "",
    };
}

internal sealed class IniSettingsDocument(IniDocument content) : ISettingsDocument
{
    public IniDocument Content { get; } = content;

    public string? GetRaw(string locator)
    {
        var (section, key) = Locate(locator);
        return Content.Get(section, key);
    }

    public bool TrySet(string locator, string value)
    {
        var (section, key) = Locate(locator);
        return Content.Set(section, key, value, addIfMissing: false, out _);
    }

    public void RestoreRaw(string locator, string? value)
    {
        var (section, key) = Locate(locator);
        if (value is null) Content.Remove(section, key);
        else if (!Content.Set(section, key, value, addIfMissing: false, out var error))
            throw new FormatException(error);
    }

    public byte[] ToBytes() => Content.ToBytes();

    static (string Section, string Key) Locate(string locator) =>
        locator.Split('|') is ["ini", var section, var key] ? (section, key)
            : throw new FormatException($"bad locator {locator}");
}
