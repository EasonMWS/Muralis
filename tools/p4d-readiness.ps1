<#
    Repeatability run for the XAML layout-readiness question.

    Enters the dock onto the same pinned app repeatedly, leaving and settling between cycles, and reports the
    instrumented readiness timeline. The point is to see, at the instant Nexus publishes its first magnified
    visual, whether the XAML tree had adopted the expanded size or was still reporting resting geometry.

    Usage: ./tools/p4d-readiness.ps1 -DockHwnd <id> [-Cycles 20]
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $DockHwnd,
    [int] $Cycles = 20,
    [string] $WorkDir = 'D:\AI\temp\dsh-cu-eval\stageB\readiness'
)

$ErrorActionPreference = 'Stop'
$profLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

Add-Type -Namespace Rdy -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
public const uint MOVE=0x0001, ABSOLUTE=0x8000;
public struct RECT { public int Left, Top, Right, Bottom; }
public struct POINT { public int X, Y; }
'@
$script:SW = [Rdy.W]::GetSystemMetrics(0)
$script:SH = [Rdy.W]::GetSystemMetrics(1)
function Ptr([int]$x, [int]$y) {
    [Rdy.W]::mouse_event([Rdy.W]::MOVE -bor [Rdy.W]::ABSOLUTE,
        [int](($x * 65535) / ($script:SW - 1)), [int](($y * 65535) / ($script:SH - 1)), 0, [IntPtr]::Zero)
}

$hwnd = [IntPtr][int]$DockHwnd
$wr = New-Object Rdy.W+RECT; [Rdy.W]::GetWindowRect($hwnd, [ref]$wr) | Out-Null
$pt = New-Object Rdy.W+POINT; [Rdy.W]::ClientToScreen($hwnd, [ref]$pt) | Out-Null

# The dock's interaction strip is its own drawn bounds, not the whole client: aim inside it, never at an absolute
# client x that may fall in the empty area left of the icons.
$m = @(Get-Content $profLog | Select-String '"name":"motion\.outside"' | ForEach-Object { $_.Line | ConvertFrom-Json })
if ($m.Count -eq 0) { throw 'no motion.outside record: is the dock up with the profiler on?' }
$last = $m[-1]
$dockLeftScreen = [int]$last.originX + [int]$last.left
$dockRightScreen = [int]$last.originX + [int]$last.right
$outX = $dockLeftScreen - 150
$outY = 200
$enterX = $dockLeftScreen + 300
$railY = $pt.Y + 45

Write-Host "=== READINESS CYCLES ($Cycles) ===" -ForegroundColor Cyan
"  dock window     : ($($wr.Left),$($wr.Top)) $($wr.Right-$wr.Left)x$($wr.Bottom-$wr.Top)"
"  client origin   : ($($pt.X),$($pt.Y))"
"  interaction strip (screen x): $dockLeftScreen .. $dockRightScreen"
"  park at         : ($outX,$outY)"
"  step-1 enter    : ($enterX,$railY)"

$startLine = @(Get-Content $profLog).Count

for ($c = 1; $c -le $Cycles; $c++) {
    Ptr $outX $outY
    Start-Sleep -Milliseconds 240
    Ptr ($outX + 25) ($outY + 15)
    Start-Sleep -Milliseconds 620
    # Step 1: land inside the interaction strip. This is the report that triggers the expansion.
    Ptr $enterX $railY
    Start-Sleep -Milliseconds 300
    Ptr ($enterX + 6) $railY
    Start-Sleep -Milliseconds 300
    Ptr $outX $outY
    Start-Sleep -Milliseconds 260
}

Start-Sleep -Milliseconds 900
$new = @(Get-Content $profLog | Select-Object -Skip $startLine)
$marks = @($new | Select-String '"name":"motion\.readiness"' | ForEach-Object { $_.Line | ConvertFrom-Json })
"  new log lines  : $($new.Count)"
"  readiness marks: $($marks.Count)"
if ($marks.Count -eq 0) { Write-Host '  NO MARKS - the probe did not fire' -ForegroundColor Yellow; return }

$marks | Select-Object seq,point,us,th,passes,scale,visualTop,sizes |
    Sort-Object { [int]$_.seq }, { [int]$_.us } |
    Export-Csv -NoTypeInformation -Encoding UTF8 -Path "$WorkDir\readiness.csv"

$seqs = $marks | Group-Object seq | Sort-Object { [int]$_.Name }
"  sequences      : $($seqs.Count)"
""
"### PER-SEQUENCE TIMELINE"
foreach ($g in $seqs) {
    "  --- seq $($g.Name) ---"
    foreach ($m in ($g.Group | Sort-Object { [int]$_.us })) {
        "    {0,7}us  {1,-30} passes={2,-3} scale={3,7} visualTop={4,8}" -f $m.us, $m.point, $m.passes, $m.scale, $m.visualTop
        "              {0}" -f $m.sizes
    }
}
""
"### SUMMARY"
$pubs = @($marks | Where-Object { $_.point -eq 'T10.published' })
"  publish samples                    : $($pubs.Count)"
"  samples with scale > 1             : $(@($pubs | Where-Object { [double]$_.scale -gt 1 }).Count)"
"  samples with visualTop < 0         : $(@($pubs | Where-Object { [double]$_.visualTop -lt 0 }).Count)"
""
"  At publish, does the string still say root=...x110 (resting) or x159 (expanded)?"
$pubs | ForEach-Object {
    $s = $_.sizes
    $rootH = if ($s -match 'root=[\d.]+x([\d.]+)') { $Matches[1] } else { '?' }
    $trackerH = if ($s -match 'tracker=[\d.]+x([\d.]+)') { $Matches[1] } else { '?' }
    $native = if ($s -match 'native=(\d+x\d+)') { $Matches[1] } else { '?' }
    "    seq {0,-3} rootH={1,-8} trackerH={2,-8} native={3,-9} passes={4,-3} scale={5,-7} visualTop={6}" -f `
        $_.seq, $rootH, $trackerH, $native, $_.passes, $_.scale, $_.visualTop
}
