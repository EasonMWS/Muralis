namespace Muralis.Core.Tests.TestSupport;

/// <summary>
/// A throwaway directory for one test: it can hand out paths, create the files and folders a test
/// needs, and removes itself afterwards. Files under it stand in for the user's own programs and
/// folders, so no test ever touches anything real.
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    private readonly string _root;

    public TempWorkspace(string name)
    {
        _root = Path.Combine(Path.GetTempPath(), "muralis-tests", name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    /// <summary>A path inside the workspace; whether anything exists at it is up to the caller.</summary>
    public string PathOf(string name) => Path.Combine(_root, name);

    /// <summary>Creates a file and returns its full path.</summary>
    public string FileAt(string name, string content = "")
    {
        var path = PathOf(name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Creates a directory and returns its full path.</summary>
    public string DirectoryAt(string name)
    {
        var path = PathOf(name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
