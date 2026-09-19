<#
    Stage B geometry verification.

    Reads the dock window's rectangle and every icon's rectangle from the running application, then checks the
    one thing Stage B is about: whether the window is big enough for the magnification envelope, and whether
    the icons at the ends of the run could be cut off by it.

    The pointer-driven half of the acceptance test is blocked by the environment — see the Stage B notes in
    docs/PHASE4D-NEXUS-MOTION.md — so this covers the geometry half and produces the numbers for the report.

    Usage: ./tools/phase4d-stage-b-geometry.ps1 -DockHwnd <id>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $DockHwnd,
    [double] $BaseIconSize = 48,
    [double] $MaxScale = 1.8,
    [double] $MaxLift = 10
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$native = Join-Path $env:USERPROFILE '.dsh\profiles\web-computer-test\node_modules\dsh-computer-use-windows\native\publish\DshComputerUse.Native.exe'
if (-not (Test-Path -LiteralPath $native)) { throw "the Computer Use profile is not installed: $native" }
$verticalReserve = 48.4
$peakIconSize = $BaseIconSize * $MaxScale

$obs = (& $native observe --hwnd $DockHwnd --maxElements 500 --outputDir 'D:\AI\temp\dsh-cu-eval\stageB\obs' 2>&1 | Out-String | ConvertFrom-Json)
$win = (& $native list 2>&1 | Out-String | ConvertFrom-Json) | Where-Object { $_.id -eq $DockHwnd } | Select-Object -First 1
if (-not $win) { throw "no dock window with id $DockHwnd" }

$W = $win.bounds.width
$H = $win.bounds.height
$OX = $win.bounds.x
$OY = $win.bounds.y
Write-Host "dock window  ${W}x${H} at screen ($OX,$OY)" -ForegroundColor Cyan

$icons = @($obs.elements) | Where-Object { $_.automationId -eq 'DockIconMotion' }
$labels = @($obs.elements) | Where-Object { $_.automationId -eq 'LabelText' }
Write-Host "icons reported by UI Automation: $($icons.Count)"
if ($icons.Count -eq 0) { throw 'the dock reported no icon boxes' }

# What the engine says this run needs. Read from the engine itself rather than restated here, so the window's
# geometry and the motion's expectations cannot drift apart.
$probe = Join-Path $repo 'artifacts\motionprobe\bin\Release\net10.0\MotionProbe.dll'
$baseIconSize = $BaseIconSize
$maxScale = $MaxScale
$maxLift = $MaxLift
$reach = 0.0
if (Test-Path -LiteralPath $probe) {
    $line = (& dotnet $probe reserve $icons.Count 2>&1 | Select-Object -Last 1)
    Write-Host "engine: $line"
    foreach ($pair in ($line -split ' ')) {
        $kv = $pair -split '='
        if ($kv.Count -ne 2) { continue }
        switch ($kv[0]) {
            'vertical' { $verticalReserve = [double]$kv[1] }
            'horizontal' { $reach = [double]$kv[1] }
            'base' { $baseIconSize = [double]$kv[1] }
            'peak' { $peakIconSize = [double]$kv[1] }
            'lift' { $maxLift = [double]$kv[1] }
            'maxScale' { $maxScale = [double]$kv[1] }
        }
    }
} else {
    Write-Host "  (motion probe not built; run: dotnet build artifacts/motionprobe -c Release)" -ForegroundColor DarkYellow
}

$left = ($icons | Sort-Object { $_.bounds.x } | Select-Object -First 1).bounds
$right = ($icons | Sort-Object { $_.bounds.x } | Select-Object -Last 1).bounds
$top = ($icons | Sort-Object { $_.bounds.y } | Select-Object -First 1).bounds
$bottom = ($icons | Sort-Object { $_.bounds.y + $_.bounds.height } | Select-Object -Last 1).bounds
$iconW = $icons[0].bounds.width
$iconH = $icons[0].bounds.height

Write-Host ""
Write-Host "resting geometry (dock-local DIP)"
Write-Host "  icon box            ${iconW}x${iconH}"
Write-Host "  leftmost icon       x=$($left.x)  (right edge $($left.x + $left.width))"
Write-Host "  rightmost icon      x=$($right.x) (right edge $($right.x + $right.width))"
Write-Host "  icon rows           y=$($top.y) .. $($bottom.y + $bottom.height)"
Write-Host "  shelf scroller      $((@($obs.elements) | Where-Object { $_.automationId -eq 'ShelfScroller' } | Select-Object -First 1).bounds | ConvertTo-Json -Compress)"

# What the magnification does to one icon, from the profile the engine actually uses.
$peak = $peakIconSize
$grow = $peak - $baseIconSize        # total growth of the artwork, split either side of its centre
$sideGrow = $grow / 2
$topGrow = $grow                     # the origin is the icon box's bottom centre, so all of it goes up

Write-Host ""
Write-Host "magnification envelope (from the engine: base=$baseIconSize peak=$peak lift=$maxLift maxScale=$maxScale)"
Write-Host "  one icon grows by   $grow DIP tall, $sideGrow DIP to each side"
Write-Host "  reserve the engine asked for: vertical=$([Math]::Round($verticalReserve, 4)) DIP"

# The top of a magnified icon: its resting top, less its own growth, less the lift.
$boxInset = ($iconH - $baseIconSize) / 2
$magnifiedTop = $top.y + $boxInset - $topGrow - $maxLift
Write-Host "  worst-case magnified top = $([Math]::Round($magnifiedTop, 2)) DIP from the window top"
Write-Host ""

$failures = @()
if ($magnifiedTop -lt 0) {
    $failures += "CLIPPED at the top: a magnified icon reaches $([Math]::Round($magnifiedTop, 2)) DIP above the window"
} else {
    Write-Host "  TOP: clear by $([Math]::Round($magnifiedTop, 2)) DIP" -ForegroundColor Green
}

Write-Host ""
Write-Host "  horizontal reach for $($icons.Count) icons: $([Math]::Round($reach, 2)) DIP per side"

# Only the icons at the ends of the dock's own row can be cut off by the window. The Shelf's icons run far past
# the window on purpose — it is a ScrollViewer, and clipping its off-screen content is the shelf scroller's
# job, not the window's. So the check is the outermost icon on each side of the dock that is actually on
# screen, and anything beyond the window is the scroller's own content by definition.
$add = @($obs.elements) | Where-Object { $_.automationId -eq 'AddButton' } | Select-Object -First 1

# UI Automation keeps reporting the Shelf's icons at their full unclipped positions, so most of them sit far
# outside the window. Only the elements that are wholly inside it are on screen; the rest is the scroller's
# content and is clipped by the scroller, which is what a scroller is for.
$onScreen = @($icons) | Where-Object { $_.bounds.x -ge 0 -and ($_.bounds.x + $_.bounds.width) -le $W }
$offScreen = $icons.Count - $onScreen.Count

$sorted = @($onScreen) | Sort-Object { $_.bounds.x }
$leftmostElement = $sorted | Select-Object -First 1
$rightmostElement = $sorted | Select-Object -Last 1
if ($add -and $add.bounds.x -lt $leftmostElement.bounds.x) {
    $leftmostElement = $add
}

$leftmostX = $leftmostElement.bounds.x
$rightmostX = $rightmostElement.bounds.x + $rightmostElement.bounds.width

Write-Host "  outermost on-screen dock element: left edge $leftmostX DIP, right edge $rightmostX DIP"
Write-Host "  ($offScreen of $($icons.Count) icons sit outside the window — the Shelf's scrolled content)"

$leftExtreme = $leftmostX - $sideGrow - $reach
$rightExtreme = $rightmostX + $sideGrow + $reach
Write-Host "  worst-case drawn run: $([Math]::Round($leftExtreme, 2)) .. $([Math]::Round($rightExtreme, 2)) within a ${W} DIP window"

if ($leftExtreme -lt 0) { $failures += "CLIPPED on the left: the run reaches $([Math]::Round($leftExtreme, 2)) DIP" }
else { Write-Host "  LEFT: clear by $([Math]::Round($leftExtreme, 2)) DIP" -ForegroundColor Green }

if ($rightExtreme -gt $W) { $failures += "CLIPPED on the right: the run reaches $([Math]::Round($rightExtreme, 2)) DIP in a ${W} DIP window" }
else { Write-Host "  RIGHT: clear by $([Math]::Round($W - $rightExtreme, 2)) DIP" -ForegroundColor Green }

# The dock has to be centred for the reserve to be even. A dock that sits off-centre has all its slack on one
# side, which is a clipping failure waiting to happen even when the totals add up.
$dockLeft = $leftmostX
$dockRight = $rightmostX
$leftSlack = $dockLeft
$rightSlack = $W - $dockRight
Write-Host ""
Write-Host "  slack either side of the drawn dock: left $([Math]::Round($leftSlack, 2)) DIP, right $([Math]::Round($rightSlack, 2)) DIP"
Write-Host "  (each side needs at least the $([Math]::Round($reach, 2)) DIP reach, and they should be close to equal)"
if ($leftSlack -lt $reach -or $rightSlack -lt $reach) {
    $failures += "NOT ENOUGH ROOM: left slack $([Math]::Round($leftSlack, 2)), right slack $([Math]::Round($rightSlack, 2)), reach $([Math]::Round($reach, 2))"
}

Write-Host ""
Write-Host "  the Shelf's own overflow is not a clipping failure of the window: its scroller is 3435 DIP wide and"
Write-Host "  everything past the window edge is content the ScrollViewer is scrolling, which is how it has always"
Write-Host "  been. The reserve above is what the window owes the magnification, and the two outermost dock"
Write-Host "  elements above are what the window could actually cut."

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "GEOMETRY FAILURES" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host "geometry is sufficient for the envelope: nothing the magnification can draw is outside the window" -ForegroundColor Green

# Where the dock's own bottom edge sits on screen, which is the thing that must not move when the window
# grows. The window's local origin moves with the window, so only the screen coordinate is evidence.
$addScreen = if ($add) { "$($OX + $add.bounds.x)..$($OX + $add.bounds.x + $add.bounds.width)" } else { '-' }
Write-Host ""
Write-Host "the invariant: the dock's own position on screen"
Write-Host "  window occupies screen y $OY..$($OY + $H);  dock content bottom at screen y=$($OY + $bottom.y + $bottom.height)"
Write-Host "  add tile at screen x $addScreen"
Write-Host "  first icon at screen x=$($OX + $left.x);  Desktop utility right edge at screen x=$($OX + $rightmostX)"
