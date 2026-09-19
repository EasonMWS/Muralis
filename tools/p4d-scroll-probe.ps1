<#
    Scroll probe: does the measured rail follow the shelf's scroll position?

    Hypothesis under test: the icons whose MotionTarget reports zero width are the ones the scroll
    viewport has clipped away, so the measured rail is only "the icons currently in view" and moves with
    the scroll offset. If scrolling the shelf changes railL and the icons count in the same direction, the
    measurement is scroll-dependent.

    If instead the counts do NOT track the scroll offset, the zero-width elements are not explained by
    clipping and the cause is elsewhere.

    Input is injected deliberately (wheel + pointer). This is a diagnostic, not a stability run.

    Usage: ./tools/p4d-scroll-probe.ps1 -DockHwnd <id>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $DockHwnd,
    [string] $OutDir = 'D:\AI\temp\dsh-cu-eval\stageB\rca'
)

$ErrorActionPreference = 'Stop'
$native = Join-Path $env:USERPROFILE '.dsh\profiles\web-computer-test\node_modules\dsh-computer-use-windows\native\publish\DshComputerUse.Native.exe'
$profLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -Namespace Scr -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
public const uint MOVE=0x0001, ABSOLUTE=0x8000, WHEEL=0x0800;
'@
$sw = [Scr.W]::GetSystemMetrics(0); $sh = [Scr.W]::GetSystemMetrics(1)
function Move-Ptr([int]$x,[int]$y) { [Scr.W]::mouse_event([Scr.W]::MOVE -bor [Scr.W]::ABSOLUTE, [int](($x*65535)/($sw-1)), [int](($y*65535)/($sh-1)), 0, [IntPtr]::Zero) }
function Wheel([int]$d) { $u = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$d),0); [Scr.W]::mouse_event([Scr.W]::WHEEL, 0, 0, $u, [IntPtr]::Zero) }
function Rebuilds { @(Get-Content $profLog | Select-String '"name":"motion\.rebuild"' | ForEach-Object { $_.Line | ConvertFrom-Json }) }
function LastRest { $r = @(Rebuilds | Where-Object { [Math]::Abs($_.originX - 803) -lt 0.5 }); if ($r.Count) { $r[-1] } else { $null } }
function UiaCounts {
    $o = (& $native observe --hwnd $DockHwnd --maxElements 700 --outputDir $OutDir 2>&1 | Out-String | ConvertFrom-Json)
    $all = @($o.elements) | Where-Object { $_.automationId -eq 'DockIconMotion' }
    [pscustomobject]@{
        Total = $all.Count
        NonZeroW = @($all | Where-Object { $_.bounds.width -gt 0 }).Count
        Widths = (($all | ForEach-Object { $_.bounds.width } | Sort-Object -Unique) -join '/')
        FirstX = if ($all.Count) { ($all | ForEach-Object { $_.bounds.x } | Measure-Object -Minimum).Minimum } else { -1 }
    }
}

Write-Host '=== SCROLL PROBE ===' -ForegroundColor Cyan
Write-Host '  pointer over the shelf, then wheel left/right; the pointer itself stays at one x.'
Write-Host ''

$rows = New-Object System.Collections.Generic.List[object]
function Sample([string]$label,[int]$wheelSteps) {
    Move-Ptr 1250 1363          # over the dock band, in the shelf region
    Start-Sleep -Milliseconds 400
    for ($i = 0; $i -lt [Math]::Abs($wheelSteps); $i++) { Wheel ([Math]::Sign($wheelSteps) * -120); Start-Sleep -Milliseconds 60 }
    Start-Sleep -Milliseconds 900
    $m = LastRest
    $u = UiaCounts
    $row = [pscustomobject]@{
        Label = $label; Wheel = $wheelSteps
        Icons = if ($m) { $m.icons } else { $null }
        RailL = if ($m) { [Math]::Round($m.dockLeft,1) } else { $null }
        RailR = if ($m) { [Math]::Round($m.dockRight,1) } else { $null }
        UiaTotal = $u.Total; UiaNonZeroW = $u.NonZeroW; UiaWidths = $u.Widths; UiaFirstX = $u.FirstX
    }
    $rows.Add($row)
    Write-Host ("  {0,-14} wheel={1,-5} icons={2,-4} railL={3,-7} railR={4,-7} uiaTotal={5,-4} uiaNonZeroW={6,-4} widths={7,-18} firstX={8}" -f `
        $row.Label,$row.Wheel,$row.Icons,$row.RailL,$row.RailR,$row.UiaTotal,$row.UiaNonZeroW,$row.UiaWidths,$row.UiaFirstX)
}

Sample 'baseline' 0
Sample 'scroll-right' 12
Sample 'scroll-right-2' 12
Sample 'scroll-left' -12
Sample 'scroll-left-2' -12
Sample 'back-to-start' -12

# Park the pointer away and let it settle, then read the resting geometry one last time.
Move-Ptr 300 300
Start-Sleep -Milliseconds 1500
$final = LastRest
Write-Host ''
Write-Host ("  after parking away: icons={0} railL={1} railR={2}" -f $final.icons,[Math]::Round($final.dockLeft,1),[Math]::Round($final.dockRight,1))
$rows | Export-Csv (Join-Path $OutDir 'scroll.csv') -NoTypeInformation -Encoding UTF8
Write-Host ''
Write-Host '=== does railL track the scroll? ===' -ForegroundColor Cyan
Write-Host ("  icons across samples : {0}" -f (($rows | ForEach-Object { $_.Icons }) -join ', '))
Write-Host ("  railL across samples : {0}" -f (($rows | ForEach-Object { $_.RailL }) -join ', '))
Write-Host ("  uia NonZeroW         : {0}" -f (($rows | ForEach-Object { $_.UiaNonZeroW }) -join ', '))
Write-Host "  wrote $(Join-Path $OutDir 'scroll.csv')"
