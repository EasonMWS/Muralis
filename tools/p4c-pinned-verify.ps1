<#
.SYNOPSIS
    Phase 4C live verification: the dock's pinned applications, driven like a user.

.DESCRIPTION
    The harness drives the running application through its own controls - the Add slot at the end of
    the pinned zone, the shell's own file picker, a click on a pin, a drag along the zone and the pin's
    own context menu - and judges the result from four places that do not depend on the app describing
    itself:

      * the dock window's UI Automation tree, which is what is really on screen and in what order
      * %LOCALAPPDATA%\Muralis\settings.json, which is what the next launch will read
      * the fixture files on disk, which prove that unpinning never touches what a pin points at
      * the log, which records what the app decided (pinned, refused as a duplicate, moved, started)

    The checks it makes:

      probe
      - the machine, the desktop view, and the fixture workspace: a copy of a real program and a real
        shortcut next to it, and that launching the copy is visible as a process

      dump
      - the dock window's UIA tree, printed, with the pinned zone, the Shelf and the utilities, so the
        selectors the later stages lean on are shown rather than assumed

      pins
      - adding a program and a shortcut through the Add slot and the shell's picker, and both being
        pinned: the shortcut is an application of its own because it points at a second copy
      - the same program being refused as a duplicate, and the program the shortcut points at being
        refused as a duplicate of the shortcut, with the user-facing line produced for each, and
        neither the order nor the file changing
      - both pins really starting what they point at, from a single click, told apart by the file the
        started process came from
      - the log recording a pin, a refusal and a start

      restore
      - the saved order coming back in the same order after a restart
      - a pointer drag along the zone reordering the saved list once, on release, and the new order
        surviving another restart
      - the drag not launching anything

      missing
      - a pin whose target was deleted still loading, still listed, with the dock up and the rest of
        the zone intact
      - clicking it refusing in the log, starting nothing
      - unpinning it from the pin's own context menu, and the file a real pin points at surviving an
        unpin of that pin untouched

      clean
      - Clean Desktop with the dock switched off by the user: the dock is forced up, the pins are
        there and still launch, and Explorer's icons are hidden while they are
      - the Shelf and the utilities still on the dock while all of that is true

      perf
      - the zone at its design limit: twelve pins restored, with the times from launch to the main
        window, to the dock window and to every pin in the zone
      - the app's processor time while idle with the zone full
      - the app's processor time across a burst of drags along the zone, per pointer movement, and the
        moves logged for them: the hot path follows the hand, and the file is written once per release

    Everything the harness changes is restored on the way out: settings.json, the desktop documents,
    the desktop's own icon flags and the recovery marker. The fixture workspace under %TEMP% is the
    harness' own and is removed at the end.

.PARAMETER Stage
    probe   - no app launch: machine state, the desktop view, and the fixture workspace
    dump    - two pins planted, the app launched, the dock's UIA tree printed
    pins    - add a program, add a shortcut, refuse a duplicate, launch both, exit
    restore - the saved order, a real drag, the committed order, and a restart
    missing - a pin whose target is gone: load, click, refuse, unpin
    clean   - Clean Desktop with the dock switched off: forced up, pins working, icons hidden
    perf    - twelve pins, the startup times, the idle cost and the cost of a drag
    full    - pins, then restore, then missing, then clean

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/p4c-pinned-verify.ps1 -Stage probe
    powershell -ExecutionPolicy Bypass -File tools/p4c-pinned-verify.ps1 -Stage dump
    powershell -ExecutionPolicy Bypass -File tools/p4c-pinned-verify.ps1 -Stage full
#>
[CmdletBinding()]
param(
    [ValidateSet('probe', 'dump', 'pins', 'restore', 'missing', 'clean', 'perf', 'full')] [string]$Stage = 'probe',
    [string]$Exe = 'src/Muralis.App/bin/Debug/net10.0-windows10.0.26100.0/win-x64/Muralis.exe',
    [string]$OutDir = 'artifacts/p4c'
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'p3-common.ps1')
. (Join-Path $PSScriptRoot 'p3d-shell-interop.ps1')
Set-BackupPaths 'p4c'

$repoRoot = Split-Path $PSScriptRoot -Parent
$exePath = if ([System.IO.Path]::IsPathRooted($Exe)) { $Exe } else { Join-Path $repoRoot $Exe }
$outPath = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repoRoot $OutDir }

$mainWindowClass = 'WinUIDesktopWin32WindowClass'
$mutexName = 'Local\Muralis.SingleInstance'

# The dock is a window of its own, named by the app. Its title is how the harness tells it apart from
# the main window: both are the same window class of the same process.
$dockTitle = 'Muralis Dock'

# The names the dock gives its own zones in XAML. They are not localized, so they are the same strings
# in every language the app can be showing.
$pinnedZoneName = 'Pinned apps'
$shelfZoneName = 'Desktop Shelf'
$utilityZoneName = 'Utilities'

# The Clean Desktop recovery marker: written before Explorer's icons are hidden, removed once they are
# verifiably back. A run must not inherit one, and must not leave one behind.
$cleanMarkerPath = Join-Path $appData 'desktop\clean-desktop-state.json'
$cleanMarkerBackup = "$cleanMarkerPath.p4c.bak"

# The user's own desktop content, from both places Windows shows on the desktop. Unpinning must not
# touch any of it, and the Shelf reads it, so it is snapshotted around the runs that touch the Shelf.
$desktopRoots = @(
    (Join-Path $env:USERPROFILE 'Desktop'),
    (Join-Path $env:PUBLIC 'Desktop')
)

# The fixture workspace. A copy of a real program rather than the program itself: the harness starts it,
# and it must never be the user's own copy of anything.
#
# The copy has to be a program that really runs from where it is put. Notepad cannot be one: the copy
# Windows ships in System32 is a stub that hands over to the packaged Notepad, so starting it leaves no
# process of its own and a launch check would read as a failure of the app. cmd.exe is a plain
# executable, runs from anywhere, and carries version information, so a pin of it is named by that
# rather than by the file name the harness gave the copy.
#
# Two copies, and the shortcut points at the second one. That is what makes the phase's two separate
# claims testable in one run: a shortcut can be pinned in its own right, because what it resolves to is
# a different application from the first copy; and a shortcut and the program it points at are the same
# application, because pinning the second copy after the shortcut is a duplicate of it.
$fixtureRoot = Join-Path $env:TEMP 'muralis-p4c'
$fixtureExeName = 'muralis-fixture-shell.exe'
$fixtureAltExeName = 'muralis-fixture-alt.exe'
$fixtureLnkName = 'muralis-shortcut.lnk'
$fixtureExe = Join-Path $fixtureRoot $fixtureExeName
$fixtureAltExe = Join-Path $fixtureRoot $fixtureAltExeName
$fixtureLnk = Join-Path $fixtureRoot $fixtureLnkName

# Everything the harness may start, by the path it was started from. The user's own command processor
# is never among them.
$fixturePrograms = @($fixtureExe, $fixtureAltExe)

# A pin whose target is planted in the settings file and then never created: the pin has to survive it.
$missingName = 'muralis-gone'
$missingTarget = Join-Path $fixtureRoot 'gone\muralis-gone.exe'

# Log lines this harness judges by. Counts are read as growth from a baseline taken before the launch,
# because the log is one file per day and holds every run.
$logRestored = 'The dock restored'
$logPinned = 'was pinned from'
$logNotPinned = 'was not pinned again'
$logUnpinned = 'was unpinned; the file it points at was not touched'
$logMoved = 'was moved to position'
$logStarted = 'The pinned application'
$logGone = 'which is gone'

$script:process = $null
$script:window = [IntPtr]::Zero
$script:markerParked = $false

# ---------------------------------------------------------------- right button input

# p3-common's pointer helpers cover the left button only, and the pinned zone's context menu is opened
# the way the user opens it: with a real right click through the input stack. The class is the same
# shape as the one p3-common declares, with the right-button flags added.
if (-not ('P4Input' -as [type])) {
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class P4Input {
  [StructLayout(LayoutKind.Sequential)]
  public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

  [StructLayout(LayoutKind.Sequential)]
  public struct INPUT { public uint type; public MOUSEINPUT mi; }

  [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, INPUT[] inputs, int size);

  public const uint INPUT_MOUSE = 0;
  public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
  public const uint MOUSEEVENTF_RIGHTUP = 0x0010;

  private static void Send(uint flags) {
    INPUT[] inputs = new INPUT[1];
    inputs[0].type = INPUT_MOUSE;
    inputs[0].mi.dwFlags = flags;
    uint sent = SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
    if (sent != 1) throw new InvalidOperationException("SendInput failed (" + Marshal.GetLastWin32Error() + ")");
  }

  public static void RightClick() { Send(MOUSEEVENTF_RIGHTDOWN); Send(MOUSEEVENTF_RIGHTUP); }
}
"@
}

# ---------------------------------------------------------------- the app's own strings

# A label the app is really showing, read from the app's own resources rather than guessed at. Both
# language files are read, so a check that matches a menu item works whether the app is showing the
# neutral or the Chinese strings.
function Get-LabelCandidates([string]$key) {
    $values = @()
    foreach ($file in @('src/Muralis.App/Strings/Resources.resx', 'src/Muralis.App/Strings/Resources.zh-CN.resx')) {
        $path = Join-Path $repoRoot $file
        if (-not (Test-Path $path)) { continue }
        try {
            [xml]$xml = Get-Content $path -Raw -Encoding UTF8
            foreach ($entry in $xml.root.data) {
                if ("$($entry.name)" -eq $key) { $values += "$($entry.value)" }
            }
        } catch { }
    }
    return @($values | Where-Object { $_ } | Select-Object -Unique)
}

# ---------------------------------------------------------------- the fixture workspace

function New-FixtureWorkspace {
    New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null

    $program = Join-Path $env:SystemRoot 'System32\cmd.exe'
    if (-not (Test-Path $program)) { throw "cmd.exe was not found at $program" }
    Copy-Item -Force $program $fixtureExe
    Copy-Item -Force $program $fixtureAltExe

    $shell = New-Object -ComObject WScript.Shell
    try {
        $link = $shell.CreateShortcut($fixtureLnk)
        $link.TargetPath = $fixtureAltExe
        $link.WorkingDirectory = $fixtureRoot
        $link.Description = 'Muralis Phase 4C verification fixture'
        $link.Save()
    } finally {
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }
}

function Remove-FixtureWorkspace {
    if (Test-Path $fixtureRoot) { Remove-Item -Recurse -Force $fixtureRoot -ErrorAction SilentlyContinue }
}

# The fixture programs running now, told apart from anything else by the file they were started from:
# the copies the harness made, never the user's own command processor.
function Get-FixtureProcessIds {
    $found = @()
    foreach ($path in $fixturePrograms) {
        $name = [System.IO.Path]::GetFileNameWithoutExtension($path)
        foreach ($process in (Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            try {
                if ($process.Path -eq $path) { $found += $process.Id }
            } catch { }
        }
    }

    # Returned as it stands: every caller wraps this in @() itself, and wrapping it here as well would
    # nest one array inside another, which reads downstream as an id that is not a number.
    return $found
}

# Where a started process was started from. The two fixture copies are the same program, so the file it
# came from is the only thing that says which pin was clicked.
function Get-StartedPaths([int[]]$Ids) {
    $paths = @()
    foreach ($id in $Ids) {
        try { $paths += (Get-Process -Id $id -ErrorAction Stop).Path } catch { }
    }
    return $paths
}

$script:fixtureBaseline = @()

function Wait-NewFixture([int]$timeoutSeconds = 20) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $new = @(Get-FixtureProcessIds | Where-Object { $script:fixtureBaseline -notcontains $_ })
        if ($new.Count -gt 0) { return $new }
        Start-Sleep -Milliseconds 300
    }
    return @()
}

function Close-FixtureProcesses([int[]]$ids) {
    foreach ($id in $ids) {
        try { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue } catch { }
    }
}

# What a pin points at, as the file system sees it, so an unpin can be shown not to have touched it.
function Get-FileFingerprint([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    $item = Get-Item -LiteralPath $path
    return ("{0}|{1}|{2}" -f $item.Length, $item.LastWriteTimeUtc.ToString('o'), $item.Name)
}

# ---------------------------------------------------------------- settings

function Read-Settings {
    for ($attempt = 0; $attempt -lt 6; $attempt++) {
        try { return (Get-Content $script:settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json) }
        catch { Start-Sleep -Milliseconds 150 }
    }
    throw 'settings.json could not be read.'
}

function Save-Settings($settings) {
    $settings | ConvertTo-Json -Depth 12 | Set-Content -Path $script:settingsPath -Encoding UTF8
}

function Get-SavedPins {
    $settings = Read-Settings
    if ($null -eq $settings.Dock -or $null -eq $settings.Dock.PinnedApps) { return ,@() }

    # The comma: PowerShell unrolls a one-element array on the way out of a function, and a single pin
    # arriving as a bare object has no Count, which would make every count read as nothing at all.
    return ,@($settings.Dock.PinnedApps)
}

function Get-SavedOrder {
    $order = @((Get-SavedPins) | ForEach-Object { "$($_.Id)" })
    return ,@($order)
}

# One pinned application as the settings file holds it. The identity is what the dock compares, and it
# is written the way the app writes it: the shortcut's target where a shortcut resolves to one.
function New-PinSettings {
    param(
        [string]$Id,
        [string]$Name,
        [string]$Target,
        [string]$Kind = 'Application',
        [string]$Identity = '',
        [string]$Arguments = '',
        [string]$WorkingDirectory = ''
    )

    if (-not $Identity) { $Identity = $Target.ToUpperInvariant() }
    return [pscustomobject]@{
        Id               = $Id
        DisplayName      = $Name
        LaunchTarget     = $Target
        IconIdentity     = $Target
        Kind             = $Kind
        Identity         = $Identity
        Arguments        = $(if ($Arguments) { $Arguments } else { $null })
        WorkingDirectory = $(if ($WorkingDirectory) { $WorkingDirectory } else { $null })
    }
}

# Plants the dock section and the mode the stage needs. SchemaVersion is forced to the version this
# build writes, because a lower one makes the app take its migration path instead of reading the mode
# this harness planted.
function Write-DockSettings {
    param(
        [array]$Pins = @(),
        [bool]$DockVisible = $true,
        [string]$Mode = 'Native'
    )

    $settings = Read-Settings
    $settings.SchemaVersion = 2
    $dock = [pscustomobject]@{ IsVisible = $DockVisible; PinnedApps = @($Pins) }
    if ($null -ne $settings.PSObject.Properties['Dock']) { $settings.Dock = $dock }
    else { $settings | Add-Member -NotePropertyName Dock -NotePropertyValue $dock }

    $experience = [pscustomobject]@{ Mode = $Mode }
    if ($null -ne $settings.PSObject.Properties['DesktopExperience']) { $settings.DesktopExperience = $experience }
    else { $settings | Add-Member -NotePropertyName DesktopExperience -NotePropertyValue $experience }

    Save-Settings $settings
}

function Park-Marker {
    if ($script:markerParked) { return }
    $script:markerParked = $true
    if (Test-Path $cleanMarkerPath) { Move-Item -Force $cleanMarkerPath $cleanMarkerBackup }
}

function Restore-Marker {
    if (Test-Path $cleanMarkerBackup) { Move-Item -Force $cleanMarkerBackup $cleanMarkerPath }
}

# ---------------------------------------------------------------- the dock window and its tree

function Get-DockWindow {
    if ($null -eq $script:process) { return [IntPtr]::Zero }
    foreach ($handle in [P3Win]::WindowsOfProcess([int]$script:process.Id)) {
        if ([P3Win]::TitleOf($handle) -eq $dockTitle) { return $handle }
    }
    return [IntPtr]::Zero
}

# The dock is prepared off the startup path, and Clean Desktop prepares it later still, so the wait has
# to be generous rather than a fixed pause.
function Wait-DockWindow([int]$timeoutSeconds = 45) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $handle = Get-DockWindow
        if ($handle -ne [IntPtr]::Zero -and [P3Win]::IsWindowVisible($handle)) { return $handle }
        Start-Sleep -Milliseconds 250
    }
    return [IntPtr]::Zero
}

function Get-DockRoot([IntPtr]$handle) {
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try { return [System.Windows.Automation.AutomationElement]::FromHandle($handle) }
        catch { Start-Sleep -Milliseconds 250 }
    }
    throw 'The dock window never became reachable through UI Automation.'
}

# A zone the dock named for itself: the pinned apps, the Shelf or the utilities.
function Get-ZoneElement($root, [string]$name) {
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        $found = Find-ByName $root $name $null
        if ($null -ne $found) { return $found }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

# An element's rectangle as four numbers, or null when UI Automation has none it can give: an element
# inside a window that is being drawn answers with a rectangle that is missing or not a number, and
# neither is a position to sort by or to click at.
function Get-RectNumbers($rect) {
    if ($null -eq $rect) { return $null }

    try {
        $left = [int]$rect.Left
        $top = [int]$rect.Top
        $width = [int]$rect.Width
        $height = [int]$rect.Height
    } catch {
        return $null
    }

    return [pscustomobject]@{ Left = $left; Top = $top; Width = $width; Height = $height }
}

# Every piece of text under one element, with where it is, ordered left to right: the dock's own order
# is the order of its zones, and a zone's order is the x of what is in it.
function Get-ZoneTexts($zone) {
    if ($null -eq $zone) { return ,@() }
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $all = $zone.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    $items = @()
    for ($i = 0; $i -lt $all.Count; $i++) {
        $element = $all.Item($i)
        try {
            $name = $element.Current.Name
            $rect = $element.Current.BoundingRectangle
        } catch { continue }
        if ($name -isnot [string] -or $name.Length -eq 0) { continue }

        # An element with no position is one the dock has not placed yet; it is skipped rather than
        # guessed at, and the waits that read the zone read it again until it has.
        $numbers = Get-RectNumbers $rect
        if ($null -eq $numbers) { continue }

        $items += [pscustomobject]@{
            Name   = $name
            Left   = $numbers.Left
            Top    = $numbers.Top
            Width  = $numbers.Width
            Height = $numbers.Height
            X      = [int][math]::Round($numbers.Left + ($numbers.Width / 2))
            Y      = [int][math]::Round($numbers.Top + ($numbers.Height / 2))
        }
    }
    return ,@($items | Sort-Object Left)
}

# The pinned labels, in dock order. Read fresh every time: the zone is redrawn from the saved list, so
# a label read before an add would answer about the dock as it was.
function Get-PinnedLabels([IntPtr]$dockHandle) {
    if ($dockHandle -eq [IntPtr]::Zero) { return ,@() }

    # Assigned before it is wrapped: @() around a call that hands back one array reads that array as a
    # single item and nests it, and a nested label is a list of x values where a number was expected.
    # The comma, so one pin still arrives as an array: a bare label object has no Count.
    $labels = Get-ZoneTexts (Get-ZoneElement (Get-DockRoot $dockHandle) $pinnedZoneName)
    return ,@($labels)
}

function Get-PinnedNames([IntPtr]$dockHandle) {
    return @((Get-PinnedLabels $dockHandle) | ForEach-Object { $_.Name })
}

function Wait-PinnedCount([IntPtr]$dockHandle, [int]$count, [int]$timeoutSeconds = 20) {
    $script:pinnedTarget = $count
    $script:pinnedHandle = $dockHandle
    return (Wait-Until { (Get-PinnedNames $script:pinnedHandle).Count -eq $script:pinnedTarget } $timeoutSeconds ("the pinned zone to hold {0} item(s)" -f $count))
}

function Wait-PinnedOrder([IntPtr]$dockHandle, [string[]]$expected, [int]$timeoutSeconds = 20) {
    $script:pinnedHandle = $dockHandle
    $script:pinnedExpected = $expected
    return (Wait-Until {
        $names = @(Get-PinnedNames $script:pinnedHandle)
        return ($names.Count -eq $script:pinnedExpected.Count -and (($names -join '|') -eq ($script:pinnedExpected -join '|')))
    } $timeoutSeconds ("the pinned zone to read {0}" -f ($expected -join ', ')))
}

function Find-ButtonByName($root, [string[]]$names) {
    $all = $root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)))
    for ($i = 0; $i -lt $all.Count; $i++) {
        $element = $all.Item($i)
        try { $name = $element.Current.Name } catch { continue }
        if ($names -contains $name) { return $element }
    }
    return $null
}

# The Add slot, looked up from the dock's own root every time rather than held on to. The zone is
# redrawn after every pin change, so an element read before an add is stale afterwards.
function Get-AddSlot([IntPtr]$dockHandle, [string[]]$names) {
    $root = Get-DockRoot $dockHandle
    if ($null -eq $root) { return $null }
    return Find-ButtonByName $root $names
}

# What the harness saw, printed when a dock check fails: a dock that is up but exposes nothing is a
# different problem from a dock that never came up.
function Show-DockTree($root, [int]$limit = 150) {
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    Write-Host ("  ({0} UIA element(s) under the dock)" -f $all.Count)
    for ($i = 0; $i -lt $all.Count -and $i -lt $limit; $i++) {
        $element = $all.Item($i)
        try {
            $type = $element.Current.ControlType.ProgrammaticName
            $name = $element.Current.Name
            $id = $element.Current.AutomationId
            $rect = $element.Current.BoundingRectangle
        } catch { continue }
        if (-not $name -and -not $id -and $type -notmatch 'Pane|Group|List|Custom|Image') { continue }
        $numbers = Get-RectNumbers $rect
        if ($null -eq $numbers) { continue }
        Write-Host ("    [{0,3}] {1,-30} id='{2}' name='{3}' rect={4},{5} {6}x{7}" -f `
            $i, $type, $id, $name, $numbers.Left, $numbers.Top, $numbers.Width, $numbers.Height)
    }
}

# ---------------------------------------------------------------- the shell's own picker

# The picker is the shell's own common dialog. Its controls are addressed by control id - 1148 is the
# file name box, 1 is the button that answers it - so neither step depends on focus, z-order or what is
# in the foreground. The windows that were already there are handed in, because the dialog appears after
# the click that asked for it and a window list taken now would already contain it.
function Select-InPicker([string]$Path, [string]$What, [long[]]$Seen, [int]$timeoutSeconds = 30) {
    $known = @{}
    foreach ($handle in $Seen) { $known[[long]$handle] = $true }

    $dialog = [IntPtr]::Zero
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        foreach ($handle in [P3Win]::WindowsOfProcess([int]$script:process.Id)) {
            if ($known.ContainsKey([long]$handle)) { continue }
            if (-not [P3Win]::IsWindowVisible($handle)) { continue }
            $dialog = $handle
            break
        }
        if ($dialog -ne [IntPtr]::Zero) { break }

        # Some builds put the common dialog in a window of another process: fall back to any new top
        # level dialog that is not one this run has already seen.
        foreach ($handle in [P3Win]::WindowsOfClass('#32770')) {
            if (-not [P3Win]::IsWindowVisible($handle)) { continue }
            if ($known.ContainsKey([long]$handle)) { continue }
            $dialog = $handle
            break
        }
        if ($dialog -ne [IntPtr]::Zero) { break }

        Start-Sleep -Milliseconds 200
    }

    if ($dialog -eq [IntPtr]::Zero) {
        throw ("The {0} picker never appeared." -f $What)
    }

    Write-Host ("  {0} picker: {1}" -f $What, [P3Win]::Describe($dialog))
    [P3Win]::SetForegroundWindow($dialog) | Out-Null
    Start-Sleep -Milliseconds 500

    $combo = [P3Win]::FindChild($dialog, 'ComboBox', [P3Win]::FileNameControlId)
    $edit = [P3Win]::FindChild($dialog, 'Edit', [P3Win]::FileNameControlId)
    $box = $edit
    if ($box -eq [IntPtr]::Zero) { $box = $combo }
    if ($box -eq [IntPtr]::Zero) { $box = [P3Win]::FindChild($dialog, 'Edit', -1) }
    if ($box -eq [IntPtr]::Zero) { throw ("The {0} picker has no file name box to answer." -f $What) }

    $button = [P3Win]::FindChild($dialog, 'Button', [P3Win]::DialogButtonId)
    if ($button -eq [IntPtr]::Zero) { throw ("The {0} picker has no button to answer it." -f $What) }

    # The path is typed into the box a character at a time: that is what a typist does to the box, so
    # the dialog's own idea of it changes and its own button wakens. Setting the text outright is only
    # the retreat for builds whose box ignores keystrokes.
    [P3Win]::SetText($box, '') | Out-Null
    [P3Win]::TypeInto($box, $Path)
    Start-Sleep -Milliseconds 600

    $said = [P3Win]::TitleOf($box)
    if (-not $said.Contains($Path)) {
        Write-Host ("  the box kept only '{0}'; setting the text instead" -f $said)
        [P3Win]::SetText($box, $Path) | Out-Null
        if ($combo -ne [IntPtr]::Zero -and $combo -ne $box) { [P3Win]::SetText($combo, $Path) | Out-Null }
        Start-Sleep -Milliseconds 500
    }

    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        if (-not [P3Win]::IsWindow($dialog) -or -not [P3Win]::IsWindowVisible($dialog)) { break }
        if ($attempt % 2 -eq 0) { [P3Win]::ClickButton($button) }
        else { [P3Win]::Send($dialog, [P3Win]::WM_COMMAND, [IntPtr][P3Win]::DialogButtonId, $button) | Out-Null }
        Start-Sleep -Milliseconds 800
    }

    $closed = Wait-Until { -not [P3Win]::IsWindow($dialog) -or -not [P3Win]::IsWindowVisible($dialog) } 20 'the picker to close'
    if (-not $closed) { throw ("The {0} picker would not close." -f $What) }
    Start-Sleep -Milliseconds 800
}

# ---------------------------------------------------------------- launching and closing

function Get-AppProcesses {
    return @(Get-Process -Name Muralis -ErrorAction SilentlyContinue)
}

# What is left running, with the id and the time each started: a count of one cannot say whether it is
# the run's own app still shutting down or an older instance the launch was handed to.
function Describe-AppProcesses {
    $described = @()
    foreach ($process in Get-AppProcesses) {
        $started = 'unknown'
        try { $started = $process.StartTime.ToString('HH:mm:ss.fff') } catch { }
        $described += ("{0} started {1}" -f $process.Id, $started)
    }
    return ($described -join ', ')
}

function Assert-MuralisNotRunning {
    if (Get-AppProcesses) { throw 'Muralis is already running; stop it first.' }
    if (-not (Test-Path $exePath)) { throw "Executable not found: $exePath" }
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

# The dock is a window of its own and carries the same window class as the main window, so the class
# alone no longer says which one is the app's own window: the dock is the one the app named, and the
# main window is the visible one of that class that is not it. Closing the wrong one would leave the app
# running with its window still open, which reads as an app that will not exit.
function Get-MainWindow([int]$processId) {
    foreach ($handle in [P3Win]::WindowsOfProcess($processId)) {
        if (-not [P3Win]::IsWindowVisible($handle)) { continue }
        if ([P3Win]::ClassOf($handle) -ne $mainWindowClass) { continue }
        if ([P3Win]::TitleOf($handle) -eq $dockTitle) { continue }
        return $handle
    }
    return [IntPtr]::Zero
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

function Get-LogLines([string]$pattern) {
    $day = (Get-Date).ToString('yyyyMMdd')
    $lines = @()
    foreach ($file in (Get-ChildItem (Join-Path $appData 'logs') -Filter ("muralis-{0}*.log" -f $day) -ErrorAction SilentlyContinue)) {
        for ($attempt = 0; $attempt -lt 5; $attempt++) {
            try {
                $stream = New-Object IO.FileStream($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
                try {
                    $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)
                    try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
                } finally { $stream.Dispose() }
                $lines += @($text -split "`r?`n" | Where-Object { $_ -match [regex]::Escape($pattern) })
                break
            } catch {
                Start-Sleep -Milliseconds 100
            }
        }
    }
    return $lines
}

function Wait-LogGrew([string]$pattern, [long]$base, [int]$timeoutSeconds, [string]$what) {
    $script:logPattern = $pattern
    $script:logBase = $base
    return (Wait-Until { (Get-LogCountAll $script:logPattern) -gt $script:logBase } $timeoutSeconds $what)
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

function Launch-App {
    # A second launch is handed to the instance already running, and every check after it would be about
    # that instance's window rather than the one this run started. The run has to own what it judges.
    Assert-MuralisNotRunning

    $app = Start-AppInstance
    $script:process = $app.Process
    $script:window = $app.Window
    if (-not $app.CameUp) { throw 'The Muralis main window never came up.' }
    return $app
}

function Close-App {
    if ($null -eq $script:process) { return }
    $window = Get-MainWindow ([int]$script:process.Id)
    if ($window -ne [IntPtr]::Zero -and [P3Win]::IsWindow($window)) {
        [P3Win]::PostMessage($window, [P3Win]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    }

    $closed = Wait-Until { $script:process.HasExited } 25 'the app to exit'
    if (-not $closed) {
        Write-Host ("  the app did not exit on its own; stopping it ({0})" -f (Describe-AppProcesses))
        Stop-Process -Id $script:process.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 800
    }

    # The process object can say it has exited a moment before the system is done with it, and the check
    # that follows asks the system. Reported rather than waited on for long: an app that will not go is a
    # finding, and the check after this one is where it is written down.
    [void](Wait-Until { -not (Get-AppProcesses) } 5 'the app to be gone from the process list')
    if (Get-AppProcesses) { Write-Host ("  still running: {0}" -f (Describe-AppProcesses)) }

    Start-Sleep -Milliseconds 600
    $script:process = $null
    $script:window = [IntPtr]::Zero
}

# ---------------------------------------------------------------- driving the zone

# A pin is clicked where its own label is: the label is inside the icon the dock handles the gesture on,
# and the label is the only part of a pin that UI Automation will name.
function Click-Pin([pscustomobject]$label) {
    Move-Pointer $label.X $label.Y
    Start-Sleep -Milliseconds 350
    Click-Pointer
    Start-Sleep -Milliseconds 400
}

# A press, a move and a release along the zone: the drag the dock's own reorder is written for. The
# pointer is carried one and a half slots past the label it started on, which is past the next slot's
# resting centre and so a different index.
function Drag-Pin([pscustomobject]$from, [pscustomobject]$to) {
    $pitch = $to.Left - $from.Left
    $targetX = [int]($from.Left + ($pitch * 1.5))
    Drag-Pointer $from.X $from.Y $targetX $to.Y 14
}

function Open-PinMenu([pscustomobject]$label) {
    Move-Pointer $label.X $label.Y
    Start-Sleep -Milliseconds 350
    [P4Input]::RightClick()
    Start-Sleep -Milliseconds 900
}

# The command the pin's own context menu is offering, looked for in the windows the right click opened:
# a flyout is a top level window of the app of its own, and the item is named by the app's own string.
function Find-FlyoutItem([long[]]$Seen, [string[]]$names, [int]$timeoutSeconds = 10) {
    $known = @{}
    foreach ($handle in $Seen) { $known[[long]$handle] = $true }
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        foreach ($handle in [P3Win]::WindowsOfProcess([int]$script:process.Id)) {
            if ($known.ContainsKey([long]$handle)) { continue }
            $root = $null
            try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($handle) } catch { continue }
            if ($null -eq $root) { continue }
            foreach ($name in $names) {
                $item = Find-ByName $root $name $null
                if ($null -ne $item) { return [pscustomobject]@{ Root = $root; Item = $item; Name = $name } }
            }
        }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

# Chooses the command by clicking the item's own rectangle, the way the user does: a flyout that is up
# and showing a command is dismissed by choosing it and by nothing else.
function Click-FlyoutItem($found) {
    if ($null -eq $found) { return $false }
    try {
        $rect = $found.Item.Current.BoundingRectangle
    } catch {
        return $false
    }

    $numbers = Get-RectNumbers $rect
    if ($null -eq $numbers -or $numbers.Width -le 0 -or $numbers.Height -le 0) { return $false }

    Move-Pointer ([int][math]::Round($numbers.Left + ($numbers.Width / 2))) ([int][math]::Round($numbers.Top + ($numbers.Height / 2)))
    Start-Sleep -Milliseconds 300
    Click-Pointer
    return $true
}

function Get-TopLevelHandles {
    if ($null -eq $script:process) { return @() }
    return @([P3Win]::WindowsOfProcess([int]$script:process.Id) | ForEach-Object { [long]$_ })
}

# ---------------------------------------------------------------- the desktop's own view

# What Explorer says about its own icons, read over COM rather than inferred from our windows. This is
# the only honest answer to whether Clean Desktop hid them.
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

# Puts the desktop's icon flag back the way this stage found it. A run that dies in the middle must not
# leave the user's icons hidden, and a run must never force them visible over the user's setting.
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

# Everything the user has on their desktop, as the file system sees it. Read only, and compared before
# and after: the Shelf reads these folders, and a run that renamed, moved or rewrote one would show up.
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

# ---------------------------------------------------------------- probe

function Invoke-ProbeStage {
    Write-Host 'Machine state'
    Write-Host ("  display:            {0}x{1}" -f [P3Win]::GetSystemMetrics(0), [P3Win]::GetSystemMetrics(1))
    Write-Host ("  executable:         {0}" -f $exePath)
    Write-Host ("  settings:           {0}" -f $script:settingsPath)
    Write-Host ("  clean marker:       {0}  {1}" -f $cleanMarkerPath, $(if (Test-Path $cleanMarkerPath) { 'PRESENT' } else { 'absent' }))
    Write-Host ("  single-instance:    {0}" -f $(if (Test-SingleInstanceFree) { 'free' } else { 'held' }))
    Write-Host ("  Muralis processes:  {0}" -f (Get-AppProcesses).Count)
    Write-Host ("  dock window title:  '{0}'" -f $dockTitle)
    Write-Host ("  fixture workspace:  {0}" -f $fixtureRoot)

    $settings = Read-Settings
    Write-Host ("  schema version:     {0}" -f $settings.SchemaVersion)
    Write-Host ("  dock section:       {0}" -f $(if ($null -ne $settings.Dock) { "IsVisible=$($settings.Dock.IsVisible), $((Get-SavedPins).Count) pin(s)" } else { 'absent' }))
    Write-Host ("  desktop experience: {0}" -f $(if ($null -ne $settings.DesktopExperience) { $settings.DesktopExperience.Mode } else { 'absent' }))

    Write-Host ''
    Write-Host 'The desktop as the shell sees it'
    $state = Get-DesktopIconState
    Write-Host ("  IFolderView2:       {0}" -f $(if ($state.Readable) { 'reached' } else { "not reachable ({0})" -f $state.Error }))
    Write-Host ("  folder flags:       {0}" -f (Format-Flags $state.Flags))
    Write-Host ("  icons hidden:       {0}" -f $state.NoIcons)
    Write-Host ("  desktop files:      {0} entries under {1} root(s)" -f (Get-DesktopSnapshot).Count, $desktopRoots.Count)

    Add-Check 'probe: the desktop view is reachable' $state.Readable ("flags {0}" -f (Format-Flags $state.Flags))
    Add-Check 'probe: the icons are not already hidden' (-not $state.NoIcons) ("FWF_NOICONS {0}" -f $state.NoIcons)
    Add-Check 'probe: no Clean Desktop marker was left behind' (-not (Test-Path $cleanMarkerPath)) $cleanMarkerPath

    Write-Host ''
    Write-Host 'The fixture workspace'
    $exeExists = Test-Path $fixtureExe
    $altExists = Test-Path $fixtureAltExe
    $lnkExists = Test-Path $fixtureLnk
    Write-Host ("  program:            {0}  {1} bytes" -f $fixtureExe, $(if ($exeExists) { (Get-Item $fixtureExe).Length } else { 0 }))
    Write-Host ("  second program:     {0}  {1} bytes" -f $fixtureAltExe, $(if ($altExists) { (Get-Item $fixtureAltExe).Length } else { 0 }))
    Write-Host ("  shortcut:           {0}" -f $fixtureLnk)
    Add-Check 'probe: the fixture program exists' $exeExists $fixtureExe
    Add-Check 'probe: the second fixture program exists' $altExists $fixtureAltExe
    Add-Check 'probe: the fixture shortcut exists' $lnkExists $fixtureLnk

    # Nothing may be launched from a unit test, but the probe's whole point is that starting this copy
    # is visible as a process: a launch check that cannot see its own fixture would read as a failure
    # of the app.
    $shell = New-Object -ComObject WScript.Shell
    try {
        $resolved = $shell.CreateShortcut($fixtureLnk).TargetPath
    } finally {
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }
    Write-Host ("  the shortcut points at: {0}" -f $resolved)
    Add-Check 'probe: the shortcut resolves to the second fixture program' ($resolved -eq $fixtureAltExe) $resolved

    $script:fixtureBaseline = @(Get-FixtureProcessIds)
    $started = Start-Process -FilePath $fixtureExe -PassThru
    $seen = Wait-NewFixture 15
    Write-Host ("  starting the copy produced {0} new process(es)" -f $seen.Count)
    Add-Check 'probe: starting the fixture is visible as a process' ($seen.Count -gt 0) ("{0} process(es)" -f $seen.Count)
    Close-FixtureProcesses $seen
    try { if (-not $started.HasExited) { Stop-Process -Id $started.Id -Force -ErrorAction SilentlyContinue } } catch { }
    Start-Sleep -Milliseconds 500

    Write-Host ''
    Write-Host 'The labels this run will match against'
    Write-Host ("  Add app:            {0}" -f ((Get-LabelCandidates 'Dock_Pinned_AddApp') -join ' | '))
    Write-Host ("  Unpin:              {0}" -f ((Get-LabelCandidates 'Dock_Pinned_Unpin') -join ' | '))

    Assert-MuralisNotRunning
}

# ---------------------------------------------------------------- dump

function Invoke-DumpStage {
    Write-Host '=== the dock window as UI Automation sees it ==='
    Backup-Settings
    Enable-CanvasInSettings
    Park-LayoutFiles

    # Two pins planted, a program and a shortcut beside it: two items are what makes the zone wide
    # enough to read, and what the drag stage needs.
    Write-DockSettings -Pins @(
        (New-PinSettings -Id 'fixture-exe' -Name 'fixture program' -Target $fixtureExe),
        (New-PinSettings -Id 'fixture-shortcut' -Name 'fixture shortcut' -Target $fixtureLnk -Kind 'Shortcut' -Identity $fixtureAltExe.ToUpperInvariant())
    ) -DockVisible $true -Mode 'Native'

    $before = @{
        Restored = Get-LogCountAll $logRestored
    }

    $app = Launch-App
    Write-Host ("  the app came up in {0} ms" -f $app.LaunchMs)

    $dockHandle = Wait-DockWindow 45
    Add-Check 'dump: the dock window came up' ($dockHandle -ne [IntPtr]::Zero) $(if ($dockHandle -ne [IntPtr]::Zero) { [P3Win]::Describe($dockHandle) } else { 'no window titled Muralis Dock' })
    if ($dockHandle -eq [IntPtr]::Zero) { return }

    Write-Host ("  dock window:        {0}" -f [P3Win]::Describe($dockHandle))
    Write-Host ("  dock rect:          {0}" -f (([P3Win]::RectOf($dockHandle)) -join ','))
    Write-Host ("  dock client:        {0}" -f (([P3Win]::ClientSizeOf($dockHandle)) -join 'x'))
    Write-Host '  top level windows of the app:'
    foreach ($handle in [P3Win]::WindowsOfProcess([int]$script:process.Id)) {
        Write-Host ("    {0} visible={1} rect={2}" -f [P3Win]::Describe($handle), [P3Win]::IsWindowVisible($handle), (([P3Win]::RectOf($handle)) -join ','))
    }

    $root = Get-DockRoot $dockHandle
    Show-DockTree $root

    foreach ($zone in @($pinnedZoneName, $shelfZoneName, $utilityZoneName)) {
        $element = Get-ZoneElement $root $zone
        $texts = Get-ZoneTexts $element
        Write-Host ("  zone '{0}': {1}" -f $zone, $(if ($null -eq $element) { 'NOT FOUND' } else { "{0} text(s): {1}" -f $texts.Count, (($texts | ForEach-Object { "{0}@{1}" -f $_.Name, $_.X }) -join ', ') }))
        Add-Check ("dump: the dock has a '{0}' zone" -f $zone) ($null -ne $element) $(if ($null -eq $element) { 'not found' } else { "{0} text(s)" -f $texts.Count })
    }

    $addNames = Get-LabelCandidates 'Dock_Pinned_AddApp'
    $addButton = Find-ButtonByName $root $addNames
    Add-Check 'dump: the Add slot is a button the run can press' ($null -ne $addButton) ($addNames -join ' | ')

    Add-Sample 'dock_rect' ([P3Win]::RectOf($dockHandle) -join ',')
    Add-Sample 'pinned_labels' ((Get-PinnedNames $dockHandle) -join ' | ')

    $grew = Wait-LogGrew $logRestored $before.Restored 20 'the dock to say what it restored'
    Add-Check 'dump: the dock reported what it restored' $grew (Get-LogLines $logRestored | Select-Object -Last 1)

    Close-App
    Add-Check 'dump: the app exited when the window was closed' (-not (Get-AppProcesses)) $(if (Get-AppProcesses) { Describe-AppProcesses } else { 'no process left' })
}

# ---------------------------------------------------------------- pins

function Invoke-PinsStage {
    Write-Host '=== 1. adding, refusing and launching ==='
    Backup-Settings
    Enable-CanvasInSettings
    Park-LayoutFiles
    Write-DockSettings -Pins @() -DockVisible $true -Mode 'Native'

    $fixtureBefore = Get-FileFingerprint $fixtureExe

    $before = @{
        Pinned    = Get-LogCountAll $logPinned
        NotPinned = Get-LogCountAll $logNotPinned
        Started   = Get-LogCountAll $logStarted
    }

    $app = Launch-App
    Write-Host ("  the app came up in {0} ms" -f $app.LaunchMs)

    $dockHandle = Wait-DockWindow 45
    Add-Check 'pins: the dock window came up' ($dockHandle -ne [IntPtr]::Zero) $(if ($dockHandle -ne [IntPtr]::Zero) { [P3Win]::Describe($dockHandle) } else { 'no window titled Muralis Dock' })
    if ($dockHandle -eq [IntPtr]::Zero) { return }

    Add-Check 'pins: the dock starts with an empty pinned zone' ((Get-PinnedNames $dockHandle).Count -eq 0) ("{0} label(s)" -f (Get-PinnedNames $dockHandle).Count)

    $addNames = Get-LabelCandidates 'Dock_Pinned_AddApp'
    Add-Check 'pins: the empty state offers an Add slot' ($null -ne (Get-AddSlot $dockHandle $addNames)) ($addNames -join ' | ')

    # ---- a program through the picker
    Write-Host ''
    Write-Host 'Adding a program through the Add slot ...'
    $seen = Get-TopLevelHandles
    Invoke-Element (Get-AddSlot $dockHandle $addNames) 'the Add slot'
    Start-Sleep -Milliseconds 1200
    Select-InPicker $fixtureExe 'program' $seen

    $grew = Wait-LogGrew $logPinned $before.Pinned 25 'the program to be pinned'
    Add-Check 'pins: the program was pinned' $grew (Get-LogLines $logPinned | Select-Object -Last 1)
    Add-Check 'pins: the pinned zone shows one item' (Wait-PinnedCount $dockHandle 1 20) ((Get-PinnedNames $dockHandle) -join ', ')

    $saved = Get-SavedPins
    Write-Host ("  saved: {0}" -f (($saved | ForEach-Object { "{0}/{1}/{2}" -f $_.DisplayName, $_.Kind, $_.LaunchTarget }) -join ' | '))
    Add-Check 'pins: the pin was written with the program it points at' `
        ($saved.Count -eq 1 -and "$($saved[0].LaunchTarget)" -eq $fixtureExe -and "$($saved[0].Kind)" -eq 'Application') `
        "$($saved.Count) pin(s); $($saved[0].Kind) -> $($saved[0].LaunchTarget)"
    $programLabel = (Get-PinnedNames $dockHandle) | Select-Object -First 1
    Add-Check 'pins: the pin is named from the program itself' ($programLabel -and $programLabel -ne $fixtureExeName) ("'$programLabel'")

    # ---- a shortcut, which is an application of its own because it resolves to the second copy
    Write-Host ''
    Write-Host 'Adding a shortcut, which points at the second copy ...'
    $seen = Get-TopLevelHandles
    $pinnedBefore = Get-LogCountAll $logPinned
    Invoke-Element (Get-AddSlot $dockHandle $addNames) 'the Add slot'
    Start-Sleep -Milliseconds 1200
    Select-InPicker $fixtureLnk 'shortcut' $seen

    $grew = Wait-LogGrew $logPinned $pinnedBefore 25 'the shortcut to be pinned'
    Add-Check 'pins: the shortcut was pinned' $grew (Get-LogLines $logPinned | Select-Object -Last 1)
    Add-Check 'pins: the pinned zone shows both items' (Wait-PinnedCount $dockHandle 2 20) ((Get-PinnedNames $dockHandle) -join ', ')

    $saved = Get-SavedPins
    Add-Check 'pins: the shortcut was written as a shortcut named by its own file' `
        ($saved.Count -eq 2 -and "$($saved[1].Kind)" -eq 'Shortcut' -and "$($saved[1].LaunchTarget)" -eq $fixtureLnk -and "$($saved[1].DisplayName)" -eq ([System.IO.Path]::GetFileNameWithoutExtension($fixtureLnkName))) `
        "$($saved.Count) pin(s); '$($saved[1].DisplayName)' $($saved[1].Kind) -> $($saved[1].LaunchTarget)"
    Add-Check 'pins: the shortcut was pinned as the application it resolves to, not as a file' `
        ("$($saved[1].Identity)" -eq $fixtureAltExe.ToUpperInvariant()) `
        "$($saved[1].Identity)"

    # ---- the same program again, by its path: a duplicate
    Write-Host ''
    Write-Host 'Adding the program again, which the dock has to refuse ...'
    $beforeNotPinned = Get-LogCountAll $logNotPinned
    $beforePinned = Get-LogCountAll $logPinned
    $seen = Get-TopLevelHandles
    Invoke-Element (Get-AddSlot $dockHandle $addNames) 'the Add slot'
    Start-Sleep -Milliseconds 1200
    Select-InPicker $fixtureExe 'program again' $seen

    $grew = Wait-LogGrew $logNotPinned $beforeNotPinned 25 'the duplicate to be refused'
    Add-Check 'pins: the repeated program was refused as already pinned' $grew (Get-LogLines $logNotPinned | Select-Object -Last 1)
    Add-Check 'pins: the refusal said Duplicate rather than read a second copy in' `
        (@(Get-LogLines $logNotPinned | Where-Object { $_ -match 'Duplicate' }).Count -gt 0) `
        (Get-LogLines $logNotPinned | Select-Object -Last 1)
    Add-Check 'pins: a duplicate was not pinned' ((Get-LogCountAll $logPinned) -eq $beforePinned) ("pinned lines {0} -> {1}" -f $beforePinned, (Get-LogCountAll $logPinned))
    Add-Check 'pins: the zone still holds exactly two items' ((Get-PinnedNames $dockHandle).Count -eq 2) ((Get-PinnedNames $dockHandle) -join ', ')
    Add-Check 'pins: the saved list still holds exactly two pins' ((Get-SavedPins).Count -eq 2) ("{0} pin(s)" -f (Get-SavedPins).Count)

    # ---- the program the shortcut points at: the same application as the shortcut, so also a duplicate
    Write-Host ''
    Write-Host 'Adding the program the shortcut points at, which is the same application ...'
    $beforeNotPinned = Get-LogCountAll $logNotPinned
    $beforePinned = Get-LogCountAll $logPinned
    $seen = Get-TopLevelHandles
    Invoke-Element (Get-AddSlot $dockHandle $addNames) 'the Add slot'
    Start-Sleep -Milliseconds 1200
    Select-InPicker $fixtureAltExe 'the shortcut''s own program' $seen

    $grew = Wait-LogGrew $logNotPinned $beforeNotPinned 25 'the program behind the shortcut to be refused'
    Add-Check 'pins: the program behind the shortcut was refused as already pinned' $grew (Get-LogLines $logNotPinned | Select-Object -Last 1)
    Add-Check 'pins: a shortcut and its own program were recognised as one application' `
        (($grew) -and ((Get-LogCountAll $logPinned) -eq $beforePinned)) `
        ("pinned lines {0} -> {1}" -f $beforePinned, (Get-LogCountAll $logPinned))
    Add-Check 'pins: the zone still holds exactly two items after it' ((Get-PinnedNames $dockHandle).Count -eq 2) ((Get-PinnedNames $dockHandle) -join ', ')

    # ---- launching from a single click, both kinds
    Write-Host ''
    Write-Host 'Launching the program with a click ...'
    $script:fixtureBaseline = @(Get-FixtureProcessIds)
    $labels = Get-PinnedLabels $dockHandle
    $started = Get-LogCountAll $logStarted
    Click-Pin $labels[0]
    $new = Wait-NewFixture 20
    Add-Check 'pins: a click on the program pin started it' ($new.Count -gt 0) ("{0} new process(es)" -f $new.Count)
    Add-Check 'pins: the app logged the start' (Wait-LogGrew $logStarted $started 15 'the start to be logged') (Get-LogLines $logStarted | Select-Object -Last 1)
    Close-FixtureProcesses $new
    Start-Sleep -Milliseconds 600

    Write-Host 'Launching the shortcut with a click ...'
    $script:fixtureBaseline = @(Get-FixtureProcessIds)
    $started = Get-LogCountAll $logStarted
    $labels = Get-PinnedLabels $dockHandle
    Click-Pin $labels[1]
    $new = Wait-NewFixture 20
    $paths = Get-StartedPaths $new
    Write-Host ("  the click started: {0}" -f (($paths) -join ', '))
    Add-Check 'pins: a click on the shortcut pin started what it points at' ($new.Count -gt 0) ("{0} new process(es)" -f $new.Count)
    Add-Check 'pins: the shortcut was started through the shell, so the program it points at ran' `
        ($paths -contains $fixtureAltExe) `
        ("{0}" -f (($paths) -join ', '))
    Add-Check 'pins: the app logged the start of the shortcut' (Wait-LogGrew $logStarted $started 15 'the start to be logged') (Get-LogLines $logStarted | Select-Object -Last 1)
    Close-FixtureProcesses $new
    Start-Sleep -Milliseconds 600

    Add-Check 'pins: nothing the run did touched the pinned file' ((Get-FileFingerprint $fixtureExe) -eq $fixtureBefore) ("$fixtureBefore -> $(Get-FileFingerprint $fixtureExe)")

    # ---- exit
    Write-Host ''
    Write-Host 'Closing the app ...'
    Close-App
    Add-Check 'pins: the app exited when the window was closed' (-not (Get-AppProcesses)) $(if (Get-AppProcesses) { Describe-AppProcesses } else { 'no process left' })
    Add-Check 'pins: the two pins survived the exit' ((Get-SavedPins).Count -eq 2) ("{0} pin(s)" -f (Get-SavedPins).Count)
}

# ---------------------------------------------------------------- restore

function Invoke-RestoreStage {
    Write-Host '=== 2. the saved order, a real drag, and a restart ==='
    Backup-Settings
    Enable-CanvasInSettings
    Park-LayoutFiles

    # A pin the dock draws is named by what is saved for it, so the names the zone is checked against are
    # the names planted here: the check is about the order and the identity of the items, not about the
    # shell being asked again what a program is called.
    $expectedFirst = 'order program'
    $expectedSecond = 'order shortcut'
    Write-DockSettings -Pins @(
        (New-PinSettings -Id 'order-exe' -Name $expectedFirst -Target $fixtureExe),
        (New-PinSettings -Id 'order-shortcut' -Name $expectedSecond -Target $fixtureLnk -Kind 'Shortcut' -Identity $fixtureAltExe.ToUpperInvariant())
    ) -DockVisible $true -Mode 'Native'

    $before = @{ Restored = Get-LogCountAll $logRestored }

    $app = Launch-App
    $dockHandle = Wait-DockWindow 45
    Add-Check 'restore: the dock window came up' ($dockHandle -ne [IntPtr]::Zero) $(if ($dockHandle -ne [IntPtr]::Zero) { [P3Win]::Describe($dockHandle) } else { 'no window titled Muralis Dock' })
    if ($dockHandle -eq [IntPtr]::Zero) { return }

    Add-Check 'restore: the saved pins came back' (Wait-PinnedCount $dockHandle 2 25) ((Get-PinnedNames $dockHandle) -join ', ')
    Add-Check 'restore: they came back in the saved order' (Wait-PinnedOrder $dockHandle @($expectedFirst, $expectedSecond) 20) ((Get-PinnedNames $dockHandle) -join ', ')
    Add-Check 'restore: the app said what it restored' (Wait-LogGrew $logRestored $before.Restored 20 'the restore line') (Get-LogLines $logRestored | Select-Object -Last 1)

    # The labels are read fresh: a drag needs where the two pins really are now.
    $labels = Get-PinnedLabels $dockHandle
    if ($labels.Count -ne 2) { throw ("The pinned zone holds {0} items; the drag needs two." -f $labels.Count) }

    Write-Host ''
    Write-Host 'Dragging the first pin past the second ...'
    $script:fixtureBaseline = @(Get-FixtureProcessIds)
    $moved = Get-LogCountAll $logMoved
    Drag-Pin $labels[0] $labels[1]
    Start-Sleep -Milliseconds 1200

    Add-Check 'restore: the drag left the zone in the new order' (Wait-PinnedOrder $dockHandle @($expectedSecond, $expectedFirst) 20) ((Get-PinnedNames $dockHandle) -join ', ')
    Add-Check 'restore: the drag committed exactly once' (Wait-LogGrew $logMoved $moved 15 'the move to be logged') (Get-LogLines $logMoved | Select-Object -Last 1)
    Add-Check 'restore: the drag launched nothing' ((Get-FixtureProcessIds).Count -eq $script:fixtureBaseline.Count) ("{0} fixture process(es)" -f (Get-FixtureProcessIds).Count)

    $order = Get-SavedOrder
    Add-Check 'restore: the new order was saved' (($order -join '|') -eq 'order-shortcut|order-exe') ("{0}" -f ($order -join ' -> '))
    Add-Check 'restore: only one order is saved' ($order.Count -eq 2) ("{0} pin(s)" -f $order.Count)

    Write-Host ''
    Write-Host 'Restarting to see whether the order comes back ...'
    Close-App
    $before = @{ Restored = Get-LogCountAll $logRestored }
    $app = Launch-App
    $dockHandle = Wait-DockWindow 45
    Add-Check 'restore: the dock window came up after the restart' ($dockHandle -ne [IntPtr]::Zero) $(if ($dockHandle -ne [IntPtr]::Zero) { [P3Win]::Describe($dockHandle) } else { 'no window titled Muralis Dock' })
    if ($dockHandle -ne [IntPtr]::Zero) {
        Add-Check 'restore: the reordered pins came back in the new order' (Wait-PinnedOrder $dockHandle @($expectedSecond, $expectedFirst) 25) ((Get-PinnedNames $dockHandle) -join ', ')
        Add-Check 'restore: the restart said what it restored' (Wait-LogGrew $logRestored $before.Restored 20 'the restore line') (Get-LogLines $logRestored | Select-Object -Last 1)
    }

    Close-App
    Add-Check 'restore: the app exited when the window was closed' (-not (Get-AppProcesses)) $(if (Get-AppProcesses) { Describe-AppProcesses } else { 'no process left' })
}

# ---------------------------------------------------------------- missing

function Invoke-MissingStage {
    Write-Host '=== 3. a pin whose target is gone ==='
    Backup-Settings
    Enable-CanvasInSettings
    Park-LayoutFiles

    $expectedReal = 'real program'
    Write-DockSettings -Pins @(
        (New-PinSettings -Id 'gone-pin' -Name $missingName -Target $missingTarget),
        (New-PinSettings -Id 'real-pin' -Name $expectedReal -Target $fixtureExe)
    ) -DockVisible $true -Mode 'Native'

    $fixtureBefore = Get-FileFingerprint $fixtureExe
    if (Test-Path $missingTarget) { throw "The missing fixture exists: $missingTarget" }

    $before = @{
        Gone     = Get-LogCountAll $logGone
        Unpinned = Get-LogCountAll $logUnpinned
    }

    $app = Launch-App
    $dockHandle = Wait-DockWindow 45
    Add-Check 'missing: the dock came up with a pin whose target is gone' ($dockHandle -ne [IntPtr]::Zero) $(if ($dockHandle -ne [IntPtr]::Zero) { [P3Win]::Describe($dockHandle) } else { 'no window titled Muralis Dock' })
    if ($dockHandle -eq [IntPtr]::Zero) { return }

    Add-Check 'missing: the rest of the zone is still there' (Wait-PinnedCount $dockHandle 2 25) ((Get-PinnedNames $dockHandle) -join ', ')
    $names = Get-PinnedNames $dockHandle
    Add-Check 'missing: the pin whose target is gone is still listed' ($names -contains $missingName) ($names -join ', ')

    # The gone pin is first in the saved order, so the first label is it.
    $labels = Get-PinnedLabels $dockHandle
    Write-Host ''
    Write-Host 'Clicking the pin whose target is gone ...'
    $script:fixtureBaseline = @(Get-FixtureProcessIds)
    Click-Pin $labels[0]
    Start-Sleep -Milliseconds 1200

    Add-Check 'missing: clicking it said the target is gone' (Wait-LogGrew $logGone $before.Gone 15 'the missing target to be logged') (Get-LogLines $logGone | Select-Object -Last 1)
    Add-Check 'missing: clicking it started nothing' ((Get-FixtureProcessIds).Count -eq $script:fixtureBaseline.Count) ("{0} fixture process(es)" -f (Get-FixtureProcessIds).Count)
    Add-Check 'missing: the pin is still listed after a refused click' ((Get-PinnedNames $dockHandle) -contains $missingName) ((Get-PinnedNames $dockHandle) -join ', ')

    # ---- unpinning the real pin from its own context menu: the file must survive
    Write-Host ''
    Write-Host 'Unpinning the real pin from its own context menu ...'
    $labels = Get-PinnedLabels $dockHandle
    $realLabel = $labels | Where-Object { $_.Name -eq $expectedReal } | Select-Object -First 1
    if ($null -eq $realLabel) { Write-Host ("  (the real pin's label '{0}' was not among {1})" -f $expectedReal, (($labels | ForEach-Object { $_.Name }) -join ', ')) }

    if ($null -ne $realLabel) {
        $unpinNames = Get-LabelCandidates 'Dock_Pinned_Unpin'
        $seen = Get-TopLevelHandles
        Open-PinMenu $realLabel
        $flyout = Find-FlyoutItem $seen $unpinNames 10
        Add-Check 'missing: the pin opened its own context menu, offering Unpin' ($null -ne $flyout) $(if ($null -eq $flyout) { 'no window offering ' + ($unpinNames -join ' | ') } else { "'{0}' in a flyout window" -f $flyout.Name })

        if ($null -ne $flyout) {
            Show-DockTree $flyout.Root 40
            $chosen = Click-FlyoutItem $flyout
            Add-Check 'missing: the Unpin command was chosen' $chosen ("'{0}'" -f $flyout.Name)
            Add-Check 'missing: the unpin was logged' (Wait-LogGrew $logUnpinned $before.Unpinned 20 'the unpin to be logged') (Get-LogLines $logUnpinned | Select-Object -Last 1)
            Add-Check 'missing: the pin left the zone' (Wait-PinnedCount $dockHandle 1 15) ((Get-PinnedNames $dockHandle) -join ', ')
            Add-Check 'missing: the pin left the saved list' ((Get-SavedPins).Count -eq 1) ("{0} pin(s) saved" -f (Get-SavedPins).Count)
        } else {
            Add-Check 'missing: the unpin was logged' $false 'no flyout to choose from'
            Add-Check 'missing: the pin left the zone' $false 'not attempted'
            Add-Check 'missing: the pin left the saved list' $false 'not attempted'
        }
    } else {
        Add-Check 'missing: the pin opened its own context menu, offering Unpin' $false 'the real pin had no label to click'
        Add-Check 'missing: the unpin was logged' $false 'not attempted'
        Add-Check 'missing: the pin left the zone' $false 'not attempted'
        Add-Check 'missing: the pin left the saved list' $false 'not attempted'
    }

    Add-Check 'missing: unpinning did not touch the file the pin pointed at' ((Get-FileFingerprint $fixtureExe) -eq $fixtureBefore) ("$fixtureBefore -> $(Get-FileFingerprint $fixtureExe)")

    Write-Host ''
    Write-Host 'Closing the app ...'
    Close-App
    Add-Check 'missing: the app exited when the window was closed' (-not (Get-AppProcesses)) $(if (Get-AppProcesses) { Describe-AppProcesses } else { 'no process left' })
}

# ---------------------------------------------------------------- clean desktop

function Invoke-CleanStage {
    Write-Host '=== 4. Clean Desktop with the dock switched off ==='
    Backup-Settings
    Enable-CanvasInSettings
    Park-LayoutFiles

    # The dock is switched off by the user and Clean Desktop is on: the dock is required to be up
    # anyway, and the pins have to work on it exactly as they do on a native desktop.
    Write-DockSettings -Pins @(
        (New-PinSettings -Id 'clean-pin' -Name 'clean program' -Target $fixtureExe)
    ) -DockVisible $false -Mode 'CleanDesktop'

    $desktopBefore = Get-DesktopSnapshot
    $before = @{
        Hid      = Get-LogCountAll 'Clean Desktop hid Explorer desktop icons'
        DockOn   = Get-LogCountAll 'The dock is now on the desktop'
        Shelf    = Get-LogCountAll 'Desktop Shelf refreshed in'
    }

    $app = Launch-App
    Write-Host 'Waiting for Clean Desktop to hide the icons ...'
    $hidden = Wait-LogGrew 'Clean Desktop hid Explorer desktop icons' $before.Hid 45 'Clean Desktop to hide the icons'
    Add-Check 'clean: Clean Desktop hid Explorer icons' $hidden (Get-LogLines 'Clean Desktop hid Explorer desktop icons' | Select-Object -Last 1)

    $dockHandle = Wait-DockWindow 45
    Add-Check 'clean: the dock came up although the user switched it off' ($dockHandle -ne [IntPtr]::Zero) $(if ($dockHandle -ne [IntPtr]::Zero) { [P3Win]::Describe($dockHandle) } else { 'no window titled Muralis Dock' })
    Add-Check 'clean: the app said the dock is required by the desktop' ((Get-LogCountAll 'The dock is now on the desktop') -gt $before.DockOn) (Get-LogLines 'The dock is now on the desktop' | Select-Object -Last 1)

    $state = Get-DesktopIconState
    Add-Check 'clean: Explorer really reports its icons hidden' ([bool]$state.NoIcons) ("flags {0}" -f (Format-Flags $state.Flags))

    if ($dockHandle -ne [IntPtr]::Zero) {
        $root = Get-DockRoot $dockHandle
        Add-Check 'clean: the pins are there under Clean Desktop' (Wait-PinnedCount $dockHandle 1 25) ((Get-PinnedNames $dockHandle) -join ', ')

        foreach ($zone in @($shelfZoneName, $utilityZoneName)) {
            $element = Get-ZoneElement $root $zone
            Add-Check ("clean: the dock still has its '{0}' zone" -f $zone) ($null -ne $element) $(if ($null -eq $element) { 'not found' } else { "{0} text(s)" -f (Get-ZoneTexts $element).Count })
        }

        Add-Check 'clean: the real Shelf read the desktop folders' ((Get-LogCountAll 'Desktop Shelf refreshed in') -gt $before.Shelf) (Get-LogLines 'Desktop Shelf refreshed in' | Select-Object -Last 1)

        Write-Host ''
        Write-Host 'Launching a pinned program under Clean Desktop ...'
        $script:fixtureBaseline = @(Get-FixtureProcessIds)
        $started = Get-LogCountAll $logStarted
        $labels = Get-PinnedLabels $dockHandle
        Click-Pin $labels[0]
        $new = Wait-NewFixture 20
        Add-Check 'clean: a pin still launches under Clean Desktop' ($new.Count -gt 0) ("{0} new process(es)" -f $new.Count)
        Add-Check 'clean: the app logged the start under Clean Desktop' (Wait-LogGrew $logStarted $started 15 'the start to be logged') (Get-LogLines $logStarted | Select-Object -Last 1)
        Close-FixtureProcesses $new
        Start-Sleep -Milliseconds 600
    }

    Add-Check 'clean: the desktop files the Shelf reads were not touched' (((Get-DesktopSnapshot) -join ',') -eq ($desktopBefore -join ',')) ("{0} entries before, {1} after" -f $desktopBefore.Count, (Get-DesktopSnapshot).Count)

    Write-Host ''
    Write-Host 'Closing the app, which has to give the icons back ...'
    Close-App
    Add-Check 'clean: the app exited when the window was closed' (-not (Get-AppProcesses)) $(if (Get-AppProcesses) { Describe-AppProcesses } else { 'no process left' })

    $restored = Wait-Until {
        $now = Get-DesktopIconState
        return ($now.Readable -and -not $now.NoIcons)
    } 20 'Explorer to draw its icons again'
    Add-Check 'clean: the icons came back when the app exited' $restored ((Format-Flags (Get-DesktopIconState).Flags))
    Add-Check 'clean: the recovery marker was cleared' (-not (Test-Path $cleanMarkerPath)) $cleanMarkerPath
}

# ---------------------------------------------------------------- perf

# The zone at the size it is designed for: twelve pins, each pointing at its own file so each has its own
# identity and its own icon to read, and every one of them a copy of the fixture program so a movement
# the harness did not mean cannot start anything of the user's.
function New-PerfPinSet([int]$count) {
    $made = @()
    for ($i = 1; $i -le $count; $i++) {
        $path = Join-Path $fixtureRoot ("muralis-perf-{0:D2}.exe" -f $i)
        if (-not (Test-Path $path)) { Copy-Item -Force $fixtureExe $path }
        $made += $path
    }
    return $made
}

# A wait with a tight poll. The dock's own waits poll every 250 ms, which is coarser than the thing the
# perf stage is measuring, so the stage asks for itself.
function Wait-DockWindowTight([datetime]$Since, [int]$timeoutSeconds = 45) {
    $deadline = $Since.AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $handle = Get-DockWindow
        if ($handle -ne [IntPtr]::Zero -and [P3Win]::IsWindowVisible($handle)) { return $handle }
        Start-Sleep -Milliseconds 20
    }
    return [IntPtr]::Zero
}

# How much processor time the app used over a stretch of wall clock. Idle and dragging are measured the
# same way, so the two numbers can be read against each other.
function Measure-AppCpu([double]$Seconds) {
    $script:process.Refresh()
    $before = $script:process.TotalProcessorTime.TotalMilliseconds
    $started = Get-Date
    Start-Sleep -Milliseconds ([int]($Seconds * 1000))
    $elapsed = ((Get-Date) - $started).TotalMilliseconds
    $script:process.Refresh()
    return [pscustomobject]@{
        CpuMs   = $script:process.TotalProcessorTime.TotalMilliseconds - $before
        Seconds = $elapsed / 1000.0
    }
}

function Invoke-PerfStage {
    Write-Host '=== 5. the zone at its design limit, and what following the pointer costs ==='
    Backup-Settings
    Enable-CanvasInSettings
    Park-LayoutFiles

    # Twelve is PinnedApps.MaximumCount. The zone is a fixed strip, so past that many it refuses rather
    # than growing across the screen, and the limit is the size the phase is judged at.
    $count = 12
    $paths = @(New-PerfPinSet $count)
    $script:fixturePrograms += $paths

    $pins = @()
    for ($i = 0; $i -lt $paths.Count; $i++) {
        $pins += (New-PinSettings -Id ("perf-{0:D2}" -f ($i + 1)) -Name ("perf app {0:D2}" -f ($i + 1)) -Target $paths[$i])
    }
    Write-DockSettings -Pins $pins -DockVisible $true -Mode 'Native'
    Add-Check 'perf: the zone was planted at its design limit' ((Get-SavedPins).Count -eq $count) ("{0} pin(s)" -f (Get-SavedPins).Count)

    $launchAt = Get-Date
    $app = Launch-App
    $dockHandle = Wait-DockWindowTight $launchAt 45
    $windowMs = [int]((Get-Date) - $launchAt).TotalMilliseconds
    Add-Check 'perf: the dock window came up' ($dockHandle -ne [IntPtr]::Zero) $(if ($dockHandle -ne [IntPtr]::Zero) { [P3Win]::Describe($dockHandle) } else { 'no window titled Muralis Dock' })
    if ($dockHandle -eq [IntPtr]::Zero) { return }

    $whole = $false
    while (-not $whole -and ((Get-Date) - $launchAt).TotalSeconds -lt 45) {
        $whole = ((Get-PinnedNames $dockHandle).Count -eq $count)
        if (-not $whole) { Start-Sleep -Milliseconds 20 }
    }
    $pinsMs = [int]((Get-Date) - $launchAt).TotalMilliseconds
    Add-Check 'perf: the whole zone came back' $whole ("{0} label(s)" -f (Get-PinnedNames $dockHandle).Count)

    Write-Host ("  main window {0} ms, dock window {1} ms, all {2} pins {3} ms from launch" -f $app.LaunchMs, $windowMs, $count, $pinsMs)
    Write-Host '  (both waits poll at 20 ms and a UI Automation read costs tens of ms, so these answer to that)'
    Add-Sample 'pins' $count
    Add-Sample 'mainWindowMs' $app.LaunchMs
    Add-Sample 'dockWindowMs' $windowMs
    Add-Sample 'allPinsMs' $pinsMs

    Write-Host ''
    Write-Host 'Idle with the zone full ...'
    # The icons for the pins and for the Shelf's own items are read off the startup path, and the
    # desktop's own items are read in the background too, so the first seconds after the dock is up are
    # not idle: an earlier run of this stage measured 2.25 s of processor time in the five seconds after
    # the window appeared. The measurement waits for a quiet second first, which is what makes it about
    # the dock being there rather than about the startup behind it.
    $quiet = Wait-Until { (Measure-AppCpu 1).CpuMs -lt 50 } 60 'a quiet second with the zone full'
    Add-Check 'perf: the app went quiet with the zone full' $quiet 'a second costing less than 50 ms of processor time'
    $idle = Measure-AppCpu 5
    $idlePercent = [math]::Round(100 * $idle.CpuMs / ($idle.Seconds * 1000), 2)
    Write-Host ("  {0:N1} ms of processor time over {1:N1} s = {2}% of one core" -f $idle.CpuMs, $idle.Seconds, $idlePercent)
    Add-Sample 'idleCpuMs' ([math]::Round($idle.CpuMs, 1))
    Add-Sample 'idleCpuMsPerSecond' ([math]::Round($idle.CpuMs / $idle.Seconds, 2))
    Add-Sample 'idleSingleCorePercent' $idlePercent

    Write-Host ''
    Write-Host 'Following the pointer inside one slot, with nothing reordered ...'

    # The two slots are read once. The pins swap places as they are dragged past one another, so the
    # same two rectangles serve every drag — and UI Automation, which the app's own UI thread is the one
    # answering, stays out of what is being measured.
    $slots = Get-PinnedLabels $dockHandle
    if ($slots.Count -lt 2) { throw ("The pinned zone holds {0} items; the drag needs two." -f $slots.Count) }

    # The hot path on its own. The pointer is pressed on the first pin and walked back and forth inside
    # that pin's own slot: past the four DIP that turn a press into a drag, and never far enough to name
    # another index. Nothing is reordered, nothing is saved and nothing is redrawn, so what is left is
    # the cost of following the hand — the number a high refresh display is judged on.
    #
    # Three rounds, and the best of them is the number reported: the app has background work of its own
    # (the icons of the deskop's own items, a wallpaper refresh) that lands in some rounds and not
    # others — one round of this stage has measured 0.39 ms per movement and another 5.5 — and the
    # question here is what the drag path itself costs, which the quietest round answers.
    $slot = $slots[0]
    $movements = 40
    $rounds = @()
    $movedBefore = Get-LogCountAll $logMoved
    $script:fixtureBaseline = @(Get-FixtureProcessIds)

    for ($round = 0; $round -lt 3; $round++) {
        Move-Pointer $slot.X $slot.Y
        Start-Sleep -Milliseconds 150
        [P3Win]::LeftDown()
        Start-Sleep -Milliseconds 150

        $script:process.Refresh()
        $cpuBefore = $script:process.TotalProcessorTime.TotalMilliseconds
        $walkAt = Get-Date
        for ($i = 0; $i -lt $movements; $i++) {
            Move-Pointer $(if ($i % 2 -eq 0) { $slot.X + 18 } else { $slot.X - 18 }) $slot.Y
            Start-Sleep -Milliseconds 20
        }
        $walkMs = ((Get-Date) - $walkAt).TotalMilliseconds
        $script:process.Refresh()
        $walkCpu = $script:process.TotalProcessorTime.TotalMilliseconds - $cpuBefore
        [P3Win]::LeftUp()
        Start-Sleep -Milliseconds 600

        $rounds += [pscustomobject]@{ CpuMs = $walkCpu; WallMs = $walkMs }
    }

    $best = ($rounds | Measure-Object -Property CpuMs -Minimum).Minimum
    $bestPerMovement = [math]::Round($best / $movements, 3)
    foreach ($entry in $rounds) {
        Write-Host ("  a round of {0} movement(s) in {1:N0} ms cost {2:N1} ms of processor time" -f $movements, $entry.WallMs, $entry.CpuMs)
    }
    Write-Host ("  the best of the three = {0} ms per movement ({1}% of one core over its own round)" -f $bestPerMovement, [math]::Round(100 * $best / $rounds[-1].WallMs, 2))
    Write-Host '  (the app''s own time only, and 20 ms of each round is the harness waiting between movements)'
    Add-Sample 'hotPathMovements' $movements
    Add-Sample 'hotPathRounds' $rounds.Count
    Add-Sample 'hotPathCpuMsPerRound' (($rounds | ForEach-Object { [math]::Round($_.CpuMs, 1) }) -join ' / ')
    Add-Sample 'hotPathCpuMsPerMovement' $bestPerMovement
    Add-Sample 'hotPathSingleCorePercent' ([math]::Round(100 * $best / $rounds[-1].WallMs, 2))
    Add-Check 'perf: following the pointer inside one slot reordered nothing' ((Get-LogCountAll $logMoved) -eq $movedBefore) ("{0} move(s) logged over {1} round(s)" -f ((Get-LogCountAll $logMoved) - $movedBefore), $rounds.Count)

    Write-Host ''
    Write-Host 'Six complete drag-and-reorder cycles, with the drop measured apart from the following ...'

    # The two paths are different things and get their own numbers: following the hand is a composition
    # write per report, and the drop is where the order is committed, the file is written and the zone
    # reacts to its own new order. The split is taken at the release, and the 700 ms after it is the
    # landing spring and whatever the dock does with the new list.
    $movedBefore = Get-LogCountAll $logMoved
    $cycles = 6
    $movementsPerDrag = 14
    $pitch = $slots[1].Left - $slots[0].Left
    $targetX = [int]($slots[0].Left + ($pitch * 1.5))

    $script:process.Refresh()
    $cpuBefore = $script:process.TotalProcessorTime.TotalMilliseconds
    $burstAt = Get-Date
    $dropCpu = 0.0
    $done = 0
    for ($i = 0; $i -lt $cycles; $i++) {
        Move-Pointer $slots[0].X $slots[0].Y
        Start-Sleep -Milliseconds 120
        [P3Win]::LeftDown()
        Start-Sleep -Milliseconds 120
        for ($step = 1; $step -le $movementsPerDrag; $step++) {
            Move-Pointer ([int]($slots[0].X + (($targetX - $slots[0].X) * $step / $movementsPerDrag))) $slots[1].Y
            Start-Sleep -Milliseconds 25
        }

        $script:process.Refresh()
        $cpuBeforeDrop = $script:process.TotalProcessorTime.TotalMilliseconds
        [P3Win]::LeftUp()
        Start-Sleep -Milliseconds 700
        $script:process.Refresh()
        $dropCpu += $script:process.TotalProcessorTime.TotalMilliseconds - $cpuBeforeDrop
        $done++
    }
    $burstMs = ((Get-Date) - $burstAt).TotalMilliseconds
    $script:process.Refresh()
    $burstCpu = $script:process.TotalProcessorTime.TotalMilliseconds - $cpuBefore

    $moveCount = $done * $movementsPerDrag
    $followCpu = $burstCpu - $dropCpu
    $perCycle = if ($done -gt 0) { [math]::Round($burstCpu / $done, 1) } else { 0 }
    $perDrop = if ($done -gt 0) { [math]::Round($dropCpu / $done, 1) } else { 0 }
    $burstPercent = if ($burstMs -gt 0) { [math]::Round(100 * $burstCpu / $burstMs, 2) } else { 0 }

    Write-Host ("  {0} cycle(s), about {1} pointer movement(s), {2:N0} ms of wall clock" -f $done, $moveCount, $burstMs)
    Write-Host ("  the app used {0:N1} ms of processor time = {1} ms per cycle, {2}% of one core" -f $burstCpu, $perCycle, $burstPercent)
    Write-Host ("  of which {0:N1} ms followed the pointer and {1:N1} ms is the drop ({2} ms per drop: the spring, the commit and the zone's new order)" -f $followCpu, $dropCpu, $perDrop)
    Add-Sample 'reorderCycles' $done
    Add-Sample 'reorderMovements' $moveCount
    Add-Sample 'reorderWallMs' ([math]::Round($burstMs, 1))
    Add-Sample 'reorderCpuMs' ([math]::Round($burstCpu, 1))
    Add-Sample 'reorderCpuMsPerCycle' $perCycle
    Add-Sample 'reorderFollowCpuMs' ([math]::Round($followCpu, 1))
    Add-Sample 'reorderDropCpuMs' ([math]::Round($dropCpu, 1))
    Add-Sample 'reorderDropCpuMsPerDrop' $perDrop
    Add-Sample 'reorderSingleCorePercent' $burstPercent

    # The hot path is the reason the numbers above are what they are, so what it is allowed to do is
    # checked here rather than only in the unit tests: one write per release, no launch on the way.
    $expectedMoves = $movedBefore + $done
    $committed = Wait-Until { (Get-LogCountAll $logMoved) -eq $expectedMoves } 10 ('{0} move(s) to be logged' -f $done)
    Add-Check 'perf: every cycle was carried out' ($done -eq $cycles) ("{0} of {1} cycle(s)" -f $done, $cycles)
    Add-Check 'perf: the file was written once per release, not once per movement' $committed ("{0} move(s) logged for {1} cycle(s) and about {2} movement(s)" -f ((Get-LogCountAll $logMoved) - $movedBefore), $done, $moveCount)
    Add-Check 'perf: the drag launched nothing' ((Get-FixtureProcessIds).Count -eq $script:fixtureBaseline.Count) ("{0} fixture process(es)" -f (Get-FixtureProcessIds).Count)
    Add-Check 'perf: the zone is still whole after the cycles' ((Get-PinnedNames $dockHandle).Count -eq $count) ("{0} label(s)" -f (Get-PinnedNames $dockHandle).Count)

    Close-App
    Add-Check 'perf: the app exited when the window was closed' (-not (Get-AppProcesses)) $(if (Get-AppProcesses) { Describe-AppProcesses } else { 'no process left' })
}

# ---------------------------------------------------------------- run

# One step of putting the machine back. A step that fails is reported and the rest still run: an error
# thrown from the cleanup would replace the stage's own error, and the reason the run failed is what it
# was run for.
function Invoke-CleanupStep([string]$What, [scriptblock]$Action) {
    try {
        & $Action
    } catch {
        Write-Host ("  ({0} failed: {1})" -f $What, $_.Exception.Message)
    }
}

$baselineIcons = $null
$stageFinished = $false
$stageError = $null
try {
    $baselineIcons = Get-DesktopIconState
    Park-Marker

    # The fixture workspace belongs to the run rather than to one stage: the probe reports on it, and
    # every other stage drives the shell's picker at it or checks a launch of it. Made here, because a
    # stage that ran on its own would otherwise hand the picker a path that is not on disk — which the
    # shell answers with its own "the path does not exist" box, and the run would read as a failure of
    # the app.
    New-FixtureWorkspace
    Add-Check 'harness: the fixture workspace is ready' ((Test-Path $fixtureExe) -and (Test-Path $fixtureLnk)) $fixtureRoot

    # The stage's own failure is caught so that the machine still gets put back and the report still
    # says what went wrong: the checks a stage never reached are the interesting part of a failed run,
    # and an error that skipped the cleanup would hide both.
    try {
        switch ($Stage) {
            'probe'   { Invoke-ProbeStage }
            'dump'    { Invoke-DumpStage }
            'pins'    { Invoke-PinsStage }
            'restore' { Invoke-RestoreStage }
            'missing' { Invoke-MissingStage }
            'clean'   { Invoke-CleanStage }
            'perf'    { Invoke-PerfStage }
            'full'    { Invoke-PinsStage; Invoke-RestoreStage; Invoke-MissingStage; Invoke-CleanStage }
        }
        $stageFinished = $true
    } catch {
        $stageError = $_.Exception.Message
        Write-Host ''
        Write-Host ("The stage stopped early: {0}" -f $stageError)
        if ($_.InvocationInfo) { Write-Host ("  at {0}" -f $_.InvocationInfo.PositionMessage.Trim()) }
    }
} finally {
    Write-Host ''
    Write-Host 'Putting the machine back the way it was found ...'
    Invoke-CleanupStep 'closing the app' { Close-App }
    Invoke-CleanupStep 'closing the fixture programs' { Close-FixtureProcesses @(Get-FixtureProcessIds) }
    Invoke-CleanupStep 'putting the desktop icon flags back' { Restore-DesktopFlags $baselineIcons }
    Invoke-CleanupStep 'putting the recovery marker back' { Restore-Marker }
    Invoke-CleanupStep 'removing the fixture workspace' { Remove-FixtureWorkspace }
    Invoke-CleanupStep 'restoring the settings and layout files' { Restore-Everything }

    # A stage that threw part way through has fewer checks than it should, which would read as a pass.
    Add-Check 'harness: the stage ran to its end' $stageFinished `
        $(if ($stageFinished) { "stage '{0}'" -f $Stage } else { "stage '{0}' stopped early: {1}" -f $Stage, $stageError })
    $failed = Write-CheckReport $Stage $outPath 'p4c-pinned-verify.json'

    # Only when nothing is propagating: an unhandled error already ends the run non-zero, and exit
    # inside finally would swallow it.
    if ($stageFinished -and $failed -gt 0) { exit 1 }
}
