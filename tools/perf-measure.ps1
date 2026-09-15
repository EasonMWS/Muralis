<#
.SYNOPSIS
    Measures Muralis startup, resources and the startup phase breakdown.

.DESCRIPTION
    Launches the built application a number of times and records:
      - cold and warm start: process start until the main window is visible
      - the app-internal clock from the log (window shown, UI idle, catalog loaded)
      - working set and private memory after the UI has settled and after a long idle
      - CPU and GPU peaks in the first seconds (the Home page opening)
      - idle CPU and GPU averaged over the tail of the sampling window
      - the startup timeline parsed from the application log

    The app is stopped between runs. Nothing else on the machine is touched.

.EXAMPLE
    pwsh tools/perf-measure.ps1 -Label validation
    pwsh tools/perf-measure.ps1 -Exe publish/win-x64/Muralis.exe -Label published -ColdRuns 1 -WarmRuns 3
#>
param(
    [string]$Exe = 'src/Muralis.App/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/Muralis.exe',
    [string]$Label = 'run',
    [int]$ColdRuns = 1,
    [int]$WarmRuns = 2,
    [int]$SettleSeconds = 4,
    [int]$SampleSeconds = 14,
    [int]$LongIdleSeconds = 60,
    [string]$OutDir = 'artifacts/perf'
)

$ErrorActionPreference = 'Stop'
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class PerfWin {
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);
  public struct RECT { public int Left, Top, Right, Bottom; }
  public static IntPtr FindVisibleWindow(uint pid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      uint wpid; GetWindowThreadProcessId(h, out wpid);
      if (wpid == pid && IsWindowVisible(h)) {
        RECT r; GetWindowRect(h, out r);
        if (r.Right - r.Left > 200 && r.Bottom - r.Top > 200) {
          var title = new System.Text.StringBuilder(64);
          GetWindowText(h, title, 64);
          if (title.ToString().Length > 0) { found = h; return false; }
        }
      }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@

$repoRoot = Split-Path $PSScriptRoot -Parent
$exePath = if ([System.IO.Path]::IsPathRooted($Exe)) { $Exe } else { Join-Path $repoRoot $Exe }
if (-not (Test-Path $exePath)) { throw "Executable not found: $exePath" }

$outPath = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repoRoot $OutDir }
New-Item -ItemType Directory -Force -Path $outPath | Out-Null

$logDirectory = Join-Path $env:LOCALAPPDATA 'Muralis\logs'
$logFile = Get-ChildItem $logDirectory -Filter 'muralis-*.log' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (Get-Process -Name Muralis -ErrorAction SilentlyContinue) {
    throw 'Muralis is already running; stop it before measuring.'
}

$cpuCount = [Environment]::ProcessorCount

function Get-LogLineCount {
    if (-not $logFile -or -not (Test-Path $logFile.FullName)) { return 0 }
    return (Get-Content $logFile.FullName -Encoding UTF8 | Measure-Object -Line).Lines
}

function Measure-GpuOnce([int]$processId) {
    try {
        $sample = Get-Counter -Counter '\GPU Engine(*)\Utilization Percentage' -MaxSamples 1 -ErrorAction Stop
        $values = @()
        foreach ($entry in $sample.CounterSamples) {
            if ($entry.InstanceName -like "*pid_$processId*") { $values += $entry.CookedValue }
        }
        if ($values.Count -eq 0) { return 0 }
        return ($values | Measure-Object -Maximum).Maximum
    } catch {
        return $null
    }
}

function Measure-Run([string]$kind, [int]$index) {
    $logStart = Get-LogLineCount
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process -FilePath $exePath -PassThru
    $windowHandle = [IntPtr]::Zero
    while ($stopwatch.Elapsed.TotalSeconds -lt 90) {
        $windowHandle = [PerfWin]::FindVisibleWindow([uint32]$process.Id)
        if ($windowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 20
    }
    $startMs = [math]::Round($stopwatch.Elapsed.TotalMilliseconds)
    if ($windowHandle -eq [IntPtr]::Zero) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "The main window never appeared ($kind run $index)."
    }

    Start-Sleep -Seconds $SettleSeconds
    $process.Refresh()
    $workingSetMb = [math]::Round($process.WorkingSet64 / 1MB, 1)
    $privateMb = [math]::Round($process.PrivateMemorySize64 / 1MB, 1)

    # Per-second sampling: peaks come from the early intervals (Home opening and its
    # thumbnails), the idle figures from the tail of the window.
    $cpuValues = @()
    $gpuValues = @()
    $previousCpu = $process.TotalProcessorTime
    for ($i = 1; $i -le $SampleSeconds; $i++) {
        $gpu = Measure-GpuOnce $process.Id
        $process.Refresh()
        $currentCpu = $process.TotalProcessorTime
        $cpuPercent = [math]::Round(
            (($currentCpu - $previousCpu).TotalMilliseconds / 1000 / $cpuCount) * 100, 3)
        $previousCpu = $currentCpu
        $cpuValues += $cpuPercent
        if ($null -ne $gpu) { $gpuValues += $gpu }
    }

    $tail = [math]::Max(1, [int]($cpuValues.Count / 2))
    $cpuIdle = [math]::Round(($cpuValues | Select-Object -Last $tail | Measure-Object -Average).Average, 3)
    $cpuPeak = [math]::Round(($cpuValues | Measure-Object -Maximum).Maximum, 3)
    $gpuIdle = if ($gpuValues.Count -gt 0) { [math]::Round(($gpuValues | Select-Object -Last $tail | Measure-Object -Average).Average, 2) } else { $null }
    $gpuPeak = if ($gpuValues.Count -gt 0) { [math]::Round(($gpuValues | Measure-Object -Maximum).Maximum, 2) } else { $null }

    # "Idle 1 minute" memory: wait out the remainder of the long idle window.
    $alreadyWaited = $SettleSeconds + $SampleSeconds
    if ($LongIdleSeconds -gt $alreadyWaited) {
        Start-Sleep -Seconds ($LongIdleSeconds - $alreadyWaited)
    }
    $process.Refresh()
    $workingSetIdleMb = [math]::Round($process.WorkingSet64 / 1MB, 1)
    $privateIdleMb = [math]::Round($process.PrivateMemorySize64 / 1MB, 1)

    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 1200

    $timeline = @()
    if ($logFile) {
        $all = Get-Content $logFile.FullName -Encoding UTF8
        $lines = if ($all.Count -gt $logStart) { $all[$logStart..($all.Count - 1)] } else { @() }
        foreach ($line in $lines) {
            if ($line -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})') {
                $text = $line.Substring(24)
                if ($text -match 'Startup: (.+?) at ([\d.]+) ms') {
                    $timeline += [pscustomobject]@{ Phase = $Matches[1]; Ms = [double]$Matches[2] }
                } elseif ($text -match 'Page ''(\w+)'' built in ([\d.]+) ms') {
                    $timeline += [pscustomobject]@{ Phase = "page $($Matches[1])"; Ms = [double]$Matches[2] }
                } elseif ($text -match 'Thumbnails warmed: (\d+) of (\d+) fetched.*in ([\d.]+) ms') {
                    $timeline += [pscustomobject]@{ Phase = "thumbnails ($($Matches[1])/$($Matches[2]))"; Ms = [double]$Matches[3] }
                } elseif ($text -match 'Home feed loaded with (\d+) wallpapers') {
                    $timeline += [pscustomobject]@{ Phase = "home feed ($($Matches[1]))"; Ms = -1 }
                }
            }
        }
    }

    $phase = { param($name) ($timeline | Where-Object { $_.Phase -like $name } | Select-Object -First 1).Ms }
    return [pscustomobject]@{
        Kind = $kind
        Index = $index
        StartMs = $startMs
        WindowShownMs = & $phase 'window shown*'
        UiIdleMs = & $phase 'ui idle*'
        CatalogMs = & $phase 'catalog loaded'
        WorkingSetMb = $workingSetMb
        PrivateMb = $privateMb
        WorkingSetAfterIdleMb = $workingSetIdleMb
        PrivateAfterIdleMb = $privateIdleMb
        IdleCpuPercent = $cpuIdle
        CpuPeakPercent = $cpuPeak
        IdleGpuPercent = $gpuIdle
        GpuPeakPercent = $gpuPeak
        Timeline = $timeline
    }
}

Write-Host "Measuring $exePath"
Write-Host "Log: $($logFile.FullName)"
Write-Host ""

$results = @()
for ($i = 1; $i -le $ColdRuns; $i++) {
    Write-Host "cold run $i ..."
    $results += Measure-Run 'cold' $i
}
for ($i = 1; $i -le $WarmRuns; $i++) {
    Write-Host "warm run $i ..."
    $results += Measure-Run 'warm' $i
}

Write-Host ""
Write-Host "=== $Label : individual runs ==="
$results | Select-Object Kind, Index, StartMs, WindowShownMs, UiIdleMs, WorkingSetMb, WorkingSetAfterIdleMb,
    IdleCpuPercent, CpuPeakPercent, IdleGpuPercent, GpuPeakPercent | Format-Table | Out-String | Write-Host

foreach ($kind in ($results | Select-Object -ExpandProperty Kind -Unique)) {
    $group = $results | Where-Object { $_.Kind -eq $kind }
    $average = [pscustomobject]@{
        Runs = $group.Count
        StartMs = [math]::Round(($group.StartMs | Measure-Object -Average).Average)
        WindowShownMs = [math]::Round(($group.WindowShownMs | Measure-Object -Average).Average)
        UiIdleMs = [math]::Round(($group.UiIdleMs | Measure-Object -Average).Average)
        WorkingSetMb = [math]::Round(($group.WorkingSetMb | Measure-Object -Average).Average, 1)
        WorkingSetAfterIdleMb = [math]::Round(($group.WorkingSetAfterIdleMb | Measure-Object -Average).Average, 1)
        IdleCpuPercent = [math]::Round(($group.IdleCpuPercent | Measure-Object -Average).Average, 3)
        CpuPeakPercent = [math]::Round(($group.CpuPeakPercent | Measure-Object -Average).Average, 3)
        IdleGpuPercent = [math]::Round(($group.IdleGpuPercent | Measure-Object -Average).Average, 2)
        GpuPeakPercent = [math]::Round(($group.GpuPeakPercent | Measure-Object -Average).Average, 2)
    }
    Write-Host "=== $Label : $kind average ==="
    $average | Format-Table | Out-String | Write-Host
}

foreach ($result in $results) {
    Write-Host "--- $($result.Kind) run $($result.Index) timeline ---"
    $result.Timeline | Format-Table Phase, Ms | Out-String | Write-Host
}

$report = [pscustomobject]@{
    Label = $Label
    Exe = $exePath
    CpuCount = $cpuCount
    SampleSeconds = $SampleSeconds
    LongIdleSeconds = $LongIdleSeconds
    MeasuredAt = (Get-Date).ToString('s')
    Runs = $results
}
$reportPath = Join-Path $outPath "$Label.json"
$report | ConvertTo-Json -Depth 6 | Set-Content -Path $reportPath -Encoding UTF8
Write-Host "report written to $reportPath"
