using Muralis.Core.Models;

namespace Muralis.Desktop.Surfaces;

/// <summary>
/// What the shell hands a surface content to mount itself on: how big it may be, at what scale,
/// and in which layer. Deliberately free of native window handles — content never needs to know
/// which windowing mechanism the shell happens to use.
/// </summary>
public interface ISurfaceTarget
{
    /// <summary>Full pixel bounds of the display this target belongs to.</summary>
    PixelRect PixelBounds { get; }

    /// <summary>Dots-per-inch scale of that display, for converting logical sizes to pixels.</summary>
    double ScaleFactor { get; }

    /// <summary>Layer the hosting window lives in.</summary>
    SurfaceLayer Layer { get; }
}

/// <summary>
/// Desktop-internal view of a target for content that renders natively (video), which needs the
/// window to create its swap chain on. Not part of the public surface contract on purpose.
/// </summary>
internal interface IWin32SurfaceTarget : ISurfaceTarget
{
    nint WindowHandle { get; }
}
