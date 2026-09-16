using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Desktop.Takeover;
using Xunit;

namespace Muralis.Core.Tests.Desktop;

/// <summary>
/// The crash-safe marker: the file whose presence is what a killed run is recovered from. Its two
/// promises are that it is written before the icons are hidden and that an unreadable one is never
/// acted on, so both are proved here against a real file rather than in the live harness alone.
/// </summary>
public sealed class DesktopTakeoverRecordStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"muralis-marker-{Guid.NewGuid():N}");
    private readonly string _path;

    public DesktopTakeoverRecordStoreTests()
    {
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "takeover-state.json");
    }

    [Fact]
    public void NoMarkerAtAll_IsTheOrdinaryCase()
    {
        var marker = Store().Load();

        Assert.Null(marker.Record);
        Assert.Null(marker.Error);
        Assert.False(marker.NeedsRecovery);
        Assert.False(marker.IsUnreadable);
    }

    [Fact]
    public void AMarkerRoundTripsWithEverythingARecoveryNeeds()
    {
        var original = new NativeDesktopVisualState
        {
            OriginalIconsVisible = true,
            OriginalFolderFlags = 0x40200224,
            HadIconWindow = true,
            Observed = true,
        };

        var record = new DesktopTakeoverRecord
        {
            TakeoverWasActive = true,
            OriginalNativeState = original,
            SessionId = "session-1",
            ProcessId = 4242,
            Timestamp = DateTimeOffset.Now,
            State = DesktopTakeoverState.Muralis,
            Strategy = DesktopIconStrategy.ShellViewFlags,
        };

        Assert.True(Store().TrySave(record));

        var marker = Store().Load();

        Assert.NotNull(marker.Record);
        Assert.True(marker.NeedsRecovery);
        Assert.Null(marker.Error);
        Assert.Equal("session-1", marker.Record!.SessionId);
        Assert.Equal(4242, marker.Record.ProcessId);
        Assert.Equal(DesktopIconStrategy.ShellViewFlags, marker.Record.Strategy);
        Assert.Equal(DesktopTakeoverState.Muralis, marker.Record.State);
        Assert.Equal(0x40200224u, marker.Record.OriginalNativeState!.OriginalFolderFlags);
        Assert.True(marker.Record.OriginalNativeState.Observed);
    }

    [Fact]
    public void AUserWhoKeepsTheirIconsHidden_IsRecordedAsSuch()
    {
        // A give-back restores what was found, and "found hidden" has to survive the file: a recovery
        // that read this as visible would show icons the user had turned off.
        var record = new DesktopTakeoverRecord
        {
            TakeoverWasActive = true,
            OriginalNativeState = new NativeDesktopVisualState
            {
                OriginalIconsVisible = false,
                OriginalFolderFlags = 0x40201224,
                Observed = true,
            },
            SessionId = "session-2",
            ProcessId = 7,
            Timestamp = DateTimeOffset.Now,
        };

        Assert.True(Store().TrySave(record));

        var marker = Store().Load();

        Assert.NotNull(marker.Record);
        Assert.False(marker.Record!.OriginalNativeState!.OriginalIconsVisible);
    }

    [Fact]
    public void ARecordThatClaimsATakeoverWithoutSayingWhatItLookedLike_IsNotUsed()
    {
        // The one combination a recovery cannot act on: it would have to guess whether to leave the
        // icons hidden or show them, and either guess overrides the user.
        File.WriteAllText(
            _path,
            """
            {
              "Kind": "muralis.desktopTakeover",
              "SchemaVersion": 1,
              "TakeoverWasActive": true,
              "SessionId": "session-3",
              "ProcessId": 9
            }
            """);

        var marker = Store().Load();

        Assert.True(marker.IsUnreadable);
        Assert.Null(marker.Record);
        Assert.NotNull(marker.Error);
        Assert.False(marker.NeedsRecovery);
    }

    [Fact]
    public void AnUnreadableMarkerIsKeptBesideTheFileAndTheNextCheckStartsClean()
    {
        File.WriteAllText(_path, "{ this is not a marker");

        var store = Store();
        var marker = store.Load();

        Assert.True(marker.IsUnreadable);
        Assert.True(File.Exists(_path + ".bad"));
        Assert.False(File.Exists(_path));

        // The second look is the ordinary "nothing was taken over", which is what makes a marker that
        // cannot be read a one-time problem rather than one every launch repeats.
        var again = store.Load();
        Assert.Null(again.Record);
        Assert.Null(again.Error);
    }

    [Fact]
    public void ARecordFromARunThatEndedCleanly_IsReadButClaimsNoTakeover()
    {
        var record = new DesktopTakeoverRecord
        {
            TakeoverWasActive = false,
            OriginalNativeState = NativeDesktopVisualState.AssumedVisible,
            SessionId = "session-4",
            ProcessId = 11,
            Timestamp = DateTimeOffset.Now,
        };

        Assert.True(Store().TrySave(record));

        var marker = Store().Load();

        Assert.NotNull(marker.Record);
        Assert.False(marker.NeedsRecovery);
        Assert.False(marker.IsUnreadable);
    }

    [Fact]
    public void ClearingRemovesTheMarkerAndItsHalfWrittenCopy()
    {
        var store = Store();
        store.TrySave(new DesktopTakeoverRecord
        {
            TakeoverWasActive = true,
            OriginalNativeState = NativeDesktopVisualState.AssumedVisible,
            SessionId = "session-5",
            ProcessId = 12,
            Timestamp = DateTimeOffset.Now,
        });
        File.WriteAllText(_path + ".tmp", "half a write");

        Assert.True(store.TryClear());

        Assert.False(File.Exists(_path));
        Assert.False(File.Exists(_path + ".tmp"));
        Assert.Null(Store().Load().Record);
    }

    [Fact]
    public void WritingOverAMarkerReplacesItWholesale()
    {
        var store = Store();
        store.TrySave(new DesktopTakeoverRecord
        {
            TakeoverWasActive = true,
            OriginalNativeState = NativeDesktopVisualState.AssumedVisible,
            SessionId = "the-first-run",
            ProcessId = 1,
            Timestamp = DateTimeOffset.Now,
        });
        store.TrySave(new DesktopTakeoverRecord
        {
            TakeoverWasActive = true,
            OriginalNativeState = NativeDesktopVisualState.AssumedVisible,
            SessionId = "the-second-run",
            ProcessId = 2,
            Timestamp = DateTimeOffset.Now,
        });

        var marker = store.Load();

        Assert.Equal("the-second-run", marker.Record!.SessionId);
        Assert.Equal(2, marker.Record.ProcessId);
    }

    private DesktopTakeoverRecordStore Store() =>
        new(NullLogger<DesktopTakeoverRecordStore>.Instance, _path);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
