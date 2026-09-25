using VsChunkDump.Cli;
using VsChunkDump.Core;
using Xunit;

namespace VsChunkDump.Core.Tests;

/// <summary>Command line argument parsing — including negative numbers in --area.</summary>
public class ArgReaderTests
{
    [Fact]
    public void ParsesPositionalAndNamedArguments()
    {
        var a = ArgReader.Parse(["/tmp/dump", "--scale", "3", "--grid"]);

        Assert.Equal("/tmp/dump", a.Positional(0));
        Assert.Equal(3, a.Int("--scale", 1));
        Assert.True(a.Has("--grid"));
    }

    [Fact]
    public void SupportsEqualsSyntax()
    {
        var a = ArgReader.Parse(["/tmp/dump", "--out=map.png"]);
        Assert.Equal("map.png", a.Get("--out"));
    }

    [Fact]
    public void SupportsShortAlias()
    {
        var a = ArgReader.Parse(["/tmp/dump", "-o", "iso.png"]);
        Assert.Equal("iso.png", a.Require("--out", "-o"));
    }

    [Fact]
    public void NegativeNumbers_AreValuesNotOptions()
    {
        var a = ArgReader.Parse(["dump", "--area", "-200,-100,200,100", "--scale", "2"]);

        var area = a.Area();
        Assert.NotNull(area);
        Assert.Equal(-200, area!.Value.MinX);
        Assert.Equal(-100, area.Value.MinZ);
        Assert.Equal(200, area.Value.MaxX);
        Assert.Equal(100, area.Value.MaxZ);
        Assert.Equal(2, a.Int("--scale", 1));
    }

    [Fact]
    public void Area_NormalisesReversedCorners()
    {
        var a = ArgReader.Parse(["dump", "--area", "50,60,10,20"]);
        var area = a.Area()!.Value;

        Assert.Equal(10, area.MinX);
        Assert.Equal(20, area.MinZ);
        Assert.Equal(50, area.MaxX);
        Assert.Equal(60, area.MaxZ);
        Assert.Equal(41, area.Width);
        Assert.Equal(41, area.Depth);
    }

    [Fact]
    public void Area_RejectsWrongArgumentCount()
    {
        var a = ArgReader.Parse(["dump", "--area", "1,2,3"]);
        Assert.Throws<ArgumentException>(() => a.Area());
    }

    [Fact]
    public void Require_ThrowsWhenMissingOrFlagLike()
    {
        Assert.Throws<ArgumentException>(() => ArgReader.Parse(["dump"]).Require("--out", "-o"));
        Assert.Throws<ArgumentException>(() => ArgReader.Parse(["dump", "--out"]).Require("--out"));
    }

    [Fact]
    public void Int_ThrowsOnNonNumeric()
    {
        var a = ArgReader.Parse(["dump", "--scale", "abc"]);
        Assert.Throws<ArgumentException>(() => a.Int("--scale", 1));
    }

    [Fact]
    public void Int_ReturnsFallbackWhenAbsent()
        => Assert.Equal(7, ArgReader.Parse(["dump"]).Int("--scale", 7));

    [Fact]
    public void MissingPositional_ReturnsEmptyString()
    {
        var a = ArgReader.Parse([]);
        Assert.Equal("", a.Positional(0));
        Assert.Equal(0, a.PositionalCount);
    }
}
