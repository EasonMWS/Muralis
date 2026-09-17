using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Muralis.Core.DockShell;
using Muralis.Desktop.Launch;
using Xunit;

namespace Muralis.Desktop.Tests.Launch;

public sealed class ShellApplicationInspectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "muralis-pin-" + Guid.NewGuid().ToString("N"));
    private readonly ShellApplicationInspector _inspector = new(NullLogger<ShellApplicationInspector>.Instance);

    [Fact]
    public async Task AProgram_IsNamedByTheFileItself()
    {
        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");

        var described = await _inspector.DescribeAsync(notepad);

        Assert.NotNull(described);
        Assert.Equal(PinnedAppKind.Application, described!.Kind);
        Assert.False(string.IsNullOrWhiteSpace(described.DisplayName));
        Assert.Equal(notepad, described.LaunchTarget);
        Assert.Null(described.ResolvedTarget);
    }

    [Fact]
    public async Task AShortcut_PointsAtTheProgramBehindIt()
    {
        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var shortcut = CreateShortcut("Editor", notepad);

        var described = await _inspector.DescribeAsync(shortcut);

        Assert.NotNull(described);
        Assert.Equal(PinnedAppKind.Shortcut, described!.Kind);
        Assert.Equal("Editor", described.DisplayName);
        Assert.Equal(notepad, described.ResolvedTarget, ignoreCase: true);
    }

    [Fact]
    public async Task AShortcut_AndTheProgramItPointsAt_AreTheSameApplication()
    {
        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var shortcut = CreateShortcut("Editor", notepad);

        var program = await _inspector.DescribeAsync(notepad);
        var link = await _inspector.DescribeAsync(shortcut);

        Assert.NotNull(program);
        Assert.NotNull(link);
        Assert.Equal(program!.Identity, link!.Identity);
    }

    [Fact]
    public async Task AShortcut_KeepsItsOwnPathAsItsLaunchTarget()
    {
        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var shortcut = CreateShortcut("Editor", notepad);

        var described = await _inspector.DescribeAsync(shortcut);

        // The shell is handed the shortcut, not the program, so the shortcut's own settings stay in
        // charge when it is opened.
        Assert.Equal(shortcut, described!.LaunchTarget);
    }

    [Fact]
    public async Task SomethingThatIsNotAnApplication_IsNotDescribed()
    {
        Directory.CreateDirectory(_root);
        var document = Path.Combine(_root, "notes.txt");
        await File.WriteAllTextAsync(document, "hello");

        Assert.Null(await _inspector.DescribeAsync(document));
        Assert.Null(await _inspector.DescribeAsync(Path.Combine(_root, "nowhere.exe")));
        Assert.Null(await _inspector.DescribeAsync(string.Empty));
    }

    [Fact]
    public async Task AShortcut_WithNoProgramBehindIt_IsStillPinnable()
    {
        var shortcut = CreateShortcut("Dangling", Path.Combine(_root, "gone.exe"));

        var described = await _inspector.DescribeAsync(shortcut);

        Assert.NotNull(described);
        Assert.Equal(PinnedAppKind.Shortcut, described!.Kind);
        Assert.Equal(shortcut, described.LaunchTarget);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    /// <summary>
    /// Writes a real shortcut with the shell's own scripting object, so what the inspector is tested
    /// against is a shortcut Explorer could have made rather than one this code produced itself.
    /// </summary>
    private string CreateShortcut(string name, string target)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name + ".lnk");

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        Assert.NotNull(shellType);
        var shell = Activator.CreateInstance(shellType!);
        Assert.NotNull(shell);

        try
        {
            var shortcut = shellType!.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                null,
                shell,
                [path]);
            Assert.NotNull(shortcut);

            var shortcutType = shortcut!.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [target]);
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            Marshal.FinalReleaseComObject(shortcut);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell!);
        }

        Assert.True(File.Exists(path), $"The test shortcut {path} was not created.");
        return path;
    }
}
