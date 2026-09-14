using System.Text;

namespace Muralis.Core.Helpers;

public static class FileNameHelper
{
    /// <summary>
    /// Turns a file name into a human-friendly title: "blue_ridge-4k.jpg" becomes
    /// "Blue Ridge 4K". Used as a fallback when a provider has no nicer title.
    /// </summary>
    public static string ToTitle(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Untitled";
        }

        var words = new List<string>();
        var current = new StringBuilder(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];

            if (c is '_' or '-' or '.' || char.IsWhiteSpace(c))
            {
                FlushWords(words, current);
                continue;
            }

            // Split "img14" into "img" + "14", but keep units like "4k" together.
            if (i > 0 && char.IsDigit(c) && char.IsLetter(name[i - 1]))
            {
                FlushWords(words, current);
            }

            current.Append(c);
        }

        FlushWords(words, current);

        return words.Count == 0 ? "Untitled" : string.Join(' ', words.Select(Capitalize));
    }

    /// <summary>Removes characters that are not allowed in file names.</summary>
    public static string SanitizeFileName(string name)
    {
        var cleaned = string.Join('_', name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        return cleaned.Length == 0 ? "wallpaper" : cleaned;
    }

    /// <summary>
    /// Returns <paramref name="path"/> when it is free, otherwise appends " (2)", " (3)", ...
    /// so a download never overwrites an existing file.
    /// </summary>
    public static string EnsureUniqueFilePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var counter = 2; counter < 1000; counter++)
        {
            var candidate = Path.Combine(directory, $"{name} ({counter}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, $"{name} ({Guid.NewGuid():N}){extension}");
    }

    private static void FlushWords(List<string> words, StringBuilder current)
    {
        if (current.Length > 0)
        {
            words.Add(current.ToString());
            current.Clear();
        }
    }

    private static string Capitalize(string word) =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];
}
