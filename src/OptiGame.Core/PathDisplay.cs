namespace OptiGame.Core;

/// <summary>Paths as shown on screen: the user's profile folders become %LOCALAPPDATA%, %APPDATA% and %USERPROFILE%, so a screenshot or a pasted line never carries the account name.</summary>
public static class PathDisplay
{
    public static string Short(string? path)
    {
        if (string.IsNullOrEmpty(path)) return path ?? "";
        foreach (var (folder, name) in new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%"),
        })
        {
            if (folder.Length > 3 && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                && (path.Length == folder.Length || path[folder.Length] is '\\' or '/'))
                return name + path[folder.Length..];
        }
        return path;
    }
}
