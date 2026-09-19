<#
    Causal proof for the Stage B participant loss.

    Drives the pointer onto a specific dock icon and records, per rebuild, how many icons entered Collect(),
    how many passed the size filter, and how many passed the rail filter - plus the tracked icon's own top
    versus the median, its filters, and whether it is still a participant.

    The point is attribution: a participant count that falls while `sizePass` stays equal to `input` and
    `railPass` falls proves the rail filter is the one dropping icons, without inferring it from totals.

    Usage: ./tools/p4d-causal-proof.ps1 -DockHwnd <id>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $DockHwnd,
    [string] $OutDir = 'D:\AI\temp\dsh-cu-eval\stageB\proof'
)

$ErrorActionPreference = 'Stop'
$native = Join-Path $env:USERPROFILE '.dsh\profiles\web-computer-test\node_modules\dsh-computer-use-windows\native\publish\DshComputerUse.Native.exe'
$profLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -Namespace Cp -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
public const uint MOVE=0x0001, ABSOLUTE=0x8000;
'@
$sw = [Cp.W]::GetSystemMetrics(0); $sh = [Cp.W]::GetSystemMetrics(1)
function Ptr([int]$x,[int]$y) { [Cp.W]::mouse_event([Cp.W]::MOVE -bor [Cp.W]::ABSOLUTE, [int](($x*65535)/($sw-1)), [int](($y*65535)/($sh-1)), 0, [IntPtr]::Zero) }
function Marks([string]$n) { @(Get-Content $profLog | Select-String $n | ForEach-Object { $_.Line | ConvertFrom-Json }) }

Write-Host '=== CAUSAL PROOF ===' -ForegroundColor Cyan
Ptr 300 300; Start-Sleep -Milliseconds 1500

$p0 = @(Marks '"name":"motion\.participants"')
"  baseline participant record: $(if ($p0.Count) { ($p0[-1] | Select-Object input,sizePass,railPass,outsideBand,medianTop,capacity | ConvertTo-Json -Compress) } else { 'none yet' })"
""
Write-Host '  Phase A: pointer parked far away (no motion expected)'
for ($i = 1; $i -le 4; $i++) { Ptr 300 (300 + $i); Start-Sleep -Milliseconds 700 }
$pA = @(Marks '"name":"motion\.participants"')
"    records: $($pA.Count)  last: $(if ($pA.Count) { ($pA[-1] | Select-Object input,sizePass,railPass,outsideBand,capacity | ConvertTo-Json -Compress) })"
""
Write-Host '  Phase B: pointer placed ON the dock rail, held and nudged (motion active)'
$mark = @(Get-Content $profLog).Count
foreach ($x in 1250,1252,1254,1256,1258,1260,1262,1264,1266,1268) { Ptr $x 1363; Start-Sleep -Milliseconds 500 }
Start-Sleep -Milliseconds 800
$pB = @(Marks '"name":"motion\.participants"')
"    records added: $($pB.Count - $pA.Count)"
"    last: $(if ($pB.Count) { ($pB[-1] | Select-Object input,sizePass,railPass,outsideBand,medianTop,capacity | ConvertTo-Json -Compress) })"
""
Write-Host '  Phase C: pointer parked far away again (does the loss recover?)'
for ($i = 1; $i -le 6; $i++) { Ptr 300 (300 + $i); Start-Sleep -Milliseconds 800 }
Start-Sleep -Milliseconds 1000
$pC = @(Marks '"name":"motion\.participants"')
"    last: $(if ($pC.Count) { ($pC[-1] | Select-Object input,sizePass,railPass,outsideBand,capacity | ConvertTo-Json -Compress) })"
""
Write-Host '=== per-rebuild filter attribution ===' -ForegroundColor Cyan
$pt = @(Marks '"name":"motion\.participants"')
"  records: $($pt.Count)"
"  distinct (input,sizePass,railPass): $(($pt | ForEach-Object { "$($_.input)/$($_.sizePass)/$($_.railPass)" } | Sort-Object -Unique) -join '  ')"
"  => if input==sizePass on every record, the SIZE filter never drops anything"
""
Write-Host '=== the tracked (pointer-nearest) icon over time ===' -ForegroundColor Cyan
$ic = @(Marks '"name":"motion\.icon"')
"  records: $($ic.Count)"
$ic | Select-Object -Last 22 | ForEach-Object {
    "    idx={0,-4} w={1,-6} h={2,-6} top={3,-9} median={4,-9} delta={5,-9} size={6,-6} rail={7,-6} part={8,-6} scale={9,-6} lift={10,-7} inside={11}" -f `
      $_.index,$_.width,$_.height,$_.top,$_.medianTop,$_.deltaFromMedian,$_.sizePass,$_.railPass,$_.stillParticipant,$_.scale,$_.lift,$_.pointerInside
}
$pt | Export-Csv (Join-Path $OutDir 'participants.csv') -NoTypeInformation -Encoding UTF8
$ic | Export-Csv (Join-Path $OutDir 'icon.csv') -NoTypeInformation -Encoding UTF8
Write-Host ''
Write-Host "  wrote $OutDir\participants.csv and icon.csv"
