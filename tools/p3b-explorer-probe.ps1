<#
.SYNOPSIS
    Focused probe: what the canvas sees of the pointer around an Explorer restart.

.DESCRIPTION
    Plants two items (an application at the display centre and a folder 130 DIP up and left), mounts
    the canvas, hovers each of them, then kills and restarts Explorer and hovers again. Every reading
    is printed with the panel's own line, the window under the point and the canvas window's place,
    so a hover that stops working after a shell restart can be told apart from one that never started.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/p3b-explorer-probe.ps1
#>
[CmdletBinding()]
param(
    [string]$Exe = 'src/Muralis.App/bin/Debug/net10.0-windows10.0.26100.0/win-x64/Muralis.exe',
    [switch]$SkipExplorerRestart
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'p3-common.ps1')
Set-BackupPaths 'p3bprobe'

$repoRoot = Split-Path $PSScriptRoot -Parent
$exePath = if ([System.IO.Path]::IsPathRooted($Exe)) { $Exe } else { Join-Path $repoRoot $Exe }

$script:process = $null
$script:scale = 1.0
$script:monitor = @(0, 0, 2560, 1440)

function Assert-MuralisNotRunning {
    if (Get-Process -Name Muralis -ErrorAction SilentlyContinue) {
        throw 'Muralis is already running; stop it first.'
    }
    if (-not (Test-Path $exePath)) { throw "Executable not found: $exePath" }
}

# The app must not be minimised before its first frame, so the launch waits it out here too.
function Start-ProbeApp {
    Write-Host ("Launching {0}" -f $exePath)
    $script:process = Start-Process -FilePath $exePath -PassThru
    $processId = [int]$script:process.Id
    Wait-Until { [P3Win]::FindWindowByClass($processId, 'WinUIDesktopWin32WindowClass') -ne [IntPtr]::Zero } 60 'the main window' | Out-Null
    Wait-Until { ([P3Win]::ClassesWithPrefix('MuralisDesktopHostWindow')).Count -ge 1 } 60 'the canvas mount' | Out-Null
    Wait-Until { ([P3Win]::ClassesWithPrefix('MuralisPointerRouter_')).Count -ge 1 } 60 'the router attach' | Out-Null
    Start-Sleep -Milliseconds 900
}

function Read-Geometry {
    $state = Read-State
    if ($null -ne $state.ScaleFactor) { $script:scale = $state.ScaleFactor }
    if ($null -ne $state.MonitorW) {
        $script:monitor = @($state.MonitorX, $state.MonitorY, $state.MonitorW, $state.MonitorH)
    }

    return $state
}

function Get-ItemCentre($item) {
    $x = $script:monitor[0] + ($script:monitor[2] / 2) + ($item.OffsetXDip * $script:scale)
    $y = $script:monitor[1] + ($script:monitor[3] / 2) + ($item.OffsetYDip * $script:scale)
    return @([int][math]::Round($x), [int][math]::Round($y))
}

function Describe-Point([int]$x, [int]$y) {
    $point = New-Object P3Win+POINT
    $point.X = $x
    $point.Y = $y
    $window = [P3Win]::WindowFromPoint($point)
    if ($window -eq [IntPtr]::Zero) { return 'nothing' }
    $root = [P3Win]::RootOf($window)
    return ("{0} 0x{1:X} (root {2})" -f [P3Win]::Describe($window), [long]$window, [P3Win]::Describe($root))
}

function Show-Pointer([string]$label, [int]$x, [int]$y) {
    $state = Read-State
    Write-Host ("{0}:" -f $label)
    Write-Host ("  panel: items {0}, hovered {1} at {2}x, pointer inside {3} at {4},{5}" -f $state.ItemCount, $state.HoverId, $state.HoverScale, $state.Inside, $state.Px, $state.Py)
    Write-Host ("  under the point: {0}" -f (Describe-Point $x $y))
    foreach ($handle in [P3Win]::WindowsOfClass('MuralisDesktopHostWindow')) {
        $rect = [P3Win]::RectOf($handle)
        Write-Host ("  canvas window 0x{0:X} at {1},{2} {3}x{4} visible {5}" -f [long]$handle, $rect[0], $rect[1], $rect[2], $rect[3], [P3Win]::IsWindowVisible($handle))
    }

    $router = [P3Win]::WindowsOfClass('MuralisPointerRouter_')
    foreach ($handle in $router) {
        Write-Host ("  router window 0x{0:X} visible {1}" -f [long]$handle, [P3Win]::IsWindowVisible($handle))
    }

    return $state
}

function Hover-Point([int]$x, [int]$y) {
    Move-Pointer ($x - 60) ($y - 60)
    Start-Sleep -Milliseconds 250
    Move-And-Settle $x $y 800
}

Assert-MuralisNotRunning

Backup-Settings
Enable-CanvasInSettings
Park-LayoutFiles

$folder = Join-Path $env:TEMP 'MuralisP3BProbe'
New-Item -ItemType Directory -Force -Path $folder | Out-Null
$items = @(
    (New-LayoutItem -Id 'app_p1' -Name 'notepad' -Kind 'application' -Path (Join-Path $env:SystemRoot 'System32\notepad.exe') -OffsetX 0 -OffsetY 0 -Z 0),
    (New-LayoutItem -Id 'dir_p1' -Name 'MuralisP3BProbe' -Kind 'folder' -Path $folder -OffsetX -130 -OffsetY -130 -IconKey 'folder' -Z 1)
)
Write-Layout $items

try {
    Start-ProbeApp
    Open-DynamicPage | Out-Null
    Read-Geometry | Out-Null
    $state = Wait-Diag { param($s) $s.ItemCount -eq 2 } 30 'the two items'
    if ($null -eq $state) { throw 'The canvas never showed the two items.' }

    Minimize-AppWindow
    $folderPoint = Get-ItemCentre (Get-LayoutItem (Read-Layout) 'dir_p1')
    $appPoint = Get-ItemCentre (Get-LayoutItem (Read-Layout) 'app_p1')

    Move-Pointer 40 40
    Clear-TheDesktop @($folderPoint[0]) @($folderPoint[1]) ' (before)'

    Write-Host ''
    Write-Host '=== before the Explorer restart ==='
    Hover-Point $folderPoint[0] $folderPoint[1]
    $before = Show-Pointer 'hover at the folder item' $folderPoint[0] $folderPoint[1]

    Hover-Point $appPoint[0] $appPoint[1]
    $beforeApp = Show-Pointer 'hover at the application item' $appPoint[0] $appPoint[1]

    if (-not $SkipExplorerRestart) {
        Write-Host ''
        Write-Host '=== the mark dance ==='
        Rename-Item -Force $folder "$folder.away"
        $marked = Wait-Diag { param($s) $s.MissingCount -ge 1 } 20 'the missing mark'
        Write-Host ("  marked: missing {0}" -f $marked.MissingCount)
        Hover-Point $folderPoint[0] $folderPoint[1]
        $whileMarked = Show-Pointer 'hover at the item while it is marked' $folderPoint[0] $folderPoint[1]
        Move-Pointer $folderPoint[0] $folderPoint[1]
        Start-Sleep -Milliseconds 200
        Click-Pointer
        Start-Sleep -Milliseconds 600
        Write-Host ("  after a click while marked: missing {0}, selected {1}" -f (Read-State).MissingCount, (Read-State).SelectedId)
        Rename-Item -Force "$folder.away" $folder
        Write-Host ("  the target is back: {0}" -f (Test-Path $folder))

        Write-Host ''
        Write-Host '=== the Explorer restart ==='
        $mountBefore = (Read-State).Mount
        taskkill /f /im explorer.exe | Out-Null
        Start-Sleep -Seconds 3
        Start-Process explorer.exe | Out-Null
        Start-Sleep -Seconds 6
        $remount = Wait-Diag { param($s) $s.Mount -gt $mountBefore } 45 'the canvas to come back'
        Write-Host ("  mount {0} -> {1}" -f $mountBefore, $remount.Mount)
        $state = Wait-Diag { param($s) $s.ItemCount -eq 2 } 30 'the two items to come back'
        Write-Host ("  items back: {0}" -f $state.ItemCount)
        Start-Sleep -Milliseconds 1500

        Move-Pointer 40 40
        Start-Sleep -Milliseconds 300
        Clear-TheDesktop @($folderPoint[0]) @($folderPoint[1]) ' (after)'

        Write-Host ''
        Write-Host '=== after the Explorer restart ==='
        Hover-Point $folderPoint[0] $folderPoint[1]
        $after = Show-Pointer 'hover at the folder item' $folderPoint[0] $folderPoint[1]

        Write-Host '  trying a click at the same point ...'
        Move-Pointer $folderPoint[0] $folderPoint[1]
        Start-Sleep -Milliseconds 200
        Click-Pointer
        Start-Sleep -Milliseconds 800
        $clicked = Read-State
        Write-Host ("  after the click: selected {0}" -f $clicked.SelectedId)

        Hover-Point $appPoint[0] $appPoint[1]
        $afterApp = Show-Pointer 'hover at the application item' $appPoint[0] $appPoint[1]
    }
} finally {
    Restore-Everything
    if (Test-Path $folder) { Remove-Item -Recurse -Force $folder -ErrorAction SilentlyContinue }
}
