<#
    Hovers one pinned dock app precisely and measures its glyph, tile and label as separate objects.

    Entering the dock expands the window, which moves the client origin, so a screen coordinate computed from
    the resting geometry lands in the wrong place. This re-reads the window after the pointer is already
    inside and only then aims at the icon, so the aim is taken in the geometry that is actually on screen.

    Usage: ./tools/p4d-hover-app.ps1 -DockHwnd <id> -Name appscale-02-hover-a -IconX 54
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $DockHwnd,
    [Parameter(Mandatory = $true)][string] $Name,
    [Parameter(Mandatory = $true)][int] $IconX,
    [int] $IconW = 52,
    [string] $ShotDir = 'screenshots',
    [string] $WorkDir = 'D:\AI\temp\dsh-cu-eval\stageB\final-acceptance'
)

$ErrorActionPreference = 'Stop'
$native = Join-Path $env:USERPROFILE '.dsh\profiles\web-computer-test\node_modules\dsh-computer-use-windows\native\publish\DshComputerUse.Native.exe'
$profLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
New-Item -ItemType Directory -Force -Path $ShotDir, $WorkDir | Out-Null

Add-Type -Namespace Hov -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
public const uint MOVE=0x0001, ABSOLUTE=0x8000;
public struct POINT { public int X; public int Y; }
public struct RECT { public int Left, Top, Right, Bottom; }
'@
$script:SW = [Hov.W]::GetSystemMetrics(0)
$script:SH = [Hov.W]::GetSystemMetrics(1)
function Ptr([int]$x, [int]$y) {
    [Hov.W]::mouse_event([Hov.W]::MOVE -bor [Hov.W]::ABSOLUTE,
        [int](($x * 65535) / ($script:SW - 1)), [int](($y * 65535) / ($script:SH - 1)), 0, [IntPtr]::Zero)
}
function ClientOrigin([IntPtr]$h) {
    $p = New-Object Hov.W+POINT
    [Hov.W]::ClientToScreen($h, [ref]$p) | Out-Null
    $p
}
function Marks([string]$n) { @(Get-Content $profLog -ErrorAction SilentlyContinue | Select-String $n | ForEach-Object { $_.Line | ConvertFrom-Json }) }

$hwnd = [IntPtr][int]$DockHwnd

# Step 1: nudge the pointer into the dock so it expands. Use the resting geometry only to get roughly inside.
$o = ClientOrigin $hwnd
$wr = New-Object Hov.W+RECT
[Hov.W]::GetWindowRect($hwnd, [ref]$wr) | Out-Null
$railY0 = [int]($o.Y + (($wr.Bottom - $wr.Top) * 0.46))
Ptr ($o.X + 320) $railY0
Start-Sleep -Seconds 2
Ptr ($o.X + 330) $railY0
Start-Sleep -Seconds 2

# Step 2: the window has moved; re-read and aim for real.
$o = ClientOrigin $hwnd
[Hov.W]::GetWindowRect($hwnd, [ref]$wr) | Out-Null
$railY = [int]($o.Y + (($wr.Bottom - $wr.Top) * 0.46))
$targetX = $o.X + $IconX + [int]($IconW / 2)
Ptr $targetX $railY
Start-Sleep -Milliseconds 900
Ptr $targetX $railY
Start-Sleep -Seconds 2

Write-Host "=== HOVER $Name ===" -ForegroundColor Cyan
"  client origin now : ($($o.X),$($o.Y))   window $($wr.Right-$wr.Left)x$($wr.Bottom-$wr.Top)"
"  rail y            : $railY"
"  aimed at screen x : $targetX  (icon local x=$IconX w=$IconW)"

& (Join-Path $PSScriptRoot 'p4d-final-capture.ps1') -DockHwnd $DockHwnd -Name $Name -Label 'pinned app hover' -ShotDir $ShotDir

$r = @(Marks '"name":"motion\.rebuild"')
if ($r.Count) {
    $l = $r[-1]
    "  dock state        : icons=$($l.icons) capacity=$($l.capacity) peak=$($l.peak) reach=$($l.reach) inside=$($l.inside) breakAt=$($l.breakAt) narrowAt=$($l.narrowAt)"
    if (-not $l.inside) { Write-Host '  WARNING: dock reports inside=False - this is NOT a hover frame' -ForegroundColor Yellow }
}
