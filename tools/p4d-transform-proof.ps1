<#
    Drives a pinned dock icon through a scale progression and reports the instrumented transform at each step.

    Reads the product's own `motion.transform` diagnostic rather than a screenshot: the composition scale, the
    drawn rect against the dock's client area, and the icon image's own size are recorded from the live elements
    after Publish. Screenshots cannot answer this question because the dock window clips the magnified icon.

    Usage: ./tools/p4d-transform-proof.ps1 -DockHwnd <id> -Name transform-01-rest [options]
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $DockHwnd,
    [Parameter(Mandatory = $true)][string] $Name,
    [int] $IconLocalX = 76,
    [int] $IconLocalW = 55,
    [string] $ShotDir = 'screenshots',
    [string] $WorkDir = 'D:\AI\temp\dsh-cu-eval\stageB\transform-proof',
    # 'rest' parks far away; 'centre' lands on one icon; 'between' lands on the midpoint of the two pins.
    [ValidateSet('rest', 'centre', 'between')][string] $Mode = 'centre'
)

$ErrorActionPreference = 'Stop'
$native = Join-Path $env:USERPROFILE '.dsh\profiles\web-computer-test\node_modules\dsh-computer-use-windows\native\publish\DshComputerUse.Native.exe'
$profLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
New-Item -ItemType Directory -Force -Path $ShotDir, $WorkDir | Out-Null

Add-Type -Namespace Tp -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
public const uint MOVE=0x0001, ABSOLUTE=0x8000;
public struct POINT { public int X; public int Y; }
'@
$script:SW = [Tp.W]::GetSystemMetrics(0)
$script:SH = [Tp.W]::GetSystemMetrics(1)
function Ptr([int]$x, [int]$y) {
    [Tp.W]::mouse_event([Tp.W]::MOVE -bor [Tp.W]::ABSOLUTE,
        [int](($x * 65535) / ($script:SW - 1)), [int](($y * 65535) / ($script:SH - 1)), 0, [IntPtr]::Zero)
}
function Origin([IntPtr]$h) { $p = New-Object Tp.W+POINT; [Tp.W]::ClientToScreen($h, [ref]$p) | Out-Null; $p }
function Marks([string]$n) { @(Get-Content $profLog -ErrorAction SilentlyContinue | Select-String $n | ForEach-Object { $_.Line | ConvertFrom-Json }) }

$hwnd = [IntPtr][int]$DockHwnd
$o = Origin $hwnd
$railY = $o.Y + 70

# The two adjacent pins, as the fixture placed them, in the dock's own client coordinates.
$calcCentre = $o.X + $IconLocalX + [int]($IconLocalW / 2)
$notepadCentre = $o.X + 153 + 24
$midX = [int](($calcCentre + $notepadCentre) / 2)

Write-Host "=== TRANSFORM PROOF: $Mode -> $Name ===" -ForegroundColor Cyan
"  client origin      : ($($o.X),$($o.Y))"
"  rail y             : $railY"
"  Calculator centre  : screen x=$calcCentre"
"  Notepad centre     : screen x=$notepadCentre"
"  midpoint           : screen x=$midX"

switch ($Mode) {
    'rest'    { Ptr 200 200; Start-Sleep -Milliseconds 400; Ptr 240 220; Start-Sleep -Seconds 4 }
    'centre'  { Ptr $calcCentre $railY; Start-Sleep -Milliseconds 700; Ptr $calcCentre $railY; Start-Sleep -Seconds 2 }
    'between' { Ptr $midX $railY; Start-Sleep -Milliseconds 700; Ptr $midX $railY; Start-Sleep -Seconds 2 }
}

& (Join-Path $PSScriptRoot 'p4d-final-capture.ps1') -DockHwnd $DockHwnd -Name $Name -Label "transform proof $Mode" -ShotDir $ShotDir

$all = @(Marks '"name":"motion\.transform"')
"  motion.transform records so far: $($all.Count)"
$all | Export-Csv -NoTypeInformation -Encoding UTF8 -Path "$WorkDir\transform-all.csv"
"  wrote $WorkDir\transform-all.csv"
