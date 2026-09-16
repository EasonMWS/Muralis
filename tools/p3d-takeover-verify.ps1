<#
.SYNOPSIS
    Phase 3D live verification: the native desktop takeover, on a real desktop.

.DESCRIPTION
    The harness drives the running application the way a user would - the page's own mode picker, the
    tray's own commands - and reads back what happened from three places that do not depend on the
    app telling the truth about itself:

      * the desktop's shell view, read directly over COM (IFolderView2::GetCurrentFolderFlags), which
        is what actually decides whether Explorer draws its icons
      * the desktop documents on disk, which are the app's memory of what the user asked for
      * the user's own Desktop and Public Desktop folders, snapshotted before and after, which is
        what proves nothing of the user's was written, moved or renamed

    The checks it makes:

      preview
      - choosing Preview puts the canvas on the desktop and adopts the user's own items by reference
      - the native icons are still there: a preview changes what Muralis adds, never what Explorer has
      - the mode the layout document remembers is the one really achieved

      takeover
      - choosing Takeover hides the native icons through the documented shell view, and the marker
        that makes a crash recoverable is on disk while the icons are hidden
      - the user's desktop files are untouched: same names, same sizes, same timestamps
      - the adopted items point at the user's own files by their real path

      give back
      - choosing Native brings the icons back, removes the marker, and leaves the flags the desktop
        was found with - not a forcing of "visible" over a setting the user owns

      a crash
      - a process killed while it owns the desktop leaves the marker and the hidden icons behind, and
        the next launch notices the marker, gives the desktop back before building anything on it, and
        only then puts the mode the user asked for back
      - the emergency restore gives the desktop back while the desktop is owned and the desktop page
        is not even the page on screen: the give-back is the app's guarantee, not the page's

      an Explorer restart
      - the rebuilt desktop gets the takeover put back on it without the app being restarted, and the
        desktop can still be given back afterwards

      the tray
      - the menu the app draws for itself offers the desktop commands while the desktop is taken over,
        and both the restore command and the turn-off command really give the native desktop back

    Everything the harness changes is restored on the way out: settings.json, the user's layout
    documents, the takeover marker and the desktop's own icon flags. The user's own desktop layout
    document is never deleted by that restore, whatever state a run ends in.

.PARAMETER Stage
    probe    - no app launch: reports the machine state, the shell view chain and the files it would touch
    ui       - the three modes, driven through the page, with the shell view read back around them
    recover  - a killed run, the recovery at the next launch, and the emergency restore
    explorer - a restart of Explorer under a taken-over desktop
    tray     - the app's own tray menu, and the two desktop commands in it
    perf     - the takeover at 10, 50 and 100 items: what it costs to take the desktop over and give
               it back, what the scan and the icons cost, and what it costs once it is running
    full     - ui, then recover, then explorer, then tray

.PARAMETER PerfCounts
    How many items the perf stage plants for each measurement. The sizes are the ones the phase is
    asked about; a smaller list is a shorter run, not a different one.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/p3d-takeover-verify.ps1 -Stage probe
    powershell -ExecutionPolicy Bypass -File tools/p3d-takeover-verify.ps1 -Stage ui
    powershell -ExecutionPolicy Bypass -File tools/p3d-takeover-verify.ps1 -Stage full
#>
[CmdletBinding()]
param(
    [ValidateSet('probe', 'ui', 'recover', 'explorer', 'tray', 'perf', 'full')] [string]$Stage = 'probe',
    [string]$Exe = 'src/Muralis.App/bin/Debug/net10.0-windows10.0.26100.0/win-x64/Muralis.exe',
    [string]$OutDir = 'artifacts/p3d',
    [int[]]$PerfCounts = @(10, 50, 100)
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'p3-common.ps1')
. (Join-Path $PSScriptRoot 'p3d-shell-interop.ps1')
Set-BackupPaths 'p3d'

$repoRoot = Split-Path $PSScriptRoot -Parent
$exePath = if ([System.IO.Path]::IsPathRooted($Exe)) { $Exe } else { Join-Path $repoRoot $Exe }
$outPath = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repoRoot $OutDir }

$mainWindowClass = 'WinUIDesktopWin32WindowClass'
$canvasWindowClass = 'MuralisDesktopHostWindow'
$mutexName = 'Local\Muralis.SingleInstance'

# The takeover marker: the file whose presence says a crash may have left the icons hidden.
$markerPath = Join-Path $appData 'desktop\takeover-state.json'
$markerBackup = "$markerPath.p3d.bak"

# The user's own desktop content, from both places Windows shows on the desktop.
$desktopRoots = @(
    (Join-Path $env:USERPROFILE 'Desktop'),
    (Join-Path $env:PUBLIC 'Desktop')
)

# Log lines this stage judges by. Counts are read as growth from a baseline taken before the first
# launch: the log is one file per day and holds every run.
$logTaken = 'The native desktop was taken over with'
$logGivenBack = 'The native desktop was given back as it was found'
$logMarkerFound = 'left the desktop taken over; giving it back'
$logReapplied = 'The desktop takeover was re-applied after the shell restarted'
$logCanvasShowing = 'The desktop canvas is showing'

# What a polling loop would keep saying. Nothing in the app runs on a timer over the desktop, so a line
# these patterns match while the desktop is only being looked at is the whole evidence that something
# started keeping time behind the user's back.
$logScan = 'The desktop holds'
$logScanned = 'The desktop was read'
$logLayoutLoaded = 'Desktop layout loaded from'

# Whether the app window is the one the keys will reach, which mode the picker was last put on, what
# the document said before the step in progress, and the dialog that step was asked on the way.
$script:appFocused = $false
$script:modeIndex = 0
$script:modeBefore = $null
$script:sawDialog = $null

# ---------------------------------------------------------------- the tray

# The tray's window is message-only: the ordinary window lists never show it, and the only way in is
# through the one window that owns every message-only window. The icon's callback arrives there as a
# message, which is what the harness posts to ask for the menu instead of clicking a pixel of the
# taskbar.
if (-not ('P3Tray' -as [type])) {
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class P3Tray {
  public const uint WmTrayCallback = 0x8001;
  public const int WmRightButtonUp = 0x0205;
  public const int WmLeftButtonUp = 0x0202;
  public const string MenuClass = "#32768";
  public const uint MnGetMenu = 0x01E1;
  public const uint WmCancelMode = 0x001F;
  public const uint MfByPosition = 0x00000400;

  [StructLayout(LayoutKind.Sequential)]
  public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder text, int count);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint message, IntPtr wParam, IntPtr lParam);
  [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint message, IntPtr wParam, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr menu);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetMenuStringW(IntPtr menu, uint item, StringBuilder text, int count, uint flags);
  [DllImport("user32.dll")] public static extern bool GetMenuItemRect(IntPtr window, IntPtr menu, uint item, out RECT rect);
  [DllImport("user32.dll")] public static extern IntPtr GetMenuItemID(IntPtr menu, int position);

  public static IntPtr MenuHandleOf(IntPtr menuWindow) {
    return SendMessageW(menuWindow, MnGetMenu, IntPtr.Zero, IntPtr.Zero);
  }

  public static string LabelOf(IntPtr menu, int position) {
    var text = new StringBuilder(512);
    GetMenuStringW(menu, (uint)position, text, 512, MfByPosition);
    return text.ToString();
  }

  public static IntPtr TrayWindowOf(int processId) {
    IntPtr after = IntPtr.Zero;
    while (true) {
      IntPtr found = FindWindowEx(new IntPtr(-3), after, null, null);
      if (found == IntPtr.Zero) return IntPtr.Zero;
      uint pid;
      GetWindowThreadProcessId(found, out pid);
      if ((int)pid == processId) {
        var text = new StringBuilder(256);
        GetClassName(found, text, 256);
        if (text.ToString().StartsWith("MuralisTrayHost_")) return found;
      }
      after = found;
    }
  }
}
"@
}

# A label the app is really showing, read from the app's own resources rather than guessed at: the run
# works in whatever language the app is set to.
function Get-AppLabels([string]$pattern) {
    $labels = @{}
    foreach ($file in @('src/Muralis.App/Strings/Resources.resx', 'src/Muralis.App/Strings/Resources.zh-CN.resx')) {
        $path = Join-Path $repoRoot $file
        if (-not (Test-Path $path)) { continue }
        try {
            [xml]$xml = Get-Content $path -Raw -Encoding UTF8
            foreach ($entry in $xml.root.data) {
                if ("$($entry.name)" -like $pattern) { $labels["$($entry.name)"] = "$($entry.value)" }
            }
        } catch { }
    }
    return $labels
}

# The tray's menu is drawn by the app from its own resources, so the harness reads those same resources
# rather than guessing at a label.
function Get-TrayMenuLabels {
    return Get-AppLabels 'Tray_*'
}

# Opens the tray's own context menu and returns the window it is drawn in. Posting the callback the
# shell would post is how the menu is asked for here: no pixel of the taskbar is touched, and the menu
# is the same one a right click opens.
function Open-TrayMenu([IntPtr]$trayWindow, [int]$processId) {
    [P3Tray]::PostMessage($trayWindow, [P3Tray]::WmTrayCallback, [IntPtr]1, [IntPtr][P3Tray]::WmRightButtonUp) | Out-Null

    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        $menu = [P3Win]::FindWindowByClass($processId, [P3Tray]::MenuClass)
        if ($menu -ne [IntPtr]::Zero) { return $menu }
        Start-Sleep -Milliseconds 100
    }
    return [IntPtr]::Zero
}

# The menu's commands, read from the menu the popup window is showing: the popup answers MN_GETHMENU
# with the handle the app built, and the labels come back as the app appended them - the same strings
# the resources hold, so the run works in whatever language the app is showing.
function Get-MenuEntries([IntPtr]$menuWindow) {
    $menu = [P3Tray]::MenuHandleOf($menuWindow)
    if ($menu -eq [IntPtr]::Zero) { return @() }

    $entries = @()
    for ($i = 0; $i -lt [P3Tray]::GetMenuItemCount($menu); $i++) {
        $entries += [pscustomobject]@{
            Index = $i
            Id    = [int][P3Tray]::GetMenuItemID($menu, $i)
            Label = [P3Tray]::LabelOf($menu, $i)
        }
    }
    return @($entries)
}

function Get-MenuItemNames([IntPtr]$menuWindow) {
    return @((Get-MenuEntries $menuWindow) | Where-Object { $_.Label } | ForEach-Object { $_.Label })
}

# Chooses a command by clicking the item's own rectangle, the way the user does: the menu was opened
# by TrackPopupMenu, so the only thing that ends it is a real choice or a dismissal.
function Invoke-MenuItem([IntPtr]$menuWindow, [string]$label) {
    $menu = [P3Tray]::MenuHandleOf($menuWindow)
    if ($menu -eq [IntPtr]::Zero) { return $false }

    $wanted = $label.Replace('&', '')
    foreach ($entry in (Get-MenuEntries $menuWindow)) {
        if ($entry.Label.Replace('&', '') -ne $wanted) { continue }

        $rect = New-Object 'P3Tray+RECT'
        if (-not [P3Tray]::GetMenuItemRect($menuWindow, $menu, [uint32]$entry.Index, [ref]$rect)) { return $false }

        [P3Win]::MoveTo([int](($rect.Left + $rect.Right) / 2), [int](($rect.Top + $rect.Bottom) / 2))
        Start-Sleep -Milliseconds 150
        [P3Win]::LeftDown()
        Start-Sleep -Milliseconds 60
        [P3Win]::LeftUp()
        return $true
    }
    return $false
}

# Ends a menu that is still up. TrackPopupMenu is a modal loop on the app's thread, so a menu left open
# would stall everything that comes after it - this is the harness's way out of a menu it decided not
# to use.
function Close-TrayMenu([IntPtr]$menuWindow, [IntPtr]$trayWindow) {
    if ($menuWindow -eq [IntPtr]::Zero -or -not [P3Tray]::IsWindow($menuWindow)) { return }

    [P3Tray]::PostMessage($menuWindow, [P3Tray]::WmCancelMode, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    for ($attempt = 0; $attempt -lt 15; $attempt++) {
        if (-not [P3Tray]::IsWindow($menuWindow)) { return }
        Start-Sleep -Milliseconds 100
    }

    [P3Tray]::PostMessage($trayWindow, [P3Tray]::WmCancelMode, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    for ($attempt = 0; $attempt -lt 15; $attempt++) {
        if (-not [P3Tray]::IsWindow($menuWindow)) { return }
        Start-Sleep -Milliseconds 100
    }

    [P3Win]::PressKey(0x1B)
    Start-Sleep -Milliseconds 400
}

# What the harness saw, printed when a menu check fails: a popup that is up but reports nothing is a
# different problem from a popup that never opened, and this is what tells them apart.
function Show-MenuDump([IntPtr]$menuWindow) {
    $menu = [P3Tray]::MenuHandleOf($menuWindow)
    $count = if ($menu -eq [IntPtr]::Zero) { 'no' } else { [P3Tray]::GetMenuItemCount($menu) }
    Write-Host ("  (menu window {0}, handle {1}, {2} item(s))" -f $menuWindow, $menu, $count)
    foreach ($entry in (Get-MenuEntries $menuWindow)) {
        Write-Host ("    [{0}] id {1} '{2}'" -f $entry.Index, $entry.Id, $entry.Label)
    }
}

# ---------------------------------------------------------------- the desktop's own view

# The desktop's icon flags, read from the shell rather than from the app: this is the ground truth for
# "are Explorer's icons hidden right now".
function Get-DesktopIconState {
    $view = Get-DesktopFolderView -Quiet
    try {
        if ($view.FolderView2 -eq [IntPtr]::Zero) {
            return [pscustomobject]@{ Readable = $false; Flags = $null; NoIcons = $null; Error = $view.Error }
        }

        $flags = Read-NativeFlags $view.FolderView2
        return [pscustomobject]@{
            Readable = ($null -ne $flags)
            Flags    = $flags
            NoIcons  = $(if ($null -eq $flags) { $null } else { [bool]($flags -band $FWF_NOICONS) })
            Error    = $null
        }
    } finally {
        Release-FolderView $view
    }
}

# Puts the desktop's icon flag back the way this stage found it. A run that dies in the middle must
# not leave the user's icons hidden, and a run must never force them visible over the user's setting.
function Restore-DesktopFlags($baseline) {
    if ($null -eq $baseline -or -not $baseline.Readable) { return }
    $current = Get-DesktopIconState
    if (-not $current.Readable) { return }
    if ($current.Flags -eq $baseline.Flags) { return }

    $view = Get-DesktopFolderView -Quiet
    try {
        if ($view.FolderView2 -eq [IntPtr]::Zero) { return }
        [void](Set-DesktopFlags $view.FolderView2 $FWF_NOICONS ([uint32]$(if ($baseline.NoIcons) { $FWF_NOICONS } else { 0 })))
        Write-Host ("  (the desktop's icon flags were put back: {0} -> {1})" -f (Format-Flags $current.Flags), (Format-Flags $baseline.Flags))
    } finally {
        Release-FolderView $view
    }
}

# The icon list the shell draws the user's items in. Hiding the icons leaves this window in place but
# invisible, which is what the window-level rung of the ladder is measured by.
function Get-IconListState {
    $defView = [P3ShellProbe]::FindIconView()
    $list = [P3ShellProbe]::FindIconList($defView)
    return [pscustomobject]@{
        Present = ($list -ne [IntPtr]::Zero)
        Visible = $(if ($list -ne [IntPtr]::Zero) { [P3ShellProbe]::IsWindowVisible($list) } else { $false })
        Items   = [P3ShellProbe]::ListViewItemCount($list)
    }
}

# Everything the user has on their desktop, as the file system sees it. Read only, and compared before
# and after: a takeover that renamed, moved or rewrote one of these would show up here.
function Get-DesktopSnapshot {
    $entries = @()
    foreach ($root in $desktopRoots) {
        if (-not (Test-Path $root)) { continue }
        foreach ($entry in (Get-ChildItem -LiteralPath $root -Force -ErrorAction SilentlyContinue)) {
            $entries += ("{0}|{1}|{2}|{3}" -f $root, $entry.Name, $(if ($entry.PSIsContainer) { 'dir' } else { $entry.Length }), $entry.LastWriteTimeUtc.ToString('o'))
        }
    }
    return ($entries | Sort-Object)
}

# ---------------------------------------------------------------- launching and closing

function Get-AppProcesses {
    return @(Get-Process -Name Muralis -ErrorAction SilentlyContinue)
}

function Assert-MuralisNotRunning {
    if (Get-AppProcesses) { throw 'Muralis is already running; stop it first.' }
    if (-not (Test-Path $exePath)) { throw "Executable not found: $exePath" }
}

# Counting only the newest file would read one of them and call the other files' lines missing, which
# is a harness artefact rather than an app failure. Every file of the day is counted together.
function Get-LogCountAll([string]$pattern) {
    $day = (Get-Date).ToString('yyyyMMdd')
    $files = @(Get-ChildItem (Join-Path $appData 'logs') -Filter ("muralis-{0}*.log" -f $day) -ErrorAction SilentlyContinue)

    $total = 0
    foreach ($file in $files) {
        for ($attempt = 0; $attempt -lt 5; $attempt++) {
            try {
                $stream = New-Object IO.FileStream($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
                try {
                    $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)
                    try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
                } finally { $stream.Dispose() }

                $total += ([regex]::Matches($text, [regex]::Escape($pattern))).Count
                break
            } catch {
                Start-Sleep -Milliseconds 100
            }
        }
    }

    return $total
}

function Wait-LogGrew([string]$pattern, [long]$base, [int]$timeoutSeconds, [string]$what) {
    $script:logPattern = $pattern
    $script:logBase = $base
    return (Wait-Until { (Get-LogCountAll $script:logPattern) -gt $script:logBase } $timeoutSeconds $what)
}

function Test-SingleInstanceFree {
    $mutex = $null
    try {
        $mutex = [System.Threading.Mutex]::OpenExisting($mutexName)
        return $false
    } catch {
        return $true
    } finally {
        if ($null -ne $mutex) { $mutex.Dispose() }
    }
}

function Get-MainWindow([int]$processId) {
    return [P3Win]::FindWindowByClass($processId, $mainWindowClass)
}

# Waits for the app to come up. Never minimized during the wait: the window is only ever brought
# forward, and the caller decides when it may be moved aside.
function Start-AppInstance {
    $started = Get-Date
    $process = Start-Process -FilePath $exePath -PassThru
    $window = [IntPtr]::Zero
    for ($attempt = 0; $attempt -lt 1200; $attempt++) {
        $window = Get-MainWindow ([int]$process.Id)
        if ($window -ne [IntPtr]::Zero) { break }
        if ($process.HasExited) { break }
        Start-Sleep -Milliseconds 10
    }

    return [pscustomobject]@{
        Process  = $process
        Window   = $window
        CameUp   = ($window -ne [IntPtr]::Zero)
        LaunchMs = [int]((Get-Date) - $started).TotalMilliseconds
    }
}

# Wide open and brought forward: the harness drives the page's controls, so the window has to be the
# one the input goes to. A window cannot be brought forward on request alone — the system only lets
# the process that owns the last input event take the foreground — so one real event of our own goes
# first, and whether it worked is recorded rather than assumed: keystrokes go to the foreground window,
# and a stage that expects the page to hear them has to be able to prove it did.
function Focus-AppWindow {
    $window = Get-MainWindow ([int]$script:process.Id)
    if ($window -eq [IntPtr]::Zero) { throw 'The Muralis main window was not found.' }
    $script:window = $window
    [P3Win]::ShowWindow($window, [P3Win]::SW_RESTORE) | Out-Null

    $cursor = Get-CursorNow
    [P3Win]::MoveTo([int]$cursor[0], [int]$cursor[1])
    Start-Sleep -Milliseconds 60

    [P3Win]::SetForegroundWindow($window) | Out-Null
    Start-Sleep -Milliseconds 400

    $front = [P3Win]::GetForegroundWindow()
    $script:appFocused = ($front -eq $window)
    if (-not $script:appFocused) {
        Write-Host ("  (the keys are not going to the app: the foreground window is {0})" -f [P3Win]::Describe($front))
    }
}

function Close-App {
    if ($null -eq $script:process) { return }
    $window = Get-MainWindow ([int]$script:process.Id)
    if ($window -ne [IntPtr]::Zero -and [P3Win]::IsWindow($window)) {
        [P3Win]::PostMessage($window, [P3Win]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    }

    $closed = Wait-Until { $script:process.HasExited } 25 'the app to exit'
    if (-not $closed) {
        Stop-Process -Id $script:process.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 800
    }
}

# ---------------------------------------------------------------- the page

# The page's own navigation, without the diagnostics panel: in Native mode there is no canvas, and the
# shared helper waits for a reading that only a mounted canvas produces.
function Open-DynamicPage {
    Open-NavPage 'Nav_Dynamic'
}

# Moves the app to the page a resource key names. It is how the dynamic page is put out of the picture:
# an emergency give-back must not need the page it is usually started from.
function Open-NavPage([string]$key) {
    Focus-AppWindow
    $label = (Get-AppLabels $key)[$key]
    if (-not $label) { throw "The label for '$key' was not found in the app's resources." }
    $nav = Find-ByName (Get-AppRoot) $label $null
    if ($null -eq $nav) { throw "The navigation item '$label' was not found." }
    $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 900
}

function Get-ModePicker {
    $picker = Find-ById (Get-AppRoot) 'DesktopModeSelector'
    if ($null -eq $picker) { throw 'The desktop mode picker was not found on the page.' }
    try { $picker.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView() } catch { }
    return $picker
}

# The modes the picker offers, as the page lists them: native, preview, takeover, in that order.
function Get-ModeOptions($picker) {
    try {
        $picker.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Start-Sleep -Milliseconds 400
        $items = $picker.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::ListItem)))
        $names = @()
        for ($i = 0; $i -lt $items.Count; $i++) {
            $names += [string]$items.Item($i).GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
        }
        [void]$picker.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
        Start-Sleep -Milliseconds 200
        return $names
    } catch {
        Write-Host ("  (the mode picker could not be opened: {0})" -f $_)
        return @()
    }
}

function Read-ModeCaption {
    $text = Find-ById (Get-AppRoot) 'DesktopModeState'
    if ($null -eq $text) { return '' }
    return [string]$text.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
}

function Get-Buttons {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $all = (Get-AppRoot).FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    $names = @()
    for ($i = 0; $i -lt $all.Count; $i++) {
        try { $names += [string]$all.Item($i).GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty) } catch { }
    }
    return $names
}

# Moves the picker by one step. The list itself cannot be trusted with the choice: a WinUI ComboBox
# realises only the item under the pointer, so the items below it are placeholders with no place on the
# screen and nothing to select. The keyboard needs none of that — Down on a collapsed picker moves to
# the next mode and Up back one — and which one it lands on is read back from the document, never from
# a label, so nothing here is bound to the display language.
function Move-ModePickerOnce([int]$key) {
    Focus-AppWindow
    $picker = Get-ModePicker
    try { $picker.SetFocus() } catch { Write-Host ("  (the picker would not take focus: {0})" -f $_.Exception.Message) }
    Start-Sleep -Milliseconds 300

    [P3Win]::PressKey($key)
    Start-Sleep -Milliseconds 320

    # An open list is the one reading that says the key got there: a picker that received a Down shows
    # its list, and a picker that received nothing looks exactly like one that was never addressed.
    $state = 'unknown'
    try {
        $picker = Get-ModePicker
        $state = [string]$picker.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState
    } catch { }
    Write-Host ("  ({0} sent; the picker's list is {1})" -f $(if ($key -eq 0x28) { 'Down' } else { 'Up' }), $state)
    return $state
}

# Waits for the confirmation dialog by counting the page's buttons: the dialog brings its own, and the
# page itself gains and loses none while a mode is chosen.
function Wait-ForDialog([int]$before, [int]$timeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $names = Get-Buttons
        if ($names.Count -gt $before) { return $names }
        Start-Sleep -Milliseconds 150
    }
    return $null
}

# Picks a mode. The picker is moved with the arrow keys, which needs no list, no realised item and no
# reading of any label.
#
# A mode change is never assumed to have landed: the run knows the three modes by name and reads the
# document back after every step, so a key that arrives late or not at all is simply pressed again, and
# the desktop ends up where the run asked for rather than a step away from it. The document is the
# oracle rather than the label, so none of this is bound to the display language.
function Move-ModePickerTo([int]$index, [int]$buttonBaseline) {
    $order = @('Native', 'Preview', 'Takeover')

    for ($attempt = 1; $attempt -le 4; $attempt++) {
        $current = Get-RememberedMode
        if ($current -eq $order[$index]) { return $true }

        $currentIndex = [Array]::IndexOf($order, $current)
        if ($currentIndex -lt 0) { return $false }

        $script:modeBefore = $current
        $key = if ($index -gt $currentIndex) { 0x28 } else { 0x26 }   # VK_DOWN / VK_UP
        $list = Move-ModePickerOnce $key

        if ($list -eq 'Expanded') {
            # The arrows moved the highlight and left the list open, and the picker still has the
            # focus, so the Return goes to the list and takes what is highlighted.
            [P3Win]::PressKey(0x0D)
            Start-Sleep -Milliseconds 400
        }

        # The step is not finished until the document says something else. On the way to a canvas mode
        # the page asks the user first, and that question waits: Return is what answers it, and it is
        # only pressed while the question is really on the screen.
        $deadline = (Get-Date).AddSeconds(12)
        while ((Get-Date) -lt $deadline) {
            if ((Get-RememberedMode) -ne $script:modeBefore) { break }
            $dialog = Wait-ForDialog $buttonBaseline 1
            if ($null -ne $dialog) {
                $script:sawDialog = $dialog
                [P3Win]::PressKey(0x0D)
            }
        }
    }

    return ((Get-RememberedMode) -eq $order[$index])
}

# Moves the picker onto one of the three modes and reports what came of it: whether the desktop really
# moved, and the dialog the page asked on the way, if it asked at all.
function Select-Mode([int]$index) {
    # The option check closes the list right before this, and a control that is still closing does not
    # open again; the beat lets it settle.
    Start-Sleep -Milliseconds 700

    $before = (Get-Buttons).Count
    $script:sawDialog = $null
    $moved = Move-ModePickerTo $index $before
    if ($moved) { $script:modeIndex = $index }

    return [pscustomobject]@{ Moved = $moved; Dialog = $script:sawDialog }
}

# ---------------------------------------------------------------- the modes, in order

function Get-RememberedMode {
    # No document at all is the space a native desktop occupies: nothing has been asked for, so there
    # is nothing to remember. An older document with no takeover section reads the same way.
    $layout = Read-Layout
    if ($null -eq $layout) { return 'Native' }
    if ($null -eq $layout.Takeover) { return 'Native' }
    return [string]$layout.Takeover.Mode
}

function Wait-LayoutMode([string]$mode, [int]$timeoutSeconds = 25) {
    $script:wantedMode = $mode
    $met = Wait-Until { (Get-RememberedMode) -eq $script:wantedMode } $timeoutSeconds ("the layout document to say {0}" -f $mode)
    return [pscustomobject]@{ Met = $met; Mode = (Get-RememberedMode) }
}

function Get-Marker {
    if (-not (Test-Path $markerPath)) { return $null }
    try { return (Get-Content $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json) } catch { return $null }
}

# The items the app adopted from the user's own desktop, as the document records them.
function Get-AdoptedItems {
    $layout = Read-Layout
    if ($null -eq $layout) { return @() }
    return @($layout.Items | Where-Object { $_.SourcePath -and $_.SourcePath.Length -gt 0 })
}

# ---------------------------------------------------------------- stages

function Invoke-ProbeStage {
    Write-Host 'Machine state'
    Write-Host ("  display:            {0}x{1}" -f [P3Win]::GetSystemMetrics(0), [P3Win]::GetSystemMetrics(1))
    Write-Host ("  executable:         {0}" -f $exePath)
    Write-Host ("  settings:           {0}" -f $script:settingsPath)
    Write-Host ("  layout document:    {0}" -f $script:layoutPath)
    Write-Host ("  takeover marker:    {0}  {1}" -f $markerPath, $(if (Test-Path $markerPath) { 'PRESENT' } else { 'absent' }))
    Write-Host ("  single-instance:    {0}" -f $(if (Test-SingleInstanceFree) { 'free' } else { 'held' }))
    Write-Host ("  Muralis processes:  {0}" -f (Get-AppProcesses).Count)
    Write-Host ''
    Write-Host 'The desktop as the shell sees it'

    $state = Get-DesktopIconState
    Write-Host ("  IFolderView2:       {0}" -f $(if ($state.Readable) { 'reached' } else { "not reachable ({0})" -f $state.Error }))
    Write-Host ("  folder flags:       {0}" -f (Format-Flags $state.Flags))
    Write-Host ("  icons hidden:       {0}" -f $state.NoIcons)

    $list = Get-IconListState
    Write-Host ("  icon list:          present {0}, visible {1}, {2} item(s)" -f $list.Present, $list.Visible, $list.Items)
    Write-Host ("  desktop files:      {0} entries under {1} root(s)" -f (Get-DesktopSnapshot).Count, $desktopRoots.Count)

    Add-Check 'probe: the desktop view is reachable' $state.Readable ("flags {0}" -f (Format-Flags $state.Flags))
    Add-Check 'probe: the icon list is there to be read' $list.Present ("visible {0}, {1} items" -f $list.Visible, $list.Items)
    Add-Check 'probe: no run left the icons hidden' (-not $state.NoIcons) ("FWF_NOICONS {0}" -f $state.NoIcons)
    Add-Check 'probe: no takeover marker was left behind' (-not (Test-Path $markerPath)) $markerPath
    Assert-MuralisNotRunning
}

function Invoke-UiStage {
    Write-Host ''
    Write-Host '=== the three desktop modes, driven from the page ==='

    $before = @{
        Icons   = Get-DesktopIconState
        Files   = Get-DesktopSnapshot
        Taken   = Get-LogCountAll $logTaken
        Given   = Get-LogCountAll $logGivenBack
        Canvas  = Get-LogCountAll $logCanvasShowing
    }

    Backup-Settings
    Disable-CloseToTray
    Park-LayoutFiles
    Park-Marker

    # One item this stage owns, so the taken-over desktop can be asked to open something without
    # aiming at anything of the user's, and the user's own desktop is adopted beside it the way a
    # first run does it.
    $uiItemId = 'ui_app'
    $uiNotepad = Join-Path $env:SystemRoot 'System32\notepad.exe'
    Write-Layout @(New-LayoutItem -Id $uiItemId -Name 'notepad' -Kind 'application' -Path $uiNotepad) 'Native' $true
    $script:notepadBaseline = Get-NotepadIds

    $app = Start-AppInstance
    $script:process = $app.Process
    $script:window = $app.Window
    Add-Check 'ui: the app came up' $app.CameUp ("launch {0} ms, window {1}" -f $app.LaunchMs, $app.Window)
    if (-not $app.CameUp) { return }

    Wait-Until { $null -ne (Get-AppRoot) } 15 'the app window to answer' | Out-Null
    Add-Check 'ui: the app started in Native, as the document said' ((Get-RememberedMode) -eq 'Native') ("remembered mode {0}" -f (Get-RememberedMode))

    Open-DynamicPage
    $script:modeIndex = 0
    $picker = Get-ModePicker
    $options = Get-ModeOptions $picker
    Add-Check 'ui: the picker offers the three modes' ($options.Count -eq 3) ("{0} option(s): {1}" -f $options.Count, ($options -join ' / '))
    Add-Check 'ui: the page says which mode the desktop is in' ((Read-ModeCaption).Length -gt 0) (Read-ModeCaption)
    Add-Check 'ui: the app window is the one the keys go to' $script:appFocused ("foreground is {0}" -f [P3Win]::Describe([P3Win]::GetForegroundWindow()))
    Add-Sample 'AppFocused' $script:appFocused

    # ---- preview
    Write-Host ''
    Write-Host '-- preview: the canvas on, the user''s own icons untouched'
    $pick = Select-Mode 1
    Add-Check 'ui: the picker moved the desktop to preview' $pick.Moved ("the document says {0}" -f (Get-RememberedMode))
    Add-Check 'ui: choosing preview asks the user first' ($null -ne $pick.Dialog) ("dialog buttons: {0}" -f $(if ($null -eq $pick.Dialog) { 'none seen' } else { $pick.Dialog -join ' / ' }))

    $mode = Wait-LayoutMode 'Preview'
    Add-Check 'ui: preview is what the document remembers' $mode.Met ("document says {0}" -f $mode.Mode)

    $canvasUp = Wait-Until { ([P3Win]::ClassesWithPrefix($canvasWindowClass)).Count -ge 1 } 25 'the canvas window'
    Add-Check 'ui: the canvas is on the desktop' $canvasUp ("{0} canvas window(s)" -f ([P3Win]::ClassesWithPrefix($canvasWindowClass)).Count)

    $icons = Get-DesktopIconState
    Add-Check 'ui: preview leaves the native icons alone' ($icons.Readable -and ($icons.Flags -eq $before.Icons.Flags)) ("before {0} / now {1}" -f (Format-Flags $before.Icons.Flags), (Format-Flags $icons.Flags))

    $adopted = Get-AdoptedItems
    $outside = @()
    foreach ($item in $adopted) {
        $inRoot = $false
        foreach ($root in $desktopRoots) {
            if ($item.SourcePath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { $inRoot = $true; break }
        }
        if (-not $inRoot) { $outside += $item }
    }
    Add-Check "ui: the user's own items were adopted by reference" ($adopted.Count -gt 0) ("{0} item(s) name a source path" -f $adopted.Count)
    Add-Check 'ui: every adopted item points into a desktop folder' ($outside.Count -eq 0) ("{0} outside: {1}" -f $outside.Count, (($outside | Select-Object -First 3 | ForEach-Object { $_.SourcePath }) -join '; '))

    # ---- takeover
    Write-Host ''
    Write-Host '-- takeover: Explorer''s icons hidden, and a marker that makes a crash recoverable'
    $pick = Select-Mode 2
    Add-Check 'ui: the picker moved the desktop to takeover' $pick.Moved ("the document says {0}" -f (Get-RememberedMode))
    Write-Host ("  (the confirm dialog was{0} shown again: the canvas was already on)" -f $(if ($null -eq $pick.Dialog) { ' not' } else { '' }))

    $mode = Wait-LayoutMode 'Takeover'
    Add-Check 'ui: takeover is what the document remembers' $mode.Met ("document says {0}" -f $mode.Mode)

    $tookLine = Wait-LogGrew $logTaken $before.Taken 25 'the takeover to be logged'
    Add-Check 'ui: the takeover went through the documented shell view' $tookLine ("the log line '{0}' appeared: {1}" -f $logTaken, $tookLine)

    $hidden = Wait-Until { (Get-DesktopIconState).NoIcons } 20 'the desktop icons to be hidden'
    $icons = Get-DesktopIconState
    Add-Check 'ui: the native icons are really hidden' ($icons.Readable -and $icons.NoIcons) (Format-Flags $icons.Flags)

    $marker = Get-Marker
    Add-Check 'ui: the marker is on disk while the icons are hidden' ($null -ne $marker -and $marker.TakeoverWasActive) ("TakeoverWasActive {0}, session {1}" -f $(if ($null -eq $marker) { 'n/a' } else { $marker.TakeoverWasActive }), $(if ($null -eq $marker) { 'n/a' } else { $marker.SessionId }))
    Add-Check 'ui: the marker remembers what the desktop looked like' ($null -ne $marker -and $null -ne $marker.OriginalNativeState) 'OriginalNativeState present'
    Add-Check 'ui: the marker names the strategy that hid them' ($null -ne $marker -and $null -ne $marker.Strategy) $(if ($null -eq $marker) { 'n/a' } else { [string]$marker.Strategy })

    $after = @(Get-DesktopSnapshot)
    $changed = @(Compare-Object -ReferenceObject @($before.Files) -DifferenceObject $after)
    Add-Check 'ui: not one of the user''s desktop files changed' ($changed.Count -eq 0) ("{0} difference(s){1}" -f $changed.Count, $(if ($changed.Count -gt 0) { ': ' + (($changed | Select-Object -First 3 | ForEach-Object { $_.InputObject }) -join '; ') } else { '' }))

    # ---- the canvas is the way in: with Explorer's icons gone, the pointer, a click, a double click
    # and a drag all have to work on what Muralis is showing instead.
    Write-Host ''
    Write-Host '-- taken over: the canvas is what answers the pointer'
    Minimize-AppWindow

    $uiItem = Get-LayoutItem (Read-Layout) $uiItemId
    if ($null -eq $uiItem) { throw "The stage's own item '$uiItemId' is not in the document." }
    $uiCentre = Get-ItemCentre $uiItem
    $blocked = Clear-TheDesktop @($uiCentre[0]) @($uiCentre[1]) ' (on the taken-over desktop)'
    Add-Check 'takeover: the canvas item is on the desktop with the icons hidden' ($blocked.Count -eq 0) ("blocked {0}" -f ($blocked -join '; '))
    if ($blocked.Count -gt 0) { throw "The desktop is still covered: $($blocked -join '; ')" }

    Move-And-Settle $uiCentre[0] $uiCentre[1] 500
    $hovered = Read-StateAt $uiCentre[0] $uiCentre[1]
    Add-Check 'takeover: the canvas answers a pointer while the icons are hidden' ($hovered.HoverId -eq $uiItemId) ("hovered {0}" -f $hovered.HoverId)
    Add-Check 'takeover: the item under the pointer magnifies as it always does' ($hovered.HoverScale -gt 1.0) ("scale {0}" -f $hovered.HoverScale)

    # A single click picks the item out and a double click opens it: the same rule the canvas follows
    # without a takeover, which is the point of taking the desktop over rather than replacing it.
    Move-And-Settle $uiCentre[0] $uiCentre[1] 200
    Click-Pointer
    Start-Sleep -Milliseconds 900
    $clicked = Read-State
    Add-Check 'takeover: a single click picks the item out' ($clicked.SelectedId -eq $uiItemId) ("selected {0}" -f $clicked.SelectedId)
    Add-Check 'takeover: a single click opens nothing' ((Wait-NewNotepad 3).Count -eq 0) 'no new process'

    Move-Pointer $uiCentre[0] $uiCentre[1]
    Start-Sleep -Milliseconds 150
    DoubleClick-Pointer
    $started = Wait-NewNotepad 15
    Add-Check 'takeover: a double click opens the program it names' ($started.Count -gt 0) ("pids {0}" -f ($started -join ', '))
    Close-FixtureNotepads $started
    Start-Sleep -Milliseconds 600

    # A drag moves the item and saves where it was let go, and a drag never opens anything — the
    # user's own desktop file behind it is not moved, which is checked again below.
    $script:droppedItem = $null
    Drag-Pointer $uiCentre[0] $uiCentre[1] ($uiCentre[0] + 260) ($uiCentre[1] - 180)
    $saved = Wait-Until {
        $script:droppedItem = Get-LayoutItem (Read-Layout) $uiItemId
        ($null -ne $script:droppedItem) -and
            (($script:droppedItem.OffsetXDip -ne $uiItem.OffsetXDip) -or ($script:droppedItem.OffsetYDip -ne $uiItem.OffsetYDip))
    } 10 'the drop to be saved'
    Add-Check 'takeover: the drop moved the item and was saved' $saved ("{0},{1} -> {2},{3}" -f $uiItem.OffsetXDip, $uiItem.OffsetYDip, $script:droppedItem.OffsetXDip, $script:droppedItem.OffsetYDip)
    Add-Check 'takeover: the drag and its release opened nothing' ((Wait-NewNotepad 2).Count -eq 0) 'no new process'

    if ($saved) {
        $dropCentre = Get-ItemCentre $script:droppedItem
        Move-And-Settle $dropCentre[0] $dropCentre[1] 500
        $hoveredDrop = Read-StateAt $dropCentre[0] $dropCentre[1]
        Add-Check 'takeover: the item is really drawn where it was let go' ($hoveredDrop.HoverId -eq $uiItemId) ("hovered {0}" -f $hoveredDrop.HoverId)
    }

    $icons = Get-DesktopIconState
    Add-Check 'takeover: the icons are still hidden after all of it' ($icons.Readable -and $icons.NoIcons) (Format-Flags $icons.Flags)

    # ---- give back
    Write-Host ''
    Write-Host '-- native: the desktop is the user''s again, as it was found'
    $pick = Select-Mode 0
    Add-Check 'ui: the picker moved the desktop back to native' $pick.Moved ("the document says {0}" -f (Get-RememberedMode))

    $mode = Wait-LayoutMode 'Native'
    Add-Check 'ui: native is what the document remembers' $mode.Met ("document says {0}" -f $mode.Mode)

    $givenLine = Wait-LogGrew $logGivenBack $before.Given 25 'the desktop to be given back'
    Add-Check 'ui: the desktop was given back' $givenLine ("the log line '{0}' appeared: {1}" -f $logGivenBack, $givenLine)

    $back = Wait-Until { -not (Get-DesktopIconState).NoIcons } 20 'the desktop icons to be visible again'
    $icons = Get-DesktopIconState
    Add-Check 'ui: the native icons are visible again' ($icons.Readable -and (-not $icons.NoIcons)) (Format-Flags $icons.Flags)
    Add-Check 'ui: the desktop flags are exactly what they were found with' ($icons.Readable -and ($icons.Flags -eq $before.Icons.Flags)) ("before {0} / now {1}" -f (Format-Flags $before.Icons.Flags), (Format-Flags $icons.Flags))

    $cleared = Wait-Until { -not (Test-Path $markerPath) } 20 'the marker to be removed'
    Add-Check 'ui: the marker is gone once the icons are back' $cleared ("marker present: {0}" -f (Test-Path $markerPath))

    $canvasGone = Wait-Until { ([P3Win]::ClassesWithPrefix($canvasWindowClass)).Count -eq 0 } 20 'the canvas to come off the desktop'
    Add-Check 'ui: the canvas came off with the mode' $canvasGone ("{0} canvas window(s) left" -f ([P3Win]::ClassesWithPrefix($canvasWindowClass)).Count)

    $after = @(Get-DesktopSnapshot)
    $changed = @(Compare-Object -ReferenceObject @($before.Files) -DifferenceObject $after)
    Add-Check 'ui: the user''s desktop files are still untouched' ($changed.Count -eq 0) ("{0} difference(s)" -f $changed.Count)

    Close-App
}

# The shell is restarted the way it is on a machine that is being used: killed and started again. The
# new desktop is built from scratch, and everything done to the old one is gone with it.
function Restart-Explorer {
    taskkill /f /im explorer.exe | Out-Null
    Start-Sleep -Milliseconds 1500
    Start-Process explorer.exe | Out-Null
}

# A run that is killed while it owns the desktop. This is the case the marker exists for: no shutdown,
# no give-back, no chance to tidy up, and a desktop whose icons are hidden with nothing on it.
function Invoke-RecoveryStage {
    Write-Host ''
    Write-Host '=== a run that is killed while it owns the desktop ==='

    $before = @{
        Icons  = Get-DesktopIconState
        Files  = Get-DesktopSnapshot
        Marker = Get-LogCountAll $logMarkerFound
        Given  = Get-LogCountAll $logGivenBack
        Taken  = Get-LogCountAll $logTaken
    }

    Backup-Settings
    Disable-CloseToTray
    Park-LayoutFiles
    Park-Marker

    $app = Start-AppInstance
    $script:process = $app.Process
    $script:window = $app.Window
    Add-Check 'recover: the app came up' $app.CameUp ("launch {0} ms, window {1}" -f $app.LaunchMs, $app.Window)
    if (-not $app.CameUp) { return }

    Wait-Until { $null -ne (Get-AppRoot) } 15 'the app window to answer' | Out-Null
    Open-DynamicPage
    $script:modeIndex = 0

    $pick = Select-Mode 2
    Add-Check 'recover: the desktop was taken over before the crash' $pick.Moved ("document says {0}" -f (Get-RememberedMode))
    $hidden = Wait-Until { (Get-DesktopIconState).NoIcons } 25 'the desktop icons to be hidden'
    Add-Check 'recover: the icons are hidden before the crash' $hidden (Format-Flags (Get-DesktopIconState).Flags)

    # ---- the crash
    Write-Host ''
    Write-Host '-- the process is killed outright, the way a crash kills it'
    Stop-Process -Id $script:process.Id -Force
    $script:process = $null
    Start-Sleep -Milliseconds 1500

    $marker = Get-Marker
    Add-Check 'recover: a killed run leaves its marker behind' ($null -ne $marker -and $marker.TakeoverWasActive) ("marker present: {0}, TakeoverWasActive {1}" -f ($null -ne $marker), $(if ($null -eq $marker) { 'n/a' } else { $marker.TakeoverWasActive }))
    $icons = Get-DesktopIconState
    Add-Check 'recover: the icons stay hidden after the crash' ($icons.Readable -and $icons.NoIcons) (Format-Flags $icons.Flags)

    # ---- the next launch
    Write-Host ''
    Write-Host '-- the next launch finds the marker and pays the desktop back before it restores the mode'
    $app = Start-AppInstance
    $script:process = $app.Process
    $script:window = $app.Window
    Add-Check 'recover: the app came up again' $app.CameUp ("launch {0} ms, window {1}" -f $app.LaunchMs, $app.Window)
    if (-not $app.CameUp) { return }

    $noticed = Wait-LogGrew $logMarkerFound $before.Marker 30 'the marker to be noticed'
    Add-Check 'recover: the next launch noticed the marker the crash left' $noticed ("the log line '{0}' appeared: {1}" -f $logMarkerFound, $noticed)
    $given = Wait-LogGrew $logGivenBack $before.Given 30 'the desktop to be given back on startup'
    Add-Check 'recover: the desktop was given back before anything was built on it' $given ("the log line '{0}' appeared: {1}" -f $logGivenBack, $given)

    $mode = Wait-LayoutMode 'Takeover' 30
    Add-Check 'recover: the mode the user asked for came back' $mode.Met ("document says {0}" -f $mode.Mode)
    $rehidden = Wait-Until { (Get-DesktopIconState).NoIcons } 25 'the desktop to be taken over again'
    Add-Check 'recover: the desktop is taken over again once it is trustworthy' $rehidden (Format-Flags (Get-DesktopIconState).Flags)

    $after = @(Get-DesktopSnapshot)
    $changed = @(Compare-Object -ReferenceObject @($before.Files) -DifferenceObject $after)
    Add-Check 'recover: not one of the user''s desktop files changed' ($changed.Count -eq 0) ("{0} difference(s)" -f $changed.Count)

    # ---- the emergency restore, from the tray, with the page that usually starts it closed. The
    # give-back is the app's guarantee, not the page's: it has to work while the desktop is owned and
    # the dynamic page is not the one on screen.
    Write-Host ''
    Write-Host '-- the emergency restore, with the desktop page out of the picture'
    $labels = Get-TrayMenuLabels
    Open-NavPage 'Nav_Settings'
    $pageGone = $null -eq (Find-ById (Get-AppRoot) 'DesktopModeSelector')
    Add-Check 'recover: the desktop page is out of the picture' $pageGone ("the page's picker is {0}" -f $(if ($pageGone) { 'gone' } else { 'still on screen' }))

    $tray = [P3Tray]::TrayWindowOf([int]$script:process.Id)
    Add-Check 'recover: the tray is still there with the page closed' ($tray -ne [IntPtr]::Zero) ("tray window {0}" -f $tray)
    if ($tray -ne [IntPtr]::Zero) {
        $menu = Open-TrayMenu $tray ([int]$script:process.Id)
        Add-Check 'recover: the tray menu opens with the page closed' ($menu -ne [IntPtr]::Zero) ("menu window {0}" -f $menu)
        if ($menu -ne [IntPtr]::Zero) {
            $chosen = Invoke-MenuItem $menu $labels['Tray_RestoreDesktop']
            Add-Check 'recover: the emergency restore was chosen' $chosen $labels['Tray_RestoreDesktop']
            Close-TrayMenu $menu $tray
        }

        $back = Wait-Until { -not (Get-DesktopIconState).NoIcons } 25 'the desktop to come back'
        Add-Check 'recover: the emergency restore puts the desktop back' $back (Format-Flags (Get-DesktopIconState).Flags)

        $cleared = Wait-Until { -not (Test-Path $markerPath) } 20 'the marker to be cleared'
        Add-Check 'recover: the emergency restore leaves no marker behind' $cleared ("marker present: {0}" -f (Test-Path $markerPath))

        $mode = Wait-LayoutMode 'Native' 25
        Add-Check 'recover: the emergency restore leaves the desktop native' $mode.Met ("document says {0}" -f $mode.Mode)
        Add-Check 'recover: the desktop is exactly as it was found' ((Get-DesktopIconState).Flags -eq $before.Icons.Flags) ("before {0} / now {1}" -f (Format-Flags $before.Icons.Flags), (Format-Flags (Get-DesktopIconState).Flags))
    }

    Close-App
}

# Explorer restarted under a taken-over desktop: the desktop it draws is new, so the icons it draws are
# its own again and the takeover has to be put back on top of it.
function Invoke-ExplorerStage {
    Write-Host ''
    Write-Host '=== Explorer restarts while the desktop is Muralis''s ==='

    $before = @{
        Icons     = Get-DesktopIconState
        Files     = Get-DesktopSnapshot
        Reapplied = Get-LogCountAll $logReapplied
    }

    Backup-Settings
    Disable-CloseToTray
    Park-LayoutFiles
    Park-Marker

    $app = Start-AppInstance
    $script:process = $app.Process
    $script:window = $app.Window
    Add-Check 'explorer: the app came up' $app.CameUp ("launch {0} ms, window {1}" -f $app.LaunchMs, $app.Window)
    if (-not $app.CameUp) { return }

    Wait-Until { $null -ne (Get-AppRoot) } 15 'the app window to answer' | Out-Null
    Open-DynamicPage
    $script:modeIndex = 0

    $pick = Select-Mode 2
    Add-Check 'explorer: the desktop was taken over before the restart' $pick.Moved ("document says {0}" -f (Get-RememberedMode))
    $hidden = Wait-Until { (Get-DesktopIconState).NoIcons } 25 'the desktop icons to be hidden'
    Add-Check 'explorer: the icons are hidden before the restart' $hidden (Format-Flags (Get-DesktopIconState).Flags)

    # ---- the restart
    Write-Host ''
    Write-Host '-- Explorer is killed and started again'
    Restart-Explorer

    $reapplied = Wait-LogGrew $logReapplied $before.Reapplied 60 'the takeover to be re-applied'
    Add-Check 'explorer: the takeover is re-applied to the new desktop' $reapplied ("the log line '{0}' appeared: {1}" -f $logReapplied, $reapplied)

    $hidden = Wait-Until { (Get-DesktopIconState).NoIcons } 60 'the rebuilt desktop to hide its icons'
    Add-Check 'explorer: the rebuilt desktop hides its icons again' $hidden (Format-Flags (Get-DesktopIconState).Flags)
    Add-Check 'explorer: the mode is still the one the user asked for' ((Get-RememberedMode) -eq 'Takeover') ("document says {0}" -f (Get-RememberedMode))

    $after = @(Get-DesktopSnapshot)
    $changed = @(Compare-Object -ReferenceObject @($before.Files) -DifferenceObject $after)
    Add-Check 'explorer: not one of the user''s desktop files changed' ($changed.Count -eq 0) ("{0} difference(s)" -f $changed.Count)

    # ---- and back again, through the page
    Write-Host ''
    Write-Host '-- the desktop is given back after the restart'
    $pick = Select-Mode 0
    Add-Check 'explorer: the desktop can still be given back' $pick.Moved ("document says {0}" -f (Get-RememberedMode))
    $back = Wait-Until { -not (Get-DesktopIconState).NoIcons } 25 'the desktop to come back'
    Add-Check 'explorer: the icons are visible again' $back (Format-Flags (Get-DesktopIconState).Flags)
    Add-Check 'explorer: the desktop flags are exactly what they were found with' ((Get-DesktopIconState).Flags -eq $before.Icons.Flags) ("before {0} / now {1}" -f (Format-Flags $before.Icons.Flags), (Format-Flags (Get-DesktopIconState).Flags))

    Close-App
}

# The tray is the one piece of the app the user can always reach, so the commands that give the native
# desktop back live there. This stage opens the menu the app draws for itself and uses it.
function Invoke-TrayStage {
    Write-Host ''
    Write-Host '=== the tray''s own desktop commands ==='

    $before = @{
        Icons = Get-DesktopIconState
        Files = Get-DesktopSnapshot
        Given = Get-LogCountAll $logGivenBack
    }

    Backup-Settings
    Disable-CloseToTray
    Park-LayoutFiles
    Park-Marker

    $app = Start-AppInstance
    $script:process = $app.Process
    $script:window = $app.Window
    Add-Check 'tray: the app came up' $app.CameUp ("launch {0} ms, window {1}" -f $app.LaunchMs, $app.Window)
    if (-not $app.CameUp) { return }

    Wait-Until { $null -ne (Get-AppRoot) } 15 'the app window to answer' | Out-Null
    Open-DynamicPage
    $script:modeIndex = 0

    $tray = [P3Tray]::TrayWindowOf([int]$script:process.Id)
    Add-Check 'tray: the icon is on the tray' ($tray -ne [IntPtr]::Zero) ("tray window {0}" -f $tray)
    if ($tray -eq [IntPtr]::Zero) { Close-App; return }

    $labels = Get-TrayMenuLabels
    Add-Check 'tray: the labels were read from the app''s own resources' ($labels.Count -gt 0) ("{0} label(s)" -f $labels.Count)

    # ---- the menu while the desktop is the user's
    $pick = Select-Mode 2
    Add-Check 'tray: the desktop was taken over first' $pick.Moved ("document says {0}" -f (Get-RememberedMode))
    $hidden = Wait-Until { (Get-DesktopIconState).NoIcons } 25 'the desktop icons to be hidden'
    Add-Check 'tray: the icons are hidden while the tray is used' $hidden (Format-Flags (Get-DesktopIconState).Flags)

    Write-Host ''
    Write-Host '-- the menu the app draws for itself'
    $menu = Open-TrayMenu $tray ([int]$script:process.Id)
    Add-Check 'tray: the menu opens' ($menu -ne [IntPtr]::Zero) ("menu window {0}" -f $menu)
    if ($menu -eq [IntPtr]::Zero) { Close-App; return }

    $names = Get-MenuItemNames $menu
    Add-Check 'tray: the menu offers the desktop commands' `
        (($names -contains $labels['Tray_TurnOffDesktop']) -and ($names -contains $labels['Tray_RestoreDesktop']) -and ($names -contains $labels['Tray_Exit'])) `
        ("{0}" -f ($names -join ' / '))
    if ($names.Count -eq 0) { Show-MenuDump $menu }

    # ---- the restore command
    Write-Host ''
    Write-Host '-- the tray''s restore command'
    $chosen = Invoke-MenuItem $menu $labels['Tray_RestoreDesktop']
    Add-Check 'tray: the restore command was chosen' $chosen $labels['Tray_RestoreDesktop']
    Close-TrayMenu $menu $tray

    $back = Wait-Until { -not (Get-DesktopIconState).NoIcons } 25 'the desktop to come back'
    Add-Check 'tray: the tray puts the native desktop back' $back (Format-Flags (Get-DesktopIconState).Flags)
    $cleared = Wait-Until { -not (Test-Path $markerPath) } 20 'the marker to be cleared'
    Add-Check 'tray: no marker is left behind' $cleared ("marker present: {0}" -f (Test-Path $markerPath))
    $mode = Wait-LayoutMode 'Native' 25
    Add-Check 'tray: the desktop is native afterwards' $mode.Met ("document says {0}" -f $mode.Mode)
    Add-Check 'tray: the desktop flags are exactly what they were found with' ((Get-DesktopIconState).Flags -eq $before.Icons.Flags) ("before {0} / now {1}" -f (Format-Flags $before.Icons.Flags), (Format-Flags (Get-DesktopIconState).Flags))

    # ---- and the turn-off command, from a desktop that is Muralis's again
    Write-Host ''
    Write-Host '-- the tray''s turn-off command'
    Open-DynamicPage
    $script:modeIndex = 0
    $pick = Select-Mode 2
    Add-Check 'tray: the desktop was taken over again' $pick.Moved ("document says {0}" -f (Get-RememberedMode))
    $hidden = Wait-Until { (Get-DesktopIconState).NoIcons } 25 'the desktop icons to be hidden again'
    Add-Check 'tray: the icons are hidden again' $hidden (Format-Flags (Get-DesktopIconState).Flags)

    $menu = Open-TrayMenu $tray ([int]$script:process.Id)
    Add-Check 'tray: the menu opens again' ($menu -ne [IntPtr]::Zero) ("menu window {0}" -f $menu)
    if ($menu -ne [IntPtr]::Zero) {
        $chosen = Invoke-MenuItem $menu $labels['Tray_TurnOffDesktop']
        Add-Check 'tray: the turn-off command was chosen' $chosen $labels['Tray_TurnOffDesktop']
        Close-TrayMenu $menu $tray
        $back = Wait-Until { -not (Get-DesktopIconState).NoIcons } 25 'the desktop to come back'
        Add-Check 'tray: turning the takeover off gives the desktop back' $back (Format-Flags (Get-DesktopIconState).Flags)
        Add-Check 'tray: the desktop flags are the ones it was found with' ((Get-DesktopIconState).Flags -eq $before.Icons.Flags) ("before {0} / now {1}" -f (Format-Flags $before.Icons.Flags), (Format-Flags (Get-DesktopIconState).Flags))
        $mode = Wait-LayoutMode 'Native' 25
        Add-Check 'tray: turning the takeover off leaves the desktop native' $mode.Met ("document says {0}" -f $mode.Mode)
    }

    $after = @(Get-DesktopSnapshot)
    $changed = @(Compare-Object -ReferenceObject @($before.Files) -DifferenceObject $after)
    Add-Check 'tray: not one of the user''s desktop files changed' ($changed.Count -eq 0) ("{0} difference(s)" -f $changed.Count)

    Close-App
}

# ---------------------------------------------------------------- the takeover under load

# The moment a log line was written, on the same clock the harness reads. Several runs write to the same
# file, so only a line newer than the ask that started the step counts as this step's answer.
function Get-LogTimes([string]$pattern) {
    $times = New-Object System.Collections.ArrayList
    foreach ($line in @(Get-LogLines $pattern)) {
        if ($line.Length -lt 23) { continue }
        $stamp = [datetime]::MinValue
        if ([datetime]::TryParseExact($line.Substring(0, 23), 'yyyy-MM-dd HH:mm:ss.fff', $null,
                [System.Globalization.DateTimeStyles]::None, [ref]$stamp)) {
            [void]$times.Add($stamp)
        }
    }
    return @($times)
}

function Get-LatestLogTime([string]$pattern) {
    $times = @(Get-LogTimes $pattern)
    if ($times.Count -eq 0) { return $null }
    return ($times | Sort-Object | Select-Object -Last 1)
}

# The takeover at the sizes the canvas is meant to carry. Every size is a fresh run of the whole
# transaction, asked for through the page the way a user asks for it: the number reported is from the
# ask to the icons really gone, so it covers the canvas being built, the user's own desktop being read,
# the icons being hidden and the result being verified — not one step of it with a stopwatch around it.
#
# Nothing here is measured with the machine being kept busy: the desktop is left alone for the idle
# reading, and the sweep walks the grid once the windows in the way have been moved aside.
function Invoke-PerfStage {
    Write-Host ''
    Write-Host '=== the takeover under load: 10, 50 and 100 items ==='

    $before = @{
        Icons = Get-DesktopIconState
        Files = Get-DesktopSnapshot
    }

    # Parked once for the whole stage, not once per size: parking again inside the loop would put the
    # document this stage planted where the user's own is kept, and the way back would then hand the user
    # the run's own file.
    Backup-Settings
    Disable-CloseToTray
    Park-LayoutFiles
    Park-Marker

    foreach ($count in $PerfCounts) {
        Write-Host ''
        Write-Host ("--- {0} items on the desktop ---" -f $count)

        $items = New-GridFixtureItems $count
        Write-Layout $items 'Native'
        Write-Host ("  planted {0} item(s); the desktop starts {1}" -f @($items).Count, (Get-RememberedMode))

        $app = Start-AppInstance
        $script:process = $app.Process
        $script:window = $app.Window
        Add-Check ("perf {0}: the app came up" -f $count) $app.CameUp ("launch {0} ms" -f $app.LaunchMs)
        if (-not $app.CameUp) { return }
        Add-Sample ("perf{0}.launchMs" -f $count) $app.LaunchMs

        Wait-Until { $null -ne (Get-AppRoot) } 15 'the app window to answer' | Out-Null
        Add-Check ("perf {0}: the desktop starts native" -f $count) ((Get-RememberedMode) -eq 'Native') ("document says {0}" -f (Get-RememberedMode))

        $scansBefore = Get-LogCountAll $logScanned
        Open-DynamicPage

        # ---- the takeover, timed from the ask
        $asked = Get-Date
        $pick = Select-Mode 2
        $hidden = Wait-Until { (Get-DesktopIconState).NoIcons } 40 'the desktop icons to be hidden'
        $enableMs = [int]((Get-Date) - $asked).TotalMilliseconds
        Add-Check ("perf {0}: the page can ask for the takeover" -f $count) $pick.Moved ("document says {0}" -f (Get-RememberedMode))
        Add-Check ("perf {0}: the icons go when the takeover is asked for" -f $count) $hidden (Format-Flags (Get-DesktopIconState).Flags)
        Add-Sample ("perf{0}.enableMs" -f $count) $enableMs

        # The scan and the plan are reported by the app itself, and the clock on that line says how much
        # of the enable was reading the user's desktop rather than building the canvas and hiding icons.
        $planAt = Get-LatestLogTime $logScan
        $scanMs = $null
        if ($null -ne $planAt -and $planAt -ge $asked) { $scanMs = [int]($planAt - $asked).TotalMilliseconds }
        Add-Sample ("perf{0}.scanAndPlanMs" -f $count) $scanMs
        $scans = (Get-LogCountAll $logScanned) - $scansBefore
        Add-Sample ("perf{0}.scans" -f $count) $scans
        Add-Check ("perf {0}: the desktop was read once for the takeover" -f $count) ($scans -ge 1) ("{0} read(s) of the desktop" -f $scans)

        $marker = Get-Marker
        Add-Check ("perf {0}: a crash during it would be recoverable" -f $count) ($null -ne $marker -and $marker.TakeoverWasActive) ("marker present: {0}" -f ($null -ne $marker))

        # ---- what the canvas is holding. The panel only has something to say once a canvas is mounted,
        # so it is opened here rather than while the desktop was still native.
        Open-DiagnosticsPanel
        $loaded = Wait-Diag { param($s) $s.ItemCount -eq $count } 60 ("{0} items on the canvas" -f $count)
        $iconItems = @($items | Where-Object { $_.Target.kind -ne 'url' }).Count
        $icons = Wait-Diag { param($s) $null -ne $s.IconEntries -and $s.IconEntries -ge $iconItems } 120 'the icons to be cached'
        Add-Check ("perf {0}: every planted item is on the canvas" -f $count) ($null -ne $loaded) ("items {0}" -f $(if ($null -ne $loaded) { $loaded.ItemCount } else { 'n/a' }))
        Add-Check ("perf {0}: the icons were resolved from the shell" -f $count) ($null -ne $icons) ("{0} cached of {1} with a file behind them" -f $(if ($null -ne $icons) { $icons.IconEntries } else { 0 }), $iconItems)
        Add-Sample ("perf{0}.iconItems" -f $count) $iconItems
        Add-Sample ("perf{0}.iconEntries" -f $count) $(if ($null -ne $icons) { $icons.IconEntries } else { 0 })
        Add-Sample ("perf{0}.iconCacheMb" -f $count) $(if ($null -ne $icons) { $icons.IconMb } else { 0 })

        $processId = [int]$script:process.Id
        $handles = (Get-Process -Id $processId).HandleCount
        $gdi = [P3Win]::GdiObjectsOf($processId)
        $user = [P3Win]::UserObjectsOf($processId)
        $working = [math]::Round((Get-Process -Id $processId).WorkingSet64 / 1MB, 1)
        Add-Sample ("perf{0}.handles" -f $count) $handles
        Add-Sample ("perf{0}.gdiObjects" -f $count) $gdi
        Add-Sample ("perf{0}.userObjects" -f $count) $user
        Add-Sample ("perf{0}.workingSetMb" -f $count) $working

        # ---- idle: the desktop owned, nothing touching it, and the log watched for a heartbeat
        Minimize-AppWindow
        $idleLinesBefore = (Get-LogCountAll $logScan) + (Get-LogCountAll $logLayoutLoaded)
        $idleStart = Get-CpuMilliseconds $processId
        $idleWall = [System.Diagnostics.Stopwatch]::StartNew()
        Wait-ForSeconds 10
        $idleWall.Stop()
        $idleMs = (Get-CpuMilliseconds $processId) - $idleStart
        $idleLines = ((Get-LogCountAll $logScan) + (Get-LogCountAll $logLayoutLoaded)) - $idleLinesBefore
        Add-Sample ("perf{0}.idleCpuMs" -f $count) $idleMs
        Add-Sample ("perf{0}.idleCpuPercentOfOneCore" -f $count) ([math]::Round(($idleMs / $idleWall.Elapsed.TotalMilliseconds) * 100, 3))
        Write-Host ("  idle: {0} ms over {1:0.0} s, {2} line(s) of desktop work" -f $idleMs, $idleWall.Elapsed.TotalSeconds, $idleLines)
        Add-Check ("perf {0}: the owned desktop costs nothing while nothing touches it" -f $count) ($idleMs -lt 500) ("{0} ms over {1:0.0} s" -f $idleMs, $idleWall.Elapsed.TotalSeconds)
        Add-Check ("perf {0}: nothing polls the desktop while it is idle" -f $count) ($idleLines -eq 0) ("{0} desktop line(s) in 10 s of idling" -f $idleLines)

        # ---- the pointer over the grid
        [void](Read-Geometry)
        $centres = @()
        $gridX = @()
        $gridY = @()
        foreach ($item in $items) {
            $centre = Get-ItemCentre $item
            $centres += , $centre
            if ($gridX -notcontains $centre[0]) { $gridX += $centre[0] }
            if ($gridY -notcontains $centre[1]) { $gridY += $centre[1] }
        }

        $blocked = Clear-TheDesktop $gridX $gridY (" for {0} items" -f $count)
        Add-Check ("perf {0}: the whole grid is on the desktop, not under an application" -f $count) ($blocked.Count -eq 0) ("blocked {0}" -f ($blocked -join '; '))

        $cpus = Get-CpuMilliseconds $processId
        $gpu = 0
        $moves = 0
        $sweep = [System.Diagnostics.Stopwatch]::StartNew()
        $deadline = (Get-Date).AddSeconds(12)
        while ((Get-Date) -lt $deadline) {
            foreach ($centre in $centres) {
                Move-Pointer $centre[0] $centre[1]
                $moves++
                Start-Sleep -Milliseconds 20
                if ((Get-Date) -ge $deadline) { break }
            }
            $sample = Measure-GpuOnce $processId
            if ($null -ne $sample -and $sample -gt $gpu) { $gpu = $sample }
        }
        $sweep.Stop()
        $sweepMs = (Get-CpuMilliseconds $processId) - $cpus
        $sweepPercent = [math]::Round(($sweepMs / $sweep.Elapsed.TotalMilliseconds) * 100, 3)
        Add-Sample ("perf{0}.sweepMoves" -f $count) $moves
        Add-Sample ("perf{0}.sweepCpuMs" -f $count) $sweepMs
        Add-Sample ("perf{0}.sweepCpuPercentOfOneCore" -f $count) $sweepPercent
        Add-Sample ("perf{0}.sweepGpuPercent" -f $count) $gpu
        Add-Check ("perf {0}: hovering stays inside one core" -f $count) ($sweepPercent -lt 100) ("{0} % of one core over {1:0.0} s" -f $sweepPercent, $sweep.Elapsed.TotalSeconds)

        # The number above only describes hovering if the sweep really hovered: land on one item and read
        # the canvas' own answer.
        Move-And-Settle $centres[0][0] $centres[0][1] 400
        $swept = Read-StateAt $centres[0][0] $centres[0][1]
        Add-Check ("perf {0}: the sweep really reached the items" -f $count) ($swept.HoverId -eq $items[0].Id) ("hovered {0}" -f $swept.HoverId)

        $handlesAfter = (Get-Process -Id $processId).HandleCount
        Add-Sample ("perf{0}.handlesAfterSweep" -f $count) $handlesAfter
        Add-Check ("perf {0}: the sweep does not grow the handle count without bound" -f $count) (($handlesAfter - $handles) -lt 200) ("{0} -> {1}" -f $handles, $handlesAfter)

        # ---- the give-back, timed from the ask
        $asked = Get-Date
        $pick = Select-Mode 0
        $back = Wait-Until { -not (Get-DesktopIconState).NoIcons } 40 'the desktop to come back'
        $disableMs = [int]((Get-Date) - $asked).TotalMilliseconds
        Add-Check ("perf {0}: the page can ask for the desktop back" -f $count) $pick.Moved ("document says {0}" -f (Get-RememberedMode))
        Add-Check ("perf {0}: the icons come back when the desktop is asked for" -f $count) $back (Format-Flags (Get-DesktopIconState).Flags)
        Add-Check ("perf {0}: the desktop is exactly as it was found" -f $count) ((Get-DesktopIconState).Flags -eq $before.Icons.Flags) ("before {0} / now {1}" -f (Format-Flags $before.Icons.Flags), (Format-Flags (Get-DesktopIconState).Flags))
        Add-Sample ("perf{0}.disableMs" -f $count) $disableMs

        $cleared = Wait-Until { -not (Test-Path $markerPath) } 20 'the marker to be cleared'
        Add-Check ("perf {0}: no marker is left behind" -f $count) $cleared ("marker present: {0}" -f (Test-Path $markerPath))
        $canvasGone = Wait-Until { ([P3Win]::ClassesWithPrefix($canvasWindowClass)).Count -eq 0 } 20 'the canvas to come off the desktop'
        Add-Check ("perf {0}: the canvas comes off with the mode" -f $count) $canvasGone ("{0} canvas window(s) left" -f ([P3Win]::ClassesWithPrefix($canvasWindowClass)).Count)

        $after = @(Get-DesktopSnapshot)
        $changed = @(Compare-Object -ReferenceObject @($before.Files) -DifferenceObject $after)
        Add-Check ("perf {0}: not one of the user's desktop files changed" -f $count) ($changed.Count -eq 0) ("{0} difference(s)" -f $changed.Count)

        # The dialog the page asks on the way to the takeover is the user's own confirmation, and it is
        # part of what the user waits for; it is recorded so the numbers are read with it in mind.
        Add-Sample ("perf{0}.enableBreakdown" -f $count) ("enable {0} ms (scan and plan {1} ms), disable {2} ms" -f $enableMs, $scanMs, $disableMs)
        Write-Host ("  {0}: enable {1} ms (scan and plan {2} ms), disable {3} ms" -f $count, $enableMs, $scanMs, $disableMs)
        Write-Host ("  {0} icons · {1} MB cache · handles {2} (GDI {3}, USER {4}) · working set {5} MB" -f $(if ($null -ne $icons) { $icons.IconEntries } else { 0 }), $(if ($null -ne $icons) { $icons.IconMb } else { 0 }), $handles, $gdi, $user, $working)
        Write-Host ("  idle {0} ms / 10 s · sweep {1} ms over {2:0.0} s ({3} % of one core) · GPU {4} % · {5} moves" -f $idleMs, $sweepMs, $sweep.Elapsed.TotalSeconds, $sweepPercent, $gpu, $moves)

        Close-App
    }

    # What the whole stage cost the user's desktop: nothing, at any size.
    $after = @(Get-DesktopSnapshot)
    $changed = @(Compare-Object -ReferenceObject @($before.Files) -DifferenceObject $after)
    Add-Check 'perf: the user''s desktop is untouched at every size' ($changed.Count -eq 0) ("{0} difference(s)" -f $changed.Count)
}

# ---------------------------------------------------------------- run

function Disable-CloseToTray {
    # Closing the window has to end the process: the harness waits for the exit to prove the give-back
    # ran, and close-to-tray would swallow it. Nothing else in the file is touched.
    $settings = Get-Content $script:settingsPath -Raw | ConvertFrom-Json
    $settings.CloseToTray = $false
    $settings | ConvertTo-Json -Depth 10 | Set-Content -Path $script:settingsPath -Encoding UTF8
}

# Once per run, like the settings and the layout document: a marker left by a crashed run is the thing
# being set aside here, and a second park could only overwrite that one with a marker this run wrote.
function Park-Marker {
    if ($script:markerParked) { return }
    $script:markerParked = $true
    if (Test-Path $markerPath) { Move-Item -Force $markerPath $markerBackup }
}

function Restore-Marker {
    if (Test-Path $markerBackup) { Move-Item -Force $markerBackup $markerPath }
}

$baselineIcons = $null
$stageFinished = $false
try {
    $baselineIcons = Get-DesktopIconState
    switch ($Stage) {
        'probe'    { Invoke-ProbeStage }
        'ui'       { Invoke-UiStage }
        'recover'  { Invoke-RecoveryStage }
        'explorer' { Invoke-ExplorerStage }
        'tray'     { Invoke-TrayStage }
        'perf'     { Invoke-PerfStage }
        'full'     { Invoke-UiStage; Invoke-RecoveryStage; Invoke-ExplorerStage; Invoke-TrayStage }
    }
    $stageFinished = $true
} finally {
    Write-Host ''
    Write-Host 'Putting the machine back the way it was found ...'
    Close-App
    Restore-DesktopFlags $baselineIcons
    Restore-Marker
    Restore-Everything

    # A stage that threw part way through has fewer checks than it should, which would read as a pass.
    Add-Check 'harness: the stage ran to its end' $stageFinished ("stage '{0}'" -f $Stage)
    $failed = Write-CheckReport $Stage $outPath 'p3d-takeover-verify.json'

    # Only when nothing is propagating: an unhandled error already ends the run non-zero, and exit
    # inside finally would swallow it.
    if ($stageFinished -and $failed -gt 0) { exit 1 }
}
