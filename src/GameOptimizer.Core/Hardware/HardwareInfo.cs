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
    public int NativeWidth { get; set; } = 1920;
    public int NativeHeight { get; set; } = 1080;
    public int MaxRefreshHz { get; set; } = 60;

    /// <summary>Combined score used to place the "Recommended" mark on the slider.</summary>
    public double Score { get; set; }
    public QualityStep Recommended { get; set; } = QualityStep.Balanced;
    public List<string> Reasons { get; set; } = new();

    /// <summary>Frame cap a few fps under the panel's refresh, so frame times stay even.</summary>
    public int FpsCap => Math.Max(30, MaxRefreshHz - 3);

    public override string ToString() =>
        $"{Cpu} ({CpuCores}C/{CpuThreads}T), {RamGb:0.#} GB RAM, {Gpu} ({GpuVramGb:0.#} GB), " +
        $"{NativeWidth}x{NativeHeight} @ {MaxRefreshHz} Hz";
}
