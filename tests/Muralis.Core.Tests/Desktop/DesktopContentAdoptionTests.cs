using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Desktop;
using Muralis.Core.Tests.TestSupport;
using Xunit;

namespace Muralis.Core.Tests.Desktop;

/// <summary>
/// What a scan means for the canvas, and what adopting it writes. The plan changes nothing, so a first
/// run can say what it is about to do; adopting records where each item came from, so the same file is
/// never put on the canvas twice and a removal the user made stays made.
/// </summary>
public sealed class DesktopContentAdoptionTests : IDisposable
{
    private readonly TempWorkspace _workspace = new("desktop-adoption");
    private readonly string _desktop;
    private readonly DesktopContentScanner _scanner;

    public DesktopContentAdoptionTests()
    {
        _desktop = _workspace.DirectoryAt("desktop");
        _scanner = new DesktopContentScanner(NullLogger<DesktopContentScanner>.Instance, [_desktop]);
    }

    public void Dispose() => _workspace.Dispose();

    private string Entry(string name, string content = "x")
    {
        var path = Path.Combine(_desktop, name);
        File.WriteAllText(path, content);
        return path;
    }

    private DesktopContentScan Scan() => _scanner.Scan();

    private DesktopAdoptionPlan Plan(DesktopLayout? layout = null) =>
        DesktopContentAdopter.Plan(Scan(), layout ?? DesktopLayout.CreateEmpty());

    private static DesktopAdoptionResult Adopt(DesktopAdoptionPlan plan, DesktopLayout layout) =>
        DesktopContentAdopter.Adopt(plan, layout, 1920, 1080);

    [Fact]
    public void EveryEntryNothingNamesYet_IsToBeAdopted()
    {
        Entry("editor.exe");
        Entry("notes.txt");

        var plan = Plan();

        Assert.Equal(2, plan.ToAdopt.Count);
        Assert.Empty(plan.AlreadyAdopted);
        Assert.Empty(plan.Declined);
        Assert.Empty(plan.Unsupported);
        Assert.False(plan.IsEmpty);
    }

    [Fact]
    public void AnEntryAnItemAlreadyNames_IsAlreadyAdopted()
    {
        var path = Entry("editor.exe");
        var layout = DesktopLayout.CreateEmpty();
        layout.Items.Add(DesktopItemFactory.CreateFromTarget(new ApplicationTarget { Path = path }, "editor", path));

        var plan = DesktopContentAdopter.Plan(Scan(), layout);

        Assert.Empty(plan.ToAdopt);
        Assert.Equal(path, plan.AlreadyAdopted.Single().SourcePath);
    }

    [Fact]
    public void AnEntryIsTheSameEntry_WhateverNameItIsShownUnder()
    {
        var path = Entry("editor.exe");
        var layout = DesktopLayout.CreateEmpty();
        layout.Items.Add(DesktopItemFactory.CreateFromTarget(new ApplicationTarget { Path = path }, "the editor", path));

        // The same file, seen under a different name than the item shows it as.
        var seen = Scan().Adoptable.Single() with { Name = "Something Else" };
        var plan = DesktopContentAdopter.Plan(new DesktopContentScan([seen], [], []), layout);

        Assert.Empty(plan.ToAdopt);
        Assert.Equal(path, plan.AlreadyAdopted.Single().SourcePath);
    }

    [Fact]
    public void AFileInAnotherFolder_IsNotExcusedByTheSameName()
    {
        var mine = Entry("editor.exe");
        var layout = DesktopLayout.CreateEmpty();
        layout.Items.Add(DesktopItemFactory.CreateFromTarget(new ApplicationTarget { Path = mine }, "editor", mine));

        var seen = Scan().Adoptable.Single(entry => entry.SourcePath == mine);
        var elsewhere = Path.Combine(_desktop, "elsewhere", "editor.exe");
        seen = seen with { SourcePath = elsewhere };

        var plan = DesktopContentAdopter.Plan(new DesktopContentScan([seen], [], []), layout);

        Assert.Equal(elsewhere, plan.ToAdopt.Single().SourcePath);
    }

    // Identity is the path, so a file that moves is a new entry — and the item the old path left behind
    // stays where it is, pointing at where the file used to be, until the user does something about it.
    // Nothing is silently re-pointed and nothing is quietly dropped.
    [Fact]
    public void AFileThatMoves_IsANewEntry_AndTheItemItLeftBehindStays()
    {
        var before = Entry("editor.exe");
        var layout = DesktopLayout.CreateEmpty();
        Adopt(Plan(layout), layout);

        var after = Path.Combine(_desktop, "renamed.exe");
        File.Move(before, after);

        var plan = Plan(layout);

        Assert.Equal(after, plan.ToAdopt.Single().SourcePath);
        Assert.Single(layout.Items);
        Assert.Equal(before, layout.Items[0].SourcePath);

        var result = Adopt(plan, layout);

        Assert.Single(result.Added);
        Assert.Equal(2, layout.Items.Count);
    }

    [Fact]
    public void AnEntryTheUserTurnedDown_IsDeclinedAgain()
    {
        var path = Entry("editor.exe");
        var layout = DesktopLayout.CreateEmpty();
        layout.Takeover.Ignore(path);

        var plan = DesktopContentAdopter.Plan(Scan(), layout);

        Assert.Empty(plan.ToAdopt);
        Assert.Equal(path, plan.Declined.Single().SourcePath);
    }

    [Fact]
    public void AnEntryThatCannotBeShown_IsReportedWithItsReason()
    {
        Entry("odd.url", "[InternetShortcut]\r\nURL=steam://run/440\r\n");

        var plan = Plan();

        Assert.Empty(plan.ToAdopt);
        Assert.Contains("http", plan.Unsupported.Single().Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Adopting_AddsItemsThatPointAtWhereTheFilesAre()
    {
        var exe = Entry("editor.exe");
        var txt = Entry("notes.txt");
        var layout = DesktopLayout.CreateEmpty();

        var result = Adopt(Plan(layout), layout);

        Assert.Equal(2, result.Added.Count);
        Assert.Equal(2, layout.Items.Count);
        Assert.Empty(layout.Validate());

        var editor = layout.Items.Single(item => item.Name == "editor");
        Assert.Equal(DesktopItemKind.Application, editor.Target.Kind);
        Assert.Equal(exe, editor.Target.Location);
        Assert.Equal(exe, editor.SourcePath);

        var notes = layout.Items.Single(item => item.Name == "notes");
        Assert.IsType<FileTarget>(notes.Target);
        Assert.Equal(txt, notes.SourcePath);
    }

    [Fact]
    public void AdoptedItems_DoNotAllLandInTheSamePlace()
    {
        Entry("a.exe");
        Entry("b.exe");
        Entry("c.exe");
        var layout = DesktopLayout.CreateEmpty();

        Adopt(Plan(layout), layout);

        var spots = layout.Items.Select(item => (item.OffsetXDip, item.OffsetYDip)).ToList();
        Assert.Equal(3, spots.Distinct().Count());
    }

    [Fact]
    public void AdoptingTwice_DoesNotPutTheSameFileOnTwice()
    {
        Entry("editor.exe");
        var layout = DesktopLayout.CreateEmpty();

        Adopt(Plan(layout), layout);
        var second = Adopt(Plan(layout), layout);

        Assert.Empty(second.Added);
        Assert.Single(layout.Items);
    }

    [Fact]
    public void ThePlanIsOnlyASnapshot_SoAnItemAddedSinceItWasMadeWins()
    {
        var exe = Entry("editor.exe");
        var layout = DesktopLayout.CreateEmpty();
        var plan = Plan(layout);

        // The user imports the same program by hand between the plan and the adoption.
        layout.Items.Add(DesktopItemFactory.CreateFromTarget(new ApplicationTarget { Path = exe }, "editor", exe));

        var result = Adopt(plan, layout);

        Assert.Empty(result.Added);
        Assert.Single(layout.Items);
    }

    [Fact]
    public void NothingIsAdopted_WhileTheUserHasAdoptionSwitchedOff()
    {
        Entry("editor.exe");
        var layout = DesktopLayout.CreateEmpty();
        layout.Takeover.AdoptDesktopItems = false;

        var result = Adopt(Plan(layout), layout);

        Assert.Empty(result.Added);
        Assert.Empty(layout.Items);
    }

    [Fact]
    public void ADeclinedEntry_IsNotBroughtBackByAdopting()
    {
        var path = Entry("editor.exe");
        var layout = DesktopLayout.CreateEmpty();
        layout.Takeover.Ignore(path);

        var result = Adopt(Plan(layout), layout);

        Assert.Empty(result.Added);
        Assert.Empty(layout.Items);
    }

    [Fact]
    public void Adopting_LeavesEverySourceFileExactlyWhereItWas()
    {
        var path = Entry("editor.exe");
        var layout = DesktopLayout.CreateEmpty();

        Adopt(Plan(layout), layout);

        Assert.True(File.Exists(path));
        Assert.Equal("x", File.ReadAllText(path));
    }

    [Fact]
    public void ThePlanDoesNotChangeTheLayout()
    {
        Entry("editor.exe");
        var layout = DesktopLayout.CreateEmpty();

        Plan(layout);

        Assert.Empty(layout.Items);
    }

    [Fact]
    public void AnUnreadableDesktopFolder_IsCarriedIntoThePlan()
    {
        var missing = _workspace.PathOf("no-such-desktop");
        var scanner = new DesktopContentScanner(NullLogger<DesktopContentScanner>.Instance, [missing]);
        var plan = DesktopContentAdopter.Plan(scanner.Scan(), DesktopLayout.CreateEmpty());

        Assert.Equal(new[] { missing }, plan.UnreadableFolders);
        Assert.False(plan.IsEmpty);
    }
}
