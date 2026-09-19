<#
    Reproducibility probe: is the rail geometry a FUNCTION of the dock state, or does it drift over time?

    This is the crux. If rebuilds at the same window state (same originX/originY/rect) report the same
    railL and the same icons count, the geometry is a pure function of state and the earlier drift was a
    measurement artifact. If the same state reports different values at different times, the difference is
    accumulated somewhere and the layer responsible can be identified.

    Input is injected on purpose (a diagnostic, not a stability run). The pointer is returned to the same
    park position between sweeps so each sweep starts from an identical situation.

    Usage: ./tools/p4d-repeatability-probe.ps1 -DockHwnd <id> -Cycles 6
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $DockHwnd,
    [int] $Cycles = 6,
    [string] $OutDir = 'D:\AI\temp\dsh-cu-eval\stageB\rca'
)

$ErrorActionPreference = 'Stop'
$native = Join-Path $env:USERPROFILE '.dsh\profiles\web-computer-test\node_modules\dsh-computer-use-windows\native\publish\DshComputerUse.Native.exe'
$profLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -Namespace Rep -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
public const uint MOVE=0x0001, ABSOLUTE=0x8000;
'@
$sw = [Rep.W]::GetSystemMetrics(0); $sh = [Rep.W]::GetSystemMetrics(1)
function Move-Ptr([int]$x,[int]$y) {
    [Rep.W]::mouse_event([Rep.W]::MOVE -bor [Rep.W]::ABSOLUTE, [int](($x*65535)/($sw-1)), [int](($y*65535)/($sh-1)), 0, [IntPtr]::Zero)
}
function Rebuilds { @(Get-Content $profLog | Select-String '"name":"motion\.rebuild"' | ForEach-Object { $_.Line | ConvertFrom-Json }) }
function WinRect { $o = ((& $native list 2>&1 | Out-String | ConvertFrom-Json) | Where-Object { $_.title -eq 'Muralis Dock' } | Select-Object -First 1); "$($o.bounds.x),$($o.bounds.y) $($o.bounds.width)x$($o.bounds.height)" }

Write-Host '=== REPEATABILITY PROBE ===' -ForegroundColor Cyan
Write-Host "  protocol: park far away -> park ON the dock -> park far away, repeated $Cycles times."
Write-Host '  input is injected deliberately; this measures reproducibility, not stability.'
Write-Host ''

$rows = New-Object System.Collections.Generic.List[object]
foreach ($c in 1..$Cycles) {
    # 1) park far away, let the dock settle to its resting state
    Move-Ptr 300 300
    Start-Sleep -Milliseconds 1200
    $before = @(Rebuilds)
    $restRows = @($before | Where-Object { [Math]::Abs($_.originX - 803) -lt 0.5 })
    $restLast = if ($restRows.Count) { $restRows[-1] } else { $null }

    # 2) park on the dock, forcing the expanded state and at least one rebuild
    Move-Ptr 1250 1363
    Start-Sleep -Milliseconds 900
    Move-Ptr 1300 1363
    Start-Sleep -Milliseconds 900
    $expanded = @(Rebuilds | Where-Object { [Math]::Abs($_.originX - 752) -lt 0.5 })
    $expLast = if ($expanded.Count) { $expanded[-1] } else { $null }

    # 3) park far away again
    Move-Ptr 300 300
    Start-Sleep -Milliseconds 1200
    $after = @(Rebuilds)
    $rest2 = @($after | Where-Object { [Math]::Abs($_.originX - 803) -lt 0.5 })
    $rest2Last = if ($rest2.Count) { $rest2[-1] } else { $null }

    $row = [pscustomobject]@{
        Cycle = $c
        RestIcons = if ($restLast) { $restLast.icons } else { $null }
        RestRailL = if ($restLast) { [Math]::Round($restLast.dockLeft,1) } else { $null }
        RestRailR = if ($restLast) { [Math]::Round($restLast.dockRight,1) } else { $null }
        RestRebuild = if ($restLast) { $restLast.rebuilds } else { $null }
        ExpIcons = if ($expLast) { $expLast.icons } else { $null }
        ExpRailL = if ($expLast) { [Math]::Round($expLast.dockLeft,1) } else { $null }
        AfterIcons = if ($rest2Last) { $rest2Last.icons } else { $null }
        AfterRailL = if ($rest2Last) { [Math]::Round($rest2Last.dockLeft,1) } else { $null }
        AfterRebuild = if ($rest2Last) { $rest2Last.rebuilds } else { $null }
        Window = (WinRect)
    }
    $rows.Add($row)
    Write-Host ("  cycle {0}: REST icons={1,-4} railL={2,-7} | EXPANDED icons={3,-4} railL={4,-7} | AFTER-REST icons={5,-4} railL={6,-7} | win={7}" -f `
        $row.Cycle,$row.RestIcons,$row.RestRailL,$row.ExpIcons,$row.ExpRailL,$row.AfterIcons,$row.AfterRailL,$row.Window)
}

$rows | Export-Csv (Join-Path $OutDir 'repeatability.csv') -NoTypeInformation -Encoding UTF8
Write-Host ''
Write-Host '=== reproducibility at the SAME dock state (originX=803, resting window) ===' -ForegroundColor Cyan
$rl = @($rows | ForEach-Object { $_.AfterRailL } | Where-Object { $_ -ne $null })
$ri = @($rows | ForEach-Object { $_.AfterIcons } | Where-Object { $_ -ne $null })
Write-Host ("  railL values across cycles : {0}" -f (($rl) -join ', '))
Write-Host ("  icons values across cycles : {0}" -f (($ri) -join ', '))
Write-Host ("  railL distinct             : {0}" -f (@($rl | Sort-Object -Unique).Count))
Write-Host ("  icons distinct             : {0}" -f (@($ri | Sort-Object -Unique).Count))
Write-Host ''
Write-Host "  wrote $(Join-Path $OutDir 'repeatability.csv')"
