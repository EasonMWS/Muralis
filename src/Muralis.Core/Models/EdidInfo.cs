namespace Muralis.Core.Models;

/// <summary>
/// The hardware identity block of a display (EDID): manufacturer, product and serial numbers.
/// Read from the display device and used to derive <see cref="MonitorIdentity.StableId"/>.
/// </summary>
public sealed record EdidInfo(
    string ManufacturerCode,
    string ProductCode,
    string? SerialNumber,
    string? SerialText);
