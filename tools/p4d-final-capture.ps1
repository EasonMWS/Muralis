<#
    Stage B final runtime & visual acceptance — capture helper.

    Saves a screenshot of the dock window and writes the runtime evidence that goes with it. The screenshot is a
    straight copy of what the native observer captured of the live window: never cropped, scaled or retouched, so
    it can be handed to a reviewer as evidence rather than as an illustration.

    Usage:
      ./tools/p4d-final-capture.ps1 -DockHwnd <id> -Name 01-resting -Label "resting" [-Note "..."]

    Prints the measured state and confirms the file exists and is non-empty. A capture that produced no file is
    reported as UNAVAILABLE; it is never described as saved.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $DockHwnd,
    [Parameter(Mandatory = $true)][string] $Name,
    [Parameter(Mandatory = $true)][string] $Label,
    [string] $Note = '',
    [string] $ShotDir = 'screenshots',
    [string] $WorkDir = 'D:\AI\temp\dsh-cu-eval\stageB\final-acceptance'
)

$ErrorActionPreference = 'Stop'
$native = Join-Path $env:USERPROFILE '.dsh\profiles\web-computer-test\node_modules\dsh-computer-use-windows\native\publish\DshComputerUse.Native.exe'
$profLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
New-Item -ItemType Directory -Force -Path $ShotDir, $WorkDir | Out-Null

$stamp = Get-Date -Format 'HH:mm:ss.fff'
Write-Host "=== CAPTURE $Name ($Label) @ $stamp ===" -ForegroundColor Cyan
if ($Note) { "  note: $Note" }

# --- window geometry, read from the window itself ---
Add-Type -Namespace Fin -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
public struct RECT { public int Left, Top, Right, Bottom; }
public struct POINT { public int X, Y; }
'@
$wr = New-Object Fin.W+RECT
[Fin.W]::GetWindowRect([IntPtr][int]$DockHwnd, [ref]$wr) | Out-Null
$cr = New-Object Fin.W+RECT
[Fin.W]::GetClientRect([IntPtr][int]$DockHwnd, [ref]$cr) | Out-Null
$pt = New-Object Fin.W+POINT
[Fin.W]::ClientToScreen([IntPtr][int]$DockHwnd, [ref]$pt) | Out-Null
$winW = $wr.Right - $wr.Left; $winH = $wr.Bottom - $wr.Top
$cliW = $cr.Right - $cr.Left; $cliH = $cr.Bottom - $cr.Top
"  window rect : ($($wr.Left),$($wr.Top)) ${winW}x${winH}"
"  client      : origin ($($pt.X),$($pt.Y)) ${cliW}x${cliH}"

# --- profiler state from this run ---
function Marks([string]$n) { @(Get-Content $profLog -ErrorAction SilentlyContinue | Select-String $n | ForEach-Object { $_.Line | ConvertFrom-Json }) }
$rb = @(Marks '"name":"motion\.rebuild"')
if ($rb.Count) {
    $r = $rb[-1]
    "  dock state  : icons=$($r.icons) capacity=$($r.capacity) rebuilds=$($r.rebuilds) updates=$($r.updates) raw=$($r.raw) inside=$($r.inside)"
    "  measured    : dockLeft=$($r.dockLeft) dockRight=$($r.dockRight) dockTop=$($r.dockTop) dockBottom=$($r.dockBottom) breakAt=$($r.breakAt) narrowAt=$($r.narrowAt)"
    "  motion      : peak=$($r.peak) reach=$($r.reach)"
}
$perf = @(Marks '"name":"motion\.performance"')
if ($perf.Count) {
    $q = $perf[-1]
    "  perf        : reason=$($q.reason) queued=$($q.queued) applied=$($q.applied) dropped=$($q.dropped) p50=$($q.latencyP50Us)us p95=$($q.latencyP95Us)us p99=$($q.latencyP99Us)us max=$($q.latencyMaxUs)us"
}

# --- capture, then verify the file actually exists ---
$before = @(Get-ChildItem "$WorkDir\window-*.png" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
& $native observe --hwnd $DockHwnd --maxElements 500 --outputDir $WorkDir 2>&1 | Out-Null
$after = @(Get-ChildItem "$WorkDir\window-*.png" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime)
$fresh = $after | Where-Object { $_.FullName -notin $before } | Select-Object -Last 1
if (-not $fresh) { $fresh = $after | Select-Object -Last 1 }

$dest = Join-Path $ShotDir "stageB-final-$Name.png"
if ($fresh -and $fresh.Length -gt 0) {
    Copy-Item $fresh.FullName $dest -Force
    $fi = Get-Item $dest
    if ($fi.Length -gt 0) {
        "  screenshot  : $($fi.Name)  $($fi.Length) bytes  OK"
    } else {
        "  screenshot  : UNAVAILABLE (zero bytes)"
    }
} else {
    "  screenshot  : UNAVAILABLE (observer produced no image)"
    $dest = $null
}

[pscustomobject]@{
    Name = $Name; Label = $Label; Time = $stamp
    WinLeft = $wr.Left; WinTop = $wr.Top; WinW = $winW; WinH = $winH
    ClientW = $cliW; ClientH = $cliH
    Icons = if ($rb.Count) { $rb[-1].icons } else { $null }
    Capacity = if ($rb.Count) { $rb[-1].capacity } else { $null }
    Updates = if ($rb.Count) { $rb[-1].updates } else { $null }
    Peak = if ($rb.Count) { $rb[-1].peak } else { $null }
    Reach = if ($rb.Count) { $rb[-1].reach } else { $null }
    BreakAt = if ($rb.Count) { $rb[-1].breakAt } else { $null }
    NarrowAt = if ($rb.Count) { $rb[-1].narrowAt } else { $null }
    Dropped = if ($perf.Count) { $perf[-1].dropped } else { $null }
    Shot = $dest
} | Export-Csv -NoTypeInformation -Encoding UTF8 -Append -Path "$WorkDir\captures.csv"
