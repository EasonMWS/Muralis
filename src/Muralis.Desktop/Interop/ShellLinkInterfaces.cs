using System.Runtime.InteropServices;
using System.Text;

namespace Muralis.Desktop.Interop;

/// <summary>
/// The shell's shortcut object and the persistence interface that loads one. These are COM interfaces,
/// so what matters is the vtable slot each method sits in, not the names: every method before the ones
/// that are actually called is declared here as well, in the order the shell declares them, because a
/// slot left out would make the call land on the wrong method.
/// </summary>
/// <remarks>
/// Nothing here writes a shortcut: <c>IPersistFile.Load</c> is the read, and the three getters are the
/// only methods called. A shortcut is read to find out what it points at, so that a pin of a shortcut
/// and a pin of the program behind it can be recognised as the same application.
/// </remarks>
internal static class ShellLinkInterfaces
{
    /// <summary>The shell's shortcut object.</summary>
    internal static readonly Guid ShellLinkClassId = new("00021401-0000-0000-C000-000000000046");

    /// <summary>The buffer size the shell's own samples use for shortcut strings.</summary>
    internal const int MaximumPathLength = 260;

    /// <summary><c>STGM_READ</c>: load the shortcut without asking the shell to update it.</summary>
    internal const uint StorageRead = 0;

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellLinkW
    {
        /// <summary>Slot 3: the path the shortcut stores, with environment variables expanded.</summary>
        void GetPath(
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
            int maximumCharacters,
            nint findData,
            uint flags);

        void GetIDList(out nint idList);

        void SetIDList(nint idList);

        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maximumCharacters);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        /// <summary>Slot 10: the folder the shortcut wants its target started in.</summary>
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maximumCharacters);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);

        /// <summary>Slot 13: the arguments the shortcut passes to its target.</summary>
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maximumCharacters);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

        void GetHotkey(out ushort hotkey);

        void SetHotkey(ushort hotkey);

        void GetShowCommand(out int showCommand);

        void SetShowCommand(int showCommand);

        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maximumCharacters, out int iconIndex);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);

        void Resolve(nint window, uint flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPersistFile
    {
        void GetClassId(out Guid classId);

        [PreserveSig]
        int IsDirty();

        /// <summary>Slot 5: reads the shortcut from disk into the object.</summary>
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string? fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
