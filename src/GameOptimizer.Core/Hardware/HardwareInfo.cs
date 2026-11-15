namespace GameOptimizer.Core.Hardware;

/// <summary>What the optimizer knows about this PC.</summary>
public sealed class HardwareInfo
{
    public string Cpu { get; set; } = "Unknown CPU";
    public int CpuCores { get; set; }
    public int CpuThreads { get; set; }
    public double RamGb { get; set; }
    public string Gpu { get; set; } = "Unknown GPU";
    public double GpuVramGb { get; set; }
    public List<string> OtherGpus { get; set; } = new();
    public bool IsLaptop { get; set; }
    public string Model { get; set; } = "";

    /// <summary>Connected displays with their active and supported modes.</summary>
    public List<DisplayInfo> Displays { get; set; } = new();

    /// <summary>The main Windows display, or a 1080p60 stand-in when none could be read.</summary>
    public DisplayInfo Primary => Displays.FirstOrDefault(d => d.IsPrimary) ?? Displays.FirstOrDefault() ?? Fallback;

    static readonly DisplayInfo Fallback = new()
    {
        DeviceName = "", FriendlyName = "Unknown display", IsPrimary = true, CurrentWidth = 1920, CurrentHeight = 1080, CurrentHz = 60,
    };

    public DisplayInfo? FindDisplay(string? deviceName) =>
        string.IsNullOrEmpty(deviceName) ? null : Displays.FirstOrDefault(d => d.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase));

    // Older code paths and the PC-wide recommendation use the main display.
    public int NativeWidth { get => _w ?? Primary.Native.w; set => _w = value; }
    public int NativeHeight { get => _h ?? Primary.Native.h; set => _h = value; }
    /// <summary>Active refresh rate of the main display (not the highest it supports).</summary>
    public double ActiveRefreshHz { get => _hz ?? Primary.CurrentHz; set => _hz = value; }
    int? _w, _h;
    double? _hz;

    /// <summary>Default automatic frame cap: 3 fps under the main display's active refresh rate.</summary>
    public int FpsCap => DisplayMath.AutoCap(ActiveRefreshHz);

    /// <summary>Combined score used to place the PC-wide "Recommended" step.</summary>
    public double Score { get; set; }
    public QualityStep Recommended { get; set; } = QualityStep.Balanced;
    public List<string> Reasons { get; set; } = new();

    public override string ToString() =>
        $"{Cpu} ({CpuCores}C/{CpuThreads}T), {RamGb:0.#} GB RAM, {Gpu} ({GpuVramGb:0.#} GB), main display {Primary.ActiveModeText}";
}
