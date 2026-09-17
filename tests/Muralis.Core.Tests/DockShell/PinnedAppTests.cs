using Muralis.Core.Dock;
using Muralis.Core.DockShell;
using Xunit;

namespace Muralis.Core.Tests.DockShell;

/// <summary>
/// The pinned list's rules as values. None of them needs a file system or a shell: what counts as the
/// same application, what the dock refuses, and where a drop lands are all decided here, which is what
/// lets the dock be tested on a machine with nothing installed on it.
/// </summary>
public sealed class PinnedAppTests
{
    [Fact]
    public void Kind_IsDecidedByTheExtensionTheShellWillOpen()
    {
        Assert.Equal(PinnedAppKind.Application, PinnedAppTargets.KindOf(@"C:\Program Files\App\app.exe"));
        Assert.Equal(PinnedAppKind.Application, PinnedAppTargets.KindOf(@"C:\Program Files\App\APP.EXE"));
        Assert.Equal(PinnedAppKind.Shortcut, PinnedAppTargets.KindOf(@"C:\Users\e\Desktop\App.lnk"));
        Assert.Equal(PinnedAppKind.Shortcut, PinnedAppTargets.KindOf(@"C:\Users\e\Desktop\APP.LNK"));

        // Packaged applications are a Non Goal for this phase; a Store entry must be refused rather
        // than pinned as something the shell would not know how to start.
        Assert.Null(PinnedAppTargets.KindOf(@"C:\Users\e\Desktop\App.appref-ms"));
        Assert.Null(PinnedAppTargets.KindOf(@"C:\Users\e\Desktop\notes.txt"));
        Assert.Null(PinnedAppTargets.KindOf(string.Empty));

        Assert.True(PinnedAppTargets.IsSupported(@"C:\tools\app.exe"));
        Assert.False(PinnedAppTargets.IsSupported(@"C:\tools\app.msi"));
    }

    [Fact]
    public void Identity_PrefersWhatAShortcutResolvesTo()
    {
        // A shortcut and the program it points at are the same application, so pinning the program
        // after the shortcut has to be refused as a duplicate — this is the rule that makes it so.
        var viaShortcut = PinnedAppIdentity.Of(@"C:\Users\e\Desktop\App.lnk", @"C:\Program Files\App\app.exe");
        var direct = PinnedAppIdentity.Of(@"C:\Program Files\App\app.exe", null);

        Assert.Equal(direct, viaShortcut);

        // An unresolvable shortcut still needs a key of its own, or a broken shortcut could be pinned
        // over and over.
        var broken = PinnedAppIdentity.Of(@"C:\Users\e\Desktop\Gone.lnk", null);
        Assert.NotEqual(broken, direct);
    }

    [Fact]
    public void Identity_IgnoresCaseAndATrailingSeparator()
    {
        Assert.Equal(
            PinnedAppIdentity.Normalize(@"C:\Tools\App"),
            PinnedAppIdentity.Normalize(@"c:\tools\app\"));
        Assert.Equal(
            PinnedAppIdentity.Normalize(@"C:\Tools\App\"),
            PinnedAppIdentity.Normalize(@"C:\Tools\App"));
    }

    [Fact]
    public void Identity_OfAPathWindowsWouldNotAccept_IsStillStable()
    {
        // A pin must not become unloadable because the file behind it was deleted, so a path that
        // cannot even be resolved is keyed on the text as given rather than throwing.
        var once = PinnedAppIdentity.Normalize("\0");
        var twice = PinnedAppIdentity.Normalize("\0");

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Add_AcceptsANewApplication()
    {
        var candidate = Pin("One", @"C:\tools\one.exe");

        var result = PinnedApps.Add([], candidate);

        Assert.Equal(PinnedAppAddOutcome.Added, result.Outcome);
        Assert.Same(candidate, result.Item);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Add_RefusesTheSameApplicationAndNamesThePinThatAlreadyExists()
    {
        var pinned = Pin("One", @"C:\tools\one.exe", id: "pin-one");

        // The same program reached by a differently written path is still the same program.
        var result = PinnedApps.Add([pinned], Pin("One", @"c:\TOOLS\one.exe"));

        Assert.Equal(PinnedAppAddOutcome.Duplicate, result.Outcome);
        Assert.Equal("pin-one", result.ExistingId);
        Assert.Null(result.Item);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Add_RefusesWhenTheZoneIsFull()
    {
        var items = PinnedApps.MaximumCount;

        var result = PinnedApps.Add(Many(items), Pin("Extra", @"C:\tools\extra.exe"));

        Assert.Equal(PinnedAppAddOutcome.LimitReached, result.Outcome);
        Assert.Contains(items.ToString(), result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_PrefersSayingYouAlreadyPinnedThisOverSayingTheZoneIsFull()
    {
        var items = Many(PinnedApps.MaximumCount);

        var result = PinnedApps.Add(items, Pin("App0", @"C:\tools\app0.exe"));

        Assert.Equal(PinnedAppAddOutcome.Duplicate, result.Outcome);
    }

    [Fact]
    public void MaximumCount_StaysInsideTheRangeTheZoneIsDesignedFor()
    {
        // Phase 4C targets 10 to 30 pinned apps: the zone is a fixed strip, so it needs a bound, and
        // the bound has to leave the zone worth using.
        Assert.InRange(PinnedApps.MaximumCount, 10, 30);
    }

    [Fact]
    public void Remove_TakesThePinOutAndLeavesEverythingElseAlone()
    {
        var items = Four();

        var remaining = PinnedApps.Remove(items, "b");

        Assert.Equal(["a", "c", "d"], remaining.Select(item => item.Id));
    }

    [Fact]
    public void Remove_OfSomethingNotPinned_HandsBackTheListItWasGiven()
    {
        var items = Four();

        Assert.Same(items, PinnedApps.Remove(items, "gone"));
    }

    [Fact]
    public void Move_PutsTheItemWhereTheDropWasDrawn()
    {
        var items = Four();

        // A target index counted among the other items, which is what the drag preview shows and
        // therefore what the drop has to mean.
        Assert.Equal(["b", "c", "a", "d"], PinnedApps.Move(items, "a", 2).Select(item => item.Id));
        Assert.Equal(["d", "a", "b", "c"], PinnedApps.Move(items, "d", 0).Select(item => item.Id));
        Assert.Equal(["a", "b", "c", "d"], PinnedApps.Move(items, "b", 1).Select(item => item.Id));
    }

    [Fact]
    public void Move_AgreesWithTheReorderRuleTheDragUses()
    {
        var items = Four();

        foreach (var from in Enumerable.Range(0, items.Count))
        {
            foreach (var target in Enumerable.Range(0, items.Count))
            {
                var moved = PinnedApps.Move(items, items[from].Id, target);
                var expected = DockReorder.OrderAfterDrop(items.Count, from, target)
                    .Select(index => items[index].Id);

                Assert.Equal(expected, moved.Select(item => item.Id));
            }
        }
    }

    [Fact]
    public void Move_ClampsATargetPastEitherEnd()
    {
        var items = Four();

        Assert.Equal(["b", "c", "d", "a"], PinnedApps.Move(items, "a", 99).Select(item => item.Id));
        Assert.Equal(["a", "b", "c", "d"], PinnedApps.Move(items, "a", -5).Select(item => item.Id));
    }

    [Fact]
    public void Move_OfSomethingNotPinned_HandsBackTheListItWasGiven()
    {
        var items = Four();

        Assert.Same(items, PinnedApps.Move(items, "gone", 2));
    }

    [Fact]
    public void Find_LooksUpByIdAndByIdentity()
    {
        var items = Four();

        Assert.Equal("c", PinnedApps.Find(items, "C")?.Id);
        Assert.Equal("c", PinnedApps.FindByIdentity(items, PinnedAppIdentity.Normalize(@"C:\tools\c.exe"))?.Id);
        Assert.Null(PinnedApps.Find(items, "gone"));
        Assert.Null(PinnedApps.FindByIdentity(items, "not-a-path-anyone-pinned"));
    }

    private static PinnedApp Pin(string name, string path, PinnedAppKind kind = PinnedAppKind.Application, string? id = null) =>
        new(id ?? name.ToLowerInvariant(), name, path, path, kind, PinnedAppIdentity.Normalize(path));

    private static IReadOnlyList<PinnedApp> Four() =>
    [
        Pin("A", @"C:\tools\a.exe"),
        Pin("B", @"C:\tools\b.lnk", PinnedAppKind.Shortcut),
        Pin("C", @"C:\tools\c.exe"),
        Pin("D", @"C:\tools\d.exe"),
    ];

    private static IReadOnlyList<PinnedApp> Many(int count) =>
        Enumerable.Range(0, count)
            .Select(index => Pin($"App{index}", $@"C:\tools\app{index}.exe", id: $"app{index}"))
            .ToArray();
}
