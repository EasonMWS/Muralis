<#
.SYNOPSIS
    Phase 5 live verification: the home page's Muralis Mode hero, driven like a user.

.DESCRIPTION
    The hero is the flagship entry point of the product, so what it is judged by is not what it says
    about itself but what the rest of the machine agrees with:

      * the page's own UI Automation tree, which is what is on screen and in what state
      * Explorer's desktop icon flag, read over COM, which is the only honest answer to whether the
        mode is really on - a hero that claimed the mode while the icons were still there would be a
        lie, and this is the check that catches it
      * the settings document, which is what the settings page and the next launch read
      * the log, which records what the app decided (the mode entered, the mode left, why it failed)
      * a screenshot of the window per state, for the judgement no automation can make: whether the
        flagship card looks like the flagship card, in both themes and both languages

    The checks it makes:

      probe
      - the machine, the single-instance mutex, the two string catalogs, and the failure lever's path

      hero
      - the home page reading the native desktop: the ready line, the Enter action, and no status for
        a mode that is not on
      - Enter: the preparing line, the buttons inert while the desktop is rearranged, the active line,
        the dock and the shelf status, the icons hidden in Explorer's own view, the dock window up
      - Customize: the settings page, showing Muralis Mode as the chosen experience
      - the settings page choosing the native desktop: the icons back, the hero following it on Home
      - Enter and Exit from the hero: the desktop handed back, the icons back, the dock released
      - a narrow window: the preview put away and nothing pushed past the right edge

      failure
      - an attempt that cannot take: the mode is reported as not started, with the reason on screen,
        Try Again instead of Enter, no way to leave a mode that was never entered, the desktop's icons
        untouched and no dock left behind
      - the lever taken away and Try Again pressed: the mode really comes up

      matrix
      - Dark and Light, English and Chinese: the ready line, the active line and a screenshot of each

    Everything the harness changes is restored on the way out: settings.json, the desktop documents,
    the desktop's own icon flags, the Clean Desktop recovery marker and the failure lever.

.PARAMETER Stage
    probe   - no app launch: machine state, the catalogs, the failure lever's path
    hero    - the whole entry point: ready, enter, customize, settings, back, exit, narrow window
    failure - an attempt that cannot take, and the retry that can
    matrix  - Dark and Light in English and Chinese, with a screenshot of each
    full    - hero, then failure

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/p5-hero-verify.ps1 -Stage probe
    powershell -ExecutionPolicy Bypass -File tools/p5-hero-verify.ps1 -Stage hero
    powershell -ExecutionPolicy Bypass -File tools/p5-hero-verify.ps1 -Stage full
#>
[CmdletBinding()]
param(
    [ValidateSet('probe', 'hero', 'failure', 'matrix', 'full')] [string]$Stage = 'probe',
    [string]$Exe = 'src/Muralis.App/bin/Debug/net10.0-windows10.0.26100.0/win-x64/Muralis.exe',
    [string]$OutDir = 'artifacts/p5-hero'
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'p3-common.ps1')
. (Join-Path $PSScriptRoot 'p3d-shell-interop.ps1')
Set-BackupPaths 'p5'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path $PSScriptRoot -Parent
$exePath = if ([System.IO.Path]::IsPathRooted($Exe)) { $Exe } else { Join-Path $repoRoot $Exe }
$outPath = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repoRoot $OutDir }
$shotPath = Join-Path $outPath 'shots'

$mainWindowClass = 'WinUIDesktopWin32WindowClass'
$mutexName = 'Local\Muralis.SingleInstance'
$dockTitle = 'Muralis Dock'

# The names the hero gives its own parts in XAML. They are identifiers rather than copy, so they are the
# same strings in every language, and they are how the harness finds the page without reading its words.
$heroTitleId = 'HeroEyebrowText'             # the caption above the headline that names the experience
$heroStatusId = 'HeroStatusText'
$heroDockId = 'HeroDockStatus'
$heroShelfId = 'HeroShelfStatus'
$heroPrimaryId = 'HeroPrimaryAction'
$heroCustomizeId = 'HeroCustomizeAction'
$heroExitId = 'HeroExitAction'
$heroPreviewId = 'HeroPreview'
$settingsModeId = 'DesktopExperienceCombo'

# The Clean Desktop recovery marker: written before Explorer's icons are hidden, removed once they are
# verifiably back. The failure stage uses the temporary file the marker write goes through, so its path
# is both the lever and something a run must never leave behind.
$cleanMarkerPath = Join-Path $appData 'desktop\clean-desktop-state.json'
$cleanMarkerBackup = "$cleanMarkerPath.p5.bak"
$cleanMarkerTemp = "$cleanMarkerPath.tmp"

# Windows and both of the sizes the hero is judged at. The wide one is where the preview belongs and the
# narrow one is below the width the page collapses it at; neither may push anything past the right edge.
$wideSize = @(1360, 900)
$narrowSize = @(820, 760)

# Where the page is put: out of the tray, and away from the desktop's own dock.
$windowOrigin = @(40, 40)

$script:theme = 'System'
$script:language = 'system-language'
$script:shotPrefix = 'run'
$script:leverPlanted = $false
$script:markerParked = $false

# ---------------------------------------------------------------- the two string catalogs

# The copy is read from the product's own catalogs rather than written out here: a harness that carried
# its own copy of the strings would agree with the app only until someone edited one of them. Both
# languages are loaded, and a read is accepted if it matches either, which is how a check stays honest
# without assuming which language the run ended up in.
function Get-Catalog([string]$path) {
    $xml = [xml](Get-Content -Raw -Encoding UTF8 $path)
    $map = @{}
    foreach ($entry in $xml.root.data) {
        if ($entry.name) { $map[[string]$entry.name] = [string]$entry.value }
    }
    return $map
}

$english = Get-Catalog (Join-Path $repoRoot 'src/Muralis.App/Strings/Resources.resx')
$chinese = Get-Catalog (Join-Path $repoRoot 'src/Muralis.App/Strings/Resources.zh-CN.resx')

# Either language is accepted: the page may be showing either one.
function Expect([string]$key) {
    return @([string]$english[$key], [string]$chinese[$key])
}

# Exactly one language: for the runs that set the language themselves.
function ExpectIn([string]$key) {
    if ($script:language -eq 'zh-CN') { return @([string]$chinese[$key]) }
    return @([string]$english[$key])
}

function Format-Expected([string[]]$values) {
    return (($values | ForEach-Object { "'{0}'" -f $_ }) -join ' or ')
}

# The hero's whole set of keys, checked in the probe stage so a missing translation is found before the
# app is launched at all.
$heroKeys = @(
    'Home_MuralisMode_Title', 'Home_MuralisMode_Tagline', 'Home_MuralisMode_Ready',
    'Home_MuralisMode_Entering', 'Home_MuralisMode_Active', 'Home_MuralisMode_Exiting',
    'Home_MuralisMode_Error', 'Home_MuralisMode_Enter', 'Home_MuralisMode_TryAgain',
    'Home_MuralisMode_Exit', 'Home_MuralisMode_Customize', 'Home_MuralisMode_Dock',
    'Home_MuralisMode_Dock_Active', 'Home_MuralisMode_Shelf', 'Home_MuralisMode_Shelf_Ready',
    'Home_MuralisMode_Motion', 'Home_MuralisMode_Widgets', 'Home_MuralisMode_ComingSoon',
    'Home_MuralisMode_Preview'
)

# ---------------------------------------------------------------- launching and closing

function Get-AppProcesses {
    return @(Get-Process -Name Muralis -ErrorAction SilentlyContinue)
}

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
# alone does not say which one is the app's own window: the dock is the one the app named, and the main
# window is the visible one of that class that is not it.
function Get-MainWindow([int]$processId) {
    foreach ($handle in [P3Win]::WindowsOfProcess($processId)) {
        if (-not [P3Win]::IsWindowVisible($handle)) { continue }
        if ([P3Win]::ClassOf($handle) -ne $mainWindowClass) { continue }
        if ([P3Win]::TitleOf($handle) -eq $dockTitle) { continue }
        return $handle
    }
    return [IntPtr]::Zero
}

function Get-DockWindow {
    if ($null -eq $script:process) { return [IntPtr]::Zero }
    foreach ($handle in [P3Win]::WindowsOfProcess([int]$script:process.Id)) {
        if (-not [P3Win]::IsWindowVisible($handle)) { continue }
        if ([P3Win]::TitleOf($handle) -eq $dockTitle) { return $handle }
    }
    return [IntPtr]::Zero
}

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

# The window is never touched before its first frame: a window that has not drawn yet answers a move
# with a failure, and that failure is the app's crash rather than a harness error. The wait is on the
# window existing, and the pause before anything moves it is the first frame.
function Launch-App {
    Assert-MuralisNotRunning

    $app = Start-AppInstance
    $script:process = $app.Process
    $script:window = $app.Window
    if (-not $app.CameUp) { throw 'The Muralis main window never came up.' }

    Start-Sleep -Milliseconds 900
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

    [void](Wait-Until { -not (Get-AppProcesses) } 5 'the app to be gone from the process list')
    if (Get-AppProcesses) { Write-Host ("  still running: {0}" -f (Describe-AppProcesses)) }

    Start-Sleep -Milliseconds 600
    $script:process = $null
    $script:window = [IntPtr]::Zero
}

# ---------------------------------------------------------------- the window and its picture

function Set-AppWindow([int]$width, [int]$height) {
    if ($null -eq $script:process -or $script:window -eq [IntPtr]::Zero) { throw 'The app is not running.' }

    $screenWidth = [P3Win]::GetSystemMetrics(0)
    $screenHeight = [P3Win]::GetSystemMetrics(1)
    $width = [Math]::Min($width, $screenWidth - 80)
    $height = [Math]::Min($height, $screenHeight - 80)

    [P3Win]::ShowWindow($script:window, [P3Win]::SW_RESTORE) | Out-Null
    [P3Win]::MoveWindow($script:window, $windowOrigin[0], $windowOrigin[1], $width, $height, $true) | Out-Null
    [P3Win]::SetForegroundWindow($script:window) | Out-Null
    Start-Sleep -Milliseconds 800

    $size = [P3Win]::ClientSizeOf($script:window)
    return [pscustomobject]@{ Width = $size[0]; Height = $size[1] }
}

# A picture of the window's own client area, taken while the window is the one on top: the region is the
# window's, so nothing of the user's screen beyond it is in the file. A window that is not in front is
# not captured at all - the pixels would belong to whatever is - and the caller reports that instead.
#
# Being brought forward is asked for more than once: the first launch has a window that has only just
# appeared, and a request made while another window still holds the front is refused rather than queued.
function Get-AppClientBitmap {
    if ($script:window -eq [IntPtr]::Zero) { return $null }

    $inFront = $false
    for ($attempt = 0; $attempt -lt 12; $attempt++) {
        [P3Win]::ShowWindow($script:window, [P3Win]::SW_RESTORE) | Out-Null
        [P3Win]::SetForegroundWindow($script:window) | Out-Null
        Start-Sleep -Milliseconds 250
        if ([P3Win]::GetForegroundWindow() -eq $script:window) { $inFront = $true; break }
    }

    if (-not $inFront) { return $null }
    Start-Sleep -Milliseconds 250

    $origin = [P3Win]::ClientOriginOf($script:window)
    $size = [P3Win]::ClientSizeOf($script:window)
    if ($size[0] -le 0 -or $size[1] -le 0) { return $null }

    $bitmap = New-Object System.Drawing.Bitmap($size[0], $size[1])
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($origin[0], $origin[1], 0, 0, (New-Object System.Drawing.Size($size[0], $size[1])))
        } finally {
            $graphics.Dispose()
        }
    } catch {
        $bitmap.Dispose()
        return $null
    }

    return [pscustomobject]@{ Bitmap = $bitmap; Origin = $origin }
}

function Save-AppShot([string]$state) {
    $shot = Get-AppClientBitmap
    if ($null -eq $shot) { return $null }

    New-Item -ItemType Directory -Force -Path $shotPath | Out-Null
    $path = Join-Path $shotPath ("{0}-{1}.png" -f $script:shotPrefix, $state)
    try {
        $shot.Bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $shot.Bitmap.Dispose()
    }

    return $path
}

function Add-Shot([string]$state, [string]$what) {
    # The card is given the length of the design's longest motion token before it is photographed. A
    # picture taken while it is still moving shows half of one state over half of the other, and the
    # states beside each other are the whole point of these pictures: the judgement they exist for is
    # made on what the card looks like once it has come to rest, and a shot taken too early has been
    # read as the card being dim or half drawn when it was neither.
    Start-Sleep -Milliseconds 500
    $path = Save-AppShot $state
    Add-Check ("a screenshot of the {0} was taken for review" -f $what) ($null -ne $path) `
        $(if ($null -ne $path) { $path } else { 'the window was not in front, so nothing was captured' })
    return $path
}

# ---------------------------------------------------------------- the hero, read and driven

function Get-HeroElement([string]$id) {
    return (Find-ById (Get-AppRoot) $id)
}

function Get-ElementName($element) {
    if ($null -eq $element) { return $null }
    try {
        $name = $element.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
        if ($name -is [string]) { return $name }
    } catch { }
    return $null
}

function Get-HeroText([string]$id) {
    return (Get-ElementName (Get-HeroElement $id))
}

# The hero's own words, waited for: the answer to "what is the page showing" is only meaningful once
# the page has settled, and every read here is of a control that exists in that state.
function Wait-HeroText([string]$id, [string[]]$expected, [int]$timeoutSeconds, [string]$what) {
    $script:p5Id = $id
    $script:p5Expected = $expected
    return (Wait-Until { $script:p5Expected -contains (Get-HeroText $script:p5Id) } $timeoutSeconds $what)
}

function Check-HeroText([string]$name, [string]$id, [string[]]$expected) {
    $text = Get-HeroText $id
    $ok = ($null -ne $text) -and ($expected -contains $text)
    Add-Check $name $ok ("read {0}, expected {1}" -f $(if ($null -eq $text) { 'nothing' } else { "'{0}'" -f $text }), (Format-Expected $expected))
    return $ok
}

function Check-HeroAbsent([string]$name, [string]$id, [string]$why) {
    $element = Get-HeroElement $id
    Add-Check $name ($null -eq $element) $(if ($null -eq $element) { 'not on the page' } else { "on the page, {0}" -f $why })
}

function Invoke-Hero([string]$id, [string]$what) {
    $element = Get-HeroElement $id
    if ($null -eq $element) { throw "The hero's $what was not on the page." }
    Invoke-Element $element $what
}

# The transition is watched rather than sampled once: the line that says the desktop is being rearranged
# is on screen for as long as the work takes, and a single read after the press would usually miss it and
# report the state after it.
#
# The reads go to the controls themselves rather than through a search of the page. Finding the headline
# again each time means walking the page's whole element tree across processes, which takes longer than
# the transition lasts: the first read lands before the press has been served and the next one after the
# desktop was already rearranged, and the state in between is never seen. One element once, then its name.
#
# What the action the transition is reported through says about itself while the page is busy. It is read
# beside the status line rather than on its own, because the question is whether the two agree at the same
# moment: a line that says the desktop is being rearranged next to an action that is still pressable is the
# defect, and either one read alone cannot show it.
function Format-ButtonState($button) {
    try { return $(if ($button.Current.IsEnabled) { ' [enabled]' } else { ' [disabled]' }) } catch { return ' [unreadable]' }
}

function Watch-HeroTransition($status, [string[]]$targets, [string[]]$busy, [int]$timeoutSeconds, $button) {
    $seen = New-Object System.Collections.ArrayList
    $timeline = New-Object System.Collections.ArrayList
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $enabledWhileBusy = $false
    $enabledWhileBusyAtMs = $null
    $buttonWasDisabled = $false
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    $reachedAt = $null

    while ((Get-Date) -lt $deadline) {
        $text = $null
        $read = [System.Diagnostics.Stopwatch]::StartNew()
        try { $text = $status.Current.Name } catch { }
        $read.Stop()

        if ($null -ne $text) {
            if (-not $seen.Contains($text)) { [void]$seen.Add($text) }

            # Read only while the page says it is busy: the state before the press and the state after it
            # both leave the action on the page, so a read of those would say nothing about reentrancy.
            if (($busy -contains $text) -and ($null -ne $button)) {
                try {
                    if ($button.Current.IsEnabled) {
                        $enabledWhileBusy = $true
                        if ($null -eq $enabledWhileBusyAtMs) { $enabledWhileBusyAtMs = $clock.ElapsedMilliseconds }
                    }
                    else { $buttonWasDisabled = $true }
                } catch { }
            }
        }

        # How long each read took and where in the transition it landed: a read served by the page's own
        # thread cannot be answered while that thread is busy, so the timeline is the evidence for whether
        # a state was given a frame at all or was simply never sampled.
        [void]$timeline.Add(("{0:N0}ms (+{1:N0}ms): {2}" -f $read.ElapsedMilliseconds, $clock.ElapsedMilliseconds,
            $(if ($null -eq $text) { 'nothing' } else { "'{0}'" -f $text }) + $(if (($busy -contains $text) -and ($null -ne $button)) { (Format-ButtonState $button) } else { '' })))

        if ($null -ne $text -and ($targets -contains $text)) { $reachedAt = $clock.ElapsedMilliseconds; break }
    }

    return [pscustomobject]@{
        Lines               = @($seen)
        Reached             = ($seen.Count -gt 0) -and ($targets -contains $seen[$seen.Count - 1])
        EnabledWhileBusy    = $enabledWhileBusy
        EnabledWhileBusyAtMs = $enabledWhileBusyAtMs
        ButtonWasDisabled   = $buttonWasDisabled
        ReachedAtMs         = $reachedAt
        Reads               = $timeline.Count
        Timeline            = @($timeline)
    }
}

# Whether the action was pressable while the desktop was still being rearranged, as opposed to in the
# instant the rearrangement finished. Finishing is what enables the action again, so the last read before
# the answer lands can already show it enabled: that hand-over is not the window the check is about, and
# counting it would make the check fail or pass on which side of it a read happened to fall.
function Test-HeroActionWasInert($watch, [int]$handOverMs = 50) {
    if (-not $watch.ButtonWasDisabled) { return $false }
    if ($null -eq $watch.EnabledWhileBusyAtMs) { return $true }
    return ($watch.ReachedAtMs - $watch.EnabledWhileBusyAtMs) -le $handOverMs
}

# The read timeline in full when it is short, and its ends when it is not: the point of it is to say
# whether the middle of the transition was ever sampled.
function Format-HeroTimeline($watch) {
    if ($watch.Reads -eq 0) { return 'no read landed' }
    if ($watch.Reads -le 8) { return ($watch.Timeline -join ' | ') }
    return (($watch.Timeline[0..3] + ' ... ' + $watch.Timeline[($watch.Reads - 3)..($watch.Reads - 1)]) -join ' | ')
}

# What the card is actually drawing, as opposed to what it is saying: the mini desktop preview's own
# pixels, and a crop of them written out so the region this measured can be looked at. The words are read
# through UI Automation, and a card whose two drawings never swapped would still answer every one of those
# reads correctly - which it did, once. This is the check that catches the card saying one thing and
# drawing another.
#
# The pixels are taken whole rather than a cell at a time: the preview is drawn in pale shapes on a pale
# panel, and the brightness of any part of it barely moves when the shapes do. A cell average would report
# no change; the difference between two pixels does not.
function Get-HeroDrawing([string]$state) {
    $element = Get-HeroElement $heroPreviewId
    if ($null -eq $element) { return $null }

    try { $bounds = $element.Current.BoundingRectangle } catch { return $null }
    if ($bounds.IsEmpty) { return $null }

    $shot = Get-AppClientBitmap
    if ($null -eq $shot) { return $null }

    try {
        $left = [int][Math]::Round($bounds.Left - $shot.Origin[0])
        $top = [int][Math]::Round($bounds.Top - $shot.Origin[1])
        $width = [int][Math]::Round($bounds.Width)
        $height = [int][Math]::Round($bounds.Height)
        if ($left -lt 0) { $left = 0 }
        if ($top -lt 0) { $top = 0 }
        if (($left + $width) -gt $shot.Bitmap.Width) { $width = $shot.Bitmap.Width - $left }
        if (($top + $height) -gt $shot.Bitmap.Height) { $height = $shot.Bitmap.Height - $top }
        if ($width -lt 40 -or $height -lt 40) { return $null }

        $region = New-Object System.Drawing.Rectangle($left, $top, $width, $height)
        $data = $shot.Bitmap.LockBits($region, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $stride = $data.Stride
            $pixels = New-Object byte[] ($stride * $height)
            [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
        } finally {
            $shot.Bitmap.UnlockBits($data)
        }

        # The most accent-like pixel in the drawing: the running mode is marked with an accent rather than
        # painted with one, so this is where a restrained accent shows up as a number.
        $cyanMost = [double]::NegativeInfinity
        for ($y = 0; $y -lt $height; $y++) {
            $line = $y * $stride
            for ($x = 0; $x -lt $width; $x++) {
                $at = $line + ($x * 4)
                $blue = [int]$pixels[$at]
                $green = [int]$pixels[$at + 1]
                $red = [int]$pixels[$at + 2]
                $cyan = (($green + $blue) / 2.0) - $red
                if ($cyan -gt $cyanMost) { $cyanMost = $cyan }
            }
        }

        $cropPath = $null
        if ($state) {
            New-Item -ItemType Directory -Force -Path $shotPath | Out-Null
            $crop = $shot.Bitmap.Clone($region, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try {
                $cropPath = Join-Path $shotPath ("{0}-drawing-{1}.png" -f $script:shotPrefix, $state)
                $crop.Save($cropPath, [System.Drawing.Imaging.ImageFormat]::Png)
            } finally {
                $crop.Dispose()
            }
        }

        return [pscustomobject]@{
            Width = $width; Height = $height; Stride = $stride; Pixels = $pixels
            Cyanness = [Math]::Round($cyanMost, 1); Crop = $cropPath
        }
    } finally {
        $shot.Bitmap.Dispose()
    }
}

# How far apart two drawings are, pixel by pixel: the share of the preview that moved by at least a few
# levels and how far it moved on average. Two captures of the same card in the same state are the same
# picture, so anything above nothing is a change of the drawing rather than of the light.
function Compare-HeroDrawings($before, $after, [int]$level = 6) {
    if (($null -eq $before) -or ($null -eq $after)) { return $null }
    if (($before.Width -ne $after.Width) -or ($before.Height -ne $after.Height)) { return $null }

    $changed = 0
    $total = 0
    $sum = 0.0
    for ($y = 0; $y -lt $before.Height; $y++) {
        $lineBefore = $y * $before.Stride
        $lineAfter = $y * $after.Stride
        for ($x = 0; $x -lt $before.Width; $x++) {
            $atBefore = $lineBefore + ($x * 4)
            $atAfter = $lineAfter + ($x * 4)
            $worst = 0
            $summed = 0.0
            for ($channel = 0; $channel -lt 3; $channel++) {
                $delta = [Math]::Abs([int]$before.Pixels[$atBefore + $channel] - [int]$after.Pixels[$atAfter + $channel])
                $summed += $delta
                if ($delta -gt $worst) { $worst = $delta }
            }
            if ($worst -ge $level) { $changed++ }
            $sum += $summed / 3.0
            $total++
        }
    }

    return [pscustomobject]@{
        Changed        = $changed
        Total          = $total
        Percent        = [Math]::Round($changed / [Math]::Max(1, $total) * 100, 2)
        MeanDelta      = [Math]::Round($sum / [Math]::Max(1, $total), 2)
        CyannessBefore = $before.Cyanness
        CyannessAfter  = $after.Cyanness
    }
}

function Format-HeroDrawing($drawing) {
    if ($null -eq $drawing) { return 'the preview could not be read from the window' }
    return ("{0}x{1}, most accent-like pixel {2:N1}" -f $drawing.Width, $drawing.Height, $drawing.Cyanness)
}

# Nothing the hero shows may sit outside the window: the page had a right-hand clipping bug once, and the
# narrow layout is where it would come back.
function Get-HeroOverflow([int]$tolerance = 2) {
    if ($script:window -eq [IntPtr]::Zero) { return @('the window is not there') }
    $rect = [P3Win]::RectOf($script:window)
    $left = $rect[0]
    $right = $rect[0] + $rect[2]

    $overflow = @()
    foreach ($id in @($heroTitleId, $heroStatusId, $heroDockId, $heroShelfId, $heroPrimaryId, $heroCustomizeId, $heroExitId, $heroPreviewId)) {
        $element = Get-HeroElement $id
        if ($null -eq $element) { continue }
        try { $bounds = $element.Current.BoundingRectangle } catch { continue }
        if ($bounds.IsEmpty) { continue }
        if ($bounds.Right -gt ($right + $tolerance)) {
            $overflow += ("{0} reaches {1:N0}, the window ends at {2:N0}" -f $id, $bounds.Right, $right)
        }
        if ($bounds.Left -lt ($left - $tolerance)) {
            $overflow += ("{0} starts at {1:N0}, the window starts at {2:N0}" -f $id, $bounds.Left, $left)
        }
    }

    return $overflow
}

# ---------------------------------------------------------------- navigating and the settings page

# The shell's own navigation, reached by the item's name. The name is the copy the shell shows, so both
# languages are accepted: which one the app ended up in is the app's own decision, not this run's.
function Open-NavPage([string]$key) {
    $names = Expect $key
    $item = $null
    for ($attempt = 0; $attempt -lt 24; $attempt++) {
        foreach ($name in $names) {
            $item = Find-ByName (Get-AppRoot) $name $null
            if ($null -ne $item) { break }
        }
        if ($null -ne $item) { break }
        Start-Sleep -Milliseconds 250
    }
    if ($null -eq $item) { throw "The navigation item '$key' ($(Format-Expected $names)) was not found." }

    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 1000
}

# The experience picker's own list, in the order the page offers it: the native desktop first, Muralis
# Mode second. Chosen by position rather than by label, so the run works in either language.
function Select-Experience([string]$mode) {
    $index = $(if ($mode -eq 'Muralis') { 1 } else { 0 })
    $combo = Get-HeroElement $settingsModeId
    if ($null -eq $combo) { throw 'The experience picker was not on the settings page.' }

    try { $combo.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView() } catch { }

    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 500

    $items = $combo.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ListItem)))

    if ($items.Count -lt 2) {
        throw ("The experience picker offered {0} option(s)." -f $items.Count)
    }

    $items.Item($index).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 1200
}

# What the picker says is chosen: the selected option's own name, which carries the label the page gave
# it. Read for the report; the page's agreement with the mode is judged by the caption below.
function Get-ExperienceSelection {
    $combo = Get-HeroElement $settingsModeId
    if ($null -eq $combo) { return $null }
    try {
        $selection = $combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
        if ($selection.Count -gt 0) { return (Get-ElementName $selection.Item(0)) }
    } catch { }
    return $null
}

# The Muralis Mode section's caption says the dock is kept up because the mode owns it; it is on the page
# only while the page believes the mode is on, which is the settings page's agreement with the mode.
function Get-SettingsAgreesMuralis {
    $caption = Find-ByName (Get-AppRoot) ([string]$english['Settings_Dock_Required']) $null
    if ($null -eq $caption) {
        $caption = Find-ByName (Get-AppRoot) ([string]$chinese['Settings_Dock_Required']) $null
    }
    return ($null -ne $caption)
}

# ---------------------------------------------------------------- the desktop's own view

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

# A run that dies in the middle must not leave the user's icons hidden, and a run must never force them
# visible over the user's own setting.
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

function Get-DesktopSnapshot {
    $roots = @((Join-Path $env:USERPROFILE 'Desktop'), (Join-Path $env:PUBLIC 'Desktop'))
    $entries = @()
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        foreach ($entry in (Get-ChildItem -LiteralPath $root -Force -ErrorAction SilentlyContinue)) {
            $entries += ("{0}|{1}|{2}|{3}" -f $root, $entry.Name, $(if ($entry.PSIsContainer) { 'dir' } else { $entry.Length }), $entry.LastWriteTimeUtc.ToString('o'))
        }
    }
    return ($entries | Sort-Object)
}

# ---------------------------------------------------------------- the settings document and the log

function Read-Settings {
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        try {
            return (Get-Content -Raw -Encoding UTF8 $script:settingsPath | ConvertFrom-Json)
        } catch {
            Start-Sleep -Milliseconds 150
        }
    }
    throw 'The settings file could not be read.'
}

function Save-Settings($settings) {
    $settings | ConvertTo-Json -Depth 12 | Set-Content -Path $script:settingsPath -Encoding UTF8
}

function Set-ThemeAndLanguage([string]$theme, [string]$language) {
    $settings = Read-Settings
    $settings.Theme = $theme
    $settings.Language = $language
    Save-Settings $settings
}

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

# ---------------------------------------------------------------- the failure lever

# The marker is written through a temporary file next to it, and a directory in that file's place cannot
# be written over: the write fails the way a disk that has stopped answering would, the presentation
# layer fails open, and the mode is reported as not started with the desktop untouched. Nothing about the
# desktop is at risk, and taking the directory away is the whole of the repair.
function Add-FailureLever {
    $directory = Split-Path $cleanMarkerTemp -Parent
    if (-not (Test-Path $directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
    if (Test-Path $cleanMarkerTemp) { Remove-Item -LiteralPath $cleanMarkerTemp -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $cleanMarkerTemp | Out-Null
    $script:leverPlanted = $true
}

function Remove-FailureLever {
    if (-not (Test-Path $cleanMarkerTemp)) { $script:leverPlanted = $false; return }
    if ((Get-Item -LiteralPath $cleanMarkerTemp).PSIsContainer) {
        Remove-Item -LiteralPath $cleanMarkerTemp -Recurse -Force
        Write-Host ("  (the failure lever was taken away: {0})" -f $cleanMarkerTemp)
    } else {
        Remove-Item -LiteralPath $cleanMarkerTemp -Force
    }
    $script:leverPlanted = $false
}

function Park-CleanMarker {
    if (Test-Path $cleanMarkerPath) {
        Copy-Item -LiteralPath $cleanMarkerPath -Destination $cleanMarkerBackup -Force
        $script:markerParked = $true
    }
}

function Restore-CleanMarker {
    if (Test-Path $cleanMarkerPath) { Remove-Item -LiteralPath $cleanMarkerPath -Force }
    if ($script:markerParked -and (Test-Path $cleanMarkerBackup)) {
        Move-Item -LiteralPath $cleanMarkerBackup -Destination $cleanMarkerPath -Force
    }
    $script:markerParked = $false
}

# ---------------------------------------------------------------- the hero's own flow

# The whole entry point, in the order the product puts it: entered on the home page, configured in
# settings, and handed back with the desktop as it was found.
function Invoke-HeroFlow([bool]$withShots) {
    Write-Host ''
    Write-Host 'The home page, reading the native desktop'
    $ready = Expect 'Home_MuralisMode_Ready'
    [void](Wait-HeroText $heroStatusId $ready 20 'the hero to read the desktop it starts on')
    Check-HeroText 'hero: the home page reads the native desktop as ready to enter' $heroStatusId $ready
    Check-HeroText 'hero: the eyebrow names the experience' $heroTitleId (Expect 'Home_MuralisMode_Title')
    Check-HeroText 'hero: the primary action says Enter rather than enable' $heroPrimaryId (Expect 'Home_MuralisMode_Enter')

    $primary = Get-HeroElement $heroPrimaryId
    $enabled = $false
    if ($null -ne $primary) { try { $enabled = $primary.Current.IsEnabled } catch { } }
    Add-Check 'hero: the Enter action is offered while the desktop is native' ($null -ne $primary) $(if ($null -eq $primary) { 'not on the page' } else { 'on the page' })
    Add-Check 'hero: the Enter action can be pressed while the desktop is native' $enabled $(if ($enabled) { 'enabled' } else { 'disabled' })

    Check-HeroAbsent 'hero: nothing offers to leave a mode that is not on' $heroCustomizeId 'the customize action'
    Check-HeroAbsent 'hero: no leave action before the mode is entered' $heroExitId 'the exit action'
    Check-HeroAbsent 'hero: the dock reports no status before the mode is entered' $heroDockId 'a dock status'
    Check-HeroAbsent 'hero: the shelf reports no status before the mode is entered' $heroShelfId 'a shelf status'
    # The page draws the preview once the card has been measured, so the wait is for the drawing rather
    # than for the page to exist: the card is a fixed width and the window it sits in is not.
    $previewDrawn = Wait-Until { $null -ne (Get-HeroElement $heroPreviewId) } 8 'the mini desktop preview to be drawn'
    Add-Check 'hero: the mini desktop preview is drawn in a wide window' $previewDrawn `
        $(if ($previewDrawn) { 'the preview is on the page' } else { 'the preview was not on the page' })
    Add-Check 'hero: nothing the hero shows is past the window edge' ((Get-HeroOverflow).Count -eq 0) ((Get-HeroOverflow) -join '; ')

    # Taken while the card says the desktop is native, to be held up against the same card once the mode
    # is on: the words changing is not the same thing as the drawing changing.
    $nativeDrawing = Get-HeroDrawing 'native'
    Add-Sample 'heroDrawingWhileNative' (Format-HeroDrawing $nativeDrawing)

    if ($withShots) { [void](Add-Shot 'ready' 'native desktop, before the mode is entered') }

    Write-Host ''
    Write-Host 'Entering the mode from the hero'
    $hides = Get-LogCountAll 'Clean Desktop hid Explorer desktop icons'

    # The controls the transition is read through are taken hold of before the press: resolving one during
    # the transition would be the slow search the tight loop exists to avoid.
    $statusElement = Get-HeroElement $heroStatusId
    $primaryElement = Get-HeroElement $heroPrimaryId
    Invoke-Hero $heroPrimaryId 'Enter'

    $watch = Watch-HeroTransition $statusElement (Expect 'Home_MuralisMode_Active') (Expect 'Home_MuralisMode_Entering') 30 $primaryElement
    $entering = Expect 'Home_MuralisMode_Entering'
    $enteringSeen = @($watch.Lines | Where-Object { $entering -contains $_ }).Count -gt 0
    Write-Host ("  the page showed: {0}" -f (($watch.Lines | ForEach-Object { "'{0}'" -f $_ }) -join ' -> '))
    Write-Host ("  the reads: {0}" -f (Format-HeroTimeline $watch))
    Write-Host ("  the desktop answered after {0:N0} ms of watching, over {1} read(s)" -f $watch.ReachedAtMs, $watch.Reads)
    Add-Sample 'heroTransitionLines' (($watch.Lines) -join ' -> ')
    Add-Sample 'heroEnterReads' $watch.Reads
    Add-Sample 'heroEnterReachedMs' $watch.ReachedAtMs
    Add-Sample 'heroEnterTimeline' ($watch.Timeline -join ' | ')
    Add-Sample 'heroEnterActionWasInert' (Test-HeroActionWasInert $watch)
    Add-Check 'hero: the page said the desktop was being prepared before it was ready' $enteringSeen `
        $(if ($enteringSeen) { 'the preparing line was on screen' } else { "the preparing line was not caught; {0} read(s) landed inside the transition and none of them saw it" -f $watch.Reads })
    Add-Check 'hero: the Enter action is inert while the desktop is being rearranged' `
        ((-not $enteringSeen) -or (Test-HeroActionWasInert $watch)) `
        $(if (-not $enteringSeen) { 'not observed' }
            elseif (-not $watch.ButtonWasDisabled) { 'the action was never read while it was busy' }
            elseif (Test-HeroActionWasInert $watch) { 'disabled for the whole time the desktop was being rearranged' }
            else { "the action was pressable {0} ms before the mode was on" -f ($watch.ReachedAtMs - $watch.EnabledWhileBusyAtMs) })

    Check-HeroText 'hero: the page reads the running mode' $heroStatusId (Expect 'Home_MuralisMode_Active')
    Check-HeroText 'hero: the dock is reported as running' $heroDockId (Expect 'Home_MuralisMode_Dock_Active')
    Check-HeroText 'hero: the shelf is reported as ready' $heroShelfId (Expect 'Home_MuralisMode_Shelf_Ready')
    Check-HeroAbsent 'hero: the Enter action is gone once the mode is on' $heroPrimaryId 'the enter action'
    Add-Check 'hero: the customize action is offered once the mode is on' ($null -ne (Get-HeroElement $heroCustomizeId)) 'the customize action'
    Add-Check 'hero: the leave action is offered once the mode is on' ($null -ne (Get-HeroElement $heroExitId)) 'the exit action'
    Add-Check 'hero: the dock window is up while the mode is on' ((Get-DockWindow) -ne [IntPtr]::Zero) `
        $(if ((Get-DockWindow) -eq [IntPtr]::Zero) { 'no dock window was found' } else { 'the dock window is visible' })

    # Explorer's own view is the honest answer to whether the mode is really on.
    $active = Get-DesktopIconState
    Add-Check 'hero: Explorer reports its desktop icons hidden while the mode is on' ($active.Readable -and $active.NoIcons) `
        $(if (-not $active.Readable) { "the desktop view could not be read: {0}" -f $active.Error } else { "flags {0}" -f (Format-Flags $active.Flags) })
    Add-Sample 'heroModeEntered' $true

    if ($withShots) { [void](Add-Shot 'active' 'running mode') }

    # The card is given the length of its own longest token to finish moving before it is read: the drawing
    # is captured mid-swap otherwise, and half of one desktop over half of the other says nothing.
    Start-Sleep -Milliseconds 450
    $runningDrawing = Get-HeroDrawing 'running'
    $drawingMoved = Compare-HeroDrawings $nativeDrawing $runningDrawing
    Add-Sample 'heroDrawingWhileRunning' (Format-HeroDrawing $runningDrawing)
    Add-Sample 'heroDrawingPercentMoved' $(if ($null -ne $drawingMoved) { $drawingMoved.Percent } else { -1 })
    Add-Sample 'heroDrawingMeanDelta' $(if ($null -ne $drawingMoved) { $drawingMoved.MeanDelta } else { -1 })
    Add-Sample 'heroDrawingAccentBefore' $(if ($null -ne $drawingMoved) { $drawingMoved.CyannessBefore } else { -1 })
    Add-Sample 'heroDrawingAccentAfter' $(if ($null -ne $drawingMoved) { $drawingMoved.CyannessAfter } else { -1 })
    if ($null -ne $drawingMoved) {
        Write-Host ("  the preview's own pixels: {0:N2}% of them moved, {1:N2} levels on average; the most accent-like pixel went {2:N1} -> {3:N1}" -f `
            $drawingMoved.Percent, $drawingMoved.MeanDelta, $drawingMoved.CyannessBefore, $drawingMoved.CyannessAfter)
        Write-Host ("  the two drawings this was read from: {0} and {1}" -f $nativeDrawing.Crop, $runningDrawing.Crop)
    } else {
        Write-Host ("  the preview's own pixels: {0}" -f (Format-HeroDrawing $runningDrawing))
    }
    Add-Check 'hero: the card draws a different desktop once the mode is on' (($null -ne $drawingMoved) -and ($drawingMoved.Percent -ge 0.5)) `
        $(if ($null -eq $drawingMoved) { "the preview could not be read from the window while {0}" -f $(if ($null -eq $nativeDrawing) { 'the desktop was native' } else { 'the mode was running' }) }
            elseif ($drawingMoved.Percent -ge 0.5) { "{0:N2}% of the preview's pixels moved with the mode" -f $drawingMoved.Percent }
            else { "the card drew the same preview in both states: {0:N2}% of its pixels moved" -f $drawingMoved.Percent })

    Write-Host ''
    Write-Host 'Customize: the experience is entered here and configured in settings'
    Invoke-Hero $heroCustomizeId 'Customize'
    $onSettings = $false
    for ($attempt = 0; $attempt -lt 24; $attempt++) {
        if ($null -ne (Get-HeroElement $settingsModeId)) { $onSettings = $true; break }
        Start-Sleep -Milliseconds 250
    }
    Add-Check 'customize: the customize action opens the settings page' $onSettings `
        $(if ($onSettings) { 'the experience picker is on the page' } else { 'the settings page never came up' })

    if ($onSettings) {
        Add-Check 'customize: the settings page shows the mode the home page entered' (Get-SettingsAgreesMuralis) `
            $(if (Get-SettingsAgreesMuralis) { 'the page keeps the dock for the mode' } else { 'the page was showing the native desktop' })
        Add-Sample 'settingsSelectionAfterEnter' (Get-ExperienceSelection)
        Write-Host ("  the picker shows: '{0}'" -f (Get-ExperienceSelection))
        if ($withShots) { [void](Add-Shot 'settings-muralis' 'settings page, mode on') }
    }

    Write-Host ''
    Write-Host 'Choosing the native desktop in settings'
    Select-Experience 'Native'
    [void](Wait-Until { -not (Get-SettingsAgreesMuralis) } 20 'the settings page to show the native desktop')
    Add-Check 'settings: choosing the native desktop takes the mode off' (-not (Get-SettingsAgreesMuralis)) `
        $(if (Get-SettingsAgreesMuralis) { 'the page still keeps the dock for the mode' } else { 'the page is back on the native desktop' })
    $back = Get-DesktopIconState
    Add-Check 'settings: Explorer reports its icons back after the mode was left in settings' ($back.Readable -and (-not $back.NoIcons)) `
        $(if (-not $back.Readable) { "the desktop view could not be read: {0}" -f $back.Error } else { "flags {0}" -f (Format-Flags $back.Flags) })

    Write-Host ''
    Write-Host 'The home page following the settings page'
    Open-NavPage 'Nav_Home'
    [void](Wait-HeroText $heroStatusId $ready 20 'the home page to read the native desktop again')
    Check-HeroText 'home: the page followed the settings page back to the native desktop' $heroStatusId $ready
    Add-Check 'home: the Enter action came back with the native desktop' ($null -ne (Get-HeroElement $heroPrimaryId)) 'the enter action'

    Write-Host ''
    Write-Host 'Entering and leaving the mode from the hero'
    $hides = Get-LogCountAll 'Clean Desktop hid Explorer desktop icons'
    $secondStatus = Get-HeroElement $heroStatusId
    $secondPrimary = Get-HeroElement $heroPrimaryId
    Invoke-Hero $heroPrimaryId 'Enter'
    if (-not (Watch-HeroTransition $secondStatus (Expect 'Home_MuralisMode_Active') (Expect 'Home_MuralisMode_Entering') 30 $secondPrimary).Reached) {
        throw 'The mode did not come up the second time.'
    }
    $secondHide = (Get-LogCountAll 'Clean Desktop hid Explorer desktop icons') - $hides
    Add-Check 'hero: the second entry asked the desktop for the mode exactly once' ($secondHide -eq 1) ("{0} activation(s) in the log" -f $secondHide)

    $restores = Get-LogCountAll 'Clean Desktop restored Explorer desktop icons'
    $exitStatus = Get-HeroElement $heroStatusId
    $exitButton = Get-HeroElement $heroExitId
    Invoke-Hero $heroExitId 'Exit'
    $exit = Watch-HeroTransition $exitStatus $ready (Expect 'Home_MuralisMode_Exiting') 30 $exitButton
    $exiting = Expect 'Home_MuralisMode_Exiting'
    $exitingSeen = @($exit.Lines | Where-Object { $exiting -contains $_ }).Count -gt 0
    Write-Host ("  the page showed: {0}" -f (($exit.Lines | ForEach-Object { "'{0}'" -f $_ }) -join ' -> '))
    Write-Host ("  the reads: {0}" -f (Format-HeroTimeline $exit))
    Write-Host ("  the desktop answered after {0:N0} ms of watching, over {1} read(s)" -f $exit.ReachedAtMs, $exit.Reads)
    Add-Sample 'heroExitTransitionLines' (($exit.Lines) -join ' -> ')
    Add-Sample 'heroExitReads' $exit.Reads
    Add-Sample 'heroExitReachedMs' $exit.ReachedAtMs
    Add-Sample 'heroExitTimeline' ($exit.Timeline -join ' | ')
    Add-Sample 'heroExitActionWasInert' (Test-HeroActionWasInert $exit)
    Add-Check 'hero: the page said the desktop was being handed back before it was' $exitingSeen `
        $(if ($exitingSeen) { 'the restoring line was on screen' } else { 'the restoring line was not caught; the desktop was handed back faster than the first read' })
    Add-Check 'hero: the leave action is inert while the desktop is being handed back' `
        ((-not $exitingSeen) -or (Test-HeroActionWasInert $exit)) `
        $(if (-not $exitingSeen) { 'not observed' }
            elseif (-not $exit.ButtonWasDisabled) { 'the action was never read while it was busy' }
            elseif (Test-HeroActionWasInert $exit) { 'disabled for the whole time the desktop was being handed back' }
            else { "the action was pressable {0} ms before the desktop was handed back" -f ($exit.ReachedAtMs - $exit.EnabledWhileBusyAtMs) })

    Check-HeroText 'hero: the page reads the native desktop after the mode was left' $heroStatusId $ready
    $after = Get-DesktopIconState
    Add-Check 'hero: Explorer reports its icons back after the mode was left from the hero' ($after.Readable -and (-not $after.NoIcons)) `
        $(if (-not $after.Readable) { "the desktop view could not be read: {0}" -f $after.Error } else { "flags {0}" -f (Format-Flags $after.Flags) })
    $restored = (Get-LogCountAll 'Clean Desktop restored Explorer desktop icons') - $restores
    Add-Check 'hero: leaving the mode handed the desktop back' ($restored -ge 1) ("{0} restore(s) in the log" -f $restored)
    Add-Check 'hero: the Enter action came back after the mode was left' ($null -ne (Get-HeroElement $heroPrimaryId)) 'the enter action'
    Add-Check 'hero: the leave action went away with the mode' ($null -eq (Get-HeroElement $heroExitId)) 'the exit action'
    [void](Wait-Until { (Get-DockWindow) -eq [IntPtr]::Zero } 20 'the dock to be released')

    Write-Host ''
    Write-Host 'A window too narrow for the preview'
    $narrow = Set-AppWindow $narrowSize[0] $narrowSize[1]
    Write-Host ("  narrow client area: {0}x{1}" -f $narrow.Width, $narrow.Height)
    $putAway = Wait-Until { $null -eq (Get-HeroElement $heroPreviewId) } 8 'the preview to be put away in a narrow window'
    Add-Check 'narrow: the preview is put away rather than squeezed' $putAway `
        $(if ($putAway) { 'the card is too narrow for it' } else { 'still on the page' })
    $narrowOverflow = Get-HeroOverflow
    Add-Check 'narrow: nothing the hero shows is past the window edge' ($narrowOverflow.Count -eq 0) ($narrowOverflow -join '; ')
    Check-HeroText 'narrow: the page still reads the native desktop' $heroStatusId $ready
    Add-Check 'narrow: the Enter action is still there to press' ($null -ne (Get-HeroElement $heroPrimaryId)) 'the enter action'
    if ($withShots) { [void](Add-Shot 'narrow' 'a window too narrow for the preview') }

    $wide = Set-AppWindow $wideSize[0] $wideSize[1]
    Write-Host ("  wide client area: {0}x{1}" -f $wide.Width, $wide.Height)
    $returned = Wait-Until { $null -ne (Get-HeroElement $heroPreviewId) } 8 'the preview to come back with the room for it'
    Add-Check 'wide: the preview comes back with the room for it' $returned $(if ($returned) { 'the preview' } else { 'the preview did not come back' })
}

# The attempt that cannot take, and the retry that can. The desktop is never rearranged, so the checks
# are about what the page says and about what was not touched.
function Invoke-FailureStage2 {
    Write-Host ''
    Write-Host 'An attempt that cannot take'
    $ready = Expect 'Home_MuralisMode_Ready'
    [void](Wait-HeroText $heroStatusId $ready 20 'the hero to read the native desktop first')

    $before = Get-DesktopIconState
    $desktopBefore = Get-DesktopSnapshot
    $failures = Get-LogCountAll 'The Clean Desktop recovery marker could not be written'

    Add-FailureLever
    Add-Check 'failure: the lever is in the place the marker is written through' (Test-Path $cleanMarkerTemp) $cleanMarkerTemp

    Invoke-Hero $heroPrimaryId 'Enter'

    $error = Expect 'Home_MuralisMode_Error'
    $reported = Wait-HeroText $heroStatusId $error 30 'the page to report that the mode did not start'
    Add-Check 'failure: the page reports that the mode did not start' $reported `
        $(if ($reported) { 'the failing line is on screen' } else { "the page showed '{0}'" -f (Get-HeroText $heroStatusId) })
    Check-HeroText 'failure: the reason is offered as a retry rather than as an entry' $heroPrimaryId (Expect 'Home_MuralisMode_TryAgain')
    Check-HeroAbsent 'failure: nothing offers to leave a mode that was never entered' $heroExitId 'the exit action'
    Check-HeroAbsent 'failure: nothing offers to configure a mode that was never entered' $heroCustomizeId 'the customize action'
    Check-HeroAbsent 'failure: the page does not claim the dock is running' $heroDockId 'a dock status'
    Check-HeroAbsent 'failure: the page does not claim the shelf is ready' $heroShelfId 'a shelf status'

    # The reason the desktop gave is on screen beside the translated line, rather than swallowed.
    $reason = $null
    foreach ($name in (Get-TextElementNames (Get-AppRoot) 60)) {
        if ($name -match 'marker could not be written') { $reason = $name; break }
    }
    Add-Check 'failure: the reason the desktop itself gave is on screen' ($null -ne $reason) `
        $(if ($null -ne $reason) { "'{0}'" -f $reason } else { 'the reason was not on the page' })
    Add-Sample 'failureReasonShown' $(if ($null -eq $reason) { '' } else { $reason })

    $after = Get-DesktopIconState
    Add-Check 'failure: Explorer reports its icons untouched by the attempt' `
        ($before.Readable -and $after.Readable -and ($after.NoIcons -eq $before.NoIcons)) `
        $(if (-not $after.Readable) { "the desktop view could not be read: {0}" -f $after.Error } else { "flags {0}" -f (Format-Flags $after.Flags) })
    Add-Check 'failure: the dock was not left on the desktop by the attempt' ((Get-DockWindow) -eq [IntPtr]::Zero) `
        $(if ((Get-DockWindow) -eq [IntPtr]::Zero) { 'no dock window' } else { 'the dock window is up' })
    $logged = (Get-LogCountAll 'The Clean Desktop recovery marker could not be written') - $failures
    Add-Check 'failure: the app wrote down why the mode could not start' ($logged -ge 1) ("{0} line(s) in the log" -f $logged)
    Add-Check 'failure: the desktop documents were not touched by the attempt' `
        (@(Compare-Object $desktopBefore (Get-DesktopSnapshot)).Count -eq 0) 'the same files, sizes and times'
    [void](Add-Shot 'failed' 'an attempt that could not take')

    Write-Host ''
    Write-Host 'Trying again once the reason is gone'
    Remove-FailureLever
    Invoke-Hero $heroPrimaryId 'Try Again'
    $active = Expect 'Home_MuralisMode_Active'
    $came = Wait-HeroText $heroStatusId $active 30 'the mode to come up on the retry'
    Add-Check 'failure: the retry brings the mode up' $came $(if ($came) { 'the running line is on screen' } else { "the page showed '{0}'" -f (Get-HeroText $heroStatusId) })
    $retried = Get-DesktopIconState
    Add-Check 'failure: Explorer reports its icons hidden after the retry' ($retried.Readable -and $retried.NoIcons) `
        $(if (-not $retried.Readable) { "the desktop view could not be read: {0}" -f $retried.Error } else { "flags {0}" -f (Format-Flags $retried.Flags) })
    [void](Add-Shot 'retried' 'the mode after a retry')

    Invoke-Hero $heroExitId 'Exit'
    [void](Wait-HeroText $heroStatusId $ready 30 'the desktop to be handed back after the retry')
    Check-HeroText 'failure: leaving the mode after the retry hands the desktop back' $heroStatusId $ready
    $final = Get-DesktopIconState
    Add-Check 'failure: Explorer reports its icons back at the end' ($final.Readable -and (-not $final.NoIcons)) `
        $(if (-not $final.Readable) { "the desktop view could not be read: {0}" -f $final.Error } else { "flags {0}" -f (Format-Flags $final.Flags) })
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
    Write-Host ("  wide window:        {0}x{1}" -f $wideSize[0], $wideSize[1])
    Write-Host ("  narrow window:      {0}x{1}  (the preview is collapsed below 1100)" -f $narrowSize[0], $narrowSize[1])

    Add-Check 'probe: the executable the run will start is there' (Test-Path $exePath) $exePath
    Add-Check 'probe: no Muralis is running, so the run owns the instance it judges' (-not (Get-AppProcesses)) (Describe-AppProcesses)
    Add-Check 'probe: the single-instance mutex is free' (Test-SingleInstanceFree) $mutexName

    # The copy the hero shows is read from the catalogs, so a run is only meaningful when both of them
    # carry every line the hero can show.
    foreach ($key in $heroKeys) {
        Add-Check ("probe: '{0}' is written in both languages" -f $key) `
            (-not [string]::IsNullOrWhiteSpace([string]$english[$key]) -and -not [string]::IsNullOrWhiteSpace([string]$chinese[$key])) `
            ("en: '{0}'  zh: '{1}'" -f [string]$english[$key], [string]$chinese[$key])
    }

    # A lever left behind by a run that died would make every later attempt fail for a reason that is the
    # harness's, so it must not be there before one is planted.
    Add-Check 'probe: the failure lever is not left over from an earlier run' (-not (Test-Path $cleanMarkerTemp)) $cleanMarkerTemp

    $icons = Get-DesktopIconState
    Add-Check 'probe: Explorer answers for its own desktop icons' $icons.Readable `
        $(if ($icons.Readable) { "flags {0}" -f (Format-Flags $icons.Flags) } else { "unreadable: {0}" -f $icons.Error })
}

# ---------------------------------------------------------------- matrix

# Dark and Light, English and Chinese, each with the picture of it: the one judgement no automation can
# make is whether the flagship card looks right, and it has to be made in both themes and both languages.
function Invoke-MatrixStage {
    $seen = @()
    foreach ($theme in @('Dark', 'Light')) {
        foreach ($language in @('en-US', 'zh-CN')) {
            Write-Host ''
            Write-Host ("=== {0} / {1} ===" -f $theme, $language)
            Set-ThemeAndLanguage $theme $language
            $script:theme = $theme
            $script:language = $language
            $script:shotPrefix = ("{0}-{1}" -f $theme.ToLowerInvariant(), $language)

            $app = Launch-App
            Add-Sample ("matrix{0}{1}Launch" -f $theme, $language.Replace('-', '')) $app.LaunchMs
            [void](Set-AppWindow $wideSize[0] $wideSize[1])

            $ready = ExpectIn 'Home_MuralisMode_Ready'
            $title = ExpectIn 'Home_MuralisMode_Title'
            [void](Wait-HeroText $heroStatusId $ready 20 'the hero to read the native desktop')
            Check-HeroText ("{0}/{1}: the ready line is the one the catalog holds" -f $theme, $language) $heroStatusId $ready
            Check-HeroText ("{0}/{1}: the eyebrow is the one the catalog holds" -f $theme, $language) $heroTitleId $title
            [void](Add-Shot 'ready' ("{0} {1}, before the mode is entered" -f $theme, $language))

            Invoke-Hero $heroPrimaryId 'Enter'
            $active = ExpectIn 'Home_MuralisMode_Active'
            $came = Wait-HeroText $heroStatusId $active 30 'the mode to come up'
            Add-Check ("{0}/{1}: the mode comes up" -f $theme, $language) $came `
                $(if ($came) { 'the running line is on screen' } else { "the page showed '{0}'" -f (Get-HeroText $heroStatusId) })
            if ($came) {
                Check-HeroText ("{0}/{1}: the dock status is the one the catalog holds" -f $theme, $language) $heroDockId (ExpectIn 'Home_MuralisMode_Dock_Active')
                [void](Add-Shot 'active' ("{0} {1}, running" -f $theme, $language))
                Invoke-Hero $heroExitId 'Exit'
                [void](Wait-HeroText $heroStatusId $ready 30 'the desktop to be handed back')
            }

            $seen += ("{0}/{1}" -f $theme, $language)
            Close-App
        }
    }

    Add-Sample 'matrixCovered' ($seen -join ', ')
}

# ---------------------------------------------------------------- run

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
    Park-CleanMarker

    if ($Stage -ne 'probe') {
        Backup-Settings

        # Closing the window has to end the app: with close-to-tray on it only hides, and the wait after
        # the close reads as an app that will not exit. The user's own file is put back at the end.
        Enable-CanvasInSettings
    }

    # The stages that change what the app is set to need the file put back, and the ones that drive the
    # desktop need the icon flags and the recovery marker put back whatever happens.
    if ($Stage -eq 'hero') {
        try {
            Launch-App
            [void](Set-AppWindow $wideSize[0] $wideSize[1])
            Invoke-HeroFlow $true
        } finally {
            Invoke-CleanupStep 'closing the app' { Close-App }
        }
        $stageFinished = $true
    } elseif ($Stage -eq 'failure') {
        try {
            Launch-App
            [void](Set-AppWindow $wideSize[0] $wideSize[1])
            Invoke-FailureStage2
        } finally {
            Invoke-CleanupStep 'closing the app' { Close-App }
        }
        $stageFinished = $true
    } elseif ($Stage -eq 'matrix') {
        Invoke-MatrixStage
        $stageFinished = $true
    } elseif ($Stage -eq 'full') {
        try {
            Launch-App
            [void](Set-AppWindow $wideSize[0] $wideSize[1])
            Invoke-HeroFlow $true
            Invoke-FailureStage2
        } finally {
            Invoke-CleanupStep 'closing the app' { Close-App }
        }
        $stageFinished = $true
    } else {
        Invoke-ProbeStage
        $stageFinished = $true
    }
} catch {
    $stageError = $_.Exception.Message
    Write-Host ''
    Write-Host ("The stage stopped early: {0}" -f $stageError)
    if ($_.InvocationInfo) { Write-Host ("  at {0}" -f $_.InvocationInfo.PositionMessage.Trim()) }
} finally {
    Write-Host ''
    Write-Host 'Putting the machine back the way it was found ...'
    Invoke-CleanupStep 'closing the app' { Close-App }
    Invoke-CleanupStep 'taking the failure lever away' { Remove-FailureLever }
    Invoke-CleanupStep 'putting the desktop icon flags back' { Restore-DesktopFlags $baselineIcons }
    Invoke-CleanupStep 'putting the recovery marker back' { Restore-CleanMarker }
    Invoke-CleanupStep 'restoring the settings and layout files' { Restore-Everything }

    Add-Check 'harness: the failure lever is not left behind' (-not (Test-Path $cleanMarkerTemp)) $cleanMarkerTemp
    Add-Check 'harness: no Muralis is left running' (-not (Get-AppProcesses)) (Describe-AppProcesses)
    Add-Check 'harness: the stage ran to its end' $stageFinished `
        $(if ($stageFinished) { "stage '{0}'" -f $Stage } else { "stage '{0}' stopped early: {1}" -f $Stage, $stageError })

    $failed = Write-CheckReport $Stage $outPath 'p5-hero-verify.json'
    if ($stageFinished -and $failed -gt 0) { exit 1 }
}
