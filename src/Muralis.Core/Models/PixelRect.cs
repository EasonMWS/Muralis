namespace Muralis.Core.Models;

/// <summary>An axis-aligned rectangle in physical pixels, relative to the virtual desktop origin.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);
