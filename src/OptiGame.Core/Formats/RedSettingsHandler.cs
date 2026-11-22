namespace OptiGame.Core.Formats;

internal sealed class RedSettingsHandler : IFormatHandler
{
    public RedSettingsDocument Read(byte[] bytes) => new(RedSettingsFile.Parse(bytes));
    ISettingsDocument IFormatHandler.Read(byte[] bytes) => Read(bytes);
}

internal sealed class RedSettingsDocument(RedSettingsFile content) : ISettingsDocument
{
    public RedSettingsFile Content { get; } = content;

    public string? GetRaw(string locator)
    {
        var (group, name) = Locate(locator);
        return Content.GetRaw(group, name);
    }

    public bool TrySet(string locator, string value)
    {
        var (group, name) = Locate(locator);
        return Content.TrySet(group, name, value, out _);
    }

    public void RestoreRaw(string locator, string? value)
    {
        var (group, name) = Locate(locator);
        string error = "";
        if (value is null || !Content.TrySet(group, name, value, out error))
            throw new FormatException(error.Length > 0 ? error : $"{name} could not be restored");
    }

    public byte[] ToBytes() => Content.ToBytes();

    static (string Group, string Name) Locate(string locator) =>
        locator.Split('|') is ["red", var group, var name] ? (group, name)
            : throw new FormatException($"bad locator {locator}");
}
