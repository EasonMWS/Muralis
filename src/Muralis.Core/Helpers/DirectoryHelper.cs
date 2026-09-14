namespace Muralis.Core.Helpers;

public static class DirectoryHelper
{
    /// <summary>
    /// Best-effort total size of all files under <paramref name="path"/>. Returns 0
    /// when the directory is missing; individual unreadable files are skipped.
    /// </summary>
    public static long GetDirectorySize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return 0;
        }

        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // File disappeared or is locked; skip it.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Directory became unreadable while enumerating; return what we have.
        }

        return total;
    }

    /// <summary>
    /// Deletes the contents of <paramref name="path"/> but keeps the directory itself.
    /// </summary>
    public static bool TryClearDirectory(string path, out string? error)
    {
        error = null;
        if (!Directory.Exists(path))
        {
            return true;
        }

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }
}
