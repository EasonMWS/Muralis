<#
    Geometry consistency probe: are the engine's own reported rail bounds consistent with the icons'
    measured positions?

    The engine reports the rail in DOCK units (dockLeft/dockRight) and separately reports the window origin
    it used (originX/originY). A transform relates the two. This probe records, for every rebuild, the rail
    bounds AND the icon centres so the relationship can be checked for internal consistency:

      - railLeft <= railRight must hold for a well-formed run of icons on one horizontal rail.
      - the rail width must not change when only the window's size changes, since the icons themselves have
        not moved.
      - the first and last centre must lie inside [dockLeft, dockRight].

    Any violation is a statement about the coordinator's cached geometry, not about the pointer.

    Usage: ./tools/p4d-geometry-consistency.ps1
#>
[CmdletBinding()]
param(
    [string] $OutDir = 'D:\AI\temp\dsh-cu-eval\stageB\rca'
)

$ErrorActionPreference = 'Stop'
$profLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$marks = @(Get-Content $profLog | Select-String '"name":"motion\.rebuild"' | ForEach-Object { $_.Line | ConvertFrom-Json })
Write-Host '=== GEOMETRY CONSISTENCY (from the dock own rebuild traces) ===' -ForegroundColor Cyan
Write-Host ("  rebuilds recorded in this process: {0}" -f $marks.Count)
Write-Host ''

$rows = New-Object System.Collections.Generic.List[object]
$i = 0
foreach ($m in $marks) {
    $i++
    $centres = @($m.centres -split ',' | ForEach-Object { [double]$_ })
    $left = [double]$m.dockLeft
    $right = [double]$m.dockRight
    $first = if ($centres.Count) { $centres[0] } else { $null }
    $last  = if ($centres.Count) { $centres[-1] } else { $null }
    # internal consistency checks, all in the coordinator's own units
    $ordered   = if ($centres.Count) { -not (@(0..([Math]::Max(0,$centres.Count-2)) | Where-Object { $centres[$_] -ge $centres[$_+1] }).Count) } else { $null }
    $wellFormed = $left -le $right
    $firstInside = if ($null -ne $first) { $first -ge $left -and $first -le $right } else { $null }
    $lastInside  = if ($null -ne $last)  { $last  -ge $left -and $last  -le $right } else { $null }
    $row = [pscustomobject]@{
        N=$i; T=[Math]::Round($m.t/1000,0); Rebuilds=$m.rebuilds
        Icons=$m.icons; Capacity=$m.capacity
        DockLeft=[Math]::Round($left,1); DockRight=[Math]::Round($right,1)
        RailWidth=[Math]::Round($right-$left,1)
        FirstCentre=if($null -ne $first){[Math]::Round($first,1)}else{$null}
        LastCentre=if($null -ne $last){[Math]::Round($last,1)}else{$null}
        OriginX=$m.originX; OriginY=$m.originY
        Sane=$m.sane; Ordered=$ordered
        WellFormed=$wellFormed; FirstInside=$firstInside; LastInside=$lastInside
        CentreCount=$centres.Count
    }
    $rows.Add($row)
    $flag = if (-not $wellFormed -or -not $ordered -or -not $firstInside -or -not $lastInside) { '  <== INCONSISTENT' } else { '' }
    Write-Host ("  #{0,-3} t={1,-4}s rb={2,-3} icons={3,-4} rail=[{4,-8},{5,-8}] w={6,-8} first={7,-8} last={8,-8} originX={9} sane={10} ordered={11} firstIn={12}{13}" -f `
        $row.N,$row.T,$row.Rebuilds,$row.Icons,$row.DockLeft,$row.DockRight,$row.RailWidth,$row.FirstCentre,$row.LastCentre,$row.OriginX,$row.Sane,$row.Ordered,$row.FirstInside,$flag)
}

$rows | Export-Csv (Join-Path $OutDir 'geometry-consistency.csv') -NoTypeInformation -Encoding UTF8
Write-Host ''
Write-Host '=== consistency summary ===' -ForegroundColor Cyan
Write-Host ("  well-formed (left<=right) : {0}/{1}" -f @($rows | Where-Object { $_.WellFormed }).Count, $rows.Count)
Write-Host ("  centres strictly ordered  : {0}/{1}" -f @($rows | Where-Object { $_.Ordered }).Count, $rows.Count)
Write-Host ("  first centre inside rail  : {0}/{1}" -f @($rows | Where-Object { $_.FirstInside }).Count, $rows.Count)
Write-Host ("  last  centre inside rail  : {0}/{1}" -f @($rows | Where-Object { $_.LastInside }).Count, $rows.Count)
Write-Host ''
Write-Host "  rail width by window state (resting should be constant, expanded should be constant):"
$rows | Group-Object OriginX | ForEach-Object {
    $ws = @($_.Group | ForEach-Object { $_.RailWidth } | Sort-Object -Unique)
    Write-Host ("    originX={0,-7} samples={1,-3} distinct rail widths: {2}" -f $_.Name, $_.Count, ($ws -join ' | '))
}
Write-Host ''
Write-Host "  icons by window state:"
$rows | Group-Object OriginX | ForEach-Object {
    $ic = @($_.Group | ForEach-Object { $_.Icons } | Sort-Object -Unique)
    Write-Host ("    originX={0,-7} distinct icons counts: {1}" -f $_.Name, ($ic -join ' | '))
}
Write-Host ''
Write-Host "  wrote $(Join-Path $OutDir 'geometry-consistency.csv')"
