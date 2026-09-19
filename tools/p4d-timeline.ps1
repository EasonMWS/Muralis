<#
    Runs enter-to-peak cycles against a pinned dock app and reports the instrumented pointer-entry timeline.

    The question is purely ordinal: did the first magnified visual get published while the dock's client was
    still the resting height? Everything is read from the product's `motion.timeline` marks, which are stamped
    from a monotonic clock, so ordering is never inferred from wall-clock timestamps.

    Usage: ./tools/p4d-timeline.ps1 -DockHwnd <id> [-Cycles 20]
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $DockHwnd,
    [int] $Cycles = 20,
    [string] $WorkDir = 'D:\AI\temp\dsh-cu-eval\stageB\closure'
)

$ErrorActionPreference = 'Stop'
$profLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

Add-Type -Namespace Tlc -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
public const uint MOVE=0x0001, ABSOLUTE=0x8000;
public struct RECT { public int Left, Top, Right, Bottom; }
public struct POINT { public int X, Y; }
'@
$script:SW = [Tlc.W]::GetSystemMetrics(0)
$script:SH = [Tlc.W]::GetSystemMetrics(1)
function Ptr([int]$x, [int]$y) {
    [Tlc.W]::mouse_event([Tlc.W]::MOVE -bor [Tlc.W]::ABSOLUTE,
        [int](($x * 65535) / ($script:SW - 1)), [int](($y * 65535) / ($script:SH - 1)), 0, [IntPtr]::Zero)
}

$hwnd = [IntPtr][int]$DockHwnd
$wr = New-Object Tlc.W+RECT; [Tlc.W]::GetWindowRect($hwnd, [ref]$wr) | Out-Null
$pt = New-Object Tlc.W+POINT; [Tlc.W]::ClientToScreen($hwnd, [ref]$pt) | Out-Null
# The dock's resting strip; enter from far outside it, then step in.
$outX = 200
$outY = 200
$railY = $pt.Y + 45
$targetX = $pt.X + 76 + 27

Write-Host "=== TIMELINE CYCLES ($Cycles) ===" -ForegroundColor Cyan
"  dock window   : ($($wr.Left),$($wr.Top)) $($wr.Right-$wr.Left)x$($wr.Bottom-$wr.Top)"
"  client origin : ($($pt.X),$($pt.Y))"
"  enter target  : screen ($targetX,$railY)   park at ($outX,$outY)"

$before = @(Get-Content $profLog).Count

for ($c = 1; $c -le $Cycles; $c++) {
    # Leave and settle fully, so each cycle starts from resting bounds.
    Ptr $outX $outY
    Start-Sleep -Milliseconds 260
    Ptr ($outX + 30) ($outY + 20)
    Start-Sleep -Milliseconds 520
    # Enter: one move straight onto the icon, so the entry report is the one that matters.
    Ptr $targetX $railY
    Start-Sleep -Milliseconds 420
    Ptr $targetX $railY
    Start-Sleep -Milliseconds 300
    Ptr $outX $outY
    Start-Sleep -Milliseconds 260
}

Start-Sleep -Milliseconds 900
$after = @(Get-Content $profLog).Count
"  log lines added: $($after - $before)"

$marks = @(Get-Content $profLog | Select-Object -Skip $before | Select-String '"name":"motion\.timeline"' | ForEach-Object { $_.Line | ConvertFrom-Json })
"  timeline marks : $($marks.Count)"
if ($marks.Count -eq 0) { Write-Host '  NO TIMELINE MARKS - instrumentation did not fire' -ForegroundColor Yellow; return }

$seqs = $marks | Group-Object seq | Sort-Object { [int]$_.Name }
"  sequences      : $($seqs.Count)"
""
"### PER-SEQUENCE TIMELINE (microseconds from entry)"
foreach ($g in $seqs) {
    "  --- seq $($g.Name) ---"
    foreach ($m in ($g.Group | Sort-Object us)) {
        "    {0,8}us  {1,-38} client={2,-14} scale={3,8} visualTop={4,8} th={5}" -f `
            $m.us, $m.point, $m.client, $m.scale, $m.visualTop, $m.th
    }
}

$marks | Select-Object seq,point,us,th,client,scale,visualTop,note |
    Export-Csv -NoTypeInformation -Encoding UTF8 -Path "$WorkDir\timeline.csv"
"  wrote $WorkDir\timeline.csv"
