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
                FlushWord(words, current);
                continue;
            }

            // Split "img14" into "img" + "14", but keep units like "4k" together.
            if (i > 0 && char.IsDigit(c) && char.IsLetter(name[i - 1]))
            {
                FlushWord(words, current);
            }

            current.Append(c);
        }

        FlushWord(words, current);

        return words.Count == 0 ? "Untitled" : string.Join(' ', words.Select(Capitalize));
    }

    private static void FlushWord(List<string> words, StringBuilder current)
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
