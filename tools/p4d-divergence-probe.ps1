<#
    Divergence probe: which layer starts changing, and when, while pointer input is injected?

    The zero-input run showed every layer stable for 90 s. This run injects the same pointer movement the
    acceptance harness uses and samples all layers, so the first layer to move identifies where the change
    originates. It is a diagnostic: it sends input, so it is NOT a stability measurement.

    Layers sampled, each in its stated space:
      logicalItems      - the shelf's own "refreshed ... with N items" log line   (data layer)
      realizedIcons     - motion.rebuild "icons" = laid-out rail icons           (visual-tree layer)
      railL / railR     - motion.rebuild dockLeft/dockRight, dock DIP units      (engine layer)
      shelfScrollX      - ScrollViewer horizontal offset via UIA ScrollPattern   (viewport layer)
      uiaDockIcons      - UIA-discovered DockIcon count                          (accessibility layer)

    Usage: ./tools/p4d-divergence-probe.ps1 -DockHwnd <id> -Seconds 60
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $DockHwnd,
    [int] $Seconds = 60,
    [string] $OutDir = 'D:\AI\temp\dsh-cu-eval\stageB\rca'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$native = Join-Path $env:USERPROFILE '.dsh\profiles\web-computer-test\node_modules\dsh-computer-use-windows\native\publish\DshComputerUse.Native.exe'
$appLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\muralis-20260918.log'
$profLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -Namespace Div -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll", SetLastError=true)] public static extern bool GetWindowRect(IntPtr h, out RECT r);
public const uint MOVE=0x0001, ABSOLUTE=0x8000;
public struct RECT { public int Left, Top, Right, Bottom; }
'@
$sw = [Div.W]::GetSystemMetrics(0); $sh = [Div.W]::GetSystemMetrics(1)
function Move-Ptr([int]$x,[int]$y) {
    [Div.W]::mouse_event([Div.W]::MOVE -bor [Div.W]::ABSOLUTE, [int](($x*65535)/($sw-1)), [int](($y*65535)/($sh-1)), 0, [IntPtr]::Zero)
}
$hwnd = [IntPtr][int]$DockHwnd
function WinRect { $r = New-Object Div.W+RECT; [Div.W]::GetWindowRect($hwnd,[ref]$r)|Out-Null; "$($r.Left),$($r.Top) $($r.Right-$r.Left)x$($r.Bottom-$r.Top)" }

function LogicalItems {
    $h = @(Select-String -Path $appLog -Pattern 'Desktop Shelf refreshed in .* with (\d+) items' -EA SilentlyContinue)
    if (-not $h) { return $null }
    $m = [regex]::Match($h[-1].Line, 'with (\d+) items')
    if ($m.Success) { [int]$m.Groups[1].Value } else { $null }
}
function MotionMarks { @(Get-Content $profLog | Select-String '"name":"motion\.rebuild"' | ForEach-Object { $_.Line | ConvertFrom-Json }) }
function UiaInfo {
    # ScrollPattern gives the viewport offset: the viewport layer, independent of the visual tree.
    $obs = (& $native observe --hwnd $DockHwnd --maxElements 700 --outputDir $OutDir 2>&1 | Out-String | ConvertFrom-Json)
    $icons = @($obs.elements) | Where-Object { $_.automationId -eq 'DockIconMotion' }
    $scroller = @($obs.elements) | Where-Object { $_.automationId -eq 'ShelfScroller' } | Select-Object -First 1
    [pscustomobject]@{
        Icons = $icons.Count
        ScrollX = if ($scroller -and $scroller.value) { $scroller.value } else { $null }
        ScrollerW = if ($scroller) { $scroller.bounds.width } else { $null }
    }
}

Write-Host '=== DIVERGENCE PROBE (input injected) ===' -ForegroundColor Cyan
Write-Host "  dock hwnd: $DockHwnd ; window: $(WinRect)"
Write-Host '  Phase 1 (0-20s): pointer parked far away, no input.'
Write-Host '  Phase 2 (20-40s): pointer swept across the dock band, no clicks/wheel.'
Write-Host '  Phase 3 (40-60s): pointer parked far away again.'
Write-Host ''

$rows = New-Object System.Collections.Generic.List[object]
$t0 = Get-Date
$shelfScroll = $null

while (((Get-Date) - $t0).TotalSeconds -lt $Seconds) {
    $el = [int]((Get-Date) - $t0).TotalSeconds
    $phase = if ($el -lt 20) { 'idle' } elseif ($el -lt 40) { 'sweep' } else { 'settle' }

    if ($phase -eq 'sweep') {
        # Move across the band below the window top, in the icon row.
        $x = 820 + (($el * 37) % 900)
        Move-Ptr $x 1363
    } elseif ($phase -eq 'settle' -and $el -eq 40) {
        Move-Ptr 300 300
    }

    Start-Sleep -Milliseconds 900
    $m = MotionMarks
    $last = if ($m.Count) { $m[-1] } else { $null }
    $u = UiaInfo
    $row = [pscustomobject]@{
        T = $el; Phase = $phase
        LogicalItems  = LogicalItems
        Rebuilds      = if ($last) { $last.rebuilds } else { $null }
        RealizedIcons = if ($last) { $last.icons } else { $null }
        RailL         = if ($last) { [Math]::Round($last.dockLeft,0) } else { $null }
        RailR         = if ($last) { [Math]::Round($last.dockRight,0) } else { $null }
        UiaIcons      = $u.Icons
        ScrollX       = $u.ScrollX
        Window        = (WinRect)
    }
    $rows.Add($row)
    Write-Host ("  t={0,3}s {1,-7} logical={2,-5} rebuilds={3,-4} realized={4,-5} railL={5,-6} railR={6,-6} uia={7,-4} scrollX={8,-6} win={9}" -f `
        $row.T,$row.Phase,$row.LogicalItems,$row.Rebuilds,$row.RealizedIcons,$row.RailL,$row.RailR,$row.UiaIcons,$row.ScrollX,$row.Window)
}

Move-Ptr 300 300
$rows | Export-Csv (Join-Path $OutDir 'divergence.csv') -NoTypeInformation -Encoding UTF8
Write-Host ''
Write-Host '=== first layer to diverge ===' -ForegroundColor Cyan
foreach ($f in 'LogicalItems','Rebuilds','RealizedIcons','RailL','UiaIcons','ScrollX','Window') {
    $vals = @($rows | ForEach-Object { $_.$f } | Where-Object { $_ -ne $null } | Sort-Object -Unique)
    Write-Host ("  {0,-14} distinct={1,-3} [{2}]" -f $f, $vals.Count, (($vals | Select-Object -First 8) -join ' | '))
}
Write-Host ''
Write-Host "  wrote $(Join-Path $OutDir 'divergence.csv')"
