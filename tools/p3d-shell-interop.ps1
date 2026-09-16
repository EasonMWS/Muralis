<#
.SYNOPSIS
    The Shell view interop the Phase 3D tools share: raw-vtable access to the desktop's
    IFolderView/IFolderView2, plus the window-level helpers for looking at the icon list.

.DESCRIPTION
    Dot-source this file to get the P3ShellProbe type:

        . (Join-Path $PSScriptRoot 'p3d-shell-interop.ps1')

    The interfaces are declared by vtable slot rather than by an [ComImport] declaration, because a
    managed IFolderView2 would be hundreds of lines of unused methods. The slots are the ones a
    read-only pass can validate against the icon view before anything is written; that evidence is
    in p3d-shell-api-probe.ps1, which uses the same file.

    Nothing here is stateful, and nothing runs on load beyond the Add-Type.
#>

if (-not ('P3ShellProbe' -as [type])) {
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public class P3ShellProbe {
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr p);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int c);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
  [DllImport("ole32.dll", EntryPoint = "CoInitializeEx")] public static extern int CoInit(IntPtr pv, uint flags);

  public const uint LVM_GETITEMCOUNT = 0x1004;
  public const int SW_HIDE = 0;
  public const int SW_SHOW = 5;
  public const int MAX_CLASS_NAME = 256;

  public static string ClassOf(IntPtr h) {
    if (h == IntPtr.Zero) { return "<null>"; }
    StringBuilder sb = new StringBuilder(MAX_CLASS_NAME);
    GetClassName(h, sb, MAX_CLASS_NAME);
    return sb.ToString();
  }

  public static int ListViewItemCount(IntPtr h) {
    if (h == IntPtr.Zero) { return -1; }
    return (int)SendMessageW(h, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
  }

  /// <summary>The icon view lives directly under the window the icons belong to.</summary>
  public static IntPtr FindIconView() {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr top, IntPtr p) {
      IntPtr defView = FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
      if (defView == IntPtr.Zero) { return true; }
      found = defView;
      return false;
    }, IntPtr.Zero);
    return found;
  }

  /// <summary>The icon list the shell draws the desktop items in, or zero while it has none.</summary>
  public static IntPtr FindIconList(IntPtr defView) {
    if (defView == IntPtr.Zero) { return IntPtr.Zero; }
    return FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
  }

  // --- raw vtable access ------------------------------------------------------------------
  // The interfaces behind the desktop view are declared here by slot rather than by an
  // [ComImport] declaration, because a managed declaration of IFolderView/IFolderView2 would be
  // hundreds of lines of unused methods. The slots are validated by the read-only calls below
  // before anything is written.

  [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int FindWindowSWFn(IntPtr self, IntPtr pvarLoc, IntPtr pvarLocRoot, int swClass, out int pHWND, int swfwOptions, out IntPtr ppdispOut);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int QueryServiceFn(IntPtr self, ref Guid service, ref Guid riid, out IntPtr ppv);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int QueryActiveShellViewFn(IntPtr self, out IntPtr ppshv);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GetCurrentViewModeFn(IntPtr self, out uint viewMode);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int ItemCountFn(IntPtr self, uint flags, out int count);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GetAutoArrangeFn(IntPtr self);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GetCurrentFolderFlagsFn(IntPtr self, out uint flags);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int SetCurrentFolderFlagsFn(IntPtr self, uint mask, uint flags);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int SetFolderViewOptionsFn(IntPtr self, uint mask, uint options);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GetFolderViewOptionsFn(IntPtr self, out uint options);

  // A VARIANT is 24 bytes on x64; only VT_I4 and VT_EMPTY are needed here, and the shell rejects a
  // null VARIANT* with RPC_X_NULL_REF_POINTER, so they are built for real.
  public static IntPtr NewVariantInt(int value) {
    IntPtr p = Marshal.AllocHGlobal(24);
    for (int i = 0; i < 24; i++) { Marshal.WriteByte(p, i, 0); }
    Marshal.WriteInt16(p, 0, 3);
    Marshal.WriteInt32(p, 8, value);
    return p;
  }

  public static IntPtr NewVariantEmpty() {
    IntPtr p = Marshal.AllocHGlobal(24);
    for (int i = 0; i < 24; i++) { Marshal.WriteByte(p, i, 0); }
    return p;
  }

  public static void FreeVariant(IntPtr p) {
    if (p != IntPtr.Zero) { Marshal.FreeHGlobal(p); }
  }

  public static IntPtr QueryInterface(IntPtr p, Guid iid) {
    IntPtr outPtr;
    int hr = Marshal.QueryInterface(p, ref iid, out outPtr);
    if (hr != 0) { return IntPtr.Zero; }
    return outPtr;
  }

  public static int Release(IntPtr p) {
    if (p == IntPtr.Zero) { return 0; }
    return Marshal.Release(p);
  }

  public static Delegate Slot(IntPtr obj, int slot, Type t) {
    if (obj == IntPtr.Zero) { throw new InvalidOperationException("no interface pointer"); }
    IntPtr vtbl = Marshal.ReadIntPtr(obj);
    IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
    if (fn == IntPtr.Zero) { throw new InvalidOperationException("empty vtable slot " + slot); }
    return Marshal.GetDelegateForFunctionPointer(fn, t);
  }

  public static FindWindowSWFn FindWindowSW(IntPtr shellWindows) { return (FindWindowSWFn)Slot(shellWindows, 15, typeof(FindWindowSWFn)); }
  public static QueryServiceFn QueryService(IntPtr serviceProvider) { return (QueryServiceFn)Slot(serviceProvider, 3, typeof(QueryServiceFn)); }
  public static QueryActiveShellViewFn QueryActiveShellView(IntPtr shellBrowser) { return (QueryActiveShellViewFn)Slot(shellBrowser, 15, typeof(QueryActiveShellViewFn)); }
  public static GetCurrentViewModeFn GetCurrentViewMode(IntPtr folderView) { return (GetCurrentViewModeFn)Slot(folderView, 3, typeof(GetCurrentViewModeFn)); }
  public static ItemCountFn ItemCount(IntPtr folderView) { return (ItemCountFn)Slot(folderView, 7, typeof(ItemCountFn)); }
  public static GetAutoArrangeFn GetAutoArrange(IntPtr folderView) { return (GetAutoArrangeFn)Slot(folderView, 14, typeof(GetAutoArrangeFn)); }
  public static SetCurrentFolderFlagsFn SetCurrentFolderFlags(IntPtr folderView2) { return (SetCurrentFolderFlagsFn)Slot(folderView2, 24, typeof(SetCurrentFolderFlagsFn)); }
  public static GetCurrentFolderFlagsFn GetCurrentFolderFlags(IntPtr folderView2) { return (GetCurrentFolderFlagsFn)Slot(folderView2, 25, typeof(GetCurrentFolderFlagsFn)); }
  public static SetFolderViewOptionsFn SetFolderViewOptions(IntPtr folderViewOptions) { return (SetFolderViewOptionsFn)Slot(folderViewOptions, 3, typeof(SetFolderViewOptionsFn)); }
  public static GetFolderViewOptionsFn GetFolderViewOptions(IntPtr folderViewOptions) { return (GetFolderViewOptionsFn)Slot(folderViewOptions, 4, typeof(GetFolderViewOptionsFn)); }
}
"@
}
# ---------------------------------------------------------------- the COM route

function Get-HResult([int]$hr) {
    if ($hr -ge 0) { return ("S_OK/0x{0:X8}" -f $hr) }
    try { return (("{0} 0x{1:X8}" -f ([System.ComponentModel.Win32Exception]::new($hr).Message), $hr)) }
    catch { return ("0x{0:X8}" -f $hr) }
}

$SWC_DESKTOP = 8
$SWFO_NEEDDISPATCH = 1

$CLSID_ShellWindows = [Guid]'9BA05972-F6A8-11CF-A442-00A0C90A8F39'
$IID_IShellWindows = [Guid]'85CB6900-4D95-11CF-960C-0080C7F4EE85'
$IID_IServiceProvider = [Guid]'6D5140C1-7436-11CE-8034-00AA006009FA'
$SID_STopLevelBrowser = [Guid]'4C96BE40-915C-11CF-99D3-00AA004AE837'
$IID_IShellBrowser = [Guid]'000214E2-0000-0000-C000-000000000046'
$IID_IFolderView = [Guid]'cde725b0-ccc9-4519-917e-325d72fab4ce'
$IID_IFolderView2 = [Guid]'1af3a467-214f-4298-908e-06b03e0b39f9'
$IID_IFolderViewOptions = [Guid]'3cc974d2-b302-4d36-ad3e-06d93f695d3f'

# FOLDERFLAGS / FOLDERVIEWOPTIONS as declared in shobjidl_core.h.
$FWF_DESKTOP = 0x00000020
$FWF_AUTOARRANGE = 0x00000001
$FWF_SNAPTOGRID = 0x00000004
$FWF_NOICONS = 0x00001000
$FVO_CUSTOMPOSITION = 0x00000002
$FVO_VISTALAYOUT = 0x00000001

function Get-DesktopFolderView([switch]$Quiet) {
    <#
        Walks the documented chain and returns every pointer it managed to reach, so a failure can
        be attributed to the exact step. Every pointer is released by the caller.
    #>
    $result = [pscustomobject]@{
        ShellWindows = [IntPtr]::Zero
        DesktopHwnd  = [IntPtr]::Zero
        Dispatch     = [IntPtr]::Zero
        ServiceProv  = [IntPtr]::Zero
        ShellBrowser = [IntPtr]::Zero
        ShellView    = [IntPtr]::Zero
        FolderView   = [IntPtr]::Zero
        FolderView2  = [IntPtr]::Zero
        FolderViewOptions = [IntPtr]::Zero
        Steps        = @()
        Error        = $null
    }

    function Add-Step($name, $hr) {
        $result.Steps += [pscustomobject]@{ step = $name; hr = $hr }
        if (-not $Quiet) { Write-Host ("  {0,-46} {1}" -f $name, (Get-HResult $hr)) }
    }

    try {
        $rcw = [Activator]::CreateInstance([Type]::GetTypeFromCLSID($CLSID_ShellWindows))
        $unknown = [Runtime.InteropServices.Marshal]::GetIUnknownForObject($rcw)
        $result.ShellWindows = [P3ShellProbe]::QueryInterface($unknown, $IID_IShellWindows)
        [P3ShellProbe]::Release($unknown)
        Add-Step 'CoCreateInstance(ShellWindows) + QI IShellWindows' $(if ($result.ShellWindows -eq [IntPtr]::Zero) { -1 } else { 0 })
        if ($result.ShellWindows -eq [IntPtr]::Zero) { throw 'IShellWindows could not be obtained.' }

        $find = [P3ShellProbe]::FindWindowSW($result.ShellWindows)
        $location = [P3ShellProbe]::NewVariantInt(0)     # CSIDL_DESKTOP
        $root = [P3ShellProbe]::NewVariantEmpty()
        try {
            $desktopHwndValue = 0
            $dispatch = [IntPtr]::Zero
            $hr = $find.Invoke($result.ShellWindows, $location, $root, $SWC_DESKTOP, [ref]$desktopHwndValue, $SWFO_NEEDDISPATCH, [ref]$dispatch)
            $result.DesktopHwnd = [IntPtr]$desktopHwndValue
        }
        finally {
            [P3ShellProbe]::FreeVariant($location)
            [P3ShellProbe]::FreeVariant($root)
        }

        $result.Dispatch = $dispatch
        Add-Step 'IShellWindows::FindWindowSW(SWC_DESKTOP)' $hr
        if ($hr -lt 0 -or $dispatch -eq [IntPtr]::Zero) { throw 'FindWindowSW did not return the desktop dispatch.' }

        $result.ServiceProv = [P3ShellProbe]::QueryInterface($dispatch, $IID_IServiceProvider)
        Add-Step 'QI IServiceProvider on the desktop dispatch' $(if ($result.ServiceProv -eq [IntPtr]::Zero) { -1 } else { 0 })
        if ($result.ServiceProv -eq [IntPtr]::Zero) { throw 'The desktop dispatch is not an IServiceProvider.' }

        $query = [P3ShellProbe]::QueryService($result.ServiceProv)
        $browser = [IntPtr]::Zero
        $hr = $query.Invoke($result.ServiceProv, [ref]$SID_STopLevelBrowser, [ref]$IID_IShellBrowser, [ref]$browser)
        $result.ShellBrowser = $browser
        Add-Step 'IServiceProvider::QueryService(SID_STopLevelBrowser)' $hr
        if ($hr -lt 0 -or $browser -eq [IntPtr]::Zero) { throw 'The top level browser could not be obtained.' }

        $active = [P3ShellProbe]::QueryActiveShellView($result.ShellBrowser)
        $view = [IntPtr]::Zero
        $hr = $active.Invoke($result.ShellBrowser, [ref]$view)
        $result.ShellView = $view
        Add-Step 'IShellBrowser::QueryActiveShellView' $hr
        if ($hr -lt 0 -or $view -eq [IntPtr]::Zero) { throw 'The desktop view is not created yet.' }

        $result.FolderView = [P3ShellProbe]::QueryInterface($view, $IID_IFolderView)
        Add-Step 'QI IFolderView on the desktop view' $(if ($result.FolderView -eq [IntPtr]::Zero) { -1 } else { 0 })

        $result.FolderView2 = [P3ShellProbe]::QueryInterface($view, $IID_IFolderView2)
        Add-Step 'QI IFolderView2 on the desktop view' $(if ($result.FolderView2 -eq [IntPtr]::Zero) { -1 } else { 0 })

        $result.FolderViewOptions = [P3ShellProbe]::QueryInterface($view, $IID_IFolderViewOptions)
        Add-Step 'QI IFolderViewOptions on the desktop view' $(if ($result.FolderViewOptions -eq [IntPtr]::Zero) { -1 } else { 0 })
    }
    catch {
        $result.Error = $_.Exception.Message
    }

    return $result
}


function Release-FolderView($view) {
    if ($null -eq $view) { return }
    [P3ShellProbe]::Release($view.FolderViewOptions) | Out-Null
    [P3ShellProbe]::Release($view.FolderView2) | Out-Null
    [P3ShellProbe]::Release($view.FolderView) | Out-Null
    [P3ShellProbe]::Release($view.ShellView) | Out-Null
    [P3ShellProbe]::Release($view.ShellBrowser) | Out-Null
    [P3ShellProbe]::Release($view.ServiceProv) | Out-Null
    [P3ShellProbe]::Release($view.Dispatch) | Out-Null
    [P3ShellProbe]::Release($view.ShellWindows) | Out-Null
}

function Read-NativeFlags([IntPtr]$folderView2) {
    $flags = 0
    $hr = ([P3ShellProbe]::GetCurrentFolderFlags($folderView2)).Invoke($folderView2, [ref]$flags)
    if ($hr -lt 0) { return $null }
    return $flags
}

# Writes the desktop's folder flags. Only a harness calls this, and only to put the machine back into
# a state it was already in: the app reaches the same interface through its own takeover service.
function Set-DesktopFlags([IntPtr]$folderView2, [uint32]$mask, [uint32]$value) {
    return ([P3ShellProbe]::SetCurrentFolderFlags($folderView2)).Invoke($folderView2, $mask, $value)
}

function Format-Flags($flags) {
    if ($null -eq $flags) { return '(unreadable)' }
    $names = @()
    if ($flags -band $FWF_AUTOARRANGE) { $names += 'FWF_AUTOARRANGE' }
    if ($flags -band $FWF_SNAPTOGRID) { $names += 'FWF_SNAPTOGRID' }
    if ($flags -band $FWF_DESKTOP) { $names += 'FWF_DESKTOP' }
    if ($flags -band $FWF_NOICONS) { $names += 'FWF_NOICONS' }
    if ($names.Count -eq 0) { $names += '(none of the interesting flags)' }
    return ("0x{0:X8} [{1}]" -f $flags, ($names -join ' | '))
}
