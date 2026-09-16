<#
.SYNOPSIS
    Evidence probe: can the documented Shell view APIs hide the Windows desktop icons, and what
    does each rung of the fallback ladder actually do on this machine?

.DESCRIPTION
    Phase 3D has to hide Explorer's own desktop icons without touching the registry as the main
    path, without injecting into Explorer and without rewriting icon positions. The documented way
    is the desktop's shell view: IShellWindows::FindWindowSW(SWC_DESKTOP) -> IServiceProvider -
    > IShellBrowser::QueryActiveShellView -> IFolderView2::SetCurrentFolderFlags(FWF_NOICONS), and
    on top of that IFolderViewOptions::SetFolderViewOptions(FVO_CUSTOMPOSITION).

    This probe answers, with readings rather than assumptions:

      1. Can the desktop's IFolderView2 be reached at all (the HRESULT of every step)?
      2. Is the hand-written vtable mapping correct? (Two read-only calls whose answers can be
         cross-checked against the icon view itself are made before anything is written.)
      3. Does FWF_NOICONS actually hide the icons, and does turning it off bring them back?
      4. Does IFolderViewOptions exist on the desktop view, and does FVO_CUSTOMPOSITION do anything?
      5. Does the flag survive an Explorer restart, or must it be re-applied?
      6. What does the registry-backed HideIcons value say right now (read only)?
      7. What does the window-level fallback (hiding the icon list view) look like from outside?

    Nothing here is written without being undone, and the whole write sequence is guarded so an
    interrupted run still puts the icons back. The registry is only ever read; the value at
    HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\HideIcons is reported so the
    takeover design can be measured against it.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/p3d-shell-api-probe.ps1
    powershell -ExecutionPolicy Bypass -File tools/p3d-shell-api-probe.ps1 -SkipExplorerRestart
#>
[CmdletBinding()]
param(
    [switch]$SkipExplorerRestart
)

$ErrorActionPreference = 'Stop'

$outPath = Join-Path (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/p3d') 'p3-shell-api-probe.json'

# ---------------------------------------------------------------- interop

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

# ---------------------------------------------------------------- small helpers

$script:checks = @()
$script:samples = @()

function Add-ProbeCheck([string]$name, [bool]$ok, [string]$detail) {
    $script:checks += [pscustomobject]@{ name = $name; ok = $ok; detail = $detail }
    $mark = if ($ok) { 'ok  ' } else { 'FAIL' }
    Write-Host ("  [{0}] {1}{2}" -f $mark, $name, $(if ($detail) { " :: $detail" } else { '' }))
}

function Add-ProbeSample([string]$name, $value) {
    $script:samples += [pscustomobject]@{ name = $name; value = [string]$value }
    Write-Host ("  {0} = {1}" -f $name, $value)
}

function Get-HResult([int]$hr) {
    if ($hr -ge 0) { return ("S_OK/0x{0:X8}" -f $hr) }
    try { return (("{0} 0x{1:X8}" -f ([System.ComponentModel.Win32Exception]::new($hr).Message), $hr)) }
    catch { return ("0x{0:X8}" -f $hr) }
}

function Get-DesktopLayer {
    $defView = [P3ShellProbe]::FindIconView()
    $list = [P3ShellProbe]::FindIconList($defView)
    $owner = if ($defView -ne [IntPtr]::Zero) { [P3ShellProbe]::GetParent($defView) } else { [IntPtr]::Zero }
    return [pscustomobject]@{
        DefView    = $defView
        DefViewCls = [P3ShellProbe]::ClassOf($defView)
        Owner      = $owner
        OwnerCls   = [P3ShellProbe]::ClassOf($owner)
        List       = $list
        ListCls    = [P3ShellProbe]::ClassOf($list)
        ListVisible = if ($list -ne [IntPtr]::Zero) { [P3ShellProbe]::IsWindowVisible($list) } else { $false }
        ListCount  = [P3ShellProbe]::ListViewItemCount($list)
    }
}

function Show-DesktopLayer([string]$label) {
    $layer = Get-DesktopLayer
    Write-Host ("  {0}: icon view {1} 0x{2:X} (owner {3} 0x{4:X}), icon list {5} 0x{6:X} visible {7}, items {8}" -f `
        $label, $layer.DefViewCls, [long]$layer.DefView, $layer.OwnerCls, [long]$layer.Owner, `
        $layer.ListCls, [long]$layer.List, $layer.ListVisible, $layer.ListCount)
    return $layer
}

# ---------------------------------------------------------------- the COM route

# SWC_DESKTOP with SWFO_NEEDDISPATCH is the documented way to ask for the desktop's automation
# object rather than a bare window handle.
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

function Test-NativeFlags([IntPtr]$folderView2, $desktopCount) {
    <#
        Two read-only calls whose answers can be checked against the icon view. They are what makes
        the hand-written slot numbers trustworthy before any write happens.
    #>
    $mode = 0
    $hr = ([P3ShellProbe]::GetCurrentViewMode($folderView2)).Invoke($folderView2, [ref]$mode)
    $modeOk = ($hr -ge 0) -and ($mode -ge 1) -and ($mode -le 6)
    Add-ProbeCheck 'slot 3 reads a plausible view mode' $modeOk ("IFolderView::GetCurrentViewMode -> hr {0}, mode {1} (FVM_ICON 1 .. FVM_TILE 6)" -f (Get-HResult $hr), $mode)

    $count = -1
    $hr = ([P3ShellProbe]::ItemCount($folderView2)).Invoke($folderView2, $FWF_DESKTOP, [ref]$count)
    $countOk = ($hr -ge 0) -and ($desktopCount -ge 0) -and ($count -eq $desktopCount)
    Add-ProbeCheck 'slot 7 counts the same items as the icon list' $countOk ("IFolderView::ItemCount(FWF_DESKTOP) -> hr {0}, {1} items; the icon list says {2}" -f (Get-HResult $hr), $count, $desktopCount)

    $auto = ([P3ShellProbe]::GetAutoArrange($folderView2)).Invoke($folderView2)
    $autoOk = ($auto -eq 0) -or ($auto -eq 1)
    Add-ProbeCheck 'slot 14 answers the auto-arrange question' $autoOk ("IFolderView::GetAutoArrange -> {0} ({1})" -f $auto, $(if ($auto -eq 0) { 'auto arrange is on' } elseif ($auto -eq 1) { 'auto arrange is off' } else { Get-HResult $auto }))

    return ($modeOk -and $countOk)
}

function Read-NativeFlags([IntPtr]$folderView2) {
    $flags = 0
    $hr = ([P3ShellProbe]::GetCurrentFolderFlags($folderView2)).Invoke($folderView2, [ref]$flags)
    if ($hr -lt 0) { return $null }
    return $flags
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

function Get-BagFFlags {
    <#
        The shell remembers per-folder view flags in its "bag"; the desktop's is bag 1. Read only —
        whether the desktop's FFlags carries FWF_NOICONS is what decides whether hiding the icons
        outlives the process, the session and a reboot.
    #>
    foreach ($path in @(
            'HKCU:\Software\Microsoft\Windows\Shell\Bags\1\Desktop',
            'HKCU:\Software\Microsoft\Windows\ShellNoRoam\Bags\1\Desktop')) {
        try {
            $props = Get-ItemProperty -Path $path -ErrorAction Stop
        } catch {
            continue
        }

        $fflags = $null
        if ($null -ne $props.PSObject.Properties['FFlags']) { $fflags = $props.FFlags }
        $value = if ($null -ne $fflags) { "0x{0:X8} {1}" -f $fflags, (Format-Flags $fflags) } else { '<absent>' }
        return [pscustomobject]@{ Path = $path; FFlags = $fflags; Text = $value }
    }

    return [pscustomobject]@{ Path = '<none>'; FFlags = $null; Text = '<the bag does not exist>' }
}

# ---------------------------------------------------------------- main

Write-Host '=== Phase 3D shell API probe ==='
try {
    [P3ShellProbe]::CoInit([IntPtr]::Zero, 0x2) | Out-Null
} catch {
    Write-Host ("  (CoInitializeEx reported: {0})" -f $_.Exception.Message)
}

$os = Get-CimInstance Win32_OperatingSystem
Add-ProbeSample 'Windows' ("{0} build {1}" -f $os.Caption, $os.BuildNumber)
Add-ProbeSample 'PowerShell' $PSVersionTable.PSVersion.ToString()
Write-Host ''

Write-Host '--- the native desktop as it stands ---'
$before = Show-DesktopLayer 'before'

$hideIconsPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
$hideIconsValue = $null
try {
    $hideIconsValue = (Get-ItemProperty -Path $hideIconsPath -Name 'HideIcons' -ErrorAction Stop).HideIcons
} catch {
    $hideIconsValue = '<absent>'
}
Add-ProbeSample 'registry HideIcons (read only)' $hideIconsValue
$bag = Get-BagFFlags
Add-ProbeSample 'shell bag FFlags (read only)' ("{0} -> {1}" -f $bag.Path, $bag.Text)
Write-Host ''

Write-Host '--- the documented chain to the desktop folder view ---'
$view = Get-DesktopFolderView
if ($view.Error) { Write-Host ("  stopped: {0}" -f $view.Error) }
Write-Host ''

Write-Host '--- validate the vtable mapping before writing anything ---'
$slotsTrusted = $false
if ($view.FolderView2 -ne [IntPtr]::Zero) {
    $slotsTrusted = Test-NativeFlags $view.FolderView2 $before.ListCount
    $flags = Read-NativeFlags $view.FolderView2
    Add-ProbeSample 'IFolderView2::GetCurrentFolderFlags' (Format-Flags $flags)
    Add-ProbeSample 'original FWF_NOICONS state' $(if ($null -ne $flags) { [bool]($flags -band $FWF_NOICONS) } else { 'unreadable' })
} else {
    Add-ProbeCheck 'the desktop view exposes IFolderView2' $false 'no IFolderView2 was reached'
}
Write-Host ''

$restoreNeeded = $false
try {
    if ($slotsTrusted) {
        $flags = Read-NativeFlags $view.FolderView2
        $originalIconsVisible = $true
        if ($null -ne $flags -and ($flags -band $FWF_NOICONS)) { $originalIconsVisible = $false }
        Add-ProbeSample 'the user had desktop icons visible' $originalIconsVisible

        Write-Host '--- FWF_NOICONS: hide, measure, restore ---'
        $set = [P3ShellProbe]::SetCurrentFolderFlags($view.FolderView2)
        $hr = $set.Invoke($view.FolderView2, $FWF_NOICONS, $FWF_NOICONS)
        $restoreNeeded = $hr -ge 0
        Add-ProbeCheck 'SetCurrentFolderFlags(FWF_NOICONS) succeeds' ($hr -ge 0) (Get-HResult $hr)
        Start-Sleep -Milliseconds 700
        $hidden = Show-DesktopLayer 'after hiding'
        $afterFlags = Read-NativeFlags $view.FolderView2
        Add-ProbeSample 'flags while hidden' (Format-Flags $afterFlags)
        Add-ProbeSample 'shell bag FFlags while hidden' (Get-BagFFlags).Text
        Add-ProbeCheck 'the icons really go away' ($hidden.ListCount -eq 0 -or -not $hidden.ListVisible) ("icon list items {0}, visible {1}" -f $hidden.ListCount, $hidden.ListVisible)

        $hr = $set.Invoke($view.FolderView2, $FWF_NOICONS, 0)
        $restoreNeeded = $false
        Add-ProbeCheck 'SetCurrentFolderFlags(0) puts them back' ($hr -ge 0) (Get-HResult $hr)
        Start-Sleep -Milliseconds 700
        $shown = Show-DesktopLayer 'after restoring'
        Add-ProbeSample 'shell bag FFlags after restoring' (Get-BagFFlags).Text
        Add-ProbeCheck 'the icons really come back' ($shown.ListCount -eq $before.ListCount -and $shown.ListVisible) ("icon list items {0} -> {1}, visible {2}" -f $before.ListCount, $shown.ListCount, $shown.ListVisible)

        Write-Host ''
        Write-Host '--- IFolderViewOptions ---'
        if ($view.FolderViewOptions -eq [IntPtr]::Zero) {
            Add-ProbeCheck 'the desktop view exposes IFolderViewOptions' $false 'QI failed; FVO_CUSTOMPOSITION is not available on the desktop view'
            Add-ProbeSample 'FVO_CUSTOMPOSITION' 'unavailable'
        } else {
            Add-ProbeCheck 'the desktop view exposes IFolderViewOptions' $true 'QI succeeded'
            $options = 0
            $hr = ([P3ShellProbe]::GetFolderViewOptions($view.FolderViewOptions)).Invoke($view.FolderViewOptions, [ref]$options)
            Add-ProbeSample 'IFolderViewOptions::GetFolderViewOptions' $(if ($hr -ge 0) { "0x{0:X8}" -f $options } else { Get-HResult $hr })
            $setOptions = [P3ShellProbe]::SetFolderViewOptions($view.FolderViewOptions)
            $hr = $setOptions.Invoke($view.FolderViewOptions, $FVO_CUSTOMPOSITION, $FVO_CUSTOMPOSITION)
            Add-ProbeSample 'SetFolderViewOptions(FVO_CUSTOMPOSITION) hr' (Get-HResult $hr)
            if ($hr -ge 0) {
                Start-Sleep -Milliseconds 500
                Show-DesktopLayer 'after FVO_CUSTOMPOSITION' | Out-Null
                $setOptions.Invoke($view.FolderViewOptions, $FVO_CUSTOMPOSITION, 0) | Out-Null
            }
        }

        if (-not $SkipExplorerRestart) {
            Write-Host ''
            Write-Host '--- does the flag survive an Explorer restart? ---'
            $hr = $set.Invoke($view.FolderView2, $FWF_NOICONS, $FWF_NOICONS)
            $restoreNeeded = $true
            Add-ProbeCheck 'hiding again before the restart' ($hr -ge 0) (Get-HResult $hr)
            Start-Sleep -Milliseconds 700
            Show-DesktopLayer 'before the restart' | Out-Null

            # These pointers belong to the Explorer that is about to die; releasing them before the
            # restart is what a shell-integrated app has to do anyway.
            Release-FolderView $view
            $view = $null

            taskkill /f /im explorer.exe | Out-Null
            Start-Sleep -Seconds 3
            Start-Process explorer.exe | Out-Null
            Start-Sleep -Seconds 6
            $afterRestart = Show-DesktopLayer 'after the restart'

            $view = Get-DesktopFolderView -Quiet
            if ($view.FolderView2 -ne [IntPtr]::Zero) {
                $freshFlags = Read-NativeFlags $view.FolderView2
                Add-ProbeSample 'flags after the restart' (Format-Flags $freshFlags)
                Add-ProbeSample 'shell bag FFlags after the restart' (Get-BagFFlags).Text
                $survived = $false
                if ($null -ne $freshFlags) { $survived = [bool]($freshFlags -band $FWF_NOICONS) }

                # Not a pass/fail: either answer is a fact the takeover design has to live with. What
                # matters is that the state is readable again and can be re-applied either way.
                Add-ProbeSample 'the hide survived the Explorer restart' $survived
                Add-ProbeSample 'the icons are still invisible after the restart' (-not $afterRestart.ListVisible)
                Add-ProbeCheck 'the hide state is readable again after a restart' ($null -ne $freshFlags) (Format-Flags $freshFlags)

                $hr = ([P3ShellProbe]::SetCurrentFolderFlags($view.FolderView2)).Invoke($view.FolderView2, $FWF_NOICONS, $FWF_NOICONS)
                Add-ProbeCheck 'the flag can be applied again after a restart' ($hr -ge 0) (Get-HResult $hr)
                Start-Sleep -Milliseconds 700
                $rehidden = Show-DesktopLayer 'after re-applying'
                Add-ProbeCheck 're-applying after a restart works' ($rehidden.ListCount -eq 0 -or -not $rehidden.ListVisible) ("icon list items {0}, visible {1}" -f $rehidden.ListCount, $rehidden.ListVisible)
                $restoreNeeded = $true
            } else {
                Add-ProbeCheck 'the desktop view can be reached again after a restart' $false $view.Error
            }

            # The window-level fallback below needs a live icon list, so the icons go back first.
            if ($view.FolderView2 -ne [IntPtr]::Zero) {
                ([P3ShellProbe]::SetCurrentFolderFlags($view.FolderView2)).Invoke($view.FolderView2, $FWF_NOICONS, 0) | Out-Null
                $restoreNeeded = $false
                Start-Sleep -Milliseconds 700
                Show-DesktopLayer 'icons restored before the fallback test' | Out-Null
                Add-ProbeSample 'shell bag FFlags after the final restore' (Get-BagFFlags).Text
            }
        }
    }

    Write-Host ''
    Write-Host '--- the window-level fallback, for the record ---'
    $layer = Get-DesktopLayer
    if ($layer.List -ne [IntPtr]::Zero) {
        [P3ShellProbe]::ShowWindow($layer.List, [P3ShellProbe]::SW_HIDE) | Out-Null
        Start-Sleep -Milliseconds 500
        $windowHidden = Show-DesktopLayer 'icon list hidden with ShowWindow'
        Add-ProbeCheck 'ShowWindow(SW_HIDE) on the icon list hides it' (-not $windowHidden.ListVisible) ("visible {0}, items still {1}" -f $windowHidden.ListVisible, $windowHidden.ListCount)
        [P3ShellProbe]::ShowWindow($layer.List, [P3ShellProbe]::SW_SHOW) | Out-Null
        Start-Sleep -Milliseconds 500
        $windowShown = Show-DesktopLayer 'icon list shown again'
        Add-ProbeCheck 'ShowWindow(SW_SHOW) brings it back' $windowShown.ListVisible ("visible {0}" -f $windowShown.ListVisible)
    }
}
finally {
    if ($restoreNeeded) {
        Write-Host ''
        Write-Host '--- restoring the icons after the interrupted run ---'
        $fresh = Get-DesktopFolderView -Quiet
        if ($fresh.FolderView2 -ne [IntPtr]::Zero) {
            ([P3ShellProbe]::SetCurrentFolderFlags($fresh.FolderView2)).Invoke($fresh.FolderView2, $FWF_NOICONS, 0) | Out-Null
            Start-Sleep -Milliseconds 700
            Show-DesktopLayer 'restored in the finally block' | Out-Null
        } else {
            Write-Host ("  the desktop view could not be reached to restore the icons: {0}" -f $fresh.Error)
        }

        Release-FolderView $fresh
    }

    Release-FolderView $view

    Write-Host ''
    $final = Show-DesktopLayer 'final'
    Add-ProbeCheck 'the desktop is as it was found' ($final.ListCount -eq $before.ListCount -and $final.ListVisible) ("icon list items {0} -> {1}" -f $before.ListCount, $final.ListCount)
}

Write-Host ''
$failed = 0
foreach ($check in $script:checks) { if (-not $check.ok) { $failed++ } }
Write-Host ("=== {0} checks, {1} failed ===" -f $script:checks.Count, $failed)

$dir = Split-Path $outPath -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
[pscustomobject]@{
    stage   = 'probe'
    when    = (Get-Date).ToString('s')
    os      = ("{0} build {1}" -f $os.Caption, $os.BuildNumber)
    samples = $script:samples
    steps   = if ($view) { $view.Steps } else { @() }
    checks  = $script:checks
} | ConvertTo-Json -Depth 6 | Set-Content -Path $outPath -Encoding UTF8
Write-Host ("report: {0}" -f $outPath)

if ($failed -gt 0) { exit 1 }
