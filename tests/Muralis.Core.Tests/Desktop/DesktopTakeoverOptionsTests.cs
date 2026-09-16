using Muralis.Core.Desktop;
using Muralis.Core.Desktop.Takeover;
using Xunit;

namespace Muralis.Core.Tests.Desktop;

/// <summary>
/// The takeover section as the layout keeps it: a choice, not a situation. It says which desktop mode
/// the user asked for and which of their own desktop entries they have turned down, and it says
/// nothing about whether the native icons are hidden right now — that is the marker's business, so a
/// stale choice can never be read as a live takeover.
/// </summary>
public sealed class DesktopTakeoverOptionsTests
{
    [Fact]
    public void AFreshSection_AsksForTheNativeDesktopAndAdopts()
    {
        var options = new DesktopTakeoverOptions();

        Assert.Equal(DesktopMode.Native, options.Mode);
        Assert.True(options.AdoptDesktopItems);
        Assert.Empty(options.IgnoredSourcePaths);
        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData(DesktopMode.Native)]
    [InlineData(DesktopMode.Preview)]
    [InlineData(DesktopMode.Takeover)]
    public void EveryModeIsACoherentChoice(DesktopMode mode)
    {
        var options = new DesktopTakeoverOptions { Mode = mode };

        Assert.Empty(options.Validate());
    }

    [Fact]
    public void AModeThatIsNotOneOfTheThree_IsRefused()
    {
        var options = new DesktopTakeoverOptions { Mode = (DesktopMode)7 };

        var problems = options.Validate();

        Assert.Single(problems);
        Assert.Contains("native, preview or takeover", problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ATurnedDownSource_IsRememberedOnceAndMatchedWithoutCase()
    {
        var options = new DesktopTakeoverOptions();

        options.Ignore(@"C:\Users\me\Desktop\notes.txt");
        options.Ignore(@"C:\Users\me\Desktop\NOTES.TXT");

        Assert.Single(options.IgnoredSourcePaths);
        Assert.True(options.IsIgnored(@"c:\users\ME\desktop\notes.txt"));
        Assert.False(options.IsIgnored(@"C:\Users\me\Desktop\other.txt"));
    }

    [Fact]
    public void AnEmptySource_IsNeverIgnoredAndNeverRemembered()
    {
        var options = new DesktopTakeoverOptions();

        options.Ignore(string.Empty);

        Assert.False(options.IsIgnored(string.Empty));
        Assert.Empty(options.IgnoredSourcePaths);
    }

    [Fact]
    public void ARelativeTurnedDownSource_IsRefused()
    {
        // The list is compared against paths that came off the file system, so a relative entry in it
        // could never match anything: a hand-edit that leaves one is reported rather than kept.
        var options = new DesktopTakeoverOptions { IgnoredSourcePaths = [@"Desktop\notes.txt"] };

        Assert.Contains("fully qualified", options.Validate()[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingTurnedDownList_IsRefusedRatherThanThrowing()
    {
        var options = new DesktopTakeoverOptions { IgnoredSourcePaths = null! };

        var problems = options.Validate();

        Assert.Single(problems);
        Assert.Contains("turned-down", problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AClone_DoesNotShareItsListWithTheOriginal()
    {
        var options = new DesktopTakeoverOptions { Mode = DesktopMode.Takeover };
        options.Ignore(@"C:\Users\me\Desktop\a.txt");

        var clone = options.Clone();
        clone.Ignore(@"C:\Users\me\Desktop\b.txt");
        clone.Mode = DesktopMode.Native;

        Assert.Single(options.IgnoredSourcePaths);
        Assert.Equal(2, clone.IgnoredSourcePaths.Count);
        Assert.Equal(DesktopMode.Takeover, options.Mode);
    }

    [Fact]
    public void CopyingInPlace_ReplacesWhatWasThere()
    {
        var options = new DesktopTakeoverOptions { Mode = DesktopMode.Takeover };
        options.Ignore(@"C:\Users\me\Desktop\a.txt");

        options.CopyFrom(new DesktopTakeoverOptions { Mode = DesktopMode.Preview });

        Assert.Equal(DesktopMode.Preview, options.Mode);
        Assert.Empty(options.IgnoredSourcePaths);
    }

    [Fact]
    public void ALayoutWithAnImpossibleTakeover_DoesNotValidate()
    {
        var layout = DesktopLayout.CreateEmpty();
        layout.Takeover.Mode = (DesktopMode)11;

        Assert.Contains("native, preview or takeover", string.Join(" ", layout.Validate()), StringComparison.Ordinal);
    }

    [Fact]
    public void ALayoutWithoutATakeoverSection_DoesNotValidate()
    {
        var layout = DesktopLayout.CreateEmpty();
        layout.Takeover = null!;

        Assert.Contains("takeover options are missing", string.Join(" ", layout.Validate()), StringComparison.Ordinal);
    }
}
