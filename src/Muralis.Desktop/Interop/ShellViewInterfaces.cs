using System.Runtime.InteropServices;

namespace Muralis.Desktop.Interop;

/// <summary>
/// The shell's own view of the desktop, reached the way Microsoft documents it: the ShellWindows
/// collection is asked for the desktop's window, that window is asked for its browser, and the browser
/// for the view it is showing. The view is then asked — through <c>IFolderView2</c> — whether its icons
/// are drawn, and told to stop drawing them.
/// </summary>
/// <remarks>
/// <para>
/// These are COM interfaces, so what matters is the vtable slot each method sits in, not the names.
/// Every declaration below is in the shell's own documented order, and only the methods that are
/// actually called are given a signature: the ones in between are covered by a
/// <c>_VtblGap</c> placeholder, which tells the runtime to leave that many slots alone.
/// </para>
/// <para>
/// The slots the gaps are counted to reach were measured on Windows 11 build 26200 — see
/// <c>tools/p3d-shell-api-probe.ps1</c> and the phase notes — and they agree with the documented
/// order: <c>GetCurrentViewMode</c> at 3, <c>ItemCount</c> at 7, <c>GetAutoArrange</c> at 14,
/// <c>SetCurrentFolderFlags</c> at 24 and <c>GetCurrentFolderFlags</c> at 25.
/// </para>
/// </remarks>
internal static class ShellViewInterfaces
{
    /// <summary>The ShellWindows collection, where the desktop is one of the windows.</summary>
    internal static readonly Guid ShellWindowsClassId = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");

    internal static readonly Guid ShellWindowsInterfaceId = new("85CB6900-4D95-11CF-960C-0080C7F4EE85");

    internal static readonly Guid ServiceProviderInterfaceId = new("6D5140C1-7436-11CE-8034-00AA006009FA");

    internal static readonly Guid ShellBrowserInterfaceId = new("000214E2-0000-0000-C000-000000000046");

    internal static readonly Guid FolderViewInterfaceId = new("cde725b0-ccc9-4519-917e-325d72fab4ce");

    internal static readonly Guid FolderView2InterfaceId = new("1af3a467-214f-4298-908e-06b03e0b39f9");

    /// <summary>The top level browser service of a shell window; asking for it is what leads to the view.</summary>
    internal static readonly Guid TopLevelBrowserServiceId = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    /// <summary><c>SWC_DESKTOP</c>: the entry of ShellWindows that is the desktop.</summary>
    internal const int ShellWindowClassDesktop = 8;

    /// <summary><c>SWFO_NEEDDISPATCH</c>: hand back the browser, which is what the view is reached from.</summary>
    internal const int ShellWindowFindNeedDispatch = 1;

    /// <summary><c>CSIDL_DESKTOP</c> / virtual folder 0: which folder the desktop keeps in that entry.</summary>
    internal const int DesktopFolderOrdinal = 0;

    /// <summary><c>FWF_NOICONS</c>: the view does not draw the folder's icons.</summary>
    internal const uint FolderFlagNoIcons = 0x00001000;

    /// <summary>
    /// A <c>VARIANT</c> as the location arguments of <c>FindWindowSW</c> use it: a type tag and, at the
    /// offset the union starts at, the value. The real type is 24 bytes, so the size matches and the
    /// shell reads what it expects.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct ShellLocation
    {
        [FieldOffset(0)]
        internal ushort Type;

        /// <summary>The value of a <c>VT_I4</c> location, which is all the desktop ever needs.</summary>
        [FieldOffset(8)]
        internal int Value;

        /// <summary>A location holding an integer.</summary>
        internal static ShellLocation Of(int value) => new() { Type = 3 /* VT_I4 */, Value = value };

        /// <summary>An empty location, which the root argument wants.</summary>
        internal static ShellLocation Empty() => new() { Type = 0 /* VT_EMPTY */ };
    }

    /// <summary>
    /// The ShellWindows collection. It is a dispatch interface, so the slots before
    /// <c>FindWindowSW</c> are the seven of <c>IUnknown</c> and <c>IDispatch</c> plus the eight of the
    /// collection itself.
    /// </summary>
    [ComImport]
    [Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellWindows
    {
        /// <summary><c>IUnknown</c>, <c>IDispatch</c> and the collection's own slots before the search.</summary>
        void _VtblGap1_12();

        /// <summary>
        /// The window of the given class showing the given folder. Both location arguments must be real
        /// pointers: the shell rejects a null one outright, which is why they are passed by reference
        /// even though the desktop's location is always the same.
        /// </summary>
        [PreserveSig]
        int FindWindowSW(
            ref ShellLocation location,
            ref ShellLocation locationRoot,
            int windowClass,
            out int window,
            int options,
            [MarshalAs(UnmanagedType.IDispatch)] out object? browser);
    }

    /// <summary>
    /// What a shell window can be asked for: its browser, through which everything else is reached.
    /// The two slots of <c>IOleWindow</c> and the ten of <c>IShellBrowser</c> before the view come
    /// first.
    /// </summary>
    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellBrowser
    {
        void _VtblGap1_12();

        /// <summary>The view the browser is showing right now.</summary>
        [PreserveSig]
        int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object? view);
    }

    /// <summary>
    /// A standard service lookup: the object behind the desktop's window hands back its browser.
    /// </summary>
    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellServiceProvider
    {
        [PreserveSig]
        int QueryService(in Guid service, in Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object? serviceObject);
    }

    /// <summary>
    /// The view itself. Only the view mode, the item count and the folder flags are ever read or
    /// written; the two <c>_VtblGap</c> entries stand in for the eleven methods that are not:
    /// <c>SetCurrentViewMode</c>, <c>GetFolder</c>, <c>Item</c>, then <c>Items</c> through
    /// <c>GetDefaultSpacing</c>.
    /// </summary>
    [ComImport]
    [Guid("cde725b0-ccc9-4519-917e-325d72fab4ce")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellFolderView
    {
        /// <summary>Which of the shell's view modes the folder is shown in; slot 3, the first of the interface.</summary>
        [PreserveSig]
        int GetCurrentViewMode(out uint viewMode);

        void _VtblGap2_3();

        /// <summary>How many items the view holds. Unaffected by whether it draws them.</summary>
        [PreserveSig]
        int ItemCount(uint flags, out int count);

        void _VtblGap6_6();

        /// <summary>Whether the shell is keeping the icons arranged in a grid.</summary>
        [PreserveSig]
        int GetAutoArrange();
    }

    /// <summary>
    /// The view with its folder flags: <c>FWF_NOICONS</c> lives here. Everything the base interface has
    /// and the eight slots of this one before the flags come first.
    /// </summary>
    [ComImport]
    [Guid("1af3a467-214f-4298-908e-06b03e0b39f9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellFolderView2
    {
        void _VtblGap1_21();

        /// <summary>
        /// Changes the folder flags named by <paramref name="mask"/> to <paramref name="flags"/>. The
        /// rest of the word — auto arrange, snap to grid, the view mode in its upper half — is left
        /// exactly as the user set it.
        /// </summary>
        [PreserveSig]
        int SetCurrentFolderFlags(uint mask, uint flags);

        /// <summary>The whole flag word, which is what a give-back puts back.</summary>
        [PreserveSig]
        int GetCurrentFolderFlags(out uint flags);
    }
}
