namespace OptiGame.Core.Formats;

internal interface IFormatHandler
{
    ISettingsDocument Read(byte[] bytes);
}

internal interface ISettingsDocument
{
    string? GetRaw(string locator);
    bool TrySet(string locator, string value);
    byte[] ToBytes();
}

internal static class FormatHandlers
{
    static readonly Dictionary<string, IFormatHandler> Handlers = new(StringComparer.Ordinal)
    {
        ["xml"] = new XmlSettingsHandler(),
        [SjsonSettings.Format] = new SjsonSettingsHandler(),
        [FlatIniSettings.Format] = new FlatIniHandler(),
    };

    public static IFormatHandler? Find(string format) => Handlers.GetValueOrDefault(format);
}
