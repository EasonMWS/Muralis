<#
    Stage B participant-proof probe. HISTORICAL — DOES NOT WORK ON THE CURRENT BUILD.

    The product no longer emits the `motion.participants` mark this probe reads. The instrumentation that produced
    it was temporary and was removed once the cause was established and covered by tests
    (`tests/Muralis.Core.Tests/Motion/DockRailMembershipTests.cs`). Running this against a current build prints
    empty counts, which is not a pass: it means the instrument is gone, not that the dock is healthy.

    It is kept because it is the tool that produced the Stage 1 evidence, and it is the shape any future
    instrumented run should take. To use it again, re-add a `motion.participants` mark carrying
    `input`, `sizePass`, `railPass` and `capacity` to `DockMotionCoordinator.Collect()`.

    When it did work, it drove the pointer through a fixed set of gestures over the running dock and reported:

      * input / sizePass / railPass per Collect(), so a loss can be attributed to one filter
      * the histogram of measured tops, so the dock's row structure is read rather than assumed
      * the clamp operands, so the coordinate-space question is answered with numbers instead of inspection
      * participant count over time, so a recovery can be distinguished from a permanent loss

    Gestures: parked away, 50+ single-icon cycles, slow sweeps, fast sweeps, leave/settle/re-enter.

    Usage: ./tools/p4d-participant-proof.ps1 -DockHwnd <id> [-OutDir <dir>]
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $DockHwnd,
    [string] $OutDir = 'D:\AI\temp\dsh-cu-eval\stageB\proof'
)

$ErrorActionPreference = 'Stop'
$profLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -Namespace P4p -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
public const uint MOVE=0x0001, ABSOLUTE=0x8000;
'@
$script:ScreenW = [P4p.W]::GetSystemMetrics(0)
$script:ScreenH = [P4p.W]::GetSystemMetrics(1)

# SetCursorPos warps the cursor but generates no motion input, and the dock follows a raw-input broker, so a
# warp leaves it completely inert. mouse_event with MOVE|ABSOLUTE is what actually reaches it.
function Ptr([int]$x, [int]$y) {
    [P4p.W]::mouse_event(
        [P4p.W]::MOVE -bor [P4p.W]::ABSOLUTE,
        [int](($x * 65535) / ($script:ScreenW - 1)),
        [int](($y * 65535) / ($script:ScreenH - 1)),
        0,
        [IntPtr]::Zero)
}
function Marks([string]$n) { @(Get-Content $profLog | Select-String $n | ForEach-Object { $_.Line | ConvertFrom-Json }) }

function Snapshot([string]$label) {
    $p = @(Marks '"name":"motion\.participants"')
    if ($p.Count -eq 0) { "  [$label] no records"; return }
    $last = $p[-1]
    "  [$label] input=$($last.input) sizePass=$($last.sizePass) railPass=$($last.railPass) outside=$($last.outsideBand) " +
    "medianTop=$($last.medianTop) inside=$($last.pointerInside) rawL=$($last.rawL) rawR=$($last.rawR) hostW=$($last.hostW) clampFired=$($last.clampFired)"
    "        hist=$($last.hist)"
}

Write-Host '=== STAGE B PARTICIPANT PROOF ===' -ForegroundColor Cyan
"  profile : $profLog"
"  records : $(@(Get-Content $profLog).Count)"

# The dock's own reported rail geometry decides where the pointer has to be, instead of a hardcoded guess.
$rb = @(Marks '"name":"motion\.rebuild"')
if ($rb.Count -eq 0) { throw 'No motion.rebuild record: is the dock up with MURALIS_DOCK_PROFILE=1?' }
$r = $rb[-1]
$scale = 1.1718
$screenLeft = $r.originX + $r.dockLeft
$screenRight = $r.originX + $r.dockRight
# The profiler does not carry the rail's vertical position, and the dock's own origin does not locate the icon
# row, so the row is taken from where it was empirically found on this window: 47 px below the dock's top edge.
$railY = $r.originY + 44
Write-Host ("  rail from profiler: originX=$($r.originX) dockLeft=$($r.dockLeft) dockRight=$($r.dockRight) " +
            "-> screen ${screenLeft}..${screenRight} at y=$railY") -ForegroundColor DarkGray

$awayX = 300; $awayY = 300

Write-Host '  Phase 1: parked away (expect input == sizePass == railPass)' -ForegroundColor Yellow
Ptr $awayX $awayY
for ($i = 0; $i -lt 4; $i++) { Start-Sleep -Milliseconds 500; Ptr ($awayX + $i) ($awayY + $i) }
Start-Sleep -Milliseconds 600
Snapshot 'away'

$first = @(Marks '"name":"motion\.participants"')
$before = $first.Count

Write-Host '  Phase 2: 55 slow cycles across a single icon' -ForegroundColor Yellow
$cx = [int](($screenLeft + $screenRight) / 2)
for ($i = 0; $i -lt 55; $i++) {
    Ptr ($cx - 26) $railY
    Start-Sleep -Milliseconds 45
    Ptr ($cx + 26) $railY
    Start-Sleep -Milliseconds 45
}
Start-Sleep -Milliseconds 700
Snapshot 'after 55 cycles'

Write-Host '  Phase 3: 10 slow full sweeps' -ForegroundColor Yellow
for ($s = 0; $s -lt 10; $s++) {
    $x = [int]$screenLeft
    while ($x -le $screenRight) { Ptr $x $railY; Start-Sleep -Milliseconds 22; $x += 12 }
    Start-Sleep -Milliseconds 120
}
Start-Sleep -Milliseconds 700
Snapshot 'after 10 slow sweeps'

Write-Host '  Phase 4: 20 fast full sweeps' -ForegroundColor Yellow
for ($s = 0; $s -lt 20; $s++) {
    $x = [int]$screenLeft
    while ($x -le $screenRight) { Ptr $x $railY; Start-Sleep -Milliseconds 5; $x += 40 }
    Start-Sleep -Milliseconds 40
}
Start-Sleep -Milliseconds 700
Snapshot 'after 20 fast sweeps'

Write-Host '  Phase 5: leave, settle 3s, re-enter (x5)' -ForegroundColor Yellow
for ($i = 0; $i -lt 5; $i++) {
    Ptr $awayX $awayY
    Start-Sleep -Milliseconds 900
    Ptr $cx $railY
    Start-Sleep -Milliseconds 300
    Ptr ($cx + 20) ($railY - 6)
    Start-Sleep -Milliseconds 300
}
Ptr $awayX $awayY
Start-Sleep -Seconds 2
Snapshot 'after leave/re-enter'

$all = @(Marks '"name":"motion\.participants"')
Write-Host ''
Write-Host '=== ATTRIBUTION OVER EVERY Collect() ===' -ForegroundColor Cyan
"  participant records: $($all.Count)  (this run added $($all.Count - $before))"

$bad = @($all | Where-Object { $_.input -ne $_.sizePass })
"  passes where the SIZE filter rejected anything : $($bad.Count)"
if ($bad.Count -gt 0) { $bad | Select-Object -First 5 | ForEach-Object { "      input=$($_.input) sizePass=$($_.sizePass)" } }

$railBad = @($all | Where-Object { $_.railPass -lt $_.sizePass })
"  passes where the RAIL filter rejected anything : $($railBad.Count)"
if ($railBad.Count -gt 0) { $railBad | Select-Object -First 5 | ForEach-Object { "      sizePass=$($_.sizePass) railPass=$($_.railPass) hist=$($_.hist)" } }

$min = ($all | Measure-Object -Property railPass -Minimum).Minimum
$max = ($all | Measure-Object -Property railPass -Maximum).Maximum
"  railPass min=$min max=$max   capacity=$($all[-1].capacity)"

Write-Host ''
Write-Host '=== VERDICT ===' -ForegroundColor Cyan
if ($min -lt $max) {
    "  PARTICIPANT LOSS REPRODUCED: railPass fell to $min (from $max)"
} else {
    "  NO PARTICIPANT LOSS: railPass held at $max across $($all.Count) passes"
}

$csv = Join-Path $OutDir 'participants-raw.csv'
$all | Select-Object t, input, sizePass, railPass, outsideBand, medianTop, capacity, pointerInside, hist |
    Export-Csv -NoTypeInformation -Encoding UTF8 -Path $csv
"  wrote $csv"
