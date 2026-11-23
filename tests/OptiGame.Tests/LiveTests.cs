using System.Diagnostics;
using OptiGame.Core.Hardware;
using OptiGame.Core.Live;

namespace OptiGame.Tests;

/// <summary>
/// The 26 checks of GameClear's SelfTest, ported. Read-only against the real system (scan, detect, hardware, advice).
/// Anything that changes state (throttle, close, GPU preference, learning) runs only on throwaway dummy processes,
/// fake paths and a temp data folder created by the test itself.
/// </summary>
public sealed class LiveTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "optigame-live-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch (IOException) { } }

    LiveStore NewStore() => new(_tmp);

    // ---------- read-only: hardware ----------
    [Fact]
    public void Hardware_is_read_from_the_one_shared_profile()
    {
        var hw = HardwareDetector.Detect();
        Assert.False(string.IsNullOrWhiteSpace(hw.Cpu) || hw.Cpu == "Unknown CPU", hw.Cpu);
        Assert.True(hw.RamGb > 1, $"{hw.RamGb} GB");
        Assert.NotEqual("Unknown GPU", hw.Gpu);
        Assert.True(hw.LogicalCpus > 0);
        Assert.False(string.IsNullOrWhiteSpace(hw.Manufacturer));
    }

    // ---------- read-only: scan ----------
    [Fact]
    public async Task Scan_finds_processes_and_never_lists_windows_components()
    {
        var store = NewStore();
        var scanner = new Scanner();
        scanner.Scan(store);
        await Task.Delay(1200);
        var scan = scanner.Scan(store);
        Assert.True(scan.Groups.Count > 10, $"{scan.Groups.Count} groups");

        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var unsafeListed = scan.Groups.Where(g => g.Listable && g.Path.StartsWith(win, StringComparison.OrdinalIgnoreCase)).Select(g => g.Key).ToList();
        Assert.True(unsafeListed.Count == 0, string.Join(",", unsafeListed));

        string[] mustProtect = { "explorer", "dwm", "svchost", "csrss" };
        var wrong = mustProtect.Where(k => scan.Groups.Any(g => g.Key == k && g.Tier != Tier.Protected)).ToList();
        Assert.True(wrong.Count == 0, string.Join(",", wrong));

        var self = scan.Groups.FirstOrDefault(g => g.Pids.Contains(Environment.ProcessId));
        Assert.Equal(Tier.Protected, self?.Tier);
    }

    // ---------- read-only: game detection and advice ----------
    [Fact]
    public void Detection_and_advice_run_read_only_and_advise()
    {
        var store = NewStore();
        var hw = HardwareDetector.Detect();
        var hit = new GameDetector().Detect(store.Data);
        var tips = Advisor.Build(new AdvisorInput { Hw = hw, Mem = LiveStats.Mem(), Power = LiveStats.Power(), Gpu = LiveStats.Gpu(), Game = hit });
        Assert.NotEmpty(tips);
    }

    // ---------- classification rules (pure) ----------
    [Theory]
    [InlineData("discord", @"C:\x\discord.exe", true, 400.0, Tier.Keep)]
    [InlineData("onedrive", @"C:\x\OneDrive.exe", false, 200.0, Tier.Suggested)]
    [InlineData("vivaldi", @"C:\x\vivaldi.exe", true, 900.0, Tier.Review)]          // never pre-ticked
    [InlineData("easyanticheat_eos", @"C:\x\eac.exe", false, 50.0, Tier.Protected)]
    [InlineData("steam", @"C:\x\steam.exe", true, 200.0, Tier.Protected)]
    [InlineData("mumunxmain", @"C:\x\a.exe", true, 900.0, Tier.Suggested)]
    public void Catalog_classifies(string key, string path, bool window, double mem, Tier expected) =>
        Assert.Equal(expected, Catalog.Classify(key, path, window, mem).tier);

    // ---------- learning (temp store only) ----------
    [Fact]
    public void Unticking_a_suggestion_twice_keeps_it()
    {
        var store = NewStore();
        var g1 = new AppGroup { Key = "fooapp", Name = "FooApp", Tier = Tier.Suggested, Reason = "x", IsSelected = false };
        store.RecordChoices(new[] { g1 }); store.RecordChoices(new[] { g1 });
        var g1b = new AppGroup { Key = "fooapp", Name = "FooApp", Tier = Tier.Suggested, Reason = "x" };
        store.ApplyLearning(g1b);
        Assert.True(g1b.Tier == Tier.Keep && g1b.Learned);
    }

    [Fact]
    public void Closing_a_reviewed_app_twice_promotes_it_to_suggested()
    {
        var store = NewStore();
        var g2 = new AppGroup { Key = "barapp", Name = "BarApp", Tier = Tier.Review, Reason = "x", IsSelected = true };
        store.RecordChoices(new[] { g2 }); store.RecordChoices(new[] { g2 });
        var g2b = new AppGroup { Key = "barapp", Name = "BarApp", Tier = Tier.Review, Reason = "x" };
        store.ApplyLearning(g2b);
        Assert.True(g2b.Tier == Tier.Suggested && g2b.Learned);
    }

    // ---------- GPU preference (fake path, removed afterwards) ----------
    [Fact(Skip = "This test writes to the real HKCU hive.")]
    public void Gpu_preference_set_and_cleaned_up_on_a_fake_path()
    {
        string fake = @"C:\OptiGameTest\fake-game-" + Guid.NewGuid().ToString("N")[..6] + ".exe";
        try
        {
            GpuPref.Remove(fake);
            Assert.Equal(0, GpuPref.Get(fake));
            GpuPref.SetHighPerformance(fake);
            Assert.Equal(2, GpuPref.Get(fake));
        }
        finally { GpuPref.Remove(fake); }
        Assert.Equal(0, GpuPref.Get(fake));
    }

    // ---------- boost on DUMMY processes only ----------
    [Fact]
    public async Task Boost_restore_and_polite_close_on_dummy_processes()
    {
        var store = NewStore();
        var opt = new Booster(store);
        Process? dummyBg = null, dummyWin = null;
        try
        {
            dummyBg = Process.Start(new ProcessStartInfo("ping.exe", "-t 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
            dummyWin = Process.Start(new ProcessStartInfo("powershell.exe",
                "-NoProfile -WindowStyle Hidden -Command \"Add-Type -AssemblyName System.Windows.Forms; $f = New-Object System.Windows.Forms.Form; $f.Text='OptiGameDummy'; [void][System.Windows.Forms.Application]::Run($f)\"")
            { UseShellExecute = false, CreateNoWindow = true });
            await Task.Delay(2500);

            uint? before = Booster.ReadPriority(dummyBg!.Id);
            var gBg = new AppGroup { Key = "ping", Name = "dummy ping", Path = @"C:\Windows\System32\PING.EXE", Pids = new List<int> { dummyBg.Id }, Tier = Tier.Background };
            var plan = new BoostPlan(); plan.Throttle.Add(gBg); plan.Trim.Add(gBg);
            var res = await opt.BoostAsync(plan);
            uint? during = Booster.ReadPriority(dummyBg.Id);
            Assert.Equal(Native.NORMAL, before);
            Assert.Equal(Native.BELOW_NORMAL, during);
            Assert.True(res.Throttled >= 1 && res.Trimmed >= 1, $"throttled {res.Throttled}, trimmed {res.Trimmed}");
            opt.Restore(false);
            Assert.Equal(Native.NORMAL, Booster.ReadPriority(dummyBg.Id));

            // polite close: a window app closes without force
            store.Data.Settings.ForceClose = false;
            var gWin = new AppGroup { Key = "powershell", Name = "dummy window", Path = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", Pids = new List<int> { dummyWin!.Id }, HasWindow = true };
            var plan2 = new BoostPlan(); plan2.Close.Add(gWin);
            var res2 = await opt.BoostAsync(plan2);
            Assert.True(dummyWin.WaitForExit(500) && res2.Closed.Count == 1, $"closed={res2.Closed.Count} stubborn={res2.Stubborn.Count}");
            opt.Restore(false);

            // a windowless dummy ignores the polite close: it must NOT be killed unless force-close is on
            var gBg2 = new AppGroup { Key = "ping", Name = "dummy ping", Path = gBg.Path, Pids = new List<int> { dummyBg.Id } };
            var plan3 = new BoostPlan(); plan3.Close.Add(gBg2);
            var res3 = await opt.BoostAsync(plan3);
            Assert.True(!dummyBg.HasExited && res3.Stubborn.Count == 1, $"stubborn={res3.Stubborn.Count}");
            opt.Restore(false);

            store.Data.Settings.ForceClose = true;
            var plan4 = new BoostPlan(); plan4.Close.Add(gBg2);
            var res4 = await opt.BoostAsync(plan4);
            Assert.True(dummyBg.WaitForExit(1500) && res4.Closed.Count == 1);
            opt.Restore(false);
        }
        finally
        {
            try { if (dummyBg is { HasExited: false }) dummyBg.Kill(); } catch (InvalidOperationException) { }
            try { if (dummyWin is { HasExited: false }) dummyWin.Kill(); } catch (InvalidOperationException) { }
        }
    }

    // ---------- merged behaviour: auto-boost never touches the power plan; detection tiers ----------
    [Fact]
    public async Task A_boost_without_the_power_plan_flag_leaves_the_plan_alone()
    {
        var store = NewStore();
        var opt = new Booster(store);
        var before = Native.GetActiveScheme();
        await opt.BoostAsync(new BoostPlan { ApplyPowerPlan = false });
        Assert.Equal(before, Native.GetActiveScheme());
        Assert.Null(store.Data.PendingPowerRestore);
        opt.Restore(false);
    }

    [Fact]
    public void Detection_scores_user_list_over_known_over_library_and_ignores_helpers()
    {
        var lib = new List<string> { @"D:\Lib\Cool Game\" };
        var usr = new List<string> { @"E:\Mine\" };
        var none = new HashSet<string>();
        Assert.Equal(3, GameDetector.Why("anything", @"E:\Mine\x\a.exe", none, lib, usr).score);
        Assert.Equal(2, GameDetector.Why("cyberpunk2077", @"C:\Odd\cyberpunk2077.exe", none, lib, usr).score);
        Assert.Equal(2, GameDetector.Why("Foo-Win64-Shipping", @"C:\Odd\Foo-Win64-Shipping.exe", none, lib, usr).score);
        Assert.Equal(1, GameDetector.Why("coolgame", @"D:\Lib\Cool Game\bin\coolgame.exe", none, lib, usr).score);
        Assert.Null(GameDetector.Why("crashreporter", @"D:\Lib\Cool Game\crashreporter.exe", none, lib, usr).why);
        Assert.Null(GameDetector.Why("cyberpunk2077", Path.Combine(Path.GetTempPath(), "x", "cyberpunk2077.exe"), none, lib, usr).why);
        Assert.Null(GameDetector.Why("notepad", @"C:\Windows\notepad.exe", none, lib, usr).why);
    }

    [Fact]
    public void Game_exes_are_found_without_running_anything()
    {
        var root = Path.Combine(_tmp, "game");
        Directory.CreateDirectory(Path.Combine(root, "Proj", "Binaries", "Win64"));
        File.WriteAllText(Path.Combine(root, "Proj", "Binaries", "Win64", "Proj-Win64-Shipping.exe"), "x");
        File.WriteAllText(Path.Combine(root, "Proj.exe"), "x");
        File.WriteAllText(Path.Combine(root, "UnrealCEFSubProcess.exe"), "x");
        File.WriteAllText(Path.Combine(root, "unins000.exe"), "x");
        var exes = GameExes.Find(root).Select(Path.GetFileName).ToList();
        Assert.Contains("Proj-Win64-Shipping.exe", exes);
        Assert.DoesNotContain("unins000.exe", exes);
    }
}
