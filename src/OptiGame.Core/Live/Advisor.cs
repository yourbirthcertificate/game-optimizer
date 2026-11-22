using System;
using OptiGame.Core.Hardware;
using System.Collections.Generic;
using System.Linq;

namespace OptiGame.Core.Live;

public sealed class AdvisorInput
{
    public HardwareInfo Hw { get; set; } = new();
    public MemStats Mem { get; set; } = new();
    public PowerInfo Power { get; set; } = new();
    public GpuStats? Gpu { get; set; }
    public ProcessScan? Scan { get; set; }
    public GameHit? Game { get; set; }
    public bool BoostActive { get; set; }
    public Action GoToApps { get; set; } = () => { };
    public Action<GameHit> FixGpu { get; set; } = _ => { };
    public Action FixPlan { get; set; } = () => { };
    /// <summary>The laptop maker's own control app (MSI Center, Armoury Crate...), when one was found.</summary>
    public OptiGame.Core.Vendors.VendorApp? Vendor { get; set; }
    public Action OpenVendor { get; set; } = () => { };
}

/// <summary>Turns live readings plus the hardware profile into a short, ranked list of suggestions.</summary>
public static class Advisor
{
    public static List<Tip> Build(AdvisorInput x)
    {
        var tips = new List<Tip>();
        bool gaming = x.Game is { Confident: true } || x.BoostActive;

        // GPU temperature (laptops throttle hard above ~87 C)
        if (x.Gpu is { } g && g.TempC >= 83)
            tips.Add(new Tip
            {
                Glyph = "", Severity = g.TempC >= 90 ? Severity.Critical : Severity.Caution,
                Title = $"GPU is running hot ({g.TempC} °C)",
                Detail = "Laptops throttle when they overheat. Raise the back of the laptop for airflow and keep the vents clear."
                    + (x.Vendor is { UserPicked: false } v ? $" {v.Tip}" : x.Vendor is { } u ? $" Use the fan or performance mode in {u.Name}." : " Use your laptop's fan boost mode if it has one."),
                ActionText = x.Vendor is { } vv ? $"Open {vv.Name}" : null,
                Action = x.Vendor is null ? null : x.OpenVendor,
            });

        // Battery
        if (x.Power.HasBattery && !x.Power.OnAc)
            tips.Add(new Tip
            {
                Glyph = "", Severity = gaming ? Severity.Critical : Severity.Caution,
                Title = $"Running on battery ({x.Power.Percent}%)",
                Detail = "Plug in the charger: on battery the GPU and CPU are capped well below their full speed."
            });

        // Which GPU is the game on?
        if (x.Game is { Confident: true } game && x.Hw.HybridGraphics && GpuPref.Get(game.Path) != 2)
            tips.Add(new Tip
            {
                Glyph = "", Severity = Severity.Caution,
                Title = $"Make {game.Name} use your {ShortGpu(x.Hw.DGpu)}",
                Detail = "This laptop has two graphics chips. Windows may run the game on Intel graphics unless you tell it otherwise. Takes effect next launch.",
                ActionText = "Use RTX GPU", Action = () => x.FixGpu(game)
            });

        // Power plan
        if (x.Power.Plan != null && !x.Power.PlanIsHigh && !x.BoostActive)
            tips.Add(new Tip
            {
                Glyph = "", Severity = gaming ? Severity.Caution : Severity.Info,
                Title = $"Power plan is “{x.Power.PlanName}”",
                Detail = "High performance keeps the CPU from down-clocking mid-game. OptiGame+ switches back when the game closes.",
                ActionText = "Switch for now", Action = x.FixPlan
            });

        // Memory pressure (16 GB is tight for modern games)
        double freeGB = x.Mem.AvailMB / 1024;
        var closable = x.Scan?.Groups.Where(a => a.Tier == Tier.Suggested).ToList() ?? new();
        double closableMB = closable.Sum(a => a.MemMB);
        if (freeGB < 3 || x.Mem.UsedPct >= 88)
            tips.Add(new Tip
            {
                Glyph = "", Severity = freeGB < 1.5 ? Severity.Critical : Severity.Caution,
                Title = $"Memory is tight: {freeGB:0.0} GB free of {x.Mem.TotalMB / 1024:0.#} GB",
                Detail = closableMB > 100
                    ? $"{closable.Count} safe-to-close app{(closable.Count == 1 ? "" : "s")} are holding about {Fmt(closableMB)}. Low memory causes stutter as Windows pages to disk."
                    : "Low memory causes stutter as Windows pages to disk. Review the Apps page for anything else you can close.",
                ActionText = "Review apps", Action = x.GoToApps
            });
        else if (closable.Count > 0 && closableMB >= 300)
            tips.Add(new Tip
            {
                Glyph = "", Severity = Severity.Info,
                Title = $"{closable.Count} app{(closable.Count == 1 ? "" : "s")} can be closed to free about {Fmt(closableMB)}",
                Detail = string.Join(", ", closable.OrderByDescending(a => a.MemMB).Take(3).Select(a => a.Name)) + (closable.Count > 3 ? " and more." : "."),
                ActionText = "Review apps", Action = x.GoToApps
            });

        // Big review-tier hogs (browsers)
        var hog = x.Scan?.Groups.Where(a => a.Tier == Tier.Review && a.MemMB > 800).OrderByDescending(a => a.MemMB).FirstOrDefault();
        if (hog != null && gaming)
            tips.Add(new Tip
            {
                Glyph = "", Severity = Severity.Info,
                Title = $"{hog.Name} is using {Fmt(hog.MemMB)}",
                Detail = "It may hold unsaved work, so OptiGame+ leaves it unticked. Save what you need and close it from the Apps page to free memory.",
                ActionText = "Review apps", Action = x.GoToApps
            });

        if (tips.Count == 0)
            tips.Add(new Tip
            {
                Glyph = "", Severity = Severity.Good,
                Title = "Everything looks good",
                Detail = x.Game != null ? $"{x.Game.Name} has the system to itself." : "Plenty of memory is free and nothing is fighting for resources."
            });

        return tips;
    }

    static string ShortGpu(string? n) => n == null ? "dedicated GPU" : n.Replace("NVIDIA ", "").Replace("GeForce ", "").Replace(" Laptop GPU", "").Trim();
    public static string Fmt(double mb) => mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";
}
