using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Muralis.Core.Abstractions;
using Windows.Graphics.Imaging;

namespace Muralis.App.UI.Dock;

/// <summary>
/// Turns a shell icon into the visual a dock icon draws. Both the Shelf and the Pinned Apps zone ask
/// the same provider for the same size, so they are given the same 48 DIP artwork and cannot end up
/// showing different pictures for the same file.
/// </summary>
internal static class ShellIconVisual
{
    /// <summary>The size asked of the shell. 48 DIP is the icon box the dock draws.</summary>
    public const int PixelSize = 48;

    /// <summary>The size drawn inside the 48 DIP box, leaving the surface its breathing room.</summary>
    public const double RenderSize = 44;

    public static FontIcon Fallback(string glyph) => new() { FontSize = 23, Glyph = glyph };

    /// <summary>
    /// The icon for <paramref name="key"/>, or null when the shell has nothing for it — in which case
    /// the caller keeps whatever it was already showing rather than blanking the item.
    /// </summary>
    public static async Task<Image?> CreateAsync(
        IShellIconProvider icons,
        string key,
        CancellationToken cancellationToken = default)
    {
        var icon = await icons.GetAsync(key, PixelSize, cancellationToken);
        if (icon is null)
        {
            return null;
        }

        // Shell icons arrive premultiplied, which is also what the compositor expects; converting
        // would only cost a pass over the pixels and soften the edges.
        var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            icon.PremultipliedBgra.AsBuffer(),
            BitmapPixelFormat.Bgra8,
            icon.Width,
            icon.Height,
            BitmapAlphaMode.Premultiplied);
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(bitmap);
        bitmap.Dispose();

        return new Image
        {
            Source = source,
            Width = RenderSize,
            Height = RenderSize,
            Stretch = Stretch.Uniform,
        };
    }
}
