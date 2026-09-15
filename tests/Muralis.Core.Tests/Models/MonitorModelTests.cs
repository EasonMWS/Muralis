using Muralis.Core.Models;
using Xunit;

namespace Muralis.Core.Tests.Models;

public sealed class MonitorModelTests
{
    [Fact]
    public void Identity_EqualityIgnoresDescriptiveMetadata()
    {
        var first = Identity("edid:one", "Left monitor");
        var second = new MonitorIdentity("edid:one", null, IdentityConfidence.SignatureFallback, "Renamed");

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void Identity_DiffersForDifferentDisplays()
    {
        Assert.NotEqual(Identity("edid:one"), Identity("edid:two"));
    }

    [Fact]
    public void Identity_ServesAsDictionaryKeyAcrossMetadataDrift()
    {
        var watched = new Dictionary<MonitorIdentity, string>
        {
            [Identity("edid:one", "Old name")] = "primary scene",
        };

        Assert.True(watched.TryGetValue(
            new MonitorIdentity("edid:one", null, IdentityConfidence.SignatureFallback, "New name"),
            out var value));
        Assert.Equal("primary scene", value);
    }

    [Fact]
    public void Identity_ExposesIdentityFactsOnly()
    {
        var names = typeof(MonitorIdentity).GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

        Assert.True(names.SetEquals(new[] { "StableId", "Edid", "Confidence", "LastKnownFriendlyName" }));
    }

    [Fact]
    public void RuntimeInfo_ExposesRuntimeFactsOnly()
    {
        var names = typeof(MonitorRuntimeInfo).GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

        Assert.True(names.SetEquals(new[]
        {
            "DevicePath", "DeviceName", "FriendlyName", "IsPrimary", "OrderIndex",
            "Bounds", "WorkArea", "Dpi", "ScaleFactor", "Orientation", "Mirroring",
        }));
    }

    [Fact]
    public void RuntimeInfo_EqualityReflectsEveryFact()
    {
        Assert.Equal(Runtime(), Runtime());
        Assert.NotEqual(Runtime(), Runtime(isPrimary: false));
        Assert.NotEqual(Runtime(), Runtime(devicePath: @"\\?\DISPLAY#OTHER"));
    }

    [Fact]
    public void Monitor_CombinesIdentityAndRuntime()
    {
        var monitor = new Monitor(Identity(), Runtime());

        Assert.Equal("edid:sample", monitor.Identity.StableId);
        Assert.True(monitor.Runtime.IsPrimary);
    }

    [Fact]
    public void MonitorRef_TracksTheStableIdOnly()
    {
        var monitor = new Monitor(Identity("edid:one", "Renamed"), Runtime());

        Assert.Equal(new MonitorRef("edid:one"), MonitorRef.From(monitor));
    }

    private static MonitorIdentity Identity(
        string stableId = "edid:sample",
        string? friendlyName = "Display 1") =>
        new(stableId, new EdidInfo("DEL", "A0B1", "SAMPLE123", null), IdentityConfidence.Exact, friendlyName);

    private static MonitorRuntimeInfo Runtime(
        string devicePath = @"\\?\DISPLAY#SAMPLE",
        bool isPrimary = true) =>
        new(
            devicePath,
            @"\\.\DISPLAY1",
            "Display 1",
            isPrimary,
            0,
            new PixelRect(0, 0, 2560, 1440),
            new PixelRect(0, 0, 2560, 1400),
            192,
            1.5,
            MonitorOrientation.Landscape,
            MirroringInfo.None);
}
