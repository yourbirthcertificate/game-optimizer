using System;
using System.Collections.Generic;
using System.Linq;

namespace OptiGame.Core.Live;

/// <summary>Built-in knowledge: which processes are untouchable, which are safe to close.</summary>
public static class Catalog
{
    static HashSet<string> S(params string[] v) => new(v, StringComparer.OrdinalIgnoreCase);

    // Windows shell / system / security: never touched.
    static readonly HashSet<string> ProtectedExact = S(
        "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "svchost",
        "dwm", "explorer", "fontdrvhost", "sihost", "ctfmon", "taskhostw", "runtimebroker", "searchhost", "searchapp",
        "startmenuexperiencehost", "shellexperiencehost", "textinputhost", "audiodg", "conhost", "msmpeng", "nissrv",
        "securityhealthsystray", "securityhealthservice", "wudfhost", "dllhost", "spoolsv", "taskmgr", "applicationframehost",
        "lockapp", "memory compression", "secure system", "mousocoreworker", "searchindexer", "backgroundtaskhost",
        "smartscreen", "sgrmbroker", "wmiprvse", "dashost", "useroobebroker", "systemsettings", "openconsole", "gamemanager",
        "optigame", "ngciso", "ngcservice", "widgetboard", "crossdeviceservice");

    // Drivers, vendor control software, anti-cheat, game launchers.
    static readonly string[] ProtectedPrefix =
    {
        "nvidia", "nvcontainer", "nvdisplay", "nvcpl", "nvtray", "nvsphelper", "nvoa", "nvbroadcast", "nvngx",
        "igfx", "intel", "ipf", "dptf", "msi", "dragoncenter", "mystic", "realtek", "rtk", "ravbg", "waves", "dolby",
        "amd", "radeon", "cncmd", "atieclxx",
        "easyanticheat", "eaanticheat", "beservice", "battleye", "vgc", "vgtray", "faceit", "xigncode", "nprotect", "gameguard",
        "steam", "epic", "eosoverlay", "galaxy", "riotclient", "ubisoft", "upc", "eadesktop", "ealauncher", "origin", "battle.net",
        "xbox", "gamebar", "gamingservices", "socialclub", "rockstar", "playnite"
    };

    // Voice chat, recording, overlays, peripherals, dev tools: kept running on purpose.
    static readonly HashSet<string> KeepExact = S(
        "discord", "discordptb", "discordcanary", "teamspeak", "ts3client", "ts3client_win64", "mumble", "obs64", "obs32", "obs",
        "streamlabs", "streamlabs desktop", "medal", "rtss", "rtsshooksloader64", "hwinfo64", "hwinfo32", "lghub", "lghub_agent",
        "ghub", "icue", "claude", "code", "cursor", "devenv", "powershell", "pwsh", "cmd", "windowsterminal", "wt", "git",
        "node", "dotnet", "python", "pythonw", "idea64", "pycharm64", "rider64", "msbuild", "vbcscompiler", "sunshine", "parsec", "moonlight");

    static readonly string[] KeepPrefix = { "razer", "steelseries", "corsair", "lghub", "streamdeck", "elgato" };

    // Safe-to-close helpers (name or prefix → reason).
    static readonly Dictionary<string, string> SuggestedExact = new(StringComparer.OrdinalIgnoreCase)
    {
        ["onedrive"] = "Cloud sync", ["dropbox"] = "Cloud sync", ["googledrivefs"] = "Cloud sync", ["googledrive"] = "Cloud sync",
        ["teams"] = "Team chat", ["ms-teams"] = "Team chat", ["msteams"] = "Team chat", ["slack"] = "Team chat", ["zoom"] = "Video calls",
        ["skype"] = "Chat", ["skypeapp"] = "Chat", ["googleupdate"] = "Updater", ["microsoftedgeupdate"] = "Updater",
        ["itunes"] = "Media app", ["ituneshelper"] = "Media helper", ["wallpaper32"] = "Animated wallpaper", ["wallpaper64"] = "Animated wallpaper",
        ["wallpaperengine"] = "Animated wallpaper", ["rainmeter"] = "Desktop widgets", ["phoneexperiencehost"] = "Phone Link",
        ["yourphone"] = "Phone Link", ["widgets"] = "Windows widgets", ["acrotray"] = "PDF helper", ["ccxprocess"] = "Adobe helper",
        ["cclibrary"] = "Adobe helper", ["coresync"] = "Adobe sync", ["hd-player"] = "Android emulator", ["dnplayer"] = "Android emulator",
        ["ldplayer"] = "Android emulator", ["noxplayer"] = "Android emulator", ["msedgewebview2_unused"] = "Embedded browser"
    };

    static readonly (string prefix, string reason)[] SuggestedPrefix =
    {
        ("adobe", "Adobe background service"), ("creative cloud", "Adobe background service"),
        ("mumu", "Android emulator"), ("bluestacks", "Android emulator"), ("hd-", "Android emulator"),
        ("googlecrashhandler", "Crash reporter"), ("onedrive", "Cloud sync")
    };

    static readonly HashSet<string> Browsers = S("chrome", "msedge", "firefox", "vivaldi", "opera", "opera_gx", "brave", "arc", "iexplore", "waterfox", "librewolf", "zen");

    static readonly Dictionary<string, string> ReviewExact = new(StringComparer.OrdinalIgnoreCase)
    {
        ["spotify"] = "Music (fine to keep if you game with music)", ["telegram"] = "Chat", ["whatsapp"] = "Chat", ["notion"] = "Notes",
        ["outlook"] = "Mail", ["olk"] = "Mail", ["onenote"] = "Notes", ["winword"] = "Open document - save first",
        ["excel"] = "Open workbook - save first", ["powerpnt"] = "Open presentation - save first", ["notepad"] = "Notepad - may hold unsaved text",
        ["notepad++"] = "Editor - may hold unsaved text"
    };

    public static bool IsBrowser(string key) => Browsers.Contains(key);

    public static bool IsMediaPlayer(string key) =>
        key is "vlc" or "mpc-hc64" or "mpc-hc" or "mpc-be64" or "potplayermini64" or "wmplayer" or "video.ui" or "movies" or "mpv" or "netflix";

    /// <summary>Returns the tier and a short reason for a process group.</summary>
    public static (Tier tier, string reason) Classify(string key, string? path, bool hasWindow, double memMB)
    {
        if (path != null && path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
            return (Tier.Protected, "Windows component");
        if (ProtectedExact.Contains(key)) return (Tier.Protected, "Windows or security component");
        foreach (var p in ProtectedPrefix)
            if (key.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                return (Tier.Protected, p is "steam" or "epic" or "galaxy" or "origin" or "xbox" or "ubisoft" or "riotclient" ? "Game launcher" : "Driver, hardware tool or anti-cheat");
        if (KeepExact.Contains(key)) return (Tier.Keep, "Voice chat, overlay or dev tool");
        foreach (var p in KeepPrefix)
            if (key.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return (Tier.Keep, "Peripheral software");
        if (SuggestedExact.TryGetValue(key, out var r)) return (Tier.Suggested, r);
        foreach (var (prefix, reason) in SuggestedPrefix)
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return (Tier.Suggested, reason);
        if (key.Contains("updater", StringComparison.OrdinalIgnoreCase) || key.EndsWith("update", StringComparison.OrdinalIgnoreCase))
            return (Tier.Suggested, "Updater");
        if (Browsers.Contains(key)) return (Tier.Review, "Browser - tabs may hold unsaved work");
        if (ReviewExact.TryGetValue(key, out var rr)) return (Tier.Review, rr);
        if (hasWindow && memMB > 250) return (Tier.Review, "Open app using a lot of memory");
        return (Tier.Background, "Background process");
    }
}
