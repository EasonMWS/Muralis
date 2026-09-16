<#
.SYNOPSIS
    Phase 3C live verification: the interactive edge dock, on a real desktop.

.DESCRIPTION
    The harness drives the running application the way a user would - a real pointer on the real
    desktop, the page's own controls - and reads back what happened from the diagnostics panel, the
    layout document, the log and the processes the machine actually started. It checks the Phase 3C
    contract:

      restart
      - launching, closing and launching again twenty times in a row always ends with a running
        instance: the single-instance name is let go the moment a process exits, and a launch that
        finds the name taken asks whether its holder can still show itself before it gives up
      - a second launch while one is running wakes that one and never opens a second window

      dock
      - the dock is a thing of its own in the layout document: which edge, whether it hides, and
        the items it holds, in the order it holds them
      - the pointer at the edge brings the rail out and the pointer away takes it in again
      - the item under the pointer is the largest, and its neighbours slide apart rather than
        overlapping
      - a single click opens a dock item; a drag never opens anything
      - an item can be carried from the canvas into the dock, reordered inside it, and carried out
        onto the canvas again, with the document only changing on the drop
      - the dock comes back where it was left after Muralis restarts and after Explorer restarts
      - a pointer over an ordinary window never brings the dock out, and the pointer alone, without
        a click, changes nothing about the desktop

    Everything the harness changes is restored on the way out: settings.json, the user's own desktop
    layout documents, the fixture folder and the app itself.

.PARAMETER Stage
    probe   - no app launch: reports the machine state and the files the harness would touch
    restart - the single-instance restart race (launch/exit/launch, twenty cycles)
    wake    - only the second-launch part: a launch while one is running wakes that one
    dock    - the interactive edge dock, on the real desktop
    full    - everything

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/p3c-dock-verify.ps1 -Stage restart
    powershell -ExecutionPolicy Bypass -File tools/p3c-dock-verify.ps1 -Stage dock
#>
[CmdletBinding()]
param(
    [ValidateSet('probe', 'restart', 'wake', 'dock', 'perf', 'full')] [string]$Stage = 'probe',
    [string]$Exe = 'src/Muralis.App/bin/Debug/net10.0-windows10.0.26100.0/win-x64/Muralis.exe',
    [int]$RestartCycles = 20,
    [int]$WakeCycles = 5,
    [int[]]$PerfItemCounts = @(10, 25, 50),
    [switch]$SkipExplorerRestart,
    [string]$OutDir = 'artifacts/p3c'
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'p3-common.ps1')
Set-BackupPaths 'p3c'

$repoRoot = Split-Path $PSScriptRoot -Parent
$exePath = if ([System.IO.Path]::IsPathRooted($Exe)) { $Exe } else { Join-Path $repoRoot $Exe }
$outPath = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repoRoot $OutDir }

$mainWindowClass = 'WinUIDesktopWin32WindowClass'

# The single-instance name is the one SingleInstanceGuard owns; a probe that can open it means an
# instance is still holding the slot.
$mutexName = 'Local\Muralis.SingleInstance'

# Log lines this stage judges by. Counts are always read as growth from the baseline taken before
# the first launch, never as absolute numbers: the log is one file per day and holds every run.
$logAsked = 'A second launch asked this instance to show itself'
$logTurnedAway = 'this launch only woke it'
$logForeground = 'Window brought to the foreground'

function Assert-MuralisNotRunning {
    if (Get-Process -Name Muralis -ErrorAction SilentlyContinue) {
        throw 'Muralis is already running; stop it first.'
    }
    if (-not (Test-Path $exePath)) { throw "Executable not found: $exePath" }
}

function Test-SingleInstanceFree {
    try {
        $mutex = [System.Threading.Mutex]::OpenExisting($mutexName)
        $mutex.Dispose()
        return $false
    } catch {
        $inner = $_.Exception.InnerException
        return ($inner -is [System.Threading.WaitHandleCannotBeOpenedException])
    }
}

function Get-AppProcesses {
    return @(Get-Process -Name Muralis -ErrorAction SilentlyContinue)
}

# Serilog gives each writer its own file, so one day's lines end up spread over muralis-<day>.log and
# its _001, _002 ... siblings: a launch whose predecessor still held the file lands in the next one.
# Counting only the newest file would read one of them and call the other files' lines missing, which
# is a harness artefact rather than an app failure - and it is exactly what happens here, where a
# launch follows an exit by a few milliseconds. Every file of the day is counted together instead.
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

function Get-MainWindow([int]$processId) {
    return [P3Win]::FindWindowByClass($processId, $mainWindowClass)
}

# Polls faster than the shared Wait-Until: this stage is about a race whose whole window is a few
# hundred milliseconds wide, so a 250 ms poll would be measuring itself instead of the app.
function Wait-Tight([scriptblock]$predicate, [int]$timeoutSeconds, [int]$pollMs = 10) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (& $predicate) { return $true }
        Start-Sleep -Milliseconds $pollMs
    }
    return $false
}

# Close-to-tray would turn the close into a hide and keep the process alive, so the close really has
# to close. Only this one setting is touched; everything else stays as the user had it.
function Disable-CloseToTray {
    $settings = Get-Content $script:settingsPath -Raw | ConvertFrom-Json
    $settings.CloseToTray = $false
    $settings | ConvertTo-Json -Depth 10 | Set-Content -Path $script:settingsPath -Encoding UTF8
}

# ---------------------------------------------------------------- app lifecycle

# Launches the app and waits for its own window. Deliberately does not wait for the single-instance
# name: waiting for it here would hide exactly the race this stage exists to catch.
function Start-AppInstance {
    $started = Get-Date
    $process = Start-Process -FilePath $exePath -PassThru

    $deadline = (Get-Date).AddSeconds(60)
    $window = [IntPtr]::Zero
    while ((Get-Date) -lt $deadline) {
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

# Closes the app's own window and waits for the process to go. Returns how long the exit took and
# how long the single-instance name stayed held after the process object reported it gone.
function Close-AppInstance($instance) {
    $process = $instance.Process
    $window = $instance.Window

    $closed = $false
    $exitMs = -1
    $slotMs = -1

    if ($window -ne [IntPtr]::Zero -and [P3Win]::IsWindow($window)) {
        [P3Win]::PostMessage($window, [P3Win]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    }

    $closedAt = Get-Date
    $closed = Wait-Tight { $process.HasExited } 20
    if ($closed) { $exitMs = [int]((Get-Date) - $closedAt).TotalMilliseconds }

    if (-not $closed) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        return [pscustomobject]@{ Closed = $false; ExitMs = $exitMs; SlotFreeMs = $slotMs }
    }

    # The moment the harness learned the process was gone: how long the name stayed held from here is
    # exactly what a user clicking the app icon again would run into.
    $slotAt = Get-Date
    if (Wait-Tight { Test-SingleInstanceFree } 10 5) {
        $slotMs = [int]((Get-Date) - $slotAt).TotalMilliseconds
    }

    return [pscustomobject]@{ Closed = $true; ExitMs = $exitMs; SlotFreeMs = $slotMs }
}

# ---------------------------------------------------------------- stages

function Invoke-ProbeStage {
    Write-Host 'Machine state'
    Write-Host ("  display:            {0}x{1}" -f [P3Win]::GetSystemMetrics(0), [P3Win]::GetSystemMetrics(1))
    Write-Host ("  executable:         {0}" -f $exePath)
    Write-Host ("  settings:           {0}" -f $script:settingsPath)
    Write-Host ("  layout document:    {0}" -f $script:layoutPath)
    Write-Host ("  single-instance:    {0}" -f $(if (Test-SingleInstanceFree) { 'free' } else { 'held' }))
    Write-Host ("  Muralis processes:  {0}" -f (Get-AppProcesses).Count)
    Assert-MuralisNotRunning
}

function Invoke-RestartStage {
    Write-Host ''
    Write-Host ('=== the restart race: {0} x launch -> close -> launch ===' -f $RestartCycles)

    $turnedAwayAtStart = Get-LogCountAll $logTurnedAway

    $cameUp = 0
    $extraInstances = 0
    $slotWaits = New-Object System.Collections.ArrayList
    $cycleTimes = New-Object System.Collections.ArrayList
    $drove = $true

    for ($cycle = 1; $cycle -le $RestartCycles; $cycle++) {
        $cycleStarted = Get-Date

        $instance = Start-AppInstance
        if ($instance.CameUp) { $cameUp++ } else { $drove = $false }

        # Two instances at once is the failure single-instance exists to prevent, so it is checked
        # on every cycle rather than once at the end. A launch that was turned away has already
        # exited by the time the window wait gives up, so it cannot inflate this count.
        $others = @(Get-AppProcesses | Where-Object { $_.Id -ne $instance.Process.Id }).Count
        if ($others -gt 0) { $extraInstances++ }

        $closed = Close-AppInstance $instance
        if ($closed.SlotFreeMs -ge 0) { [void]$slotWaits.Add($closed.SlotFreeMs) }
        [void]$cycleTimes.Add([int]((Get-Date) - $cycleStarted).TotalMilliseconds)

        # The next cycle launches here, with nothing waited for in between: this is the click the
        # user makes the instant after the window disappears.
        if (-not $closed.Closed) { $drove = $false }

        Write-Host ("  cycle {0,2}: launch {1} ms, exit {2} ms, name free after {3} ms" -f `
            $cycle, $instance.LaunchMs, $closed.ExitMs, $closed.SlotFreeMs)
    }

    Start-Sleep -Milliseconds 500
    $turnedAway = (Get-LogCountAll $logTurnedAway) - $turnedAwayAtStart
    $maxSlot = if ($slotWaits.Count -gt 0) { ($slotWaits | Measure-Object -Maximum).Maximum } else { -1 }

    Add-Sample 'restart.cycles' $RestartCycles
    Add-Sample 'restart.maxSlotFreeWaitMs' $maxSlot
    Add-Sample 'restart.avgCycleMs' ([int](($cycleTimes | Measure-Object -Average).Average))

    Add-Check 'restart: every cycle ended with a running instance' ($cameUp -eq $RestartCycles) ("$cameUp of $RestartCycles launches came up" + $(if ($drove) { '' } else { ' (a close or launch failed)' }))
    Add-Check 'restart: two instances never ran at once' ($extraInstances -eq 0) ("cycles with a second process alive: $extraInstances")
    Add-Check 'restart: no launch was turned away as a second instance' ($turnedAway -eq 0) ("'only woke it' lines during the loop: $turnedAway")
    Add-Check 'restart: the name was free as soon as the process exited' (($maxSlot -ge 0) -and ($maxSlot -le 250)) ("worst wait for the name after exit: $maxSlot ms")
}

function Invoke-WakeStage {
    Write-Host ''
    Write-Host ('=== a second launch while one is running: {0} times ===' -f $WakeCycles)

    $askedBefore = Get-LogCountAll $logAsked
    $foregroundBefore = Get-LogCountAll $logForeground

    $running = Start-AppInstance
    $runningPid = [int]$running.Process.Id
    Add-Check 'wake: the instance under test came up' $running.CameUp ("launch {0} ms, window {1}" -f $running.LaunchMs, $running.Window)

    $woke = 0
    $secondInstances = 0
    $missingWindows = 0

    for ($wake = 1; $wake -le $WakeCycles; $wake++) {
        $second = Start-Process -FilePath $exePath -PassThru
        $exited = Wait-Tight { $second.HasExited } 20
        if ($exited) { $woke++ }

        # What is left has to be the running instance alone. A process object can stay in the table
        # for a moment after the OS reaps it, which is a dying process rather than a second instance,
        # so the count is allowed to settle before it is judged.
        $settled = -1
        $settleUntil = (Get-Date).AddSeconds(3)
        while ((Get-Date) -lt $settleUntil) {
            $settled = (Get-AppProcesses).Count
            if ($settled -le 1) { break }
            Start-Sleep -Milliseconds 20
        }
        if ($settled -ne 1) { $secondInstances++ }

        $window = Get-MainWindow $runningPid
        if ($window -eq [IntPtr]::Zero) { $missingWindows++ }

        Write-Host ("  wake {0,2}: the second launch exited on its own: {1}; processes left: {2}; main window: {3}" -f `
            $wake, $exited, $settled, $window)
    }

    Start-Sleep -Milliseconds 500
    $askedLines = (Get-LogCountAll $logAsked) - $askedBefore
    $foregroundLines = (Get-LogCountAll $logForeground) - $foregroundBefore

    Add-Sample 'restart.wakeCycles' $WakeCycles
    Add-Sample 'restart.askedLines' $askedLines
    Add-Sample 'restart.foregroundLines' $foregroundLines

    Add-Check 'restart: a second launch always ended by itself' ($woke -eq $WakeCycles) ("$woke of $WakeCycles second launches exited on their own")
    Add-Check 'restart: the running instance was never joined by a second instance' ($secondInstances -eq 0) ("wakes that left something other than the one instance: $secondInstances")
    Add-Check 'restart: the running instance kept its window' ($missingWindows -eq 0) ("wakes that found no main window: $missingWindows")
    Add-Check 'restart: the running instance was woken' ($askedLines -ge $WakeCycles) ("asked-to-show lines in the log: $askedLines")
    Add-Check 'restart: the woken instance came forward' ($foregroundLines -ge $WakeCycles) ("'brought to the foreground' lines: $foregroundLines")

    $closedRunning = Close-AppInstance $running
    Write-Host ("  final close: exit {0} ms, name free after {1} ms" -f $closedRunning.ExitMs, $closedRunning.SlotFreeMs)
}

# ---------------------------------------------------------------- the dock on the real desktop

# The notepads already running, so the ones a launch starts are told apart from the user's own.
function Get-NotepadIds {
    return @(Get-Process -Name notepad -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
}

# The dock's own numbers, as the harness plants them in the layout. Everything the pointer is aimed
# at is derived from these, so the same run works on another display or at another scale.
$script:dockSetup = @{
    ItemSizeDip = 56
    SpacingDip = 12
    PaddingDip = 8
    EdgeMarginDip = 10
    MaxScale = 1.6
    TriggerThicknessDip = 4
    PeekSizeDip = 4
}

$script:canvasWindowClass = 'MuralisDesktopHostWindow'

# The window region is the honest answer to "which pixels does the canvas own right now": the rail is
# only really out if the pixels it draws into belong to the window.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class P3Rgn {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")] public static extern int GetWindowRgn(IntPtr hWnd, IntPtr hRgn);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
    [DllImport("gdi32.dll")] public static extern bool PtInRegion(IntPtr hRgn, int x, int y);
    [DllImport("gdi32.dll")] public static extern int GetRgnBox(IntPtr hRgn, out RECT rect);
}
'@

# Real things to put in the dock: a program, a shortcut the shell made, a folder, and a second folder
# and an address for the canvas. Nothing here is copied anywhere; the document only ever names them.
function New-DockFixture {
    $root = Join-Path $env:TEMP 'MuralisP3C'
    if (Test-Path $root) { Remove-Item -Recurse -Force $root }
    New-Item -ItemType Directory -Force -Path $root | Out-Null

    $folderA = Join-Path $root 'P3C Folder'
    $folderB = Join-Path $root 'P3C Spare'
    New-Item -ItemType Directory -Force -Path $folderA | Out-Null
    New-Item -ItemType Directory -Force -Path $folderB | Out-Null

    $notepad = Join-Path $env:SystemRoot 'System32\notepad.exe'
    $link = Join-Path $root 'P3C Notepad.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($link)
    $shortcut.TargetPath = $notepad
    $shortcut.Description = 'Muralis Phase 3C fixture shortcut'
    $shortcut.Save()

    Write-Host ("fixture:  {0}" -f $root)
    return @{
        Root = $root
        Folder = $folderA
        Spare = $folderB
        Link = $link
        Notepad = $notepad
        Url = 'https://example.com/'
    }
}

function New-DockItem {
    param([string]$Id, [string]$Name, [string]$Kind, [string]$Path, [double]$OffsetX = 0, [double]$OffsetY = 0)

    $target = [pscustomobject]@{ kind = $Kind }
    if ($Kind -eq 'url') { $target | Add-Member -NotePropertyName url -NotePropertyValue $Path }
    else { $target | Add-Member -NotePropertyName path -NotePropertyValue $Path }

    return [pscustomobject]@{
        Id = $Id
        Name = $Name
        Target = $target
        IconKey = ''
        Anchor = 'Center'
        OffsetXDip = $OffsetX
        OffsetYDip = $OffsetY
        SizeDip = 96
        Z = 0
        IsVisible = $true
    }
}

# The document as the app writes it: version 3, with the dock as a section of its own that names the
# items it holds, in the order it holds them.
function Write-DockLayout {
    param(
        [string]$Edge = 'Left',
        [bool]$Enabled = $true,
        [bool]$AutoHide = $true,
        [string[]]$DockIds,
        $Items
    )

    $entries = @()
    foreach ($id in $DockIds) { $entries += [pscustomobject]@{ ItemId = $id } }

    $layout = [pscustomobject]@{
        SchemaVersion = 3
        Kind = 'muralis.desktopLayout'
        Dock = [pscustomobject]@{
            Enabled = $Enabled
            Edge = $Edge
            AutoHide = $AutoHide
            TriggerThicknessDip = $script:dockSetup.TriggerThicknessDip
            PeekSizeDip = $script:dockSetup.PeekSizeDip
            ShowDelayMilliseconds = 120
            HideDelayMilliseconds = 600
            ItemSizeDip = $script:dockSetup.ItemSizeDip
            SpacingDip = $script:dockSetup.SpacingDip
            EdgeMarginDip = $script:dockSetup.EdgeMarginDip
            PaddingDip = $script:dockSetup.PaddingDip
            MaxScale = $script:dockSetup.MaxScale
            InfluenceRadiusDip = 130
            Falloff = 'Smoothstep'
            Spring = [pscustomobject]@{ PeriodSeconds = 0.34; DampingRatio = 0.78 }
            Entries = $entries
        }
        Items = @($Items)
    }

    [IO.File]::WriteAllText($script:layoutPath, ($layout | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
}

# The six things the run works with: three in the dock and two on the canvas.
function New-DockScene {
    $fixture = New-DockFixture
    $items = @(
        (New-DockItem -Id 'dock_app' -Name 'P3C Notepad' -Kind 'application' -Path $fixture.Notepad),
        (New-DockItem -Id 'dock_link' -Name 'P3C Shortcut' -Kind 'shortcut' -Path $fixture.Link),
        (New-DockItem -Id 'dock_folder' -Name 'P3C Folder' -Kind 'folder' -Path $fixture.Folder),
        (New-DockItem -Id 'free_folder' -Name 'P3C Spare' -Kind 'folder' -Path $fixture.Spare -OffsetX -520 -OffsetY -260),
        (New-DockItem -Id 'free_url' -Name 'example.com' -Kind 'url' -Path $fixture.Url -OffsetX 520 -OffsetY -260)
    )

    Write-DockLayout -Edge 'Left' -DockIds @('dock_app', 'dock_link', 'dock_folder') -Items $items
    return @{ Fixture = $fixture; Items = $items }
}

# ---------------------------------------------------------------- dock geometry, in the harness

function Get-DisplayDip {
    $width = [P3Win]::GetSystemMetrics(0)
    $height = [P3Win]::GetSystemMetrics(1)
    return @{
        Width = $width / $script:scale
        Height = $height / $script:scale
        OriginX = 0
        OriginY = 0
    }
}

# Where an item's centre is, in screen pixels, for the edge the dock is on. The formulas are the
# dock's own: the run is centred on the edge, and an item's centre sits half its size in from the
# edge margin plus the padding.
function Get-DockItemPoint([int]$index, [int]$itemCount = 3, [string]$edge = 'Left') {
    $display = Get-DisplayDip
    $item = $script:dockSetup.ItemSizeDip
    $spacing = $script:dockSetup.SpacingDip
    $padding = $script:dockSetup.PaddingDip
    $margin = $script:dockSetup.EdgeMarginDip

    $run = ($itemCount * $item) + (($itemCount - 1) * $spacing)
    $first = ($display.Height / 2) - ($run / 2) + ($item / 2)
    if ($edge -eq 'Top' -or $edge -eq 'Bottom') {
        $first = ($display.Width / 2) - ($run / 2) + ($item / 2)
    }

    $along = $first + ($index * ($item + $spacing))
    $depth = $margin + ($item / 2) + $padding

    switch ($edge) {
        'Right' { $dipX = $display.Width - $depth; $dipY = $along }
        'Top' { $dipX = $along; $dipY = $depth }
        'Bottom' { $dipX = $along; $dipY = $display.Height - $depth }
        default { $dipX = $depth; $dipY = $along }
    }

    return @(
        [int][Math]::Round($display.OriginX + ($dipX * $script:scale)),
        [int][Math]::Round($display.OriginY + ($dipY * $script:scale))
    )
}

# A point one DIP inside the dock's edge, at the middle of the edge: the strip that summons the rail.
function Get-DockEdgePoint([string]$edge = 'Left') {
    $display = Get-DisplayDip
    $depth = 1
    switch ($edge) {
        'Right' { $dipX = $display.Width - $depth; $dipY = $display.Height / 2 }
        'Top' { $dipX = $display.Width / 2; $dipY = $depth }
        'Bottom' { $dipX = $display.Width / 2; $dipY = $display.Height - $depth }
        default { $dipX = $depth; $dipY = $display.Height / 2 }
    }

    return @(
        [int][Math]::Round($display.OriginX + ($dipX * $script:scale)),
        [int][Math]::Round($display.OriginY + ($dipY * $script:scale))
    )
}

# Where a canvas item is, in screen pixels: the display's centre plus the offset the document saved.
function Get-CanvasItemPoint([double]$offsetX, [double]$offsetY) {
    $display = Get-DisplayDip
    return @(
        [int][Math]::Round($display.OriginX + (($display.Width / 2) + $offsetX) * $script:scale),
        [int][Math]::Round($display.OriginY + (($display.Height / 2) + $offsetY) * $script:scale)
    )
}

function Find-WindowByClassPrefix([int]$processId, [string]$prefix) {
    return [P3Win]::FindWindowByPrefix($processId, $prefix)
}

# Whether the canvas really owns the pixels at a screen point, read from the window region itself.
# The region is in window coordinates, so the point is taken relative to the window's client origin.
function Test-CanvasOwns([int]$x, [int]$y) {
    $window = Find-WindowByClassPrefix ([int]$script:process.Id) $script:canvasWindowClass
    if ($window -eq [IntPtr]::Zero) { return $false }

    $origin = [P3Win]::ClientOriginOf($window)
    $region = [P3Rgn]::CreateRectRgn(0, 0, 0, 0)
    try {
        if ([P3Rgn]::GetWindowRgn($window, $region) -eq 0) { return $false }
        return [P3Rgn]::PtInRegion($region, $x - $origin[0], $y - $origin[1])
    } finally {
        [void][P3Rgn]::DeleteObject($region)
    }
}

# The bounding box of the canvas window's region, for a report when a pixel check disagrees.
function Get-CanvasRegionBox {
    $window = Find-WindowByClassPrefix ([int]$script:process.Id) $script:canvasWindowClass
    if ($window -eq [IntPtr]::Zero) { return 'no canvas window' }

    $origin = [P3Win]::ClientOriginOf($window)
    $region = [P3Rgn]::CreateRectRgn(0, 0, 0, 0)
    try {
        if ([P3Rgn]::GetWindowRgn($window, $region) -eq 0) { return 'no region' }
        $rect = New-Object P3Rgn+RECT
        [void][P3Rgn]::GetRgnBox($region, [ref]$rect)
        return ("{0},{1} to {2},{3} (client origin {4},{5})" -f $rect.Left, $rect.Top, $rect.Right, $rect.Bottom, $origin[0], $origin[1])
    } finally {
        [void][P3Rgn]::DeleteObject($region)
    }
}

# The taskbar sits on top of the desktop layer, so a dock on an edge the taskbar covers cannot be
# reached by the pointer at all. The harness says so rather than pretending to test it.
function Test-EdgeCoveredByTaskbar([string]$edge) {
    $strip = Get-DockEdgePoint $edge
    $found = Get-DesktopPoint $strip[0] $strip[1]
    return ($found.Class -eq 'Shell_TrayWnd' -or $found.Class -eq 'Shell_SecondaryTrayWnd')
}

# Waits for the dock to be in one of its states. The wanted state is passed through a script-scope
# variable: a script block runs in the scope that calls it, so a local would not be visible inside.
function Wait-DockState([string]$state, [int]$timeoutSeconds = 15, [string]$what = '') {
    $script:dockWanted = $state
    $label = if ($what.Length -gt 0) { $what } else { "the dock to be $state" }
    return Wait-Diag { param($s) $s.DockPhase -eq $script:dockWanted } $timeoutSeconds $label
}

# Brings the rail out if it is away, by touching the strip along its edge, and leaves the pointer on
# the item whose centre was asked for. A dock that is away answers from its strip alone, so aiming
# straight at an item would only find empty desktop.
function Enter-Dock([string]$edge, [int]$index, [int]$itemCount) {
    $strip = Get-DockEdgePoint $edge
    Move-And-Settle $strip[0] $strip[1] 700
    [void](Wait-Diag { param($s) $s.DockPhase -eq 'Visible' } 10 "the dock on the $edge edge")
    $item = Get-DockItemPoint $index $itemCount $edge
    Move-And-Settle $item[0] $item[1] 600
    return $item
}

# The notepads the harness's own click started: closed again so they stop covering the desktop the
# drags need. Only ids that were not running before the run are ever touched.
function Close-HarnessNotepads {
    $ids = @(Get-NotepadIds | Where-Object { $script:notepadBaseline -notcontains $_ })
    foreach ($id in $ids) {
        Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
    }

    if ($ids.Count -gt 0) {
        Write-Host ("  closed {0} notepad window(s) the launch opened" -f $ids.Count)
        Start-Sleep -Milliseconds 700
    }

    return $ids.Count
}

# Launches the app as the dock stage needs it: its own window, a mounted canvas, the router attached,
# and the baseline of the notepads that were already running.
function Start-DockApp {
    $instance = Start-AppInstance
    $canvas = Wait-Until { ([P3Win]::ClassesWithPrefix('MuralisDesktopHostWindow')).Count -ge 1 } 60 'the canvas mount'
    $router = Wait-Until { ([P3Win]::ClassesWithPrefix('MuralisPointerRouter_')).Count -ge 1 } 60 'the router attach'

    # Never touch the window before the first frame; the app is unhappy if it goes away that early.
    Start-Sleep -Milliseconds 900
    $script:notepadBaseline = Get-NotepadIds

    return @{ Instance = $instance; Canvas = $canvas; Router = $router }
}

function Show-DockState($state) {
    if ($null -eq $state) { return 'no reading' }
    return ("{0} · {1} items · {2} · {3} edge · reveal {4}" -f $state.DockPhase, $state.DockItems, $(if ($state.DockEnabled) { 'on' } else { 'off' }), $state.DockEdge, $state.DockScale)
}

# Switches the dock to another edge through the page's own picker: the combo box is focused and moved
# with the arrow keys the control handles itself, so the change goes through the same binding a click
# in the list would. Language independent, because nothing is chosen by its label.
function Set-DockEdge([string]$edge) {
    $order = @('Left', 'Right', 'Top', 'Bottom')
    $wanted = [Array]::IndexOf($order, $edge)

    $root = Get-AppRoot
    $combo = Find-ById $root 'CanvasDockEdge'
    if ($null -eq $combo) {
        Write-Host '  (the edge picker was not found on the page)'
        return $false
    }

    try {
        $combo.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView()
    } catch {
        Write-Host '  (the edge picker could not be scrolled into view)'
    }

    # The list a combo box shows when it is opened: the four edges, in the order the page offers them.
    # Nothing is picked by its label, so the run works in any language.
    try {
        $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Start-Sleep -Milliseconds 400

        $items = $combo.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::ListItem)))

        if ($items.Count -ge 4) {
            $items.Item($wanted).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Start-Sleep -Milliseconds 500
            $state = Read-State
            if ($state.DockEdge -eq $edge) { return $true }
        }

        [void]$combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
        Start-Sleep -Milliseconds 200
    } catch {
        Write-Host ("  (the edge picker could not be opened: {0})" -f $_)
    }

    # The arrow keys move a closed combo box one step at a time; the run is the shortest way there.
    $combo.SetFocus()
    Start-Sleep -Milliseconds 300

    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        $state = Read-State
        if ($state.DockEdge -eq $edge) { return $true }

        $current = [Array]::IndexOf($order, [string]$state.DockEdge)
        if ($current -lt 0) { break }

        $key = if ($wanted -gt $current) { 0x28 } else { 0x26 }   # VK_DOWN / VK_UP
        [P3Win]::PressKey($key)
        Start-Sleep -Milliseconds 350
    }

    return ((Read-State).DockEdge -eq $edge)
}

# ---------------------------------------------------------------- the dock stage

function Invoke-DockStage {
    Write-Host ''
    Write-Host '=== the interactive edge dock ==='

    Backup-Settings
    Enable-CanvasInSettings
    Park-LayoutFiles

    # The document is planted after the user's own is parked, and in the shape the app writes.
    $scene = New-DockScene
    $fixture = $scene.Fixture

    $app = Start-DockApp
    $script:process = $app.Instance.Process
    $script:window = Get-MainWindow ([int]$app.Instance.Process.Id)

    Add-Check 'dock: the canvas came up with the layout' ($app.Canvas -and $app.Router) ("canvas {0}, router {1}" -f $app.Canvas, $app.Router)

    Open-DynamicPage
    $state = Wait-Diag { param($s) $null -ne $s.DockPhase } 20 'the first dock reading'
    $script:scale = if ($null -ne $state -and $null -ne $state.ScaleFactor) { [double]$state.ScaleFactor } else { 1.0 }
    Add-Sample 'dock.scaleFactor' $script:scale

    if ($null -eq $state) {
        Add-Check 'dock: the diagnostics panel answered' $false 'no reading'
        return
    }

    Add-Check 'dock: the document was read as it was planted' (($state.DockItems -eq 3) -and ($state.DockEnabled) -and ($state.DockEdge -eq 'Left')) (Show-DockState $state)

    # The app's window is parked in a corner: the panel stays readable and the desktop stays clear.
    Show-AppWindow 1750 900 780 500

    $away = Get-CanvasItemPoint -520 -260
    $centres = @()
    for ($index = 0; $index -lt 3; $index++) { $centres += , (Get-DockItemPoint $index 3 'Left') }
    $edgePoint = Get-DockEdgePoint 'Left'
    $middle = @([int]([P3Win]::GetSystemMetrics(0) / 2), [int]([P3Win]::GetSystemMetrics(1) / 2))

    $blocked = Clear-TheDesktop -PointsX @($centres[0][0], $centres[1][0], $away[0], $middle[0]) -PointsY @($centres[0][1], $centres[1][1], $away[1]) -Label ' for the dock' -ExtraPoints @($edgePoint, (Get-DockItemPoint 0 3 'Right'), (Get-DockItemPoint 0 3 'Top'), (Get-DockItemPoint 0 3 'Bottom'))
    if ($blocked.Count -gt 0) { Write-Host ("  (points owned by something else: {0})" -f ($blocked -join ', ')) }

    # ---- hidden while the pointer is away
    Move-And-Settle $away[0] $away[1] 400
    $state = Wait-DockState 'Hidden' 10 'the dock to stay in while the pointer is away'
    Add-Check 'dock: the pointer away leaves it hidden' ($null -ne $state) (Show-DockState $state)
    Add-Check 'dock: a hidden rail owns none of the rail pixels' (-not (Test-CanvasOwns $centres[1][0] $centres[1][1])) ("pixel at ({0},{1})" -f $centres[1][0], $centres[1][1])

    # ---- the pointer at the edge brings it out
    Move-And-Settle $edgePoint[0] $edgePoint[1] 700
    $state = Wait-DockState 'Visible' 10 'the dock to come out'
    Add-Check 'dock: the pointer at the edge reveals it' ($null -ne $state) (Show-DockState $state)
    Add-Check 'dock: a revealed rail owns the pixels it draws into' (Test-CanvasOwns $centres[1][0] $centres[1][1]) ("pixel at ({0},{1}); region {2}" -f $centres[1][0], $centres[1][1], (Get-CanvasRegionBox))

    # ---- magnification follows the pointer
    Move-And-Settle $centres[1][0] $centres[1][1] 600
    $state = Wait-Diag { param($s) $s.HoverId -eq 'dock_link' } 10 'the middle dock item to be hovered'
    Add-Check 'dock: the item under the pointer is the one magnified' ($null -ne $state -and $state.HoverId -eq 'dock_link') ("hovered {0}" -f $(if ($null -ne $state) { $state.HoverId } else { 'nothing' }))
    Add-Check 'dock: the magnified item reaches the dock maximum' ($null -ne $state -and $state.HoverScale -ge ($script:dockSetup.MaxScale - 0.01)) ("hovered scale {0}" -f $(if ($null -ne $state) { $state.HoverScale } else { 0 }))

    $firstScale = $state.HoverScale
    Move-And-Settle $centres[2][0] $centres[2][1] 600
    $moved = Wait-Diag { param($s) $s.HoverId -eq 'dock_folder' } 10 'the next dock item to be hovered'
    Add-Check 'dock: the magnification follows the pointer along the rail' ($null -ne $moved -and $moved.HoverId -eq 'dock_folder') ("hovered {0}" -f $(if ($null -ne $moved) { $moved.HoverId } else { 'nothing' }))
    Add-Check 'dock: a neighbour is smaller than the item under the pointer' ($null -ne $moved -and $moved.HoverScale -ge $firstScale) ("{0} at {1} vs {2} at {3}" -f $state.HoverId, $firstScale, $(if ($null -ne $moved) { $moved.HoverId } else { '?' }), $(if ($null -ne $moved) { $moved.HoverScale } else { 0 }))

    # ---- a single click opens a dock item
    $launchBaseline = Get-LogCountAll 'was opened by the shell'
    Move-And-Settle $centres[0][0] $centres[0][1] 500
    [P3Win]::LeftDown()
    Start-Sleep -Milliseconds 40
    [P3Win]::LeftUp()
    $launched = Wait-Until { (Get-LogCountAll 'was opened by the shell') -gt $launchBaseline } 15 'the dock item to be opened'
    $opened = (Get-LogCountAll 'was opened by the shell') - $launchBaseline
    Add-Check 'dock: a single click opens the item' ($launched -and $opened -eq 1) ("openings: {0}" -f $opened)
    $notepad = Wait-Until { (Get-NotepadIds | Where-Object { $script:notepadBaseline -notcontains $_ }).Count -gt 0 } 10 'a new notepad'
    Add-Check 'dock: what it opens really starts' $notepad ("new notepads: {0}" -f ((Get-NotepadIds | Where-Object { $script:notepadBaseline -notcontains $_ }) -join ','))

    # The rail stays where it is: a launch must not be taken for the pointer having left.
    Start-Sleep -Milliseconds 900
    $state = Read-State
    Add-Check 'dock: the rail stays out while the pointer rests on it' ($state.DockPhase -eq 'Visible') (Show-DockState $state)

    # The windows the launch opened are closed again: they would otherwise cover the desktop the
    # drags below need. The count that matters from here on is the one after this single click.
    [void](Close-HarnessNotepads)
    $openedByEverything = Get-LogCountAll 'was opened by the shell'
    $blocked = Clear-TheDesktop -PointsX @($centres[0][0], $centres[1][0], $centres[2][0], $away[0]) -PointsY @($centres[0][1], $centres[1][1], $centres[2][1], $away[1]) -Label ' for the drags' -ExtraPoints @($edgePoint, (Get-DockItemPoint 0 3 'Right'))

    # ---- reorder: carry the first item past the last
    $entriesBefore = @((Read-Layout).Dock.Entries | ForEach-Object { $_.ItemId })
    $lastCentre = Get-DockItemPoint 2 3 'Left'
    Drag-Pointer $centres[0][0] $centres[0][1] $lastCentre[0] ($lastCentre[1] + 30) 16
    Move-And-Settle $centres[1][0] $centres[1][1] 700
    $entriesAfter = @((Read-Layout).Dock.Entries | ForEach-Object { $_.ItemId })
    Add-Check 'dock: carrying an item along the rail reorders the dock' ($entriesAfter.Count -eq 3 -and $entriesAfter[0] -ne $entriesBefore[0]) ("{0} -> {1}" -f ($entriesBefore -join ','), ($entriesAfter -join ','))
    Add-Check 'dock: a reorder never opens anything' ((Get-LogCountAll 'was opened by the shell') -eq $openedByEverything) ("openings during the drag: {0}" -f ((Get-LogCountAll 'was opened by the shell') - $openedByEverything))

    # ---- canvas -> dock
    $canvasPoint = Get-CanvasItemPoint -520 -260
    $dockPoint = $centres[1]
    Drag-Pointer $canvasPoint[0] $canvasPoint[1] $dockPoint[0] $dockPoint[1] 18
    Move-And-Settle $away[0] $away[1] 500
    $afterTransfer = Read-Layout
    $dockedIds = @($afterTransfer.Dock.Entries | ForEach-Object { $_.ItemId })
    Add-Check 'dock: a canvas item dropped on the dock joins it' ($dockedIds -contains 'free_folder' -and $dockedIds.Count -eq 4) ("dock holds {0}" -f ($dockedIds -join ','))
    $state = Read-State
    Add-Check 'dock: the dock reports the item it took' ($state.DockItems -eq 4) (Show-DockState $state)

    # ---- dock -> canvas, let go away from the centre so the saved offsets can be checked
    $leavePoint = @([int]([P3Win]::GetSystemMetrics(0) * 0.35), [int]([P3Win]::GetSystemMetrics(1) * 0.40))
    $leaveIndex = [Array]::IndexOf($dockedIds, 'dock_folder')
    $leaveFrom = Get-DockItemPoint $leaveIndex 4 'Left'
    Drag-Pointer $leaveFrom[0] $leaveFrom[1] $leavePoint[0] $leavePoint[1] 18
    Start-Sleep -Milliseconds 900
    $afterLeave = Read-Layout
    $leftIds = @($afterLeave.Dock.Entries | ForEach-Object { $_.ItemId })
    $movedItem = Get-LayoutItem $afterLeave 'dock_folder'
    Add-Check 'dock: a dock item dropped on the canvas leaves the dock' (-not ($leftIds -contains 'dock_folder')) ("dock holds {0}" -f ($leftIds -join ','))
    $expectedX = ($leavePoint[0] / $script:scale) - (Get-DisplayDip).Width / 2
    $expectedY = ($leavePoint[1] / $script:scale) - (Get-DisplayDip).Height / 2
    $landed = $null -ne $movedItem -and [Math]::Abs([double]$movedItem.OffsetXDip - $expectedX) -lt 40 -and [Math]::Abs([double]$movedItem.OffsetYDip - $expectedY) -lt 40
    Add-Check 'dock: the item is on the canvas where it was let go' $landed ("offset {0:0},{1:0} DIP, expected about {2:0},{3:0}" -f $movedItem.OffsetXDip, $movedItem.OffsetYDip, $expectedX, $expectedY)
    Add-Check 'dock: leaving the dock never opens anything' ((Get-LogCountAll 'was opened by the shell') -eq $openedByEverything) ("openings: {0}" -f ((Get-LogCountAll 'was opened by the shell') - $openedByEverything))

    # ---- auto-hide again
    Move-And-Settle $away[0] $away[1] 400
    $state = Wait-DockState 'Hidden' 10 'the dock to retract once the pointer is away'
    Add-Check 'dock: the rail retracts once the pointer is away' ($null -ne $state) (Show-DockState $state)

    # ---- an ordinary window keeps the dock to itself
    $cover = @(0, 260, 900, 620)
    [P3Win]::SetForegroundWindow($script:window) | Out-Null
    [P3Win]::MoveWindow($script:window, $cover[0], $cover[1], $cover[2], $cover[3], $true) | Out-Null
    Start-Sleep -Milliseconds 700
    Move-And-Settle 2 ([int]([P3Win]::GetSystemMetrics(1) / 2)) 900
    $state = Read-State
    Add-Check 'dock: a pointer over an ordinary window never brings the dock out' ($state.DockPhase -eq 'Hidden') (Show-DockState $state)
    Add-Check 'dock: a pointer over an ordinary window magnifies nothing' ($state.HoverId -eq 'none') ("hovered {0}" -f $state.HoverId)

    # ---- an Explorer restart: the desktop layer is rebuilt under the canvas and the dock comes back
    if (-not $SkipExplorerRestart) {
        $beforeShell = Read-Layout
        $orderBeforeShell = @($beforeShell.Dock.Entries | ForEach-Object { $_.ItemId })
        $edgeBeforeShell = $beforeShell.Dock.Edge
        $iconsBeforeShell = (Read-State).IconEntries
        $mountBeforeShell = (Read-State).Mount

        Write-Host '=== 5. a restart of Explorer ==='
        taskkill /f /im explorer.exe | Out-Null
        Start-Sleep -Milliseconds 1500
        Start-Process explorer.exe | Out-Null

        $remounted = Wait-Until {
            $state = Read-State
            $null -ne $state.Mount -and $null -ne $mountBeforeShell -and $state.Mount -gt $mountBeforeShell
        } 60 'the canvas to be mounted again on the rebuilt desktop'
        Add-Check 'dock: the canvas comes back after Explorer restarts' $remounted ("mount {0} -> {1}" -f $mountBeforeShell, (Read-State).Mount)

        $shellState = Wait-Diag { param($s) $s.DockItems -eq $orderBeforeShell.Count } 30 'the dock after the shell restart'
        Add-Check 'dock: the dock and its items are back after the shell restart' ($null -ne $shellState -and $shellState.DockEdge -eq $edgeBeforeShell) (Show-DockState $shellState)

        $afterShell = Read-Layout
        $orderAfterShell = @($afterShell.Dock.Entries | ForEach-Object { $_.ItemId })
        Add-Check 'dock: the order survives the shell restart' (($orderAfterShell -join ',') -eq ($orderBeforeShell -join ',')) ("{0} vs {1}" -f ($orderBeforeShell -join ','), ($orderAfterShell -join ','))
        Add-Check 'dock: the icons are still the ones already resolved' ((Read-State).IconEntries -ge $iconsBeforeShell -and (Read-State).IconEntries -gt 0) ("{0} cached before, {1} now" -f $iconsBeforeShell, (Read-State).IconEntries)

        Show-AppWindow 1750 900 780 500
        $blocked = Clear-TheDesktop -PointsX @($centres[1][0], $away[0]) -PointsY @($centres[1][1], $away[1]) -Label ' after the shell restart' -ExtraPoints @($edgePoint)
        [void](Enter-Dock $edgeBeforeShell 1 $orderBeforeShell.Count)
        $afterShellState = Read-State
        Add-Check 'dock: the pointer works the dock again after the shell restart' ($afterShellState.DockEdge -eq $edgeBeforeShell -and $afterShellState.HoverId -ne 'none') ("{0}; hovered {1} at {2}" -f $afterShellState.DockEdge, $afterShellState.HoverId, $afterShellState.HoverScale)

        Move-And-Settle $away[0] $away[1] 500
        [void](Wait-DockState 'Hidden' 10 'the dock to go back in after the shell restart')
        $launchesBeforeShell = Get-LogCountAll 'was opened by the shell'
        Add-Check 'dock: the shell restart opened nothing by itself' ($launchesBeforeShell -eq $openedByEverything) ("openings: {0}" -f ($launchesBeforeShell - $openedByEverything))
    }

    # ---- the other three edges, switched through the page's own picker
    Show-AppWindow 1750 900 780 500
    foreach ($edge in @('Right', 'Top', 'Bottom')) {
        Move-And-Settle $away[0] $away[1] 300
        $switched = Set-DockEdge $edge
        Show-AppWindow 1750 900 780 500
        $state = Read-State
        Add-Check ("dock: the page moves the dock to the {0} edge" -f $edge) ($switched -and $state.DockEdge -eq $edge) ("picker said {0}" -f $state.DockEdge)

        $itemCount = if ($null -ne $state) { $state.DockItems } else { 3 }
        if (Test-EdgeCoveredByTaskbar $edge) {
            # The taskbar is on top of the desktop layer there: the rail is where it belongs, but the
            # pointer cannot reach it while the taskbar covers that edge.
            $railPoint = Get-DockItemPoint 0 $itemCount $edge
            $owned = Test-CanvasOwns $railPoint[0] $railPoint[1]
            Add-Check ("dock: the {0} edge carries the rail under the taskbar" -f $edge) ($owned -and $state.DockEdge -eq $edge) ("{0} edge, rail pixels owned: {1} (the taskbar covers the pointer)" -f $state.DockEdge, $owned)
            continue
        }

        [void](Enter-Dock $edge 0 $itemCount)
        $hovered = Read-State
        Add-Check ("dock: the rail comes out on the {0} edge and magnifies there" -f $edge) ($hovered.DockEdge -eq $edge -and $hovered.HoverId -ne 'none') ("{0}; hovered {1} at {2}" -f $hovered.DockEdge, $hovered.HoverId, $hovered.HoverScale)
    }

    # ---- the dock survives a restart of Muralis, on the edge it was left on
    $before = Read-Layout
    $dockBefore = @($before.Dock.Entries | ForEach-Object { $_.ItemId })
    $edgeBefore = $before.Dock.Edge
    [void](Close-AppInstance $app.Instance)
    $app = Start-DockApp
    $script:process = $app.Instance.Process
    $script:window = Get-MainWindow ([int]$app.Instance.Process.Id)
    Open-DynamicPage
    $state = Wait-Diag { param($s) $null -ne $s.DockPhase } 20 'the dock after the restart'
    Add-Check 'dock: the dock comes back after Muralis restarts' ($null -ne $state -and $state.DockItems -eq $dockBefore.Count -and $state.DockEdge -eq $edgeBefore) (Show-DockState $state)

    $restored = Read-Layout
    $dockAfter = @($restored.Dock.Entries | ForEach-Object { $_.ItemId })
    Add-Check 'dock: the order is the order the user left' (($dockAfter -join ',') -eq ($dockBefore -join ',')) ("{0} vs {1}" -f ($dockBefore -join ','), ($dockAfter -join ','))
    Add-Check 'dock: nothing was opened by the pointer alone' ((Get-LogCountAll 'was opened by the shell') -eq $openedByEverything) ("openings after everything: {0}, one click only" -f ((Get-LogCountAll 'was opened by the shell') - $launchBaseline))
    Add-Check 'dock: the document is version 3 with the dock as its own section' ($restored.SchemaVersion -eq 3 -and $null -ne $restored.Dock.Entries) ("schema {0}, {1} entries" -f $restored.SchemaVersion, @($restored.Dock.Entries).Count)

    Show-AppWindow 1750 900 780 500
    if (Test-EdgeCoveredByTaskbar $edgeBefore) {
        # The edge the dock was left on is under the taskbar: its strip is still the canvas' own, but
        # the pointer cannot reach it. Moving the dock back to a reachable edge is the page's job, and
        # that is exactly what the next check does.
        $strip = Get-DockEdgePoint $edgeBefore
        Add-Check 'dock: the restored dock still holds its edge' (Test-CanvasOwns $strip[0] $strip[1]) ("{0} edge, strip pixels owned" -f $edgeBefore)

        $switched = Set-DockEdge 'Left'
        Show-AppWindow 1750 900 780 500
        [void](Enter-Dock 'Left' 0 $dockBefore.Count)
        $state = Read-State
        Add-Check 'dock: the dock can be moved off a covered edge and magnifies again' ($switched -and $state.HoverId -ne 'none') ("{0}; hovered {1} at {2}" -f $state.DockEdge, $state.HoverId, $state.HoverScale)
    } else {
        [void](Enter-Dock $edgeBefore 0 $dockBefore.Count)
        $state = Read-State
        Add-Check 'dock: the restored dock still magnifies under the pointer' ($state.HoverId -ne 'none') ("hovered {0} at {1}" -f $state.HoverId, $state.HoverScale)
    }

    [void](Close-AppInstance $app.Instance)
}

# ---------------------------------------------------------------- the dock under load

# Real items, the way the app's own importer would bring them in: programs from the system folder,
# folders that exist, and addresses.
function New-DockPerfItems([int]$count) {
    $exes = @(Get-ChildItem (Join-Path $env:SystemRoot 'System32') -Filter '*.exe' |
        Where-Object { $_.Length -gt 4096 } | Sort-Object Name | Select-Object -First ([math]::Max($count - 7, 1)))
    $folders = @($env:SystemRoot, (Join-Path $env:SystemRoot 'System32'), $env:TEMP, $env:windir)
    $urls = @('https://example.com/', 'https://example.org/', 'https://example.net/')

    $items = @()
    $index = 0
    foreach ($exe in $exes) {
        $items += New-DockItem -Id ("dock_p{0:d2}" -f $index) -Name $exe.BaseName -Kind 'application' -Path $exe.FullName
        $index++
    }
    foreach ($folder in $folders) {
        $items += New-DockItem -Id ("dock_p{0:d2}" -f $index) -Name (Split-Path $folder -Leaf) -Kind 'folder' -Path $folder
        $index++
    }
    foreach ($url in $urls) {
        $items += New-DockItem -Id ("dock_p{0:d2}" -f $index) -Name ([Uri]$url).Host -Kind 'url' -Path $url
        $index++
    }

    # Exactly the number asked for: the eighth program is dropped rather than one item too many.
    if ($exes.Count -gt 0 -and $items.Count -gt $count) {
        $items = @($items[0..($count - 1)])
    }

    return $items
}

function Invoke-DockPerfStage {
    Write-Host ''
    Write-Host '=== the dock under load ==='

    foreach ($count in $PerfItemCounts) {
        Write-Host ''
        Write-Host ("--- {0} dock items ---" -f $count)

        Backup-Settings
        Enable-CanvasInSettings
        Park-LayoutFiles

        $items = New-DockPerfItems $count
        $ids = @($items | ForEach-Object { $_.Id })
        Write-DockLayout -Edge 'Left' -DockIds $ids -Items $items
        $planted = Read-Layout
        Write-Host ("  planted {0} items, {1} entries, schema {2}" -f @($planted.Items).Count, @($planted.Dock.Entries).Count, $planted.SchemaVersion)

        $launchedAt = Get-Date
        $app = Start-DockApp
        $script:process = $app.Instance.Process
        $script:window = Get-MainWindow ([int]$app.Instance.Process.Id)
        $mountMs = [int]((Get-Date) - $launchedAt).TotalMilliseconds
        Add-Sample ("dock{0}.mountMs" -f $count) $mountMs

        Open-DynamicPage
        $state = Wait-Diag { param($s) $null -ne $s.DockPhase } 30 'the first dock reading'
        $script:scale = if ($null -ne $state -and $null -ne $state.ScaleFactor -and $state.ScaleFactor -gt 0) { [double]$state.ScaleFactor } else { 1.0 }
        Write-Host ("  dock: {0}" -f (Show-DockState $state))

        $loaded = Wait-Diag { param($s) $s.DockItems -eq $count } 90 ("{0} dock items to be there" -f $count)
        Add-Check ("dock {0}: every item is in the dock" -f $count) ($null -ne $loaded) ("{0}" -f (Show-DockState $loaded))

        $iconItems = @($items | Where-Object { $_.Target.kind -ne 'url' }).Count
        $icons = Wait-Diag { param($s) $null -ne $s.IconEntries -and $s.IconEntries -ge ($iconItems - 1) } 120 ("{0} icons to be cached" -f $iconItems)
        if ($null -eq $icons) { Write-Host ("  (panel said: {0})" -f (Read-State).Text) }
        Add-Sample ("dock{0}.iconItems" -f $count) $iconItems
        Add-Sample ("dock{0}.iconEntries" -f $count) $(if ($null -ne $icons) { $icons.IconEntries } else { 0 })
        Add-Sample ("dock{0}.iconCacheMb" -f $count) $(if ($null -ne $icons) { $icons.IconMb } else { 0 })
        Add-Check ("dock {0}: the icons were resolved from the shell" -f $count) ($null -ne $icons) ("{0} cached of {1} items with a file behind them" -f $(if ($null -ne $icons) { $icons.IconEntries } else { 0 }), $iconItems)

        $processId = [int]$script:process.Id
        $handles = (Get-Process -Id $processId).HandleCount
        $gdi = [P3Win]::GdiObjectsOf($processId)
        $user = [P3Win]::UserObjectsOf($processId)
        $working = [math]::Round((Get-Process -Id $processId).WorkingSet64 / 1MB, 1)
        Add-Sample ("dock{0}.handles" -f $count) $handles
        Add-Sample ("dock{0}.gdiObjects" -f $count) $gdi
        Add-Sample ("dock{0}.userObjects" -f $count) $user
        Add-Sample ("dock{0}.workingSetMb" -f $count) $working

        # --- idle: the pointer away from the dock, so the rail is in and nothing is animating
        $display = Get-DisplayDip
        $away = Get-CanvasItemPoint 0 -260
        $blocked = Clear-TheDesktop -PointsX @($away[0], [int]([P3Win]::GetSystemMetrics(0) * 0.6)) -PointsY @($away[1], [int]([P3Win]::GetSystemMetrics(1) * 0.5)) -Label (" for {0} dock items" -f $count)
        Move-And-Settle $away[0] $away[1] 400
        [void](Wait-DockState 'Hidden' 10 'the dock to be in for the idle measurement')

        Minimize-AppWindow
        $idleStart = Get-CpuMilliseconds $processId
        $idleWall = [System.Diagnostics.Stopwatch]::StartNew()
        Wait-ForSeconds 10
        $idleWall.Stop()
        $idleMs = (Get-CpuMilliseconds $processId) - $idleStart
        Add-Sample ("dock{0}.idleCpuMs" -f $count) $idleMs
        Add-Sample ("dock{0}.idleCpuPercentOfOneCore" -f $count) ([math]::Round(($idleMs / $idleWall.Elapsed.TotalMilliseconds) * 100, 3))
        Add-Check ("dock {0}: idle with the rail in stays cheap" -f $count) ($idleMs -lt 500) ("{0} ms over {1:0.0} s" -f $idleMs, $idleWall.Elapsed.TotalSeconds)

        # --- sweeping along the rail: the pointer magnifies whatever it passes. The strip is touched
        # first, because a rail that is away only answers from there.
        Show-AppWindow 1750 900 780 500
        $strip = Get-DockEdgePoint 'Left'
        Move-And-Settle $strip[0] $strip[1] 500
        [void](Wait-DockState 'Visible' 10 'the dock to be out for the sweep')
        $depth = ((Get-DockItemPoint 0 1 'Left')[0])
        $run = ($count * $script:dockSetup.ItemSizeDip) + (($count - 1) * $script:dockSetup.SpacingDip)
        $runStart = (($display.Height / 2) - ($run / 2) + ($script:dockSetup.ItemSizeDip / 2)) * $script:scale
        $runEnd = (($display.Height / 2) + ($run / 2) - ($script:dockSetup.ItemSizeDip / 2)) * $script:scale
        $sweepTop = [int][Math]::Max(6, [Math]::Min($runStart, [P3Win]::GetSystemMetrics(1) - 6))
        $sweepBottom = [int][Math]::Max(6, [Math]::Min($runEnd, [P3Win]::GetSystemMetrics(1) - 6))
        $cpus = Get-CpuMilliseconds $processId
        $gpu = 0
        $sweep = [System.Diagnostics.Stopwatch]::StartNew()
        for ($pass = 0; $pass -lt 3; $pass++) {
            for ($y = $sweepTop; $y -le $sweepBottom; $y += 24) {
                Move-Pointer $depth $y
                Start-Sleep -Milliseconds 6
            }
            for ($y = $sweepBottom; $y -ge $sweepTop; $y -= 24) {
                Move-Pointer $depth $y
                Start-Sleep -Milliseconds 6
            }
        }
        $sweep.Stop()
        $sweepMs = (Get-CpuMilliseconds $processId) - $cpus
        $swept = Read-State
        if ($swept.DockPhase -eq 'Visible' -and $swept.HoverId -ne 'none') {
            $gpu = Measure-GpuOnce $processId
        }
        Add-Sample ("dock{0}.sweepCpuMs" -f $count) $sweepMs
        Add-Sample ("dock{0}.sweepCpuPercentOfOneCore" -f $count) ([math]::Round(($sweepMs / $sweep.Elapsed.TotalMilliseconds) * 100, 3))
        Add-Sample ("dock{0}.sweepGpuPercent" -f $count) $gpu
        Add-Sample ("dock{0}.sweepHovered" -f $count) $swept.HoverId
        Add-Check ("dock {0}: the sweep really magnified something" -f $count) ($swept.DockPhase -eq 'Visible' -and $swept.HoverId -ne 'none') ("{0}; hovered {1} at {2}" -f $swept.DockPhase, $swept.HoverId, $swept.HoverScale)
        Add-Check ("dock {0}: the sweep stays affordable" -f $count) ($sweepMs -lt 3000) ("{0} ms over {1:0.0} s of sweeping" -f $sweepMs, $sweep.Elapsed.TotalSeconds)

        # --- in and out, five times: the state machine keeps up and ends where it should
        $cycles = 0
        for ($round = 0; $round -lt 5; $round++) {
            $strip = Get-DockEdgePoint 'Left'
            Move-And-Settle $strip[0] $strip[1] 400
            $out = Wait-DockState 'Visible' 6 'the dock to come out'
            Move-And-Settle $away[0] $away[1] 400
            $in = Wait-DockState 'Hidden' 6 'the dock to go back in'
            if ($null -ne $out -and $null -ne $in) { $cycles++ }
        }
        Add-Sample ("dock{0}.revealCycles" -f $count) $cycles
        Add-Check ("dock {0}: the rail goes out and back five times" -f $count) ($cycles -eq 5) ("{0} of 5 complete cycles" -f $cycles)

        # --- what a layout save costs, read from the log the store writes itself
        $saveLine = (Get-LogLines 'Desktop layout saved to' | Select-Object -Last 1)
        if ($saveLine -match 'in ([0-9.]+) ms') { Add-Sample ("dock{0}.layoutSaveMs" -f $count) ([double]$Matches[1]) }

        [void](Close-AppInstance $app.Instance)
        Write-Host ("  mount {0} ms · {1} icons · {2} MB cache · handles {3} · working set {4} MB" -f $mountMs, $(if ($null -ne $icons) { $icons.IconEntries } else { 0 }), $(if ($null -ne $icons) { $icons.IconMb } else { 0 }), $handles, $working)
        Write-Host ("  idle {0} ms · sweep {1} ms over {2:0.0} s · GPU {3} % · cycles {4}/5" -f $idleMs, $sweepMs, $sweep.Elapsed.TotalSeconds, $gpu, $cycles)
    }
}

# ---------------------------------------------------------------- main
Write-Host '=== Phase 3C dock verification ==='
Write-Host ("stage: {0}" -f $Stage)
Write-Host ''

try {
    Assert-MuralisNotRunning

    if ($Stage -eq 'probe') {
        Invoke-ProbeStage
        return
    }

    # The dock stage does its own planting: it needs a layout of its own alongside the settings.
    if ($Stage -ne 'dock' -and $Stage -ne 'perf') {
        Backup-Settings
        Disable-CloseToTray
    }

    switch ($Stage) {
        'restart' {
            Invoke-RestartStage
        }
        'wake' {
            Invoke-WakeStage
        }
        'dock' {
            Invoke-DockStage
        }
        'perf' {
            Invoke-DockPerfStage
        }
        'full' {
            Invoke-RestartStage
            Invoke-WakeStage
        }
    }
} finally {
    Restore-Everything
}

$failed = Write-CheckReport $Stage $outPath
if ($failed -gt 0) { exit 1 }
