using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OptiGame.Core.Hardware;

namespace OptiGame.Core.Vendors;

/// <summary>One app a vendor ships, as written in vendors.json.</summary>
public sealed class VendorAppDef
{
    public string Name { get; set; } = "";
    /// <summary>Store (AppX) package family name, e.g. 9426MICRO-STARINTERNATION.MSICenter_kzh8wxbdkxb8p.</summary>
    public string? PackageFamily { get; set; }
    /// <summary>The app's id inside the package. When null it is looked up with Get-StartApps at launch time.</summary>
    public string? AppId { get; set; }
    /// <summary>Classic install paths; %ProgramFiles% style variables are expanded.</summary>
    public List<string> Exe { get; set; } = new();
}

public sealed class VendorMatch
{
    public List<string> Manufacturer { get; set; } = new();
    public List<string> Product { get; set; } = new();
    public List<string> Gpu { get; set; } = new();
}

/// <summary>A row of vendors.json: which machines it is for, which apps to look for, and a short tip.</summary>
public sealed class VendorEntry
{
    public string Id { get; set; } = "";
    /// <summary>"maker" (matched on the BIOS manufacturer and model) or "gpu" (matched on the graphics adapter names).</summary>
    public string Kind { get; set; } = "maker";
    public VendorMatch Match { get; set; } = new();
    public List<VendorAppDef> Apps { get; set; } = new();
    public string Tip { get; set; } = "";
}

/// <summary>An app that was found on this PC.</summary>
public sealed class VendorApp
{
    public string VendorId { get; init; } = "";
    public string Kind { get; init; } = "maker";
    public string Name { get; init; } = "";
    public string Tip { get; init; } = "";
    public string? PackageFamily { get; init; }
    public string? AppId { get; set; }
    public string? ExePath { get; init; }
    /// <summary>The user chose it by hand with "Choose your vendor app".</summary>
    public bool UserPicked { get; init; }

    public bool IsStoreApp => PackageFamily is not null && ExePath is null;
    public string Source => UserPicked ? "chosen by you" : IsStoreApp ? "Microsoft Store app" : "installed program";
    /// <summary>The shell target for a Store app, or null when the app id is not known yet.</summary>
    public string? AppsFolderTarget => IsStoreApp && AppId is not null ? VendorCatalog.AppsFolderTarget(PackageFamily!, AppId) : null;
}

public static class VendorCatalog
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static string UserFile => Path.Combine(AppPaths.DataDir, "vendors.json");

    sealed class FileModel { public List<VendorEntry> Vendors { get; set; } = new(); }

    /// <summary>The shipped table, then the user's file: an entry with the same id replaces the shipped one.</summary>
    public static List<VendorEntry> Load(string? userFile = null, List<string>? problems = null)
    {
        var list = new List<VendorEntry>();
        using (var s = typeof(VendorCatalog).Assembly.GetManifestResourceStream("OptiGame.Core.data.vendors.json"))
            if (s is not null) list.AddRange(Parse(new StreamReader(s).ReadToEnd(), problems, "built-in table"));
        var file = userFile ?? UserFile;
        if (File.Exists(file))
        {
            try
            {
                foreach (var e in Parse(File.ReadAllText(file), problems, file))
                {
                    list.RemoveAll(x => x.Id.Equals(e.Id, StringComparison.OrdinalIgnoreCase));
                    list.Add(e);
                }
            }
            catch (IOException ex) { problems?.Add($"{file}: {ex.Message}"); }
        }
        return list;
    }

    public static List<VendorEntry> Parse(string json, List<string>? problems = null, string source = "vendors.json")
    {
        try { return (JsonSerializer.Deserialize<FileModel>(json, Json) ?? new()).Vendors.Where(v => v.Id.Length > 0).ToList(); }
        catch (JsonException ex) { problems?.Add($"{source}: {ex.Message}"); return new(); }
    }

    // ------------------------------------------------------------------ matching

    /// <summary>Words match from their start; short ones ("hp", "msi") must be whole words so "hp" never matches "chip".</summary>
    public static bool TokenMatches(string text, string token)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(token)) return false;
        var tail = token.Length <= 3 ? @"(?![a-z0-9])" : "";
        return Regex.IsMatch(text, @"(?<![a-z0-9])" + Regex.Escape(token.Trim()) + tail, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool Matches(VendorEntry v, string manufacturer, string product, IEnumerable<string> gpus)
    {
        var m = v.Match;
        if (v.Kind.Equals("gpu", StringComparison.OrdinalIgnoreCase))
            return m.Gpu.Any(t => gpus.Any(g => TokenMatches(g, t)));
        bool maker = m.Manufacturer.Count == 0 || m.Manufacturer.Any(t => TokenMatches(manufacturer, t) || TokenMatches(product, t));
        bool prod = m.Product.Count == 0 || m.Product.Any(t => TokenMatches(product, t));
        return maker && prod;
    }

    /// <summary>
    /// Store package families installed for this user, read from the package repository in the registry
    /// (no PowerShell). Package full names look like Name_Version_Arch__PublisherId.
    /// </summary>
    public static HashSet<string> InstalledFamilies()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (k is null) return set;
            foreach (var full in k.GetSubKeyNames())
                if (FamilyOf(full) is { } fam) set.Add(fam);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return set;
    }

    /// <summary>"Pub.App_1.2.3.0_x64__abc123" becomes "Pub.App_abc123".</summary>
    public static string? FamilyOf(string packageFullName)
    {
        var parts = packageFullName.Split('_');
        if (parts.Length < 5) return null;                      // Name, Version, Arch, ResourceId, PublisherId
        var publisher = parts[^1];
        return publisher.Length == 0 ? null : parts[0] + "_" + publisher;
    }

    public static string AppsFolderTarget(string family, string appId) => $@"shell:AppsFolder\{family}!{appId}";

    public static string ExpandPath(string p) => Environment.ExpandEnvironmentVariables(p);

    /// <summary>Everything the table finds on this machine. Testable: all system lookups are parameters.</summary>
    public static List<VendorApp> Detect(IEnumerable<VendorEntry> table, string manufacturer, string product, IEnumerable<string> gpus,
                                         ISet<string> installedFamilies, Func<string, bool> fileExists)
    {
        var found = new List<VendorApp>();
        var gpuList = gpus.ToList();
        foreach (var v in table.Where(v => Matches(v, manufacturer, product, gpuList)))
            foreach (var a in v.Apps)
            {
                string? exe = a.Exe.Select(ExpandPath).FirstOrDefault(fileExists);
                bool store = a.PackageFamily is not null && installedFamilies.Contains(a.PackageFamily);
                if (!store && exe is null) continue;
                found.Add(new VendorApp
                {
                    VendorId = v.Id, Kind = v.Kind, Name = a.Name, Tip = v.Tip,
                    PackageFamily = store ? a.PackageFamily : null, AppId = store ? a.AppId : null,
                    ExePath = store ? null : exe,
                });
            }
        return found;
    }

    public static List<VendorApp> DetectHere(HardwareInfo hw, string? userPickedPath = null, string? userPickedName = null, string? userFile = null)
    {
        var gpus = new List<string> { hw.Gpu };
        gpus.AddRange(hw.OtherGpus);
        if (hw.DGpu is not null) gpus.Add(hw.DGpu);
        if (hw.IGpu is not null) gpus.Add(hw.IGpu);
        var found = Detect(Load(userFile), hw.Manufacturer, hw.Product, gpus, InstalledFamilies(), File.Exists);
        if (!string.IsNullOrWhiteSpace(userPickedPath) && File.Exists(userPickedPath))
            found.Insert(0, new VendorApp
            {
                VendorId = "user", Kind = "maker", UserPicked = true, ExePath = userPickedPath,
                Name = string.IsNullOrWhiteSpace(userPickedName) ? Path.GetFileNameWithoutExtension(userPickedPath) : userPickedName,
                Tip = "You chose this app. OptiGame+ only opens it.",
            });
        return found;
    }

    // ------------------------------------------------------------------ launching (never drives the app's settings)

    /// <summary>Looks the app id up with Get-StartApps (the Start menu's own list). Null when not found.</summary>
    public static async Task<string?> ResolveAppIdAsync(string family, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"(Get-StartApps | Where-Object {{ $_.AppID -like '{family.Replace("'", "''")}!*' }} | Select-Object -First 1).AppID\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var text = (await p.StandardOutput.ReadToEndAsync(ct)).Trim();
            await p.WaitForExitAsync(ct);
            int bang = text.IndexOf('!');
            return bang > 0 && text.StartsWith(family, StringComparison.OrdinalIgnoreCase) ? text[(bang + 1)..] : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException) { return null; }
    }

    /// <summary>Opens the app. Returns an empty string on success, or a short reason it could not.</summary>
    public static async Task<string> LaunchAsync(VendorApp app)
    {
        try
        {
            if (app.IsStoreApp)
            {
                app.AppId ??= await ResolveAppIdAsync(app.PackageFamily!);
                if (app.AppId is null) return $"Windows does not list {app.Name} in the Start menu, so it cannot be opened from here.";
                Process.Start(new ProcessStartInfo("explorer.exe", AppsFolderTarget(app.PackageFamily!, app.AppId)) { UseShellExecute = true });
                return "";
            }
            if (app.ExePath is null || !File.Exists(app.ExePath)) return $"{app.Name} was not found at its usual place any more.";
            Process.Start(new ProcessStartInfo(app.ExePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(app.ExePath) ?? "" });
            return "";
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return e.Message; }
    }
}
