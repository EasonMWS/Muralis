<#
    Drives the pointer onto a pinned dock icon and captures a coherent evidence set in ONE pass.

    Diagnosis of the previous attempt: the dock's client origin was read before the window had finished
    expanding, so the aim landed on empty pixels and the capture showed a dock at rest while the transform
    record showed an expanded one. The two halves of the evidence disagreed because they were taken at
    different window states.

    This reads the window rectangle, aims, waits for the window to stop moving, re-reads it, re-aims, and only
    then captures - so the screenshot and the transform record describe the same moment.

    Usage: ./tools/p4d-pin-drive.ps1 -DockHwnd <id> -Name transform-03-max-scale -IconIndex 0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $DockHwnd,
    [Parameter(Mandatory = $true)][string] $Name,
    # 0 = first pinned app, 1 = second. 'pair' hovers the midpoint of the two.
    [ValidateSet('0', '1', 'pair', 'rest')][string] $Target = '0',
    [string] $ShotDir = 'screenshots'
)

$ErrorActionPreference = 'Stop'
$profLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'

Add-Type -Namespace Pd -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
public const uint MOVE=0x0001, ABSOLUTE=0x8000;
public struct RECT { public int Left, Top, Right, Bottom; }
public struct POINT { public int X, Y; }
'@
$script:SW = [Pd.W]::GetSystemMetrics(0)
$script:SH = [Pd.W]::GetSystemMetrics(1)
function Ptr([int]$x, [int]$y) {
    [Pd.W]::mouse_event([Pd.W]::MOVE -bor [Pd.W]::ABSOLUTE,
        [int](($x * 65535) / ($script:SW - 1)), [int](($y * 65535) / ($script:SH - 1)), 0, [IntPtr]::Zero)
}
function Geometry([IntPtr]$h) {
    $wr = New-Object Pd.W+RECT; [Pd.W]::GetWindowRect($h, [ref]$wr) | Out-Null
    $cr = New-Object Pd.W+RECT; [Pd.W]::GetClientRect($h, [ref]$cr) | Out-Null
    $pt = New-Object Pd.W+POINT; [Pd.W]::ClientToScreen($h, [ref]$pt) | Out-Null
    [pscustomobject]@{
        WinX = $wr.Left; WinY = $wr.Top; WinW = $wr.Right - $wr.Left; WinH = $wr.Bottom - $wr.Top
        CliX = $pt.X; CliY = $pt.Y; CliW = $cr.Right - $cr.Left; CliH = $cr.Bottom - $cr.Top
    }
}

$hwnd = [IntPtr][int]$DockHwnd

# Step 1: get inside so the window expands, using a point that is inside BOTH the resting and expanded strips.
$g = Geometry $hwnd
Ptr ($g.CliX + 400) ($g.WinY + 90)
Start-Sleep -Seconds 2
Ptr ($g.CliX + 410) ($g.WinY + 90)
Start-Sleep -Seconds 2

# Step 2: the window has moved; read it again and confirm it has stopped.
$g2 = Geometry $hwnd
if ($g2.CliX -eq $g.CliX -and $g2.WinH -eq $g.WinH) {
    Write-Host '  WARNING: window did not change size - expansion may not have happened' -ForegroundColor Yellow
}
"  window : ($($g2.WinX),$($g2.WinY)) $($g2.WinW)x$($g2.WinH)"
"  client : origin ($($g2.CliX),$($g2.CliY)) $($g2.CliW)x$($g2.CliH)"

# Pinned icons sit at client x=76 (first) and x=153 (second), 55 and 48 wide; icon box 52 tall.
$rail = $g2.CliY + 72
if ($Target -eq 'rest') {
    Ptr 200 200; Start-Sleep -Milliseconds 400; Ptr 240 220; Start-Sleep -Seconds 4
    $aimX = 240
}
else {
    $a = $g2.CliX + 76 + 27
    $b = $g2.CliX + 153 + 24
    $aimX = switch ($Target) { '0' { $a } '1' { $b } 'pair' { [int](($a + $b) / 2) } }
    Ptr $aimX $rail; Start-Sleep -Milliseconds 800
    Ptr $aimX $rail; Start-Sleep -Seconds 3
}

# Step 3: re-read once more so the captured screenshot and the record describe the same window state.
$g3 = Geometry $hwnd
"  aim    : screen ($aimX,$rail)"
"  window at capture: ($($g3.WinX),$($g3.WinY)) $($g3.WinW)x$($g3.WinH)  client $($g3.CliW)x$($g3.CliH)"
if ($g3.WinW -ne $g2.WinW -or $g3.CliX -ne $g2.CliX) {
    Write-Host '  NOTE: the window moved between aim and capture; re-aiming once' -ForegroundColor Yellow
    if ($Target -ne 'rest') {
        $a = $g3.CliX + 76 + 27; $b = $g3.CliX + 153 + 24
        $aimX = switch ($Target) { '0' { $a } '1' { $b } 'pair' { [int](($a + $b) / 2) } }
        $rail = $g3.CliY + 72
        Ptr $aimX $rail; Start-Sleep -Milliseconds 800
        Ptr $aimX $rail; Start-Sleep -Seconds 3
        "  re-aimed at screen ($aimX,$rail)"
    }
}

& (Join-Path $PSScriptRoot 'p4d-final-capture.ps1') -DockHwnd $DockHwnd -Name $Name -Label "pin drive target=$Target" -ShotDir $ShotDir

$last = @(Get-Content $profLog | Select-String '"name":"motion\.transform"' | ForEach-Object { $_.Line | ConvertFrom-Json }) | Select-Object -Last 1
if ($last) {
    "  transform record: client=$($last.clientW)x$($last.clientH) inside=$($last.inside) pointerAlong=$($last.pointerAlong)"
    foreach ($p in $last.pins) {
        "    {0,-11} engine={1,7} SX={2,7} SY={3,7} visT={4,7} visB={5,7} over={6}" -f $p.label,
            [Math]::Round($p.engineScale,4),[Math]::Round($p.nexusScaleX,4),[Math]::Round($p.nexusScaleY,4),
            [Math]::Round($p.visualT,2),[Math]::Round($p.visualB,2),[Math]::Round($p.topOverflow,2)
    }
}
