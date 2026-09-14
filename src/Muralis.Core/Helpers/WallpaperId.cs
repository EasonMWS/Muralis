using System.Security.Cryptography;
using System.Text;

namespace Muralis.Core.Helpers;

/// <summary>Creates stable ids for wallpapers across sessions.</summary>
public static class WallpaperId
{
    public static string ForLocalFile(string path) => "local:" + ShortHash(path);

    public static string ForRemote(string providerId, string key) => $"{providerId}:{key}";

    /// <summary>Stable short hash used for ids and cache file names.</summary>
    public static string ShortHash(string value)
    {
        var normalized = value.Replace('\\', '/').ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
