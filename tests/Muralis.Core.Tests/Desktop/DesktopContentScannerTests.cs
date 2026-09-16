using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.Desktop;
using Muralis.Core.Tests.TestSupport;
using Xunit;

namespace Muralis.Core.Tests.Desktop;

/// <summary>
/// Reading the user's own desktop. Every test works in a throwaway folder that stands in for the
/// desktop, and the point of all of them together is that a scan only ever looks: nothing is written,
/// nothing is moved, and an entry it cannot use is reported rather than guessed at.
/// </summary>
public sealed class DesktopContentScannerTests : IDisposable
{
    private readonly TempWorkspace _workspace = new("desktop-content");
    private readonly string _desktop;
    private readonly string _publicDesktop;

    public DesktopContentScannerTests()
    {
        _desktop = _workspace.DirectoryAt("desktop");
        _publicDesktop = _workspace.DirectoryAt("public-desktop");
    }

    public void Dispose() => _workspace.Dispose();

    private DesktopContentScanner Scanner() => new(NullLogger<DesktopContentScanner>.Instance, [_desktop]);

    private DesktopContentScanner BothDesktops() => new(NullLogger<DesktopContentScanner>.Instance, [_desktop, _publicDesktop]);

    private string Entry(string name, string content = "")
    {
        var path = Path.Combine(_desktop, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void EachKindOfEntry_IsReadAsItsOwnKind()
    {
        Entry("editor.exe");
        Entry("steam.lnk");
        Entry("notes.txt");
        Directory.CreateDirectory(Path.Combine(_desktop, "Projects"));

        var scan = Scanner().Scan();

        Assert.Empty(scan.Skipped);
        var kinds = scan.Adoptable.ToDictionary(entry => entry.Name, entry => entry.Target.Kind);
        Assert.Equal(DesktopItemKind.Application, kinds["editor"]);
        Assert.Equal(DesktopItemKind.Shortcut, kinds["steam"]);
        Assert.Equal(DesktopItemKind.File, kinds["notes"]);
        Assert.Equal(DesktopItemKind.Folder, kinds["Projects"]);
    }

    [Fact]
    public void AnAdoptedEntry_PointsAtWhereTheFileAlreadyIs()
    {
        var path = Entry("editor.exe");

        var entry = Scanner().Scan().Adoptable.Single();

        Assert.Equal(path, entry.SourcePath);
        Assert.Equal(path, entry.Target.Location);
        Assert.Equal("editor", entry.Name);
    }

    [Fact]
    public void AnInternetShortcut_IsReadAsTheAddressItHolds()
    {
        Entry("docs.url", "[InternetShortcut]\r\nURL=https://example.com/docs\r\n");

        var entry = Scanner().Scan().Adoptable.Single();

        Assert.Equal(DesktopItemKind.Url, entry.Target.Kind);
        Assert.Equal("https://example.com/docs", entry.Target.Location);
        Assert.Equal("docs", entry.Name);
    }

    [Theory]
    [InlineData("steam://run/440", "not an http or https address")]
    [InlineData("", "holds no address")]
    public void AnInternetShortcutWithSomethingElseInIt_IsReportedNotAdopted(string address, string expected)
    {
        Entry("odd.url", $"[InternetShortcut]\r\nURL={address}\r\n");

        var scan = Scanner().Scan();

        Assert.Empty(scan.Adoptable);
        Assert.Contains(expected, scan.Skipped.Single().Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AHiddenEntry_IsLeftWhereItIs()
    {
        var path = Entry("secret.txt");
        File.SetAttributes(path, FileAttributes.Hidden);

        var scan = Scanner().Scan();

        Assert.Empty(scan.Adoptable);
        Assert.Contains("hidden on the desktop", scan.Skipped.Single().Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFoldersOwnSettingsFile_IsLeftWhereItIs()
    {
        Entry("desktop.ini", "[.ShellClassInfo]\r\n");

        var scan = Scanner().Scan();

        Assert.Empty(scan.Adoptable);
        Assert.Single(scan.Skipped);
    }

    [Fact]
    public void OnlyTheTopLevelIsRead()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_desktop, "Projects")).FullName;
        File.WriteAllText(Path.Combine(folder, "inside.txt"), "x");

        var scan = Scanner().Scan();

        Assert.Single(scan.Adoptable);
        Assert.Equal(DesktopItemKind.Folder, scan.Adoptable[0].Target.Kind);
        Assert.Equal(folder, scan.Adoptable[0].SourcePath);
    }

    [Fact]
    public void AFileTheUsersOwnDesktopDraws_ShadowsTheSharedOneOfTheSameName()
    {
        var mine = Path.Combine(_desktop, "notes.txt");
        File.WriteAllText(mine, "mine");
        File.WriteAllText(Path.Combine(_publicDesktop, "notes.txt"), "theirs");

        var scan = BothDesktops().Scan();

        // Explorer draws one icon for the two files; the scan adopts the one the user sees.
        Assert.Equal(mine, scan.Adoptable.Single().SourcePath);
        Assert.Contains("same name", scan.Skipped.Single().Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ASharedEntryTheUsersOwnDesktopDoesNotDraw_IsStillAdopted()
    {
        // The user's own entry is hidden, so Explorer draws the shared one and so does the scan.
        var hidden = Path.Combine(_desktop, "notes.txt");
        File.WriteAllText(hidden, "mine");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        var shared = Path.Combine(_publicDesktop, "notes.txt");
        File.WriteAllText(shared, "theirs");

        var scan = BothDesktops().Scan();

        Assert.Equal(shared, scan.Adoptable.Single().SourcePath);
    }

    [Fact]
    public void ReadingTheDesktop_ChangesNothingOnIt()
    {
        var path = Entry("editor.exe", "content");
        var attributesBefore = File.GetAttributes(path);
        var timeBefore = File.GetLastWriteTimeUtc(path);

        Scanner().Scan();

        Assert.True(File.Exists(path));
        Assert.Equal("content", File.ReadAllText(path));
        Assert.Equal(attributesBefore, File.GetAttributes(path));
        Assert.Equal(timeBefore, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void AFolderThatIsNotThere_IsReportedRatherThanTakenAsAnEmptyDesktop()
    {
        var missing = _workspace.PathOf("no-such-desktop");
        var scanner = new DesktopContentScanner(NullLogger<DesktopContentScanner>.Instance, [missing, _desktop]);
        Entry("notes.txt");

        var scan = scanner.Scan();

        Assert.Single(scan.Adoptable);
        Assert.Equal(new[] { missing }, scan.UnreadableFolders);
    }

    [Fact]
    public void ADesktopWithNothingOnIt_IsAnEmptyScan()
    {
        var scan = Scanner().Scan();

        Assert.Empty(scan.Adoptable);
        Assert.Empty(scan.Skipped);
        Assert.Empty(scan.UnreadableFolders);
        Assert.False(scan.HasEntries);
    }

    [Fact]
    public void AnInternetShortcutTooLargeToBeOne_IsReported()
    {
        Entry("huge.url", new string('x', (int)(70 * 1024)));

        var scan = Scanner().Scan();

        Assert.Empty(scan.Adoptable);
        Assert.Contains("too large", scan.Skipped.Single().Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultFolders_AreTheTwoTheShellDraws()
    {
        var folders = DesktopContentScanner.DefaultFolders();

        Assert.Contains(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), folders);
        Assert.Contains(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), folders);
    }
}
