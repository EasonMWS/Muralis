<#
.SYNOPSIS
    Phase 3A live verification: the desktop pointer router, driven on a real desktop.

.DESCRIPTION
    The harness moves the real pointer with SendInput (which produces the same raw input reports a
    physical mouse does) and reads the canvas diagnostics panel through UI Automation to see what
    the desktop actually did. It checks the Phase 3A contract:

      - a fast sweep across the row: hover magnification is continuous, never drops to nothing
      - the same sweep 120 DIP above the row, outside the window region: the pointer is read at all
      - leaving the canvas: hover decays to rest, the dock stays retracted
      - the dock trigger band: the rail expands, and retracts after the pointer leaves
      - ordinary application windows and the taskbar: the desktop stops reacting entirely
      - hit testing: the grown item box receives a drag, outside the region nothing is delivered
      - 30 s of continuous fast movement: no anomaly, bounded dispatch rate, CPU and GPU cost
      - Explorer restart: the router rides it out untouched and the canvas re-mounts
      - a graceful exit: the raw input registration and the router window are released

    The geometry every expectation below is computed from is planted as a schema 2 document before
    the app starts (see New-SeedLayoutItems): the four free items at the Phase 2 seed's offsets and
    the four dock items. Everything the harness changes is restored on the way out: settings.json,
    the user's own desktop layout document, the Phase 2 prototype file and the running app.

.PARAMETER Stage
    probe   - no app launch, only reports the machine state and what sits under the probe points
    latency - how fresh a UI Automation read of the diagnostics panel is
    full    - the whole matrix

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/p3a-pointer-verify.ps1 -Stage probe
    powershell -ExecutionPolicy Bypass -File tools/p3a-pointer-verify.ps1 -Stage full
#>
[CmdletBinding()]
param(
    [ValidateSet('probe', 'latency', 'full')] [string]$Stage = 'probe',
    [string]$Exe = 'src/Muralis.App/bin/Debug/net10.0-windows10.0.26100.0/win-x64/Muralis.exe',
    [int]$HoldSeconds = 30,
    [switch]$SkipExplorerRestart,
    [string]$OutDir = 'artifacts/p3a'
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'p3-common.ps1')
Set-BackupPaths 'p3a'

$repoRoot = Split-Path $PSScriptRoot -Parent
$exePath = if ([System.IO.Path]::IsPathRooted($Exe)) { $Exe } else { Join-Path $repoRoot $Exe }
$outPath = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repoRoot $OutDir }

# The canvas, as the seed layout puts it on the 2560x1440 display at 1x this harness was verified
# against: four 96 DIP items, centre anchor, 130 DIP apart, 220 DIP above the middle.
$itemCentresX = @(1085, 1215, 1345, 1475)
$rowY = 500
$offRowY = 380  # 120 DIP above the centres: inside the influence radius, outside the region
$offRowScale = 1.21
$dockProbe = @(5, 720)
$taskbarProbe = @(1280, 1435)
$blankProbes = @(@(1700, 900), @(700, 1200))
$growProbe = @(1280, 500)     # the blender item's grown box covers this point
$outsideProbe = @(1615, 500)  # right of the region, which ends at 1475 + 78.8

# The band the pointer tests sweep and the single points they also rely on: the canvas row, the
# dock strip, and two blank corners.
$clearPointsX = @(1015, 1075, 1135, 1195, 1255, 1315, 1375, 1435, 1495, 1555, 1615)
$clearPointsY = @(380, 500)
$clearExtraPoints = @(@(1215, 300), @(48, 620), @(1700, 900), @(700, 1200))

$logPatterns = [ordered]@{
    UiIdle          = 'ui idle'
    CanvasShowing   = 'The desktop canvas is showing'
    LayoutLoaded    = 'Desktop layout loaded from'
    RouterListening = 'The desktop pointer router is listening'
    RouterReleased  = 'released its raw mouse input registration'
    DockExpanded    = 'The desktop dock expanded'
    DockRetracted   = 'The desktop dock retracted'
    ItemDropped     = ' was dropped at'
    ItemSelected    = ' was selected'
    SurfaceBack     = 'The desktop surface is back on the desktop'
}

# ---------------------------------------------------------------- the planted document

# The harness plants its own desktop instead of relying on a product seed: the four free items sit
# exactly where the Phase 2 seed put them, so the four item centres, the grown box and the dock
# trigger band below are the geometry this file was verified against. Addresses stand in for the
# prototype's tiles — a target that cannot go missing and is never opened keeps the run free of
# anything a launch could start, and the glyph tiles are drawn exactly as they always were.
function New-SeedLayoutItems {
    return @(
        (New-LayoutItem -Id 'seed_free_1' -Name 'Steam'    -Kind 'url' -Path 'https://seed1.example/' -IconKey 'steam'    -OffsetX (-195) -OffsetY (-220) -Z 0)
        (New-LayoutItem -Id 'seed_free_2' -Name 'Chrome'   -Kind 'url' -Path 'https://seed2.example/' -IconKey 'chrome'   -OffsetX (-65)  -OffsetY (-220) -Z 1)
        (New-LayoutItem -Id 'seed_free_3' -Name 'Blender'  -Kind 'url' -Path 'https://seed3.example/' -IconKey 'blender'  -OffsetX 65    -OffsetY (-220) -Z 2)
        (New-LayoutItem -Id 'seed_free_4' -Name 'ComfyUI'  -Kind 'url' -Path 'https://seed4.example/' -IconKey 'comfyui'  -OffsetX 195   -OffsetY (-220) -Z 3)
        (New-LayoutItem -Id 'seed_dock_1' -Name 'Files'    -Kind 'url' -Path 'https://seed-dock1.example/' -IconKey 'files'    -Placement 'Dock' -Z 0)
        (New-LayoutItem -Id 'seed_dock_2' -Name 'Music'    -Kind 'url' -Path 'https://seed-dock2.example/' -IconKey 'music'    -Placement 'Dock' -Z 1)
        (New-LayoutItem -Id 'seed_dock_3' -Name 'Settings' -Kind 'url' -Path 'https://seed-dock3.example/' -IconKey 'settings' -Placement 'Dock' -Z 2)
        (New-LayoutItem -Id 'seed_dock_4' -Name 'Terminal' -Kind 'url' -Path 'https://seed-dock4.example/' -IconKey 'terminal' -Placement 'Dock' -Z 3)
    )
}

function Write-SeedLayout {
    $items = New-SeedLayoutItems
    Write-Layout $items
    Write-Host ("planted {0} items in {1} (4 free at the Phase 2 seed offsets, 4 in the dock)" -f $items.Count, $script:layoutPath)
}

# ---------------------------------------------------------------- probe

function Invoke-Probe {
    Write-Host '=== Phase 3A probe ==='
    $running = Get-Process -Name Muralis -ErrorAction SilentlyContinue
    Write-Host ("Muralis running: {0}" -f [bool]$running)
    Write-Host ("Screen: {0}x{1} at {2},{3}" -f `
        [P3Win]::GetSystemMetrics(0), [P3Win]::GetSystemMetrics(1),
        [P3Win]::GetSystemMetrics([P3Win]::SM_XVIRTUALSCREEN), [P3Win]::GetSystemMetrics([P3Win]::SM_YVIRTUALSCREEN))

    if (Test-Path $settingsPath) {
        $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
        $canvasProperty = $settings.PSObject.Properties['DesktopCanvas']
        if ($null -eq $canvasProperty) { Write-Host 'settings.json: no DesktopCanvas key' }
        else { Write-Host ("settings.json: DesktopCanvas.Enabled = {0}" -f $canvasProperty.Value.Enabled) }
        Write-Host ("settings.json: CloseToTray = {0}" -f $settings.CloseToTray)
    }
    Write-Host ("desktop layout:  {0} (present: {1})" -f $layoutPath, (Test-Path $layoutPath))
    if (Test-Path $layoutPath) {
        $layout = Read-Layout
        Write-Host ("  schema {0}, kind '{1}', {2} item(s)" -f $layout.SchemaVersion, $layout.Kind, @($layout.Items).Count)
    }
    Write-Host ("prototype file:  {0} (present: {1})" -f $prototypePath, (Test-Path $prototypePath))

    $probes = @(
        @{ Name = 'row item centre'; X = 1215; Y = 500 },
        @{ Name = 'off-row (outside region)'; X = 1215; Y = 380 },
        @{ Name = 'grown box (inside region)'; X = 1280; Y = 500 },
        @{ Name = 'right of the region'; X = 1615; Y = 500 },
        @{ Name = 'dock trigger'; X = 5; Y = 720 },
        @{ Name = 'taskbar'; X = 1280; Y = 1435 },
        @{ Name = 'blank 1'; X = 1700; Y = 900 },
        @{ Name = 'blank 2'; X = 700; Y = 1200 }
    )

    Write-Host ''
    Write-Host 'Point probes (WindowFromPoint) :'
    foreach ($probe in $probes) {
        $point = New-Object P3Win+POINT
        $point.X = $probe.X
        $point.Y = $probe.Y
        $hit = [P3Win]::WindowFromPoint($point)
        $root = [P3Win]::RootOf($hit)
        $processName = '?'
        $processId = [P3Win]::ProcessOf($root)
        if ($processId -gt 0) {
            try { $processName = (Get-Process -Id $processId).ProcessName } catch { $processName = '?' }
        }
        Write-Host ("  {0,-28} ({1},{2}) -> {3} | root {4} ({5}, pid {6})" -f `
            $probe.Name, $probe.X, $probe.Y, [P3Win]::Describe($hit), [P3Win]::ClassOf($root), $processName, $processId)
    }

    Write-Host ''
    Write-Host 'Visible windows over the canvas area (x 980..1580, y 360..900) and the dock strip (x 0..140, y 540..900):'
    $areas = @(
        @{ Name = 'canvas row'; Left = 980; Top = 360; Right = 1580; Bottom = 900 },
        @{ Name = 'dock strip'; Left = 0; Top = 540; Right = 140; Bottom = 900 }
    )
    foreach ($window in [P3Win]::VisibleTopLevel(60, 40)) {
        $rect = [P3Win]::RectOf($window)
        foreach ($area in $areas) {
            $overlaps = ($rect[0] -lt $area.Right) -and (($rect[0] + $rect[2]) -gt $area.Left) -and
                        ($rect[1] -lt $area.Bottom) -and (($rect[1] + $rect[3]) -gt $area.Top)
            if ($overlaps) {
                $processId = [P3Win]::ProcessOf($window)
                $processName = '?'
                try { $processName = (Get-Process -Id $processId).ProcessName } catch { $processName = '?' }
                Write-Host ("  [{0}] {1} at {2},{3} {4}x{5} ({6}, pid {7}) | {8}" -f `
                    $area.Name, [P3Win]::ClassOf($window), $rect[0], $rect[1], $rect[2], $rect[3], $processName, $processId, [P3Win]::TitleOf($window))
            }
        }
    }

    Write-Host ''
    Write-Host ("Router windows now: {0}" -f (([P3Win]::ClassesWithPrefix('MuralisPointerRouter_')).Count))
    Write-Host ("Canvas host windows now: {0}" -f (([P3Win]::ClassesWithPrefix('MuralisDesktopHostWindow')).Count))
}

# ---------------------------------------------------------------- latency stage

# Moves the pointer to one point and watches how long the diagnostics text takes to reflect it,
# printing every distinct text with its timestamp. Answers the one question the checks depend on:
# how fresh is a UI Automation read of the panel, and does a minimised window update it at all.
function Watch-Point([int]$x, [int]$y, [int]$timeoutSeconds) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    Move-Pointer $x $y
    $cursor = Get-CursorNow
    $under = Get-DesktopPoint $x $y
    Write-Host ("  -> ({0},{1}); cursor now ({2},{3}); under: {4} | root {5}" -f `
        $x, $y, $cursor[0], $cursor[1], [P3Win]::Describe($under.Hit), $under.Class)
    $lastText = ''
    $changes = 0
    $settledAt = -1
    while ($watch.Elapsed.TotalSeconds -lt $timeoutSeconds) {
        $text = Read-Diag
        if ($text -ne $lastText) {
            $state = Parse-Diag $text
            $cursor = Get-CursorNow
            Write-Host ("     +{0,5} ms  px={1} py={2} hover={3}@{4} dock={5} ctx={6} cursor=({7},{8}) reps={9} disps={10} upd={11}" -f `
                [int]$watch.Elapsed.TotalMilliseconds, $state.Px, $state.Py, $state.HoverId, $state.HoverScale,
                $state.DockPhase, $state.Context, $cursor[0], $cursor[1], $state.Reports, $state.Dispatches, $state.Updates)
            $changes++
            $lastText = $text
            if ($settledAt -lt 0 -and $null -ne $state.Px -and [math]::Abs($state.Px - $x) -le 1.5) {
                $settledAt = [int]$watch.Elapsed.TotalMilliseconds
            }
        }
        Start-Sleep -Milliseconds 100
    }
    Write-Host ("     text changes: {0}; pointer seen at the target after {1}" -f $changes, `
        $(if ($settledAt -ge 0) { "$settledAt ms" } else { 'never' }))
}

function Invoke-LatencyStage {
    if (Get-Process -Name Muralis -ErrorAction SilentlyContinue) { throw 'Muralis is already running; stop it first.' }
    if (-not (Test-Path $exePath)) { throw "Executable not found: $exePath" }

    Backup-Settings
    Enable-CanvasInSettings
    Park-LayoutFiles
    Write-SeedLayout

    Write-Host '=== Phase 3A diagnostics latency probe ==='
    $script:process = Start-Process -FilePath $exePath -PassThru
    $processId = [int]$script:process.Id
    [void](Wait-Until { [P3Win]::FindWindowByClass($processId, 'WinUIDesktopWin32WindowClass') -ne [IntPtr]::Zero } 60 'the main window')
    [void](Wait-Until { ([P3Win]::ClassesWithPrefix('MuralisPointerRouter_')).Count -ge 1 } 60 'the router attach')
    Start-Sleep -Milliseconds 900
    Open-DynamicPage
    Minimize-AppWindow
    $blocked = Clear-TheDesktop $clearPointsX $clearPointsY ' (latency stage)' $clearExtraPoints
    if ($blocked.Count -gt 0) { Write-Host ("  (still covered: {0})" -f ($blocked -join '; ')) }

    Write-Host ''
    Write-Host '--- window minimised ---'
    Watch-Point 1215 500 6
    Watch-Point 1215 380 6
    Watch-Point 1475 500 6
    Watch-Point 700 1200 6

    Write-Host ''
    Write-Host '--- window visible in the corner ---'
    Show-AppWindow 1750 900 780 500
    Watch-Point 1215 500 6
    Watch-Point 1215 440 6
    Watch-Point 1215 380 6
    Watch-Point 1215 300 6
    Watch-Point 1475 500 6
    Watch-Point 700 1200 6
}

# ---------------------------------------------------------------- full run

function Invoke-Full {
    if (Get-Process -Name Muralis -ErrorAction SilentlyContinue) { throw 'Muralis is already running; stop it first.' }
    if (-not (Test-Path $exePath)) { throw "Executable not found: $exePath" }
    New-Item -ItemType Directory -Force -Path $outPath | Out-Null

    # --- prepare: back up settings, enable the canvas, park any saved document so the planted seed
    #     geometry (which every expectation below is computed from) is what actually shows.
    Backup-Settings
    Enable-CanvasInSettings
    Park-LayoutFiles
    Write-SeedLayout

    Write-Host ("Launching {0}" -f $exePath)
    $logBase = Get-LogCounts $logPatterns
    $script:process = Start-Process -FilePath $exePath -PassThru
    $processId = [int]$script:process.Id

    # Direct observables rather than log lines: the on-disk log lags while the app runs.
    $windowUp = Wait-Until { [P3Win]::FindWindowByClass($processId, 'WinUIDesktopWin32WindowClass') -ne [IntPtr]::Zero } 60 'the main window'
    $canvasUp = Wait-Until { ([P3Win]::ClassesWithPrefix('MuralisDesktopHostWindow')).Count -ge 1 } 60 'the canvas mount'
    $routerUp = Wait-Until { ([P3Win]::ClassesWithPrefix('MuralisPointerRouter_')).Count -ge 1 } 60 'the router attach'
    Add-Check 'startup: the main window appears' $windowUp ''
    Add-Check 'startup: the canvas mounts' $canvasUp ''
    Add-Check 'startup: the router attaches' $routerUp ''
    Add-Sample 'exe' $exePath

    # Never minimise before the first frame: the app crashes if the window goes away that early.
    Start-Sleep -Milliseconds 900
    Open-DynamicPage
    Minimize-AppWindow

    $routerWindows = [P3Win]::ClassesWithPrefix('MuralisPointerRouter_')
    Add-Check 'one router window for the whole desktop' ($routerWindows.Count -eq 1) ("count {0}" -f $routerWindows.Count)

    # --- the planted geometry only means anything on the display it was computed for
    $geometry = Read-State
    $expectedDisplay = ($geometry.MonitorW -eq 2560) -and ($geometry.MonitorH -eq 1440) -and
        ($geometry.MonitorX -eq 0) -and ($geometry.MonitorY -eq 0) -and ($geometry.ScaleFactor -eq 1.0)
    Add-Check 'preflight: the display is the one these expectations were computed for' $expectedDisplay `
        ("{0}x{1} at {2},{3} {4}x" -f $geometry.MonitorW, $geometry.MonitorH, $geometry.MonitorX, $geometry.MonitorY, $geometry.ScaleFactor)
    if (-not $expectedDisplay) { throw 'The display is not 2560x1440 at 1x with origin 0,0; the probe points in this file do not describe it.' }

    $itemsOnCanvas = Wait-Diag { param($s) $s.ItemCount -eq 8 } 30 'the eight planted items to mount'
    Add-Check 'startup: the planted document is on the desktop' ($null -ne $itemsOnCanvas) ("items {0}" -f $itemsOnCanvas.ItemCount)
    Add-Check 'startup: no planted item is marked missing' ($null -ne $itemsOnCanvas -and $itemsOnCanvas.MissingCount -eq 0) ("missing {0}" -f $itemsOnCanvas.MissingCount)

    # --- the desktop has to be reachable before any pointer check means anything
    $blocked = Clear-TheDesktop $clearPointsX $clearPointsY '' $clearExtraPoints
    Add-Check 'preflight: the canvas area is on the desktop, not under an application' ($blocked.Count -eq 0) ("blocked points: {0}" -f ($blocked -join '; '))
    if ($blocked.Count -gt 0) { throw "The desktop is still covered: $($blocked -join '; ')" }

    # --- preflight: the desktop must be visible where the canvas sits. The *root* class counts:
    # WindowFromPoint over the desktop lands on the icon list view (SysListView32), whose root is
    # the desktop window.
    $desktopHit = Get-DesktopPoint $itemCentresX[1] $offRowY
    $clear = $desktopHit.Class -in @('SHELLDLL_DefView', 'WorkerW', 'Progman')
    Add-Check 'preflight: the off-row probe point is desktop, not a window' $clear ("root class '{0}'" -f $desktopHit.Class)
    if (-not $clear) { throw "Something covers the canvas (root class '$($desktopHit.Class)'); nothing can be swept." }

    $state = Read-State
    Write-Host ''
    Write-Host '--- diagnostics baseline ---'
    Write-Host $state.Text
    Write-Host ''

    $sweep = @()

    # --- 1: sweep along the row, inside the region
    Write-Host 'Test 1: sweep along the row (y=500, inside the window region)'
    # The first sample has no predecessor in the sweep: continuity is between consecutive samples.
    $previousScale = $null
    $maxJump = 0.0
    $minScale = 9.0
    $rowOk = $true
    for ($x = 1015; $x -le 1555; $x += 20) {
        Move-Pointer $x $rowY
        $state = Read-StateAt $x $rowY
        $scale = 1.0
        if ($null -ne $state.HoverScale) { $scale = $state.HoverScale }
        if ($null -ne $previousScale) {
            $jump = [math]::Abs($scale - $previousScale)
            if ($jump -gt $maxJump) { $maxJump = $jump }
        }
        if ($scale -lt $minScale) { $minScale = $scale }
        if (-not $state.Inside) { $rowOk = $false }
        $sweep += [pscustomobject]@{ Phase = 'row'; X = $x; Y = $rowY; Scale = $scale; Item = $state.HoverId; Context = $state.Context; Inside = $state.Inside }
        $previousScale = $scale
    }
    Add-Check 'row sweep: the canvas sees the pointer at every step' $rowOk ''
    Add-Check 'row sweep: hover never falls back to rest' ($minScale -gt 1.05) ("min scale {0:0.00}" -f $minScale)
    Add-Check 'row sweep: scale stays continuous (step jump <= 0.12)' ($maxJump -le 0.12) ("max jump {0:0.000}" -f $maxJump)

    # --- 2: sweep 120 DIP above the row: outside the window region, inside the influence radius
    Write-Host 'Test 2: sweep above the row (y=380, outside the window region)'
    $offRowScales = @()
    $offRowContexts = @()
    for ($x = 1015; $x -le 1555; $x += 20) {
        Move-Pointer $x $offRowY
        $state = Read-StateAt $x $offRowY
        $scale = 0.0
        if ($null -ne $state.HoverScale) { $scale = $state.HoverScale }
        $offRowScales += $scale
        $offRowContexts += $state.Context
        $sweep += [pscustomobject]@{ Phase = 'offrow'; X = $x; Y = $offRowY; Scale = $scale; Item = $state.HoverId; Context = $state.Context; Inside = $state.Inside }
    }
    $offRowMax = ($offRowScales | Measure-Object -Maximum).Maximum
    $overCentre = $offRowScales[[int](($itemCentresX[1] - 1015) / 20)]
    $desktopContexts = ($offRowContexts | Where-Object { $_ -eq 'Desktop' }).Count
    Add-Check 'off-row sweep: the pointer is read beyond the region' ($desktopContexts -gt 0) ("$desktopContexts of $($offRowContexts.Count) samples report the desktop context")
    Add-Check 'off-row sweep: items still magnify (max 1.21x expected)' ($offRowMax -gt 1.10 -and $offRowMax -lt 1.35) ("max scale {0:0.00}" -f $offRowMax)
    Add-Check 'off-row sweep: value above the item centre is the expected 1.21x' ([math]::Abs($overCentre - $offRowScale) -le 0.06) ("{0:0.00} vs 1.21" -f $overCentre)

    Add-Sample 'sweep' $sweep

    # --- 3: decay back to rest
    Write-Host 'Test 3: decay, pointer leaves the canvas'
    $atRest = { param($s) ($null -eq $s.HoverId -or $s.HoverId -eq 'none') -and ($null -eq $s.HoverScale -or $s.HoverScale -le 1.001) }
    $decayScales = @()
    foreach ($blank in $blankProbes) {
        Move-Pointer $blank[0] $blank[1]
        $state = Read-StateUntil $atRest 2500
        $decayScales += [pscustomobject]@{ X = $blank[0]; Y = $blank[1]; Item = $state.HoverId; Scale = $state.HoverScale; Dock = $state.DockPhase }
    }
    # and on the way out of the row itself: straight up, 200 DIP above the item centres, one
    # influence radius away from the row, so a correct decay ends with nothing hovered
    Move-Pointer 1215 300
    $state = Read-StateUntil $atRest 2500
    $decayScales += [pscustomobject]@{ X = 1215; Y = 300; Item = $state.HoverId; Scale = $state.HoverScale; Dock = $state.DockPhase }
    $allRest = $true
    foreach ($sample in $decayScales) {
        if ($sample.Item -ne 'none' -or $null -eq $sample.Scale -or $sample.Scale -gt 1.001) { $allRest = $false }
    }
    $last = $decayScales[$decayScales.Count - 1]
    Add-Check 'decay: everything returns to rest away from the canvas' $allRest ("last hovered {0} at {1:0.00}x" -f $last.Item, $last.Scale)
    Add-Sample 'decay' $decayScales

    # --- 4: dock trigger band. The log lines "The desktop dock expanded/retracted" are checked
    #     after the exit, when the log has been flushed; here the diagnostics phase is the evidence.
    Write-Host 'Test 4: dock trigger band and auto-hide'
    Move-Pointer $dockProbe[0] $dockProbe[1]
    $state = Read-StateUntil { param($s) $s.DockPhase -eq 'Shown' } 2500
    $dockShown = ($state.DockPhase -eq 'Shown')
    Add-Check 'dock: the trigger band summons the rail' $dockShown ("phase '{0}'" -f $state.DockPhase)

    Move-Pointer 700 720
    $state = Read-StateUntil { param($s) $s.DockPhase -eq 'Collapsed' } 3000
    Add-Check 'dock: the rail retracts after the pointer leaves' ($state.DockPhase -eq 'Collapsed') ("phase '{0}'" -f $state.DockPhase)

    # --- 5: an ordinary application window and the taskbar stop the desktop
    Write-Host 'Test 5: ordinary application windows'
    Move-Pointer $itemCentresX[1] $offRowY
    $state = Read-StateUntil { param($s) $s.Context -eq 'Desktop' -and $s.HoverScale -gt 1.1 } 2500
    $beforeHover = $state.HoverId
    Add-Check 'foreign: the desktop magnifies before the window comes up' ($null -ne $beforeHover -and $beforeHover -ne 'none' -and $state.HoverScale -gt 1.1) ("hovered {0} at {1:0.00}x" -f $state.HoverId, $state.HoverScale)

    [P3Win]::ShowWindow($script:window, [P3Win]::SW_RESTORE) | Out-Null
    [P3Win]::SetForegroundWindow($script:window) | Out-Null
    Start-Sleep -Milliseconds 700
    $rect = [P3Win]::RectOf($script:window)
    Write-Host ("  main window at {0},{1} {2}x{3}" -f $rect[0], $rect[1], $rect[2], $rect[3])
    $foreignProbe = @($itemCentresX[1], $rowY)
    if ($rect[0] -gt $foreignProbe[0] -or ($rect[0] + $rect[2]) -lt $foreignProbe[0] -or
        $rect[1] -gt $foreignProbe[1] -or ($rect[1] + $rect[3]) -lt $foreignProbe[1]) {
        $foreignProbe = @(($rect[0] + [int]($rect[2] / 2)), ($rect[1] + 40))
        Write-Host ("  (the window does not cover the item; probing its own area at {0},{1})" -f $foreignProbe[0], $foreignProbe[1])
    }
    Move-Pointer $foreignProbe[0] $foreignProbe[1]
    $state = Read-StateUntil { param($s) $s.Context -eq 'Foreign' -and ($null -eq $s.HoverId -or $s.HoverId -eq 'none') } 2500
    $context = $state.Context
    $hoverId = $state.HoverId
    $hoverScale = $state.HoverScale
    Add-Check 'foreign: an application window puts the router out of desktop context' ($context -eq 'Foreign') ("context '$context'")
    Add-Check 'foreign: the canvas stops reacting under an application window' (($null -eq $hoverId -or $hoverId -eq 'none') -and ($null -eq $hoverScale -or $hoverScale -le 1.001)) ("hovered {0} at {1:0.00}x" -f $hoverId, $hoverScale)

    $point = New-Object P3Win+POINT
    $point.X = $foreignProbe[0]; $point.Y = $foreignProbe[1]
    $under = [P3Win]::ClassOf([P3Win]::RootOf([P3Win]::WindowFromPoint($point)))
    Add-Check 'foreign: the probe point really is over the application window' ($under -eq 'WinUIDesktopWin32WindowClass') ("root class '$under'")

    [P3Win]::ShowWindow($script:window, [P3Win]::SW_MINIMIZE) | Out-Null
    Start-Sleep -Milliseconds 500

    # the taskbar hangs off the desktop window but is not the desktop
    Move-Pointer $itemCentresX[1] $offRowY
    $state = Read-StateUntil { param($s) $s.Context -eq 'Desktop' -and $s.HoverScale -gt 1.1 } 2500
    $restored = ($state.Context -eq 'Desktop' -and $state.HoverScale -gt 1.1)
    Add-Check 'foreign: the desktop reacts again once the pointer returns' $restored ("context '{0}', hovered {1} at {2:0.00}x" -f $state.Context, $state.HoverId, $state.HoverScale)

    Move-Pointer $taskbarProbe[0] $taskbarProbe[1]
    $state = Read-StateUntil { param($s) $s.Context -eq 'Foreign' } 2500
    Add-Check 'foreign: the taskbar is not the desktop' ($state.Context -eq 'Foreign') ("context '{0}'" -f $state.Context)
    Add-Check 'foreign: the canvas clears over the taskbar' (($null -eq $state.HoverId -or $state.HoverId -eq 'none') -and $state.HoverScale -le 1.001) ("hovered {0} at {1:0.00}x" -f $state.HoverId, $state.HoverScale)

    # --- 6: hit testing against the visual scale. Evidence here is file system observable: a drag
    #     inside the region commits the drop and rewrites the layout document; a press outside the
    #     region cannot reach the canvas at all. The log lines are checked after the exit.
    Write-Host 'Test 6: hit testing, region and the grown item box'
    $outsideHit = Get-DesktopPoint $outsideProbe[0] $outsideProbe[1]
    Add-Check 'hit test: outside the region the desktop is under the point' ($outsideHit.Class -in @('SHELLDLL_DefView', 'WorkerW', 'Progman')) ("root class '{0}' at {1},{2}" -f $outsideHit.Class, $outsideProbe[0], $outsideProbe[1])

    $hashBeforeDrag = (Get-FileHash -Algorithm SHA256 $script:layoutPath).Hash
    Move-And-Settle $growProbe[0] $growProbe[1] 450
    [P3Win]::LeftDown()
    Start-Sleep -Milliseconds 40
    for ($step = 1; $step -le 8; $step++) {
        Move-Pointer ($growProbe[0] + $step * 6) $growProbe[1]
        Start-Sleep -Milliseconds 15
    }
    Start-Sleep -Milliseconds 60
    [P3Win]::LeftUp()
    Start-Sleep -Milliseconds 500
    $hashAfterDrag = (Get-FileHash -Algorithm SHA256 $script:layoutPath).Hash
    Add-Check 'hit test: the grown box receives the drag (the drop rewrites the layout)' ($hashAfterDrag -ne $hashBeforeDrag) ("{0} -> {1}" -f $hashBeforeDrag.Substring(0, 8), $hashAfterDrag.Substring(0, 8))

    # A press outside the region is not delivered to the canvas: no click, no drag, no commit.
    Move-And-Settle $outsideProbe[0] $outsideProbe[1] 120
    Click-Pointer
    Start-Sleep -Milliseconds 400
    $hashAfterOutside = (Get-FileHash -Algorithm SHA256 $script:layoutPath).Hash
    Add-Check 'hit test: a press outside the region changes nothing' ($hashAfterOutside -eq $hashAfterDrag) ''

    # --- 7: 30 s of continuous fast movement
    Write-Host ("Test 7: {0}s of continuous fast movement" -f $HoldSeconds)
    $points = @(
        @(1005, 500), @(1215, 500), @(1475, 500), @(1555, 500), @(1215, 380), @(1085, 380),
        @(215, 380), @(1215, 500), @(1300, 460), @(700, 1200), @(1280, 1435), @(5, 720),
        @(48, 620), @(1700, 900), @(1215, 500)
    )
    $cpuStart = $script:process.TotalProcessorTime
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $gpuValues = @()
    $rateValues = @()
    $reportsSeen = @()
    $dispatchSeen = @()
    $index = 0
    $gpuTick = 0
    $nextSampleAt = 2.5
    while ($watch.Elapsed.TotalSeconds -lt $HoldSeconds) {
        $target = $points[$index % $points.Count]
        $index++
        Move-Pointer $target[0] $target[1]
        Start-Sleep -Milliseconds 4
        # Read on a wall-clock cadence: the diagnostics panel itself writes every 250 ms, so a
        # reading taken faster than that would only repeat the previous sample.
        if ($watch.Elapsed.TotalSeconds -ge $nextSampleAt) {
            $nextSampleAt += 2.5
            $state = Read-State
            if ($null -ne $state.Rate) { $rateValues += $state.Rate }
            if ($null -ne $state.Reports) { $reportsSeen += $state.Reports }
            if ($null -ne $state.Dispatches) { $dispatchSeen += $state.Dispatches }
            $gpuTick++
            if (($gpuTick % 5) -eq 1) {
                $gpu = Measure-GpuOnce $script:process.Id
                if ($null -ne $gpu) { $gpuValues += $gpu }
            }
        }
    }
    $watch.Stop()
    $finalSample = Read-State
    if ($null -ne $finalSample.Reports) { $reportsSeen += $finalSample.Reports }
    if ($null -ne $finalSample.Dispatches) { $dispatchSeen += $finalSample.Dispatches }
    $cpuUsed = ($script:process.TotalProcessorTime - $cpuStart).TotalMilliseconds
    $moves = $index
    $cpuCount = [Environment]::ProcessorCount

    $state = Read-State
    $alive = -not $script:process.HasExited
    Add-Check 'flood: the app is alive after the movement' $alive ''
    Add-Check 'flood: still one mount, one router window' ((($state.Mount) -eq 1) -and (([P3Win]::ClassesWithPrefix('MuralisPointerRouter_')).Count -eq 1)) ("mount {0}" -f $state.Mount)
    $maxRate = 0.0
    if ($rateValues.Count -gt 0) { $maxRate = ($rateValues | Measure-Object -Maximum).Maximum }
    $avgRate = 0.0
    if ($rateValues.Count -gt 0) { $avgRate = ($rateValues | Measure-Object -Average).Average }
    Add-Check 'flood: the dispatch rate stays under the coalescing cap (<=170/s)' ($maxRate -le 170) ("avg {0:0.0}/s, max {1:0.0}/s" -f $avgRate, $maxRate)
    $reportGrowth = 0
    if ($reportsSeen.Count -ge 2) { $reportGrowth = $reportsSeen[$reportsSeen.Count - 1] - $reportsSeen[0] }
    $dispatchGrowth = 0
    if ($dispatchSeen.Count -ge 2) { $dispatchGrowth = $dispatchSeen[$dispatchSeen.Count - 1] - $dispatchSeen[0] }
    Add-Check 'flood: reports arrive and are coalesced' ($reportGrowth -gt 0 -and $dispatchGrowth -gt 0 -and $dispatchGrowth -le $reportGrowth) ("{0} reports -> {1} dispatches in the window" -f $reportGrowth, $dispatchGrowth)

    $cpuPerSecond = $cpuUsed / $watch.Elapsed.TotalSeconds
    $cpuPercent = $cpuPerSecond / 1000 / $cpuCount * 100
    $gpuMax = 0
    if ($gpuValues.Count -gt 0) { $gpuMax = ($gpuValues | Measure-Object -Maximum).Maximum }
    $movesPerSecond = $moves / $watch.Elapsed.TotalSeconds
    Write-Host ("  movement: {0} moves over {1:0.0}s ({2:0.0} moves/s)" -f $moves, $watch.Elapsed.TotalSeconds, $movesPerSecond)
    Write-Host ("  CPU during movement: {0:0.0} ms total, {1:0.00} ms/s, {2:0.000} % of one core ({3} cores)" -f $cpuUsed, $cpuPerSecond, $cpuPercent, $cpuCount)
    Write-Host ("  GPU during movement: max {0:0.0} %" -f $gpuMax)
    Add-Sample 'flood' ([pscustomobject]@{
        Seconds = $watch.Elapsed.TotalSeconds
        Moves = $moves
        MovesPerSecond = $movesPerSecond
        CpuMs = $cpuUsed
        CpuMsPerSecond = $cpuPerSecond
        CpuPercentOfOneCore = $cpuPercent
        GpuMaxPercent = $gpuMax
        DispatchRateAverage = $avgRate
        DispatchRateMax = $maxRate
        Reports = $reportGrowth
        Dispatches = $dispatchGrowth
    })

    # --- 8: idle: nothing moves, nothing happens
    Write-Host 'Test 8: idle while the pointer rests on the desktop'
    Move-Pointer 1215 380
    $state = Read-StateAt 1215 380
    $reportsBefore = $state.Reports
    $cpuStart = $script:process.TotalProcessorTime
    $idleWatch = [System.Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds 5
    $idleWatch.Stop()
    $cpuIdle = ($script:process.TotalProcessorTime - $cpuStart).TotalMilliseconds
    $state = Read-State
    $reportsGrew = $state.Reports - $reportsBefore
    Add-Check 'idle: no reports while the pointer rests' ($reportsGrew -eq 0) ("{0} reports in 5 s" -f $reportsGrew)
    Write-Host ("  CPU while idle, diagnostics panel open: {0:0.0} ms over {1:0.0}s" -f $cpuIdle, $idleWatch.Elapsed.TotalSeconds)
    Add-Sample 'idle' ([pscustomobject]@{
        Seconds = $idleWatch.Elapsed.TotalSeconds
        CpuMs = $cpuIdle
        CpuMsPerSecond = $cpuIdle / $idleWatch.Elapsed.TotalSeconds
        Reports = $reportsGrew
        DispatchesPerSecond = $state.Rate
        UpdatesTotal = $state.Updates
    })

    # and again with the panel closed: that is the app at rest with no development tooling on top
    try {
        $script:diagExpander.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
        Start-Sleep -Milliseconds 800
        $cpuStart = $script:process.TotalProcessorTime
        $quietWatch = [System.Diagnostics.Stopwatch]::StartNew()
        Start-Sleep -Seconds 5
        $quietWatch.Stop()
        $cpuQuiet = ($script:process.TotalProcessorTime - $cpuStart).TotalMilliseconds
        Write-Host ("  CPU while idle, diagnostics panel closed: {0:0.0} ms over {1:0.0}s" -f $cpuQuiet, $quietWatch.Elapsed.TotalSeconds)
        Add-Sample 'idleQuiet' ([pscustomobject]@{
            Seconds = $quietWatch.Elapsed.TotalSeconds
            CpuMs = $cpuQuiet
            CpuMsPerSecond = $cpuQuiet / $quietWatch.Elapsed.TotalSeconds
        })
        $script:diagExpander.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Start-Sleep -Milliseconds 700
    } catch {
        Write-Host ("  (the panel could not be collapsed for the quiet reading: {0})" -f $_)
    }

    # --- 9: the coalescer never drops the last position
    Write-Host 'Test 9: a burst of reports always lands on its final position'
    $matches = 0
    $repetitions = 40
    $misses = @()
    for ($round = 1; $round -le $repetitions; $round++) {
        $targetX = 1215 + (($round * 37) % 400)
        for ($burst = 0; $burst -lt 40; $burst++) {
            $x = 1005 + (($targetX - 1005) * $burst / 40)
            Move-Pointer $x 380
        }
        Move-Pointer $targetX 380
        $state = Read-StateAt $targetX 380
        if ($null -ne $state.Px -and [math]::Abs($state.Px - $targetX) -le 1.5) { $matches++ }
        else { $misses += ("round {0}: wanted x {1}, the panel shows {2}" -f $round, $targetX, $state.Px) }
    }
    $missDetail = ''
    if ($misses.Count -gt 0) { $missDetail = '; ' + ($misses -join '; ') }
    Add-Check 'coalescing: the final position of a burst is never lost' ($matches -eq $repetitions) ("$matches of $repetitions rounds$missDetail")

    # --- 10: Explorer restart. The shell thread outlives Explorer, so the router must ride the
    #     restart out untouched: the same router window, still exactly one, no re-attach.
    if (-not $SkipExplorerRestart) {
        Write-Host 'Test 10: Explorer restart'
        $mountBefore = (Read-State).Mount
        $routerClassBefore = [P3Win]::ClassesWithPrefix('MuralisPointerRouter_')
        Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue

        $back = $false
        $deadline = (Get-Date).AddSeconds(45)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
            $probe = Get-DesktopPoint 1215 380
            if ($probe.Class -in @('SHELLDLL_DefView', 'WorkerW', 'Progman')) {
                $state = Read-State
                if ($null -ne $state.Mount -and $state.Mount -gt $mountBefore) { $back = $true; break }
            }
        }
        $mountAfter = (Read-State).Mount
        Add-Check 'explorer: the shell comes back and the canvas re-mounts' $back ("mount {0} -> {1}" -f $mountBefore, $mountAfter)

        Start-Sleep -Seconds 1
        $routerClassAfter = [P3Win]::ClassesWithPrefix('MuralisPointerRouter_')
        $sameRouter = ($routerClassAfter.Count -eq 1) -and ($routerClassBefore.Count -eq 1) -and ($routerClassAfter[0] -eq $routerClassBefore[0])
        Add-Check 'explorer: the router is untouched (one window, same window)' $sameRouter `
            ("before '{0}' -> after '{1}'" -f ($routerClassBefore -join ','), ($routerClassAfter -join ','))

        Move-Pointer 1215 380
        $state = Read-StateUntil { param($s) $s.Context -eq 'Desktop' -and $s.HoverScale -gt 1.1 } 3000
        Add-Check 'explorer: hover works again through the router' ($state.HoverScale -gt 1.1) ("hovered {0} at {1:0.00}x" -f $state.HoverId, $state.HoverScale)
    } else {
        Write-Host 'Test 10: Explorer restart skipped'
    }

    # --- 11: exit releases everything
    Write-Host 'Test 11: graceful exit releases the raw input registration'
    $window = [P3Win]::FindWindowByClass([int]$script:process.Id, 'WinUIDesktopWin32WindowClass')
    if ($window -eq [IntPtr]::Zero) { $window = $script:window }
    [P3Win]::PostMessage($window, [P3Win]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    $exited = $script:process.WaitForExit(15000)
    if (-not $exited) {
        Stop-Process -Id $script:process.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 800
    }
    Add-Check 'exit: the app closes on the window close' $exited ''
    $released = Wait-LogCountGrew 'released its raw mouse input registration' $logBase['RouterReleased'] 8 'the release log line'
    Start-Sleep -Milliseconds 700
    $routerWindows = [P3Win]::ClassesWithPrefix('MuralisPointerRouter_')
    $hostWindows = [P3Win]::ClassesWithPrefix('MuralisDesktopHostWindow')
    Add-Check 'exit: the raw input registration is released' $released ''
    Add-Check 'exit: no router window is left behind' ($routerWindows.Count -eq 0) ("count {0}" -f $routerWindows.Count)
    Add-Check 'exit: no canvas window is left behind' ($hostWindows.Count -eq 0) ("count {0}" -f $hostWindows.Count)

    # --- 12: the run's story, read back from the now-flushed log
    Write-Host 'Test 12: the log after the exit (Serilog flushes on shutdown)'
    $logAfter = Get-LogCounts $logPatterns
    Add-Sample 'logs' ([pscustomobject]@{ Base = $logBase; After = $logAfter })
    Add-Check 'log: the first frame settled in this run' ($logAfter.UiIdle -gt $logBase['UiIdle']) ("{0} -> {1}" -f $logBase['UiIdle'], $logAfter.UiIdle)
    Add-Check 'log: the canvas mounted in this run' ($logAfter.CanvasShowing -gt $logBase['CanvasShowing']) ("{0} -> {1}" -f $logBase['CanvasShowing'], $logAfter.CanvasShowing)
    Add-Check 'log: the router attached exactly once' ($logAfter.RouterListening -eq ($logBase['RouterListening'] + 1)) ("{0} -> {1}" -f $logBase['RouterListening'], $logAfter.RouterListening)
    Add-Check 'log: the router released exactly once, at the exit' ($logAfter.RouterReleased -eq ($logBase['RouterReleased'] + 1)) ("{0} -> {1}" -f $logBase['RouterReleased'], $logAfter.RouterReleased)
    Add-Check 'log: the dock expanded and retracted' (($logAfter.DockExpanded -gt $logBase['DockExpanded']) -and ($logAfter.DockRetracted -gt $logBase['DockRetracted'])) `
        ("expanded {0}->{1}, retracted {2}->{3}" -f $logBase['DockExpanded'], $logAfter.DockExpanded, $logBase['DockRetracted'], $logAfter.DockRetracted)
    Add-Check 'log: exactly one drop reached the canvas, and nothing was selected or launched' `
        ((($logAfter.ItemDropped -eq ($logBase['ItemDropped'] + 1)) -and ($logAfter.ItemSelected -eq $logBase['ItemSelected']))) `
        ("dropped {0}->{1}, selected {2}->{3}" -f $logBase['ItemDropped'], $logAfter.ItemDropped, $logBase['ItemSelected'], $logAfter.ItemSelected)
    if (-not $SkipExplorerRestart) {
        Add-Check 'log: the shell put the content back' ($logAfter.SurfaceBack -gt $logBase['SurfaceBack']) ("{0} -> {1}" -f $logBase['SurfaceBack'], $logAfter.SurfaceBack)
    }

    Write-CheckReport 'full' $outPath 'p3a-verify.json' | Out-Null
}

# ---------------------------------------------------------------- entry

trap {
    Write-Host ''
    Write-Host ("HARNESS ERROR: {0}" -f $_)
    Write-Host $_.ScriptStackTrace
    try { Restore-Everything } catch { Write-Host ("restore failed: {0}" -f $_) }
    exit 1
}

try {
    if ($Stage -eq 'probe') {
        Invoke-Probe
    } elseif ($Stage -eq 'latency') {
        Invoke-LatencyStage
    } else {
        Invoke-Full
    }
} finally {
    if ($Stage -ne 'probe') {
        Restore-Everything
        Write-Host 'Done; settings and layout restored, app stopped.'
    }
}
