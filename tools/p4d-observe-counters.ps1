<#
    Zero-input multi-layer observation of the running dock.

    Purpose: find out which layer a changing count actually belongs to, without modifying the product.

    Four layers are sampled at once, and each is labelled with its coordinate space:
      * logical shelf items   - from the app's own "Desktop Shelf refreshed ... with N items" log line
      * realized/motion icons - from the dock's "motion.rebuild" profiler mark ("icons" = laid-out rail icons)
      * UIA DockIcon count    - from UI Automation, with the bounding rectangle's coordinate space stated
      * window rectangle      - from Win32 GetWindowRect, physical screen pixels

    No input of any kind is sent: no pointer move, no wheel, no drag, no click. The pointer is parked away
    from the dock before sampling starts and left there.

    Usage: ./tools/p4d-observe-counters.ps1 -DockHwnd <id> -Seconds 90 [-IntervalMs 3000]
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $DockHwnd,
    [int] $Seconds = 90,
    [int] $IntervalMs = 3000,
    [string] $OutDir = 'D:\AI\temp\dsh-cu-eval\stageB\rca'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$native = Join-Path $env:USERPROFILE '.dsh\profiles\web-computer-test\node_modules\dsh-computer-use-windows\native\publish\DshComputerUse.Native.exe'
$appLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\muralis-20260918.log'
$profLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -Namespace Obs -Name W -MemberDefinition @'
[DllImport("user32.dll", SetLastError=true)] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll", SetLastError=true)] public static extern bool GetClientRect(IntPtr h, out RECT r);
[DllImport("user32.dll", SetLastError=true)] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
public struct RECT { public int Left, Top, Right, Bottom; }
public struct POINT { public int X, Y; }
'@

$hwnd = [IntPtr][int]$DockHwnd

function Get-WindowSpace {
    # Physical screen pixels, from Win32. This is the reference everything else is expressed against.
    $wr = New-Object Obs.W+RECT; $cr = New-Object Obs.W+RECT
    [Obs.W]::GetWindowRect($hwnd, [ref]$wr) | Out-Null
    [Obs.W]::GetClientRect($hwnd, [ref]$cr) | Out-Null
    $p = New-Object Obs.W+POINT
    [Obs.W]::ClientToScreen($hwnd, [ref]$p) | Out-Null
    [pscustomobject]@{
        WindowLeft = $wr.Left; WindowTop = $wr.Top; WindowRight = $wr.Right; WindowBottom = $wr.Bottom
        ClientWidthPx = $cr.Right - $cr.Left; ClientHeightPx = $cr.Bottom - $cr.Top
        ClientOriginX = $p.X; ClientOriginY = $p.Y
    }
}

function Get-LogicalItems {
    # The shelf's own log line. This is the logical (data) count, independent of any visual realization.
    $hits = @(Select-String -Path $appLog -Pattern 'Desktop Shelf refreshed in .* with (\d+) items' -AllMatches -EA SilentlyContinue)
    if (-not $hits) { return @() }
    $hits | ForEach-Object {
        $m = [regex]::Match($_.Line, '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}).*with (\d+) items \((\d+) user, (\d+) public\)')
        if ($m.Success) {
            [pscustomobject]@{ At = $m.Groups[1].Value; Items = [int]$m.Groups[2].Value; User = [int]$m.Groups[3].Value; Public = [int]$m.Groups[4].Value }
        }
    }
}

function Get-MotionMarks {
    if (-not (Test-Path $profLog)) { return @() }
    $fs = [System.IO.File]::Open($profLog,'Open','Read','ReadWrite')
    try {
        $sr = New-Object System.IO.StreamReader($fs)
        $out = New-Object System.Collections.Generic.List[object]
        while (-not $sr.EndOfStream) {
            $line = $sr.ReadLine()
            if ($line -notlike '*"name":"motion.rebuild"*') { continue }
            try { $o = $line | ConvertFrom-Json } catch { continue }
            $out.Add($o)
        }
        return $out
    } finally { $fs.Dispose() }
}

function Get-UiaIcons {
    # UIA bounding rectangles. Reported in the window's PHYSICAL PIXEL space as UIA delivers them;
    # the client origin is subtracted later and both numbers are kept separate.
    $obs = (& $native observe --hwnd $DockHwnd --maxElements 700 --outputDir $OutDir 2>&1 | Out-String | ConvertFrom-Json)
    $all = @($obs.elements) | Where-Object { $_.automationId -eq 'DockIconMotion' }
    $onScreen = @($all | Where-Object { $_.bounds.y -ge 0 -and $_.bounds.y -lt 300 })
    [pscustomobject]@{
        Discovered = $all.Count
        WithNonZeroWidth = @($all | Where-Object { $_.bounds.width -gt 0 }).Count
        YRange = if ($all.Count) { "$(($all | ForEach-Object { $_.bounds.y } | Measure-Object -Minimum).Minimum)..$(($all | ForEach-Object { $_.bounds.y } | Measure-Object -Maximum).Maximum)" } else { 'n/a' }
        Widths = (($all | ForEach-Object { $_.bounds.width } | Sort-Object -Unique) -join '/')
        FirstX = if ($all.Count) { ($all | ForEach-Object { $_.bounds.x } | Measure-Object -Minimum).Minimum } else { -1 }
        LastX = if ($all.Count) { ($all | ForEach-Object { $_.bounds.x + $_.bounds.width } | Measure-Object -Maximum).Maximum } else { -1 }
    }
}

Write-Host "=== ZERO-INPUT OBSERVATION ===" -ForegroundColor Cyan
Write-Host "  dock hwnd   : $DockHwnd"
Write-Host "  app log     : $appLog"
Write-Host "  profiler log: $profLog"
Write-Host "  duration    : ${Seconds}s at ${IntervalMs}ms"
Write-Host ''

# Park the pointer away from the dock and never touch it again. This is the only input, and it happens
# before sampling so that every sample below is taken with zero user input in flight.
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point(300, 300)
Write-Host '  pointer parked at (300,300) before sampling; no further input will be sent.'
Write-Host ''

$w0 = Get-WindowSpace
Write-Host ("  window  (screen px): ({0},{1})..({2},{3})  client {4}x{5} at ({6},{7})" -f `
    $w0.WindowLeft,$w0.WindowTop,$w0.WindowRight,$w0.WindowBottom,$w0.ClientWidthPx,$w0.ClientHeightPx,$w0.ClientOriginX,$w0.ClientOriginY)
Write-Host ("  UIA coordinate space: bounding rectangles as delivered by UIA (checked against the client origin below)")
Write-Host ''

$rows = New-Object System.Collections.Generic.List[object]
$logStart = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
$deadline = (Get-Date).AddSeconds($Seconds)
$t0 = Get-Date
$lastLogical = -1
$lastRealized = -1

while ((Get-Date) -lt $deadline) {
    $elapsed = [int]((Get-Date) - $t0).TotalSeconds
    $w = Get-WindowSpace
    $m = @(Get-MotionMarks)
    $lastMotion = if ($m.Count) { $m[-1] } else { $null }
    $logical = @(Get-LogicalItems)
    $logicalNow = if ($logical.Count) { $logical[-1] } else { $null }
    $uia = Get-UiaIcons

    $row = [pscustomobject]@{
        T = $elapsed
        LogicalItems   = if ($logicalNow) { $logicalNow.Items } else { $null }
        LogicalAt      = if ($logicalNow) { $logicalNow.At } else { $null }
        ShelfRefreshes = $logical.Count
        RealizedIcons  = if ($lastMotion) { $lastMotion.icons } else { $null }
        Rebuilds       = if ($lastMotion) { $lastMotion.rebuilds } else { $null }
        Capacity       = if ($lastMotion) { $lastMotion.capacity } else { $null }
        RailL          = if ($lastMotion) { $lastMotion.dockLeft } else { $null }
        RailR          = if ($lastMotion) { $lastMotion.dockRight } else { $null }
        Sane           = if ($lastMotion) { $lastMotion.sane } else { $null }
        UiaCount       = $uia.Discovered
        UiaNonZeroW    = $uia.WithNonZeroWidth
        UiaYRange      = $uia.YRange
        UiaWidths      = $uia.Widths
        UiaFirstX      = $uia.FirstX
        UiaLastX       = $uia.LastX
        WinLeft        = $w.WindowLeft
        WinTop         = $w.WindowTop
        ClientW        = $w.ClientWidthPx
        ClientH        = $w.ClientHeightPx
    }
    $rows.Add($row)

    # Print only when something changed, so the trace shows transitions rather than noise.
    $changed = ($row.LogicalItems -ne $lastLogical) -or ($row.RealizedIcons -ne $lastRealized)
    $mark = if ($changed) { ' <== CHANGED' } else { '' }
    Write-Host ("  t={0,4}s logical={1,-5} realized={2,-5} rebuilds={3,-4} uia={4,-4} uiaNZ={5,-4} railL={6,-7} railR={7,-7} sane={8,-6} win=({9},{10}) {11}x{12}{13}" -f `
        $row.T, $row.LogicalItems, $row.RealizedIcons, $row.Rebuilds, $row.UiaCount, $row.UiaNonZeroW, $row.RailL, $row.RailR, $row.Sane, `
        $row.WinLeft, $row.WinTop, $row.ClientW, $row.ClientH, $mark)
    $lastLogical = $row.LogicalItems
    $lastRealized = $row.RealizedIcons

    Start-Sleep -Milliseconds $IntervalMs
}

$rows | Export-Csv (Join-Path $OutDir 'counters.csv') -NoTypeInformation -Encoding UTF8
$rows | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'counters.json') -Encoding UTF8

Write-Host ''
Write-Host '=== SUMMARY ===' -ForegroundColor Cyan
$logicalVals = @($rows | Where-Object { $_.LogicalItems -ne $null } | ForEach-Object { $_.LogicalItems } | Sort-Object -Unique)
$realVals = @($rows | Where-Object { $_.RealizedIcons -ne $null } | ForEach-Object { $_.RealizedIcons } | Sort-Object -Unique)
$uiaVals = @($rows | ForEach-Object { $_.UiaCount } | Sort-Object -Unique)
Write-Host ("  logical  distinct values: {0}   [{1}]" -f $logicalVals.Count, ($logicalVals -join ','))
Write-Host ("  realized distinct values: {0}   [{1}]" -f $realVals.Count, ($realVals -join ','))
Write-Host ("  UIA      distinct values: {0}   [{1}]" -f $uiaVals.Count, ($uiaVals -join ','))
Write-Host ("  shelf refresh events during run: {0}" -f @($rows[-1].ShelfRefreshes))
Write-Host ("  window moved during run: {0}" -f (@($rows | ForEach-Object { "$($_.WinLeft),$($_.WinTop) $($_.ClientW)x$($_.ClientH)" } | Sort-Object -Unique).Count -gt 1))
Write-Host ''
Write-Host "  wrote $(Join-Path $OutDir 'counters.csv')"
