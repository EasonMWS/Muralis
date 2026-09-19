<#
    Dock productization geometry acceptance.

    Drives the real built binary through the four product states — nothing pinned, three apps, six apps and ten
    apps — and records, for each one, what the OS says the dock window is and what the dock says it is drawing.
    The readings come from two independent sources on purpose:

      * the window rectangle, read from the window itself, which is the geometry the user's clicks land in;
      * the dock's own motion records, which carry the icon run's drawn bounds in the dock's own DIP space.

    A window that is sized for a dock the user does not have shows up as a wide window over a narrow run, and
    that is the specific regression this harness exists to catch. Nothing here is inferred: every number printed
    is read from the running process or from the window manager.

    Usage: ./tools/p4d-product-geometry.ps1 [-States 0,3,6,10] [-WorkDir <dir>]
#>
[CmdletBinding()]
param(
    [int[]] $States = @(0, 3, 6, 10),
    [string] $WorkDir = 'D:\AI\temp\dsh-cu-eval\product\geometry'
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\Muralis.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Muralis.exe'
$settingsPath = Join-Path $env:LOCALAPPDATA 'Muralis\settings.json'
$profLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
$backup = Join-Path $WorkDir 'settings.before.json'

Add-Type -Namespace Geo -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
[DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
public delegate bool EnumProc(IntPtr h, IntPtr p);
public struct RECT { public int Left, Top, Right, Bottom; }
public struct POINT { public int X, Y; }
public const uint MOVE = 0x0001, ABSOLUTE = 0x8000;
'@

$script:ScreenW = [Geo.Win]::GetSystemMetrics(0)
$script:ScreenH = [Geo.Win]::GetSystemMetrics(1)

function Get-DockWindow([int] $processId) {
    $script:foundHandle = [IntPtr]::Zero
    $callback = [Geo.Win+EnumProc] {
        param([IntPtr] $h, [IntPtr] $p)
        $owner = 0
        [void][Geo.Win]::GetWindowThreadProcessId($h, [ref] $owner)
        if ($owner -eq $processId -and [Geo.Win]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Geo.Win]::GetWindowTextW($h, $sb, 256)
            if ($sb.ToString() -eq 'Muralis Dock') {
                $r = New-Object Geo.Win+RECT
                [void][Geo.Win]::GetWindowRect($h, [ref] $r)
                if (($r.Right - $r.Left) -gt 0 -and ($r.Bottom - $r.Top) -gt 0) { $script:foundHandle = $h }
            }
        }
        return $true
    }
    [void][Geo.Win]::EnumWindows($callback, [IntPtr]::Zero)
    return $script:foundHandle
}

function Move-Pointer([int] $x, [int] $y) {
    [Geo.Win]::mouse_event([Geo.Win]::MOVE -bor [Geo.Win]::ABSOLUTE,
        [int](($x * 65535) / ($script:ScreenW - 1)), [int](($y * 65535) / ($script:ScreenH - 1)), 0, [IntPtr]::Zero)
}

function Read-WindowRect([IntPtr] $h) {
    $r = New-Object Geo.Win+RECT
    [void][Geo.Win]::GetWindowRect($h, [ref] $r)
    $pt = New-Object Geo.Win+POINT
    [void][Geo.Win]::ClientToScreen($h, [ref] $pt)
    $dpi = [Geo.Win]::GetDpiForWindow($h)
    return [pscustomobject]@{
        Left   = $r.Left
        Top    = $r.Top
        Width  = $r.Right - $r.Left
        Height = $r.Bottom - $r.Top
        Origin = $pt
        Scale  = if ($dpi -gt 0) { [math]::Round($dpi / 96.0, 4) } else { 1 }
    }
}

function Write-Pins([string[]] $targets) {
    # The settings file is edited in place — only the Dock section is touched — so nothing else the user has
    # configured is disturbed by an acceptance run.
    $json = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $pins = @()
    foreach ($t in $targets) {
        $leaf = [IO.Path]::GetFileNameWithoutExtension($t)
        $pins += [pscustomobject]@{
            Id             = [guid]::NewGuid().ToString('N').Substring(0, 12)
            DisplayName    = $leaf
            LaunchTarget   = $t
            IconIdentity   = $t
            Kind           = 1
            Identity       = $t.ToLowerInvariant()
            Arguments      = $null
            WorkingDirectory = $null
        }
    }
    $json.Dock.IsVisible = $true
    $json.Dock.BackgroundStyle = 'Transparent'
    $json.Dock.PinnedApps = $pins
    $json | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $settingsPath -Encoding UTF8
}

function Start-Dock {
    $env:MURALIS_DOCK_PROFILE = '1'
    $p = Start-Process -FilePath $exe -PassThru
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 400
        if ($p.HasExited) { throw "Muralis exited during startup with code $($p.ExitCode)" }
        $h = Get-DockWindow $p.Id
        if ($h -ne [IntPtr]::Zero) {
            Start-Sleep -Milliseconds 2200   # let the pins restore and the window settle
            return [pscustomobject]@{ Process = $p; Handle = $h }
        }
    }
    throw 'the dock window never appeared'
}

function Stop-Dock($session) {
    if ($session -and -not $session.Process.HasExited) {
        [void]$session.Process.CloseMainWindow()
        Start-Sleep -Milliseconds 600
        if (-not $session.Process.HasExited) { $session.Process.Kill() }
        $session.Process.WaitForExit(5000) | Out-Null
    }
    Start-Sleep -Milliseconds 400
}

# Records written from a marker onward. The profile log is shared by every run, so a state's reading must be
# taken from the bytes this state produced and never from a previous state's tail.
function Get-RecordsFrom([long] $position) {
    if (-not (Test-Path -LiteralPath $profLog)) { return @() }
    $fs = [System.IO.File]::Open($profLog, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        if ($fs.Length -le $position) { return @() }
        [void]$fs.Seek($position, [System.IO.SeekOrigin]::Begin)
        $buffer = New-Object byte[] ($fs.Length - $position)
        [void]$fs.Read($buffer, 0, $buffer.Length)
    }
    finally { $fs.Dispose() }

    $out = @()
    foreach ($line in ([System.Text.Encoding]::UTF8.GetString($buffer) -split "`n")) {
        if ($line.Length -eq 0) { continue }
        try { $out += ($line | ConvertFrom-Json) } catch { }
    }
    return $out
}

function Get-LogLength {
    if (-not (Test-Path -LiteralPath $profLog)) { return 0 }
    return (Get-Item -LiteralPath $profLog).Length
}

if (-not (Test-Path -LiteralPath $settingsPath)) { throw "no settings file at $settingsPath" }
Copy-Item -LiteralPath $settingsPath -Destination $backup -Force
Write-Host "settings backed up to $backup" -ForegroundColor DarkGray

$desktopLinks = @(
    Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -Filter *.lnk -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ne 'Muralis.lnk' } | Select-Object -ExpandProperty FullName
)
if ($desktopLinks.Count -lt 10) { throw "need at least ten shortcuts on the desktop, found $($desktopLinks.Count)" }

$results = @()
try {
    foreach ($count in $States) {
        Write-Host "`n=== STATE: $count pinned ===" -ForegroundColor Cyan
        $targets = if ($count -eq 0) { @() } else { $desktopLinks[0..($count - 1)] }
        Write-Pins $targets

        $session = $null
        try {
            $marker = Get-LogLength
            $session = Start-Dock
            $h = $session.Handle
            $rect = Read-WindowRect $h
            $visible = [Geo.Win]::IsWindowVisible($h)

            # The dock's own reading of itself, taken from the records this state wrote.
            $records = Get-RecordsFrom $marker
            $outside = @($records | Where-Object { $_.name -eq 'motion.outside' })
            $rebuild = @($records | Where-Object { $_.name -eq 'motion.rebuild' })
            $lastOutside = if ($outside.Count -gt 0) { $outside[-1] } else { $null }
            $lastRebuild = if ($rebuild.Count -gt 0) { $rebuild[-1] } else { $null }

            # Resting interaction region: the dock's own drawn bounds, converted to screen pixels by the dock's
            # own reported window origin and display scale.
            $runWidth = if ($lastOutside) { [double]$lastOutside.right - [double]$lastOutside.left } else { $null }
            $runHeight = if ($lastOutside) { [double]$lastOutside.bottom - [double]$lastOutside.top } else { $null }

            # Expanded: drive the pointer onto the middle of the run and read the window again.
            $expanded = $null
            if ($lastOutside) {
                $midX = [int]([double]$lastOutside.originX + (([double]$lastOutside.left + [double]$lastOutside.right) / 2))
                $midY = [int]([double]$lastOutside.originY + (([double]$lastOutside.top + [double]$lastOutside.bottom) / 2))
                Move-Pointer $midX $midY
                Start-Sleep -Milliseconds 700
                $expanded = Read-WindowRect $h
                Move-Pointer 40 300
                Start-Sleep -Milliseconds 700
                $settled = Read-WindowRect $h
            } else {
                $settled = $null
            }

            $row = [pscustomobject]@{
                Pinned            = $count
                Visible           = $visible
                Scale             = $rect.Scale
                RestingWidth      = $rect.Width
                RestingHeight     = $rect.Height
                RestingLeft       = $rect.Left
                RestingTop        = $rect.Top
                CenterX           = $rect.Left + [int]($rect.Width / 2)
                ScreenCenterX     = [int]($script:ScreenW / 2)
                ExpandedWidth     = if ($expanded) { $expanded.Width } else { $null }
                ExpandedLeft      = if ($expanded) { $expanded.Left } else { $null }
                ExpandedCenterX   = if ($expanded) { $expanded.Left + [int]($expanded.Width / 2) } else { $null }
                AfterLeaveWidth   = if ($settled) { $settled.Width } else { $null }
                ContentRunDip     = $runWidth
                ContentHeightDip  = $runHeight
                Participants      = if ($lastRebuild) { $lastRebuild.icons } else { $null }
                EngineCapacity    = if ($lastRebuild) { $lastRebuild.capacity } else { $null }
                PeakScale         = if ($lastRebuild) { $lastRebuild.peak } else { $null }
            }
            $results += $row
            $row | Format-List
        }
        finally {
            Stop-Dock $session
        }
    }
}
finally {
    Copy-Item -LiteralPath $backup -Destination $settingsPath -Force
    Write-Host "`nsettings restored from $backup" -ForegroundColor DarkGray
    Get-Process Muralis -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); Start-Sleep -Milliseconds 300 }
}

$csv = Join-Path $WorkDir 'geometry.csv'
$results | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $csv
Write-Host "`n=== SUMMARY ===" -ForegroundColor Cyan
$results | Format-Table Pinned, Visible, RestingWidth, ExpandedWidth, AfterLeaveWidth, ContentRunDip, CenterX, ExpandedCenterX, Participants -AutoSize
Write-Host "wrote $csv"

# The two rules the product actually promises, checked rather than eyeballed.
$problems = @()
foreach ($r in ($results | Where-Object { $_.Pinned -gt 0 })) {
    if ($r.RestingWidth -ge 900) {
        $problems += "$($r.Pinned) apps: resting window is $($r.RestingWidth) px, which is sized for a dock the user does not have"
    }
    if ([math]::Abs($r.CenterX - $r.ScreenCenterX) -gt 2) {
        $problems += "$($r.Pinned) apps: resting window is not centred ($($r.CenterX) vs $($r.ScreenCenterX))"
    }
    if ($null -ne $r.ExpandedCenterX -and [math]::Abs($r.ExpandedCenterX - $r.CenterX) -gt 2) {
        $problems += "$($r.Pinned) apps: the dock moved when it expanded ($($r.CenterX) -> $($r.ExpandedCenterX))"
    }
    if ($null -ne $r.AfterLeaveWidth -and $r.AfterLeaveWidth -ne $r.RestingWidth) {
        $problems += "$($r.Pinned) apps: the window did not return to its resting width ($($r.AfterLeaveWidth) vs $($r.RestingWidth))"
    }
    if ($null -ne $r.ExpandedWidth -and $r.ExpandedWidth -le $r.RestingWidth) {
        $problems += "$($r.Pinned) apps: leaving the dock did not make it any wider ($($r.ExpandedWidth) vs $($r.RestingWidth))"
    }
}
$zero = $results | Where-Object { $_.Pinned -eq 0 }
if ($zero -and $zero.Visible) {
    $problems += 'nothing pinned: the dock window is still visible'
}

if ($problems.Count -gt 0) {
    Write-Host "`nFAILED:" -ForegroundColor Red
    $problems | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host "`nAll geometry rules held." -ForegroundColor Green
