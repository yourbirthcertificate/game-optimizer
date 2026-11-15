using System.Text;
using GameOptimizer.Core.Formats;

namespace GameOptimizer.Tests;

/// <summary>Hand-built GVAS files in both tag layouts. No real save files are used.</summary>
public class GvasTests
{
    sealed class Builder
    {
        readonly MemoryStream _ms = new();
        public Builder Bytes(params byte[] b) { _ms.Write(b); return this; }
        public Builder I32(int v) => Bytes(BitConverter.GetBytes(v));
        public Builder U16(ushort v) => Bytes(BitConverter.GetBytes(v));
        public Builder Str(string s) { var b = Encoding.ASCII.GetBytes(s + "\0"); I32(b.Length); return Bytes(b); }
        public Builder Type(string name, params Action<Builder>[] ps) { Str(name); I32(ps.Length); foreach (var p in ps) p(this); return this; }
        public byte[] ToArray() => _ms.ToArray();
    }

    static Builder Header(int ue5)
    {
        var b = new Builder().Bytes("GVAS"u8.ToArray()).I32(3).I32(522).I32(ue5).U16(5).U16(ue5 >= 1012 ? (ushort)7 : (ushort)3).U16(1)
            .I32(1234).Str("++Test+Main").I32(3).I32(1).Bytes(new byte[16]).I32(1).Str("/Game/Test/BP_Settings.BP_Settings_C");
        if (ue5 >= 1011) b.Bytes(0);
        return b;
    }

    /// <summary>UE5.4+ layout: complete type names, bools in the flags byte.</summary>
    static byte[] Modern()
    {
        var b = Header(1018);
        b.Str("Resolution").Type("StructProperty", x => x.Type("IntPoint", y => y.Type("/Script/CoreUObject"))).I32(8).Bytes(0x08).I32(1920).I32(1080);
        b.Str("WindowMode").Type("ByteProperty", x => x.Type("EWindowMode", y => y.Type("/Script/Engine")))
            .I32(4 + "EWindowMode::WindowedFullscreen".Length + 1).Bytes(0).Str("EWindowMode::WindowedFullscreen");
        b.Str("VSyncEnabled?").Type("BoolProperty").I32(0).Bytes(0x10);
        b.Str("FrameRateLimit").Type("DoubleProperty").I32(8).Bytes(0).Bytes(BitConverter.GetBytes(60.0));
        b.Str("ShadowQuality_12_0123456789ABCDEF0123456789ABCDEF").Type("IntProperty").I32(4).Bytes(0).I32(3);
        b.Str("Language").Type("StrProperty").I32(7).Bytes(0).Str("en");
        b.Str("None").I32(0);
        return b.ToArray();
    }

    /// <summary>Classic UE4 to UE5.3 layout.</summary>
    static byte[] Classic()
    {
        var b = Header(1004);
        b.Str("TextureQuality").Str("IntProperty").I32(4).I32(0).Bytes(0).I32(2);
        b.Str("bVSync").Str("BoolProperty").I32(0).I32(0).Bytes(1).Bytes(0);
        b.Str("Gamma").Str("FloatProperty").I32(4).I32(0).Bytes(0).Bytes(BitConverter.GetBytes(2.2f));
        b.Str("Resolution").Str("StructProperty").I32(8).I32(0).Str("IntPoint").Bytes(new byte[16]).Bytes(0).I32(1280).I32(720);
        b.Str("Quality").Str("ByteProperty").I32(1).I32(0).Str("None").Bytes(0).Bytes(1);
        b.Str("None").I32(0);
        return b.ToArray();
    }

    [Fact]
    public void Modern_layout_round_trips_and_reads_values()
    {
        var data = Modern();
        var f = GvasFile.Load(data);
        Assert.True(f.CompleteTypeNames);
        Assert.Equal(data, f.Save());
        Assert.Equal(new[] { 1920, 1080 }, f.Find("Resolution")!.Get());
        Assert.Equal("EWindowMode::WindowedFullscreen", f.Find("WindowMode")!.Get());
        Assert.Equal(true, f.Find("VSyncEnabled?")!.Get());
        Assert.Equal(60.0, f.Find("FrameRateLimit")!.Get());
        Assert.Equal("en", f.Find("Language")!.Get());
    }

    [Fact]
    public void Modern_layout_edits_survive_a_reload()
    {
        var f = GvasFile.Load(Modern());
        Assert.True(f.Find("Resolution")!.TrySet(new[] { 2560, 1440 }, out _));
        Assert.True(f.Find("WindowMode")!.TrySet("EWindowMode::Fullscreen", out _));
        Assert.True(f.Find("VSyncEnabled?")!.TrySet(false, out _));
        Assert.True(f.Find("FrameRateLimit")!.TrySet(162.0, out _));
        Assert.True(f.Find("ShadowQuality")!.TrySet(2, out _));

        var g = GvasFile.Load(f.Save());
        Assert.Equal(new[] { 2560, 1440 }, g.Find("Resolution")!.Get());
        Assert.Equal("EWindowMode::Fullscreen", g.Find("WindowMode")!.Get());
        Assert.Equal(false, g.Find("VSyncEnabled?")!.Get());
        Assert.Equal(162.0, g.Find("FrameRateLimit")!.Get());
        Assert.Equal(2, g.Find("ShadowQuality")!.Get());
    }

    [Fact]
    public void Blueprint_mangled_names_match_by_prefix()
    {
        var f = GvasFile.Load(Modern());
        Assert.Equal("ShadowQuality_12_0123456789ABCDEF0123456789ABCDEF", f.Find("ShadowQuality")!.Name);
        Assert.Null(f.Find("Shadow"));
    }

    [Fact]
    public void Wrong_value_kind_is_refused_and_changes_nothing()
    {
        var data = Modern();
        var f = GvasFile.Load(data);
        Assert.False(f.Find("VSyncEnabled?")!.TrySet(1, out var err));
        Assert.Contains("BoolProperty", err);
        Assert.False(f.Find("WindowMode")!.TrySet("Fullscreen", out _)); // enum values need Enum::Value
        Assert.Equal(data, f.Save());
    }

    [Fact]
    public void Classic_layout_round_trips_and_edits()
    {
        var data = Classic();
        var f = GvasFile.Load(data);
        Assert.False(f.CompleteTypeNames);
        Assert.Equal(data, f.Save());
        Assert.Equal(2, f.Find("TextureQuality")!.Get());
        Assert.Equal(true, f.Find("bVSync")!.Get());
        Assert.Equal(1, f.Find("Quality")!.Get());
        Assert.True(f.Find("bVSync")!.TrySet(false, out _));
        Assert.True(f.Find("Resolution")!.TrySet(new[] { 2560, 1440 }, out _));
        Assert.True(f.Find("Gamma")!.TrySet(2.0, out _));
        var g = GvasFile.Load(f.Save());
        Assert.Equal(false, g.Find("bVSync")!.Get());
        Assert.Equal(new[] { 2560, 1440 }, g.Find("Resolution")!.Get());
        Assert.Equal(2.0, (double)g.Find("Gamma")!.Get()!, 3);
    }

    [Fact]
    public void Unknown_property_type_is_not_guessed()
    {
        var b = Header(1004);
        b.Str("Mystery").Str("FancyNewProperty").I32(4).I32(0).Bytes(0).I32(7);
        b.Str("None").I32(0);
        Assert.Throws<FormatException>(() => GvasFile.Load(b.ToArray()));
    }

    [Fact]
    public void Truncated_file_is_rejected()
    {
        var data = Modern();
        Assert.Throws<FormatException>(() => GvasFile.Load(data[..(data.Length - 20)]));
    }
}
