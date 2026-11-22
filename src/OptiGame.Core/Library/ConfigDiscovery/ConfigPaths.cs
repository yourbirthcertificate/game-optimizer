using System.Security;

namespace OptiGame.Core.Library.ConfigDiscovery;

internal sealed record ConfigRoots(
    string Local, string LocalLow, string Roaming, string Documents, string SavedGames, string ProgramData,
    string Profile, string DataDir)
{
    public static ConfigRoots Current()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new(local, Child(Path.GetDirectoryName(local) ?? "", "LocalLow"),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Child(profile, "Saved Games"),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), profile, AppPaths.DataDir);
    }

    public string MyGames => Child(Documents, "My Games");

    public IEnumerable<string> Folders => new[] { Local, LocalLow, Roaming, Documents, MyGames, SavedGames, ProgramData }
        .Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

    static string Child(string root, string name) => root.Length == 0 ? "" : Path.Combine(root, name);
}

internal enum PathState { Missing, File, Directory, Blocked }

/// <summary>Restricts discovery to local paths and checks ancestors before directory listings or file probes.</summary>
internal sealed class ConfigPaths
{
    readonly string[] _allowed;
    readonly string[] _excluded;
    public ConfigRoots Roots { get; }
    public string? Install { get; }

    public ConfigPaths(ConfigRoots roots, string install)
    {
        Roots = roots;
        var candidate = CanonicalLocal(install);
        Install = candidate is not null && candidate.Length > 3 ? candidate : null;
        _allowed = roots.Folders.Append(roots.Profile).Append(Install ?? "")
            .Select(CanonicalLocal).OfType<string>().Where(path => path.Length > 3).ToArray();
        _excluded = new[] { roots.DataDir, AppPaths.DefaultDataDir }.Select(CanonicalLocal).OfType<string>().ToArray();
    }

    public string? CanonicalFile(string path)
    {
        var full = CanonicalLocal(path);
        return full is not null && _allowed.Any(root => IsWithin(full, root)) && !IsExcluded(full) ? full : null;
    }

    public bool IsExcluded(string path) => _excluded.Any(root => IsWithin(path, root));

    static bool IsWithin(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    public static string? CanonicalLocal(string path)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || !Path.IsPathFullyQualified(path))
            return null;
        try
        {
            var tail = path[3..].Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            return tail.All(IsFileComponent) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public static bool IsComponent(string name) =>
        name.Length > 0 && !name.EndsWith(' ') && !name.EndsWith('.') &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    public static bool IsFileComponent(string name)
    {
        if (!IsComponent(name)) return false;
        var stem = name.Split('.', 2)[0].TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$") return false;
        return stem.Length != 4 || !(stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
            || !"123456789¹²³".Contains(stem[3]);
    }

    public static bool IsId(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);

    public static bool IsReadError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SecurityException;

    public static bool IsBlocked(FileAttributes attributes) =>
        (attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.Device)) != 0;

    public static PathState Inspect(string canonical)
    {
        var root = Path.GetPathRoot(canonical)!;
        var drive = new DriveInfo(root).DriveType;
        if (drive is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
            return PathState.Blocked;

        try
        {
            var current = root;
            var attributes = File.GetAttributes(current);
            foreach (var segment in canonical[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                if (IsBlocked(attributes)) return PathState.Blocked;
                if (!attributes.HasFlag(FileAttributes.Directory)) return PathState.Missing;
                current = Path.Combine(current, segment);
                attributes = File.GetAttributes(current);
            }
            if (IsBlocked(attributes)) return PathState.Blocked;
            return attributes.HasFlag(FileAttributes.Directory) ? PathState.Directory : PathState.File;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return PathState.Missing;
        }
    }

    public static byte[] ReadBounded(string path)
    {
        const int maxBytes = 2 * 1024 * 1024;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        if (length > maxBytes) throw new IOException("The file exceeds the discovery read limit.");
        var bytes = new byte[(int)length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new IOException("The file grew while being read.");
        return bytes;
    }
}
