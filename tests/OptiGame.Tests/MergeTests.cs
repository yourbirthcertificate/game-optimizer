using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OptiGame.Core;
using OptiGame.Core.Library;
using OptiGame.Core.Live;
using OptiGame.Core.SpecCheck;
using OptiGame.Core.Updates;
using OptiGame.Core.Vendors;

namespace OptiGame.Tests;

/// <summary>Vendor-app table, Game Spec Check link, updater and data-folder compatibility.</summary>
public sealed class VendorTests
{
    const string MsiFamily = "9426MICRO-STARINTERNATION.MSICenter_kzh8wxbdkxb8p";
    static readonly HashSet<string> NoFamilies = new(StringComparer.OrdinalIgnoreCase);

    static List<VendorApp> Find(string maker, string product, IEnumerable<string>? gpus = null, ISet<string>? families = null, Func<string, bool>? exists = null) =>
        VendorCatalog.Detect(VendorCatalog.Load(Path.Combine(Path.GetTempPath(), "no-such-vendors.json")), maker, product, gpus ?? new[] { "Intel UHD" },
                             families ?? NoFamilies, exists ?? (_ => false));

    [Fact]
    public void The_shipped_table_covers_the_brands_asked_for()
    {
        var ids = VendorCatalog.Load(Path.Combine(Path.GetTempPath(), "none.json")).Select(v => v.Id).ToList();
        foreach (var id in new[] { "msi", "asus", "lenovo", "hp", "dell", "acer", "gigabyte", "razer", "nvidia" }) Assert.Contains(id, ids);
    }

    [Fact]
    public void MSI_Center_is_found_by_its_package_family_and_launches_through_AppsFolder()
    {
        var apps = Find("Micro-Star International Co., Ltd.", "Vector GP66 12UGS", families: new HashSet<string>(new[] { MsiFamily, "9426MICRO-STARINTERNATION.MSIGameBar_kzh8wxbdkxb8p" }, StringComparer.OrdinalIgnoreCase));
        var app = Assert.Single(apps);
        Assert.Equal("MSI Center", app.Name);
        Assert.True(app.IsStoreApp);
        Assert.Equal($@"shell:AppsFolder\{MsiFamily}!App", app.AppsFolderTarget);
    }

    [Fact]
    public void A_maker_whose_app_is_not_installed_shows_nothing()
        => Assert.Empty(Find("ASUSTeK COMPUTER INC.", "ROG Zephyrus"));

    [Theory]
    [InlineData("ASUSTeK COMPUTER INC.", "ASUS", "Armoury Crate")]
    [InlineData("LENOVO", "Legion 5", "Legion Space")]
    [InlineData("HP", "OMEN 16", "OMEN Gaming Hub")]
    [InlineData("Hewlett-Packard", "OMEN", "OMEN Gaming Hub")]
    [InlineData("Dell Inc.", "Alienware m16", "Alienware Command Center")]
    [InlineData("Acer", "Predator Helios", "PredatorSense")]
    public void Each_brand_maps_to_its_app(string maker, string product, string expectedName)
    {
        var table = VendorCatalog.Load(Path.Combine(Path.GetTempPath(), "none.json"));
        var entry = table.Single(v => VendorCatalog.Matches(v, maker, product, new[] { "x" }) && v.Kind == "maker");
        Assert.Contains(entry.Apps, a => a.Name == expectedName);
    }

    [Fact]
    public void Short_names_match_whole_words_only()
    {
        Assert.True(VendorCatalog.TokenMatches("HP Inc.", "hp"));
        Assert.False(VendorCatalog.TokenMatches("Chip Corp", "hp"));
        Assert.True(VendorCatalog.TokenMatches("ASUSTeK COMPUTER INC.", "asus"));
        Assert.False(VendorCatalog.TokenMatches("Micro-Star", "msi"));
        Assert.True(VendorCatalog.TokenMatches("Micro-Star", "micro-star"));
    }

    [Fact]
    public void Classic_programs_are_found_by_exe_path_and_unknown_brands_show_no_maker_app()
    {
        var gcc = Find("GIGABYTE", "AORUS 15", exists: p => p.EndsWith("GCC.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Gigabyte Control Center", Assert.Single(gcc).Name);
        Assert.False(gcc[0].IsStoreApp);
        Assert.DoesNotContain(Find("Some Brand", "X1"), a => a.Kind == "maker");
    }

    [Fact]
    public void NVIDIA_App_is_matched_on_the_graphics_card_not_the_maker()
    {
        var apps = Find("Some Brand", "X1", gpus: new[] { "NVIDIA GeForce RTX 3070 Ti Laptop GPU" }, exists: p => p.EndsWith("NVIDIA App.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("gpu", Assert.Single(apps).Kind);
    }

    [Fact]
    public void A_user_file_replaces_an_entry_by_id_and_can_add_new_brands()
    {
        var file = Path.Combine(Path.GetTempPath(), "vendors-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, """
            { "vendors": [
              { "id": "msi", "match": { "manufacturer": ["micro-star"] }, "apps": [ { "name": "My MSI thing", "exe": ["C:\\x\\a.exe"] } ], "tip": "mine" },
              { "id": "acme", "match": { "manufacturer": ["acme"] }, "apps": [ { "name": "Acme Hub", "exe": ["C:\\acme.exe"] } ] } ] }
            """);
        try
        {
            var table = VendorCatalog.Load(file);
            Assert.Equal("My MSI thing", table.Single(v => v.Id == "msi").Apps.Single().Name);
            Assert.Contains(table, v => v.Id == "acme");
            Assert.Contains(table, v => v.Id == "asus");     // shipped entries stay
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_broken_user_file_is_reported_and_ignored()
    {
        var file = Path.Combine(Path.GetTempPath(), "vendors-bad-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, "{ not json");
        try
        {
            var problems = new List<string>();
            var table = VendorCatalog.Load(file, problems);
            Assert.NotEmpty(problems);
            Assert.Contains(table, v => v.Id == "msi");
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Package_family_is_derived_from_a_full_name()
    {
        Assert.Equal(MsiFamily, VendorCatalog.FamilyOf("9426MICRO-STARINTERNATION.MSICenter_2.0.75.0_x64__kzh8wxbdkxb8p"));
        Assert.Null(VendorCatalog.FamilyOf("garbage"));
    }

    [Fact]
    public void A_vendor_app_chosen_by_hand_comes_first()
    {
        var exe = Path.Combine(Path.GetTempPath(), "MyVendor-" + Guid.NewGuid().ToString("N")[..6] + ".exe");
        File.WriteAllText(exe, "x");
        try
        {
            var apps = VendorCatalog.DetectHere(new OptiGame.Core.Hardware.HardwareInfo { Manufacturer = "Nobody", Product = "X", Gpu = "Intel" }, exe, null, Path.Combine(Path.GetTempPath(), "none.json"));
            Assert.True(apps[0].UserPicked);
            Assert.StartsWith("MyVendor-", apps[0].Name);
        }
        finally { File.Delete(exe); }
    }

    [Fact]
    public void Vendor_tips_point_the_GPU_hot_advice_at_the_detected_app()
    {
        var vendor = new VendorApp { VendorId = "msi", Name = "MSI Center", Tip = "Pick Cooler Boost in MSI Center." };
        var tips = Advisor.Build(new AdvisorInput
        {
            Hw = new OptiGame.Core.Hardware.HardwareInfo(), Mem = new MemStats { TotalMB = 16000, AvailMB = 9000 }, Power = new PowerInfo(),
            Gpu = new GpuStats { TempC = 88 }, Vendor = vendor,
        });
        var hot = tips.First(t => t.Title.StartsWith("GPU is running hot"));
        Assert.Contains("MSI Center", hot.Detail);
        Assert.Equal("Open MSI Center", hot.ActionText);
        var generic = Advisor.Build(new AdvisorInput { Hw = new OptiGame.Core.Hardware.HardwareInfo(), Mem = new MemStats { TotalMB = 16000, AvailMB = 9000 }, Power = new PowerInfo(), Gpu = new GpuStats { TempC = 88 } })
            .First(t => t.Title.StartsWith("GPU is running hot"));
        Assert.DoesNotContain("MSI", generic.Detail);
        Assert.Null(generic.ActionText);
    }
}

public sealed class SpecCheckTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "optigame-spec-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    static GameInfo Game(string name, string? steam) => new() { Id = "x:" + name, Name = name, InstallDir = @"C:\g", Store = "Steam", SteamAppId = steam };

    [Fact]
    public void The_page_address_uses_the_Steam_id_or_the_PCGamingWiki_title()
    {
        Assert.Equal("http://127.0.0.1:8765/#steam/1091500", SpecCheckLauncher.GameUrl(8765, Game("Cyberpunk 2077", "1091500")));
        Assert.Equal("http://127.0.0.1:8766/#pcgw/Some%20Game%20%26%20Co", SpecCheckLauncher.GameUrl(8766, Game("Some Game & Co", null)));
        Assert.Equal("http://127.0.0.1:8765/#steam/42", SpecCheckLauncher.GameUrl(8765, Game("X", null), linkedSteamId: "42"));
        Assert.StartsWith("http://127.0.0.1:8765/#pcgw/", SpecCheckLauncher.GameUrl(8765, Game("X", null), linkedSteamId: "4x2"));
    }

    void MakeSource(string folder, string version)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(Path.Combine(dir, "specbench"));
        File.WriteAllText(Path.Combine(dir, "run.py"), "# stub");
        File.WriteAllText(Path.Combine(dir, "specbench", "__init__.py"), $"\"\"\"x\"\"\"\n__version__ = \"{version}\"\n");
    }

    [Fact]
    public void The_newest_game_spec_check_beside_the_app_wins()
    {
        MakeSource("game-spec-check", "1.1.0");
        MakeSource("game-spec-check-update", "1.1.1");
        var appDir = Path.Combine(_root, "game-optimizer", "worktree", "bin");
        Directory.CreateDirectory(appDir);
        var found = SpecCheckLauncher.Locate(null, appDir);
        Assert.NotNull(found);
        Assert.Contains("game-spec-check-update", found!.Path);
        Assert.Equal(new Version(1, 1, 1), found.Version);
    }

    [Fact]
    public void A_chosen_path_is_considered_and_nothing_found_returns_null()
    {
        var appDir = Path.Combine(_root, "empty", "bin");
        Directory.CreateDirectory(appDir);
        Assert.Null(SpecCheckLauncher.Locate(null, appDir, exists: _ => false));
        MakeSource("elsewhere", "9.0.0");
        var chosen = Path.Combine(_root, "elsewhere", "run.py");
        Assert.Equal(chosen, SpecCheckLauncher.Locate(chosen, appDir)!.Path);
    }

    [Fact]
    public async Task Ping_recognises_game_spec_check_and_nothing_else()
    {
        using var good = Serve("""{"app": "game-spec-check", "version": "1.1.1"}""", out int goodPort);
        using var other = Serve("""{"app": "something-else"}""", out int otherPort);
        Assert.True(await SpecCheckLauncher.IsSpecCheckAsync(goodPort));
        Assert.False(await SpecCheckLauncher.IsSpecCheckAsync(otherPort));
        Assert.Equal(goodPort, await SpecCheckLauncher.FindRunningAsync(goodPort, 1));
    }

    /// <summary>A tiny HTTP server that answers every request with the given JSON.</summary>
    static IDisposable Serve(string json, out int port)
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        port = ((IPEndPoint)l.LocalEndpoint).Port;
        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    using var c = await l.AcceptTcpClientAsync(cts.Token);
                    var s = c.GetStream();
                    var buf = new byte[4096]; await s.ReadAsync(buf, cts.Token);
                    var body = Encoding.UTF8.GetBytes(json);
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await s.WriteAsync(head, cts.Token); await s.WriteAsync(body, cts.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or SocketException) { }
            }
        });
        return new Stopper(() => { cts.Cancel(); l.Stop(); });
    }

    sealed class Stopper(Action stop) : IDisposable { public void Dispose() => stop(); }
}

public sealed class UpdateTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "optigame-upd-" + Guid.NewGuid().ToString("N")[..8]);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch (IOException) { } }

    static string Release(string tag, string? digest, string url = "https://github.com/yourbirthcertificate/game-optimizer/releases/download/v9.9.9/OptiGame-Setup-9.9.9.exe", bool pre = false) => $$"""
        { "tag_name": "{{tag}}", "name": "OptiGame+ {{tag}}", "body": "notes", "html_url": "https://github.com/yourbirthcertificate/game-optimizer/releases/tag/{{tag}}",
          "draft": false, "prerelease": {{(pre ? "true" : "false")}},
          "assets": [ { "name": "OptiGame-Setup-9.9.9.exe", "browser_download_url": "{{url}}", "size": 5, "digest": {{(digest is null ? "null" : $"\"{digest}\"")}} },
                      { "name": "optigame.exe", "browser_download_url": "https://example.com/x", "size": 1 } ] }
        """;

    [Theory]
    [InlineData("v0.3.0", "0.3.0")]
    [InlineData("0.3.0+abc123", "0.3.0")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("V2.0.1-beta", "2.0.1")]
    public void Versions_parse(string text, string expected)
    {
        Assert.True(UpdateService.TryParseVersion(text, out var v));
        Assert.Equal(Version.Parse(expected), v);
    }

    [Fact]
    public void Release_json_gives_the_setup_asset_and_its_digest()
    {
        var hex = new string('a', 64);
        var info = UpdateService.Parse(Release("v9.9.9", "sha256:" + hex))!;
        Assert.Equal(new Version(9, 9, 9), info.Version);
        Assert.Equal("OptiGame-Setup-9.9.9.exe", info.AssetName);
        Assert.Equal(hex, info.Sha256);
        Assert.True(UpdateService.IsNewer(info, new Version(0, 3, 0)));
        Assert.False(UpdateService.IsNewer(info, new Version(9, 9, 9)));
        Assert.True(UpdateService.CanInstall(info));
    }

    [Fact]
    public void A_release_without_a_digest_or_from_another_address_cannot_be_installed()
    {
        Assert.False(UpdateService.CanInstall(UpdateService.Parse(Release("v9.9.9", null))!));
        Assert.False(UpdateService.CanInstall(UpdateService.Parse(Release("v9.9.9", "sha256:" + new string('b', 64), "https://evil.example/OptiGame-Setup.exe"))!));
        Assert.Null(UpdateService.Parse(Release("v9.9.9", null, pre: true)));
        Assert.Null(UpdateService.Parse("not json"));
    }

    sealed class Fake(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }

    [Fact]
    public async Task Download_is_verified_against_the_digest_and_a_mismatch_is_deleted()
    {
        var data = Encoding.UTF8.GetBytes("setup-bytes");
        var good = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var info = UpdateService.Parse(Release("v9.9.9", "sha256:" + good))!;
        var file = await UpdateService.DownloadAsync(info, _tmp, http: new HttpClient(new Fake(data)));
        Assert.Equal(data, await File.ReadAllBytesAsync(file));

        var bad = UpdateService.Parse(Release("v9.9.9", "sha256:" + new string('0', 64)))!;
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateService.DownloadAsync(bad, _tmp, http: new HttpClient(new Fake(data))));
        Assert.False(File.Exists(Path.Combine(_tmp, "OptiGame-Setup-9.9.9.exe")));

        var none = UpdateService.Parse(Release("v9.9.9", null))!;
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateService.DownloadAsync(none, _tmp, http: new HttpClient(new Fake(data))));
    }

    [Fact]
    public async Task Check_reports_newer_same_and_no_release()
    {
        var hex = new string('c', 64);
        var h = new HttpClient(new Fake(Encoding.UTF8.GetBytes(Release("v9.9.9", "sha256:" + hex))));
        var (up, _) = await UpdateService.CheckAsync(h, "http://x/", new Version(0, 3, 0));
        Assert.NotNull(up);
        var (none, status) = await UpdateService.CheckAsync(h, "http://x/", new Version(9, 9, 9));
        Assert.Null(none);
        Assert.Contains("latest", status);
    }

    [Fact]
    public void An_exe_outside_the_install_folder_is_not_updated_in_place()
    {
        Assert.False(UpdateService.RunsFromInstall(@"C:\somewhere\else"));
        Assert.True(UpdateService.RunsFromInstall(UpdateService.InstallDir + Path.DirectorySeparatorChar));
    }
}

/// <summary>The data folder keeps the optimizer's name, so existing backups and history keep working.</summary>
[Collection("data dir")]
public sealed class DataFolderTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "optigame-data-" + Guid.NewGuid().ToString("N")[..8]);
    readonly string? _oldEnv = Environment.GetEnvironmentVariable("GAMEOPT_DATA_DIR");
    public DataFolderTests() => Environment.SetEnvironmentVariable("GAMEOPT_DATA_DIR", null);
    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GAMEOPT_DATA_DIR", _oldEnv);
        AppPaths.Override = null;
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    [Fact]
    public void The_default_folder_is_still_LocalAppData_GameOpt()
    {
        Assert.EndsWith(Path.Combine("AppData", "Local", "GameOpt"), AppPaths.DefaultDataDir, StringComparison.OrdinalIgnoreCase);
        Assert.True(AppPaths.IsDefault);
        Assert.Equal(Path.Combine(AppPaths.DefaultDataDir, "backups"), AppPaths.BackupsDir);
        Assert.Equal(Path.Combine(AppPaths.DefaultDataDir, "prefs.json"), AppPaths.PrefsFile);
    }

    [Fact]
    public void Override_and_environment_variables_move_everything()
    {
        AppPaths.Override = Path.Combine(_tmp, "o");
        Assert.Equal(Path.Combine(_tmp, "o", "backups"), AppPaths.BackupsDir);
        Assert.False(AppPaths.IsDefault);
        AppPaths.Override = null;
        Environment.SetEnvironmentVariable("OPTIGAME_DATA_DIR", Path.Combine(_tmp, "e"));
        try { Assert.Equal(Path.Combine(_tmp, "e", "prefs.json"), AppPaths.PrefsFile); }
        finally { Environment.SetEnvironmentVariable("OPTIGAME_DATA_DIR", null); }
    }

    [Fact]
    public void Backup_runs_made_by_the_old_layout_are_still_listed()
    {
        // The run folder layout of GameOptimizer 0.2: <data>\backups\<id>\run.json
        AppPaths.Override = Path.Combine(_tmp, "data");
        Directory.CreateDirectory(Path.Combine(AppPaths.BackupsDir, "20261115-183627-302"));
        var real = Path.Combine(_tmp, "g.ini");
        File.WriteAllText(real, "[A]\nx=1\n");
        var game = new GameInfo { Id = "t:1", Name = "T", InstallDir = _tmp, Store = "Test" };
        var plan = new OptiGame.Core.Optimize.GamePlan { Game = game, Step = OptiGame.Core.Hardware.QualityStep.High, Status = OptiGame.Core.Optimize.PlanStatus.Ready };
        var edit = new OptiGame.Core.Optimize.FileEdit { Path = real, Original = File.ReadAllBytes(real), Updated = Encoding.UTF8.GetBytes("[A]\nx=2\n"), Format = "ini" };
        edit.Changes.Add(new OptiGame.Core.Optimize.SettingChange("x", "1", "2"));
        plan.Files.Add(edit);
        var res = OptiGame.Core.Safety.Applier.Apply(new[] { plan }, "after the merge");
        Assert.NotNull(res.RunId);
        Assert.Contains(OptiGame.Core.Safety.Applier.Runs(), r => r.Id == res.RunId);
        OptiGame.Core.Safety.Applier.Restore(OptiGame.Core.Safety.Applier.Runs().First(r => r.Id == res.RunId));
        Assert.Equal("[A]\nx=1\n", File.ReadAllText(real));
    }

    [Fact]
    public void GameClear_data_is_imported_once_and_only_when_asked()
    {
        var legacy = Path.Combine(_tmp, "GameClear", "data.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, JsonSerializer.Serialize(new LiveData
        {
            BoostCount = 7, Games = { new SavedGame { Name = "Old", Path = @"C:\old\old.exe" } },
            Prefs = { ["foo"] = new Pref { Name = "Foo", Keep = true } },
        }));

        var plain = new LiveStore(Path.Combine(_tmp, "none"));               // temp / --data-dir folders never import
        Assert.False(plain.ImportedFromGameClear);

        var dir = Path.Combine(_tmp, "real");
        var first = new LiveStore(dir, legacy);
        Assert.True(first.ImportedFromGameClear);
        Assert.Equal(7, first.Data.BoostCount);
        Assert.True(first.Data.Prefs["foo"].Keep);
        Assert.Equal(@"C:\old\old.exe", first.Data.Games.Single().Path);
        Assert.True(File.Exists(Path.Combine(dir, "live.json")));

        first.Data.BoostCount = 8; first.Save();
        var second = new LiveStore(dir, legacy);                               // live.json now wins over the old file
        Assert.False(second.ImportedFromGameClear);
        Assert.Equal(8, second.Data.BoostCount);
    }
}
