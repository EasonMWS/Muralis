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

    Everything the harness changes is restored on the way out: settings.json, the user's own desktop
    layout documents, the fixture folder and the app itself.

.PARAMETER Stage
    probe   - no app launch: reports the machine state and the files the harness would touch
    restart - the single-instance restart race (launch/exit/launch, twenty cycles)
    wake    - only the second-launch part: a launch while one is running wakes that one
    full    - everything

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/p3c-dock-verify.ps1 -Stage restart
#>
[CmdletBinding()]
param(
    [ValidateSet('probe', 'restart', 'wake', 'full')] [string]$Stage = 'probe',
    [string]$Exe = 'src/Muralis.App/bin/Debug/net10.0-windows10.0.26100.0/win-x64/Muralis.exe',
    [int]$RestartCycles = 20,
    [int]$WakeCycles = 5,
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

    Backup-Settings
    Disable-CloseToTray

    switch ($Stage) {
        'restart' {
            Invoke-RestartStage
        }
        'wake' {
            Invoke-WakeStage
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
