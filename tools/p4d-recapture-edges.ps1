<#
    Re-captures the two edge states as a matched pair, with the shelf's scroll position recorded.

    An earlier attempt captured "right edge" while the desktop shelf happened to be scrolled to a different
    region than the other captures, so the pair was not comparable and one frame showed the shelf mid-repaint.
    Both frames here are taken from the same freshly-settled resting state, with the shelf position read before
    and after so the comparison is only made between like states.

    Usage: ./tools/p4d-recapture-edges.ps1 -DockHwnd <id>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $DockHwnd,
    [string] $ShotDir = 'screenshots'
)

$ErrorActionPreference = 'Stop'
$native = Join-Path $env:USERPROFILE '.dsh\profiles\web-computer-test\node_modules\dsh-computer-use-windows\native\publish\DshComputerUse.Native.exe'
$profLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'

Add-Type -Namespace Rec -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
public const uint MOVE=0x0001, ABSOLUTE=0x8000, WHEEL=0x0800;
public struct RECT { public int Left, Top, Right, Bottom; }
public struct POINT { public int X; public int Y; }
'@
$script:SW = [Rec.W]::GetSystemMetrics(0)
$script:SH = [Rec.W]::GetSystemMetrics(1)
function Ptr([int]$x, [int]$y) {
    [Rec.W]::mouse_event([Rec.W]::MOVE -bor [Rec.W]::ABSOLUTE,
        [int](($x * 65535) / ($script:SW - 1)), [int](($y * 65535) / ($script:SH - 1)), 0, [IntPtr]::Zero)
}
function Wheel([int]$d) {
    $u = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$d), 0)
    [Rec.W]::mouse_event([Rec.W]::WHEEL, 0, 0, $u, [IntPtr]::Zero)
}

# The shelf's scroll position is part of the state a dock screenshot has to record, because the shelf holds more
# icons than the viewport shows. Read it as the x of the left-most rendered item.
function ShelfFirstItemX {
    $obs = (& $native observe --hwnd $DockHwnd --maxElements 500 2>&1 | Out-String | ConvertFrom-Json)
    $items = $obs.elements | Where-Object { $_.name -eq 'Icon' -and $_.bounds.width -gt 0 -and $_.bounds.y -lt 100 } |
        Sort-Object { $_.bounds.x }
    if ($items.Count -eq 0) { return -1 }
    $items[0].bounds.x
}

$h = [IntPtr][int]$DockHwnd
$cr = New-Object Rec.W+RECT
[Rec.W]::GetClientRect($h, [ref]$cr) | Out-Null
$pt = New-Object Rec.W+POINT
[Rec.W]::ClientToScreen($h, [ref]$pt) | Out-Null

# Settle to rest first: the shelf is scrolled back to its origin so both edge captures start from the same place.
Ptr ($pt.X - 200) ($pt.Y - 200)
Start-Sleep -Seconds 1
Ptr ($pt.X + 300) ($pt.Y + 45)
Start-Sleep -Milliseconds 600
for ($i = 0; $i -lt 40; $i++) { Wheel 120; Start-Sleep -Milliseconds 30 }
Start-Sleep -Seconds 2
Ptr ($pt.X - 200) ($pt.Y - 200)
Start-Sleep -Seconds 3

$shelfX = ShelfFirstItemX
Write-Host "shelf left-most item x (window-relative) after reset: $shelfX" -ForegroundColor DarkGray

$railY = $pt.Y + 45
# Icon centres, in the window's own client coordinates, taken from the live element bounds rather than guessed:
# the left-most and right-most icons the shelf is currently showing.
$obs = (& $native observe --hwnd $DockHwnd --maxElements 500 2>&1 | Out-String | ConvertFrom-Json)
$shown = $obs.elements | Where-Object { $_.name -eq 'Icon' -and $_.bounds.width -gt 0 -and $_.bounds.height -gt 40 } |
    Sort-Object { $_.bounds.x }
if ($shown.Count -lt 2) { throw "only $($shown.Count) icons visible to automation; cannot place the pointer reliably" }
$leftIcon = $pt.X + [int]($shown[0].bounds.x + $shown[0].bounds.width / 2)
$rightIcon = $pt.X + [int]($shown[-1].bounds.x + $shown[-1].bounds.width / 2)
Write-Host "  left icon screen x=$leftIcon  right icon screen x=$rightIcon  railY=$railY" -ForegroundColor DarkGray

function Snap([string]$name, [int]$x) {
    Ptr $x $railY
    Start-Sleep -Seconds 2
    & (Join-Path $PSScriptRoot 'p4d-final-capture.ps1') -DockHwnd $DockHwnd -Name $name -Label 'matched edge pair' -ShotDir $ShotDir
    $r = @(Get-Content $profLog | Select-String '"name":"motion\.rebuild"' | ForEach-Object { $_.Line | ConvertFrom-Json })
    if ($r.Count -and -not $r[-1].inside) {
        Write-Host "  WARNING: the dock reports inside=False - this frame captured the dock at rest, not under the pointer" -ForegroundColor Yellow
    }
}

Write-Host '=== LEFT EDGE ===' -ForegroundColor Cyan
Snap '04-left-edge' $leftIcon

Write-Host ''
Write-Host '=== RIGHT EDGE ===' -ForegroundColor Cyan
Snap '05-right-edge' $rightIcon

Write-Host ''
$shelfX2 = ShelfFirstItemX
"shelf left-most item x after both captures: $shelfX2   (unchanged = the pair is comparable)"
