using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using OptiGame.Core.Live;

namespace OptiGame.App;

/// <summary>
/// Windows 11 window chrome for every OptiGame+ window: the native title bar (dark when Windows is dark),
/// rounded corners and the Mica backdrop. Call <see cref="Attach"/> once per window.
/// </summary>
public static class Chrome
{
    public static void Attach(Window w)
    {
        w.Background = Brushes.Transparent;
        w.SourceInitialized += (_, _) => Apply(w);
        SystemEvents.UserPreferenceChanged += OnPrefs;
        w.Closed += (_, _) => SystemEvents.UserPreferenceChanged -= OnPrefs;

        void OnPrefs(object? s, UserPreferenceChangedEventArgs e) => w.Dispatcher.BeginInvoke(() => Apply(w));
    }

    public static void Apply(Window w)
    {
        IntPtr hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } ct) ct.BackgroundColor = Colors.Transparent;

        int dark = IsDarkTheme() ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int round = 2;      // DWMWCP_ROUND
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        int mica = 2;       // DWMSBT_MAINWINDOW = Mica
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref mica, sizeof(int));
        var m = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Native.DwmExtendFrameIntoClientArea(hwnd, ref m);
    }

    /// <summary>Set by <c>--theme dark|light</c> (for testing both looks); null follows Windows.</summary>
    public static bool? ForceDark { get; set; }

    public static bool IsDarkTheme()
    {
        if (ForceDark is { } f) return f;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
    }
}
