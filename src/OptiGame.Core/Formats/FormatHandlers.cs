using OptiGame.Core.Optimize;
using static OptiGame.Core.Optimize.Planner;

namespace OptiGame.Core.Formats;

internal interface IFormatHandler
{
    ISettingsDocument Read(byte[] bytes);
    ISettingsDocument ReadForUndo(byte[] bytes) => Read(bytes);
}

internal interface IRuleFormatHandler : IFormatHandler
{
    void EditRule(GamePlan plan, OptimizeRequest request, FileEdit edit, IReadOnlyList<RuleValue> values);
}

internal interface ISettingsDocument
{
    string? GetRaw(string locator);
    bool TrySet(string locator, string value);
    byte[] ToBytes();

    void RestoreRaw(string locator, string? value)
    {
        if (value is null || !TrySet(locator, value))
            throw new FormatException("The recorded setting could not be restored safely.");
    }
}

internal static class FormatHandlers
{
    internal static readonly IniSettingsHandler Ini = new();
    internal static readonly KvSettingsHandler Kv = new();
    internal static readonly RedSettingsHandler Red = new();
    internal static readonly GvasSettingsHandler Gvas = new();

    static readonly Dictionary<string, IFormatHandler> Handlers = new(StringComparer.Ordinal)
    {
        ["ini"] = Ini,
        ["kv"] = Kv,
        ["json"] = new JsonSettingsHandler(),
        ["gvas"] = Gvas,
        ["red"] = Red,
        ["xml"] = new XmlSettingsHandler(),
        [SjsonSettings.Format] = new SjsonSettingsHandler(),
        [FlatIniSettings.Format] = new FlatIniHandler(),
    };

    public static IFormatHandler? Find(string format) => Handlers.GetValueOrDefault(format);
}
