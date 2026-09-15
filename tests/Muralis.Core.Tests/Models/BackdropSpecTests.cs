using System.Text.Json;
using System.Text.Json.Serialization;
using Muralis.Core.Models;
using Xunit;

namespace Muralis.Core.Tests.Models;

public sealed class BackdropSpecTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void VideoSpec_RoundTripsThroughJson()
    {
        var spec = BackdropSpec.ForVideo("assets/loop.mp4", VideoPlaybackOptions.Default with { Muted = false });

        var json = JsonSerializer.Serialize(spec, JsonOptions);
        var restored = JsonSerializer.Deserialize<BackdropSpec>(json, JsonOptions);

        Assert.Contains("\"Video\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Muted\": false", json, StringComparison.Ordinal);
        Assert.Equal(spec, restored);
    }

    [Fact]
    public void NoneSpec_RoundTripsThroughJson()
    {
        var json = JsonSerializer.Serialize(BackdropSpec.None, JsonOptions);

        Assert.Contains("\"None\"", json, StringComparison.Ordinal);
        Assert.Equal(BackdropSpec.None, JsonSerializer.Deserialize<BackdropSpec>(json, JsonOptions));
    }

    [Fact]
    public void Validate_AcceptsVideoSpecWithAsset()
    {
        Assert.Empty(BackdropSpec.ForVideo("assets/loop.mp4").Validate());
    }

    [Fact]
    public void Validate_RejectsVideoSpecWithoutAsset()
    {
        Assert.NotEmpty(new BackdropSpec(BackdropKind.Video).Validate());
        Assert.NotEmpty(new BackdropSpec(BackdropKind.Video, "   ").Validate());
    }

    [Fact]
    public void Validate_RejectsContentOnNoneSpec()
    {
        Assert.NotEmpty(new BackdropSpec(BackdropKind.None, "assets/loop.mp4").Validate());
        Assert.NotEmpty(new BackdropSpec(BackdropKind.None, null, VideoPlaybackOptions.Default).Validate());
    }

    [Fact]
    public void Validate_AcceptsPlainNoneSpec()
    {
        Assert.Empty(BackdropSpec.None.Validate());
    }
}
