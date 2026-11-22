using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Windows;
using OptiGame.App.ViewModels;
using OptiGame.Core;
using OptiGame.Core.Live;
using OptiGame.Core.Updates;

namespace OptiGame.App;

/// <summary>
/// Command line: <c>--tray</c> start hidden in the tray (used by "Start with Windows"); <c>--no-auto</c> start with
/// auto-boost off; <c>--data-dir &lt;folder&gt;</c> keep every setting, backup and cache in another folder (testing);
/// <c>--page dashboard|apps|games|settings</c> open on that page; <c>--elevated</c> used by "Restart as administrator".
/// </summary>
public partial class App : Application
{
    Mutex? _mutex;
    EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        int dd = Array.IndexOf(args, "--data-dir");
        if (dd >= 0 && dd + 1 < args.Length) AppPaths.Override = Path.GetFullPath(args[dd + 1]);
        bool elevated = args.Contains("--elevated");

        int th = Array.IndexOf(args, "--theme");
        if (th >= 0 && th + 1 < args.Length)
        {
            Chrome.ForceDark = args[th + 1].Equals("dark", StringComparison.OrdinalIgnoreCase) ? true : args[th + 1].Equals("light", StringComparison.OrdinalIgnoreCase) ? false : null;
            if (Chrome.ForceDark is { } dark) ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
        }

        // One copy per data folder, so a test copy started with --data-dir never wakes the real one.
        string suffix = AppPaths.IsDefault ? "" : "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.DataDir.ToLowerInvariant())))[..12];
        _mutex = new Mutex(false, @"Local\OptiGame.Single" + suffix);
        bool got;
        try { got = _mutex.WaitOne(elevated ? 8000 : 0); } catch (AbandonedMutexException) { got = true; }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\OptiGame.Show" + suffix);
        if (!got)
        {
            _showEvent.Set();      // wake the running instance and leave
            Shutdown();
            return;
        }

        // GameClear's data is imported once, and only into the real data folder.
        var store = new LiveStore(AppPaths.DataDir, AppPaths.IsDefault ? LiveStore.LegacyGameClearFile : null);
        if (args.Contains("--no-auto")) store.Data.Settings.AutoBoost = false;
        var vm = new AppVm(store);
        var win = new MainWindow(vm);
        MainWindow = win;
        if (store.ImportedFromGameClear) vm.Message = "Your GameClear settings and learned apps were carried over.";

        int pg = Array.IndexOf(args, "--page");
        if (pg >= 0 && pg + 1 < args.Length)
            win.Nav.SelectedIndex = Array.FindIndex(new[] { "dashboard", "apps", "games", "settings" }, n => n.Equals(args[pg + 1], StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i : 0;

        // Windows asks apps to close for an installer's Restart Manager, a sign-out or a shutdown: put everything back and go.
        SessionEnding += (_, _) => win.ExitApp();

        var waiter = new Thread(() =>
        {
            while (_showEvent.WaitOne()) Dispatcher.BeginInvoke(win.ShowFromTray);
        }) { IsBackground = true };
        waiter.Start();

        if (!args.Contains("--tray")) win.Show();
        vm.Start();
        _ = win.StartBackgroundWorkAsync();
        _ = Task.Run(UpdateService.CleanUp);     // setup files left by an earlier update
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
        base.OnExit(e);
    }
}
