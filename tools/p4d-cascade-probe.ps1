<#
    Cascade reproduction: does the participant count degrade permanently under repeated motion traffic?

    This runs an extended traffic pattern - parking on one icon, full sweeps, and fast alternation - sampling
    the filter counts throughout. A `railPass` that falls while `input == sizePass` attributes the loss to
    the rail filter; a `medianTop` that moves explains whether a row-offset or a pose change caused it.

    Usage: ./tools/p4d-cascade-probe.ps1 -DockHwnd <id> [-Minutes 4]
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $DockHwnd,
    [int] $Cycles = 60,
    [string] $OutDir = 'D:\AI\temp\dsh-cu-eval\stageB\proof'
)

$ErrorActionPreference = 'Stop'
$native = Join-Path $env:USERPROFILE '.dsh\profiles\web-computer-test\node_modules\dsh-computer-use-windows\native\publish\DshComputerUse.Native.exe'
$profLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
Add-Type -Namespace Cas -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
public const uint MOVE=0x0001, ABSOLUTE=0x8000;
'@
$sw = [Cas.W]::GetSystemMetrics(0); $sh = [Cas.W]::GetSystemMetrics(1)
function Ptr([int]$x,[int]$y) { [Cas.W]::mouse_event([Cas.W]::MOVE -bor [Cas.W]::ABSOLUTE, [int](($x*65535)/($sw-1)), [int](($y*65535)/($sh-1)), 0, [IntPtr]::Zero) }
function Marks([string]$n) { @(Get-Content $profLog | Select-String $n | ForEach-Object { $_.Line | ConvertFrom-Json }) }

Write-Host '=== CASCADE PROBE ===' -ForegroundColor Cyan
Ptr 300 300; Start-Sleep -Milliseconds 1500
$base = @(Marks '"name":"motion\.participants"')
"  baseline: $(if ($base.Count) { ($base[-1] | Select-Object input,sizePass,railPass,medianTop) | ConvertTo-Json -Compress })"
"  traffic: $Cycles cycles of (park on rail, full sweep, park away)"
""
$seen = @()
for ($c = 1; $c -le $Cycles; $c++) {
    # park on the rail
    Ptr (1000 + (($c * 29) % 700)) 1363; Start-Sleep -Milliseconds 90
    # sweep
    foreach ($x in 850,1000,1150,1300,1450,1600,1750) { Ptr $x 1363; Start-Sleep -Milliseconds 30 }
    # park away
    Ptr 300 300; Start-Sleep -Milliseconds 60
    if ($c % 10 -eq 0) {
        $m = @(Marks '"name":"motion\.participants"')
        if ($m.Count) {
            $last = $m[-1]
            $seen += $last
            "    cycle {0,3}: records={1,-4} input={2} sizePass={3} railPass={4} outside={5} medianTop={6} capacity={7} inside={8}" -f `
                $c,$m.Count,$last.input,$last.sizePass,$last.railPass,$last.outsideBand,$last.medianTop,$last.capacity,$last.pointerInside
        }
    }
}
Start-Sleep -Milliseconds 1500
$final = @(Marks '"name":"motion\.participants"')
Write-Host ''
Write-Host '=== final state ===' -ForegroundColor Cyan
if ($final.Count) {
    $f = $final[-1]
    "  input=$($f.input) sizePass=$($f.sizePass) railPass=$($f.railPass) outsideBand=$($f.outsideBand) medianTop=$($f.medianTop) capacity=$($f.capacity)"
    "  rejected: $($f.rejected | ConvertTo-Json -Compress)"
}
$rb = @(Marks '"name":"motion\.rebuild"')
"  rebuilds: $($rb.Count)  icons distinct: $(($rb | ForEach-Object { $_.icons } | Sort-Object -Unique) -join ',')"
"  capacity distinct: $(($rb | ForEach-Object { $_.capacity } | Sort-Object -Unique) -join ',')"
"  dockLeft distinct: $(($rb | ForEach-Object { [Math]::Round($_.dockLeft,0) } | Sort-Object -Unique | Select-Object -First 12) -join ',')"
"  inverted rails (Left>Right): $(@($rb | Where-Object { $_.dockLeft -gt $_.dockRight }).Count)"
$final | Export-Csv (Join-Path $OutDir 'cascade-participants.csv') -NoTypeInformation -Encoding UTF8
Write-Host ''
Write-Host "  wrote $OutDir\cascade-participants.csv"
