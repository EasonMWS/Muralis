<#
    Captures the REAL screen, not the UI Automation tree.

    The `observe` capture used elsewhere renders the accessibility tree onto a synthetic background, so a
    transparent window over the wallpaper comes out as an opaque white rectangle: it proves nothing about
    whether a dock-wide surface is painted. This grabs the composited screen instead, then writes both a
    full-screen frame and a crop around the dock, so "icons floating on the wallpaper" or "a plate behind
    them" is decided by the pixels the display actually showed.

    Usage: ./tools/p4d-screen-capture.ps1 -States 0,3,6,10 [-ShotDir screenshots]
#>
[CmdletBinding()]
param(
    [int[]] $States = @(0, 3, 6, 10),
    [string] $ShotDir = 'screenshots',
    [string] $WorkDir = 'D:\AI\temp\dsh-cu-eval\product'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$shotRoot = if ([IO.Path]::IsPathRooted($ShotDir)) { $ShotDir } else { Join-Path $root $ShotDir }
New-Item -ItemType Directory -Force -Path $shotRoot, $WorkDir | Out-Null

$exe = Join-Path $root 'src\Muralis.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Muralis.exe'
$settingsPath = Join-Path $env:LOCALAPPDATA 'Muralis\settings.json'
$profLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
$backup = Join-Path $WorkDir 'settings.screen.before.json'

Add-Type -AssemblyName System.Drawing

Add-Type -Namespace Sc -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
[DllImport("user32.dll")] public static extern IntPtr GetWindowDC(IntPtr h);
[DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
[DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
public delegate bool EnumProc(IntPtr h, IntPtr p);
public struct RECT { public int Left, Top, Right, Bottom; }
public struct POINT { public int X, Y; }
public const uint MOVE = 0x0001, ABSOLUTE = 0x8000;
'@
$SW = [Sc.W]::GetSystemMetrics(0)
$SH = [Sc.W]::GetSystemMetrics(1)

function Find-Dock([int]$owner) {
    $script:hit = [IntPtr]::Zero
    $cb = [Sc.W+EnumProc] {
        param([IntPtr]$h, [IntPtr]$p)
        $o = 0; [void][Sc.W]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -eq $owner -and [Sc.W]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Sc.W]::GetWindowTextW($h, $sb, 256)
            if ($sb.ToString() -eq 'Muralis Dock') { $script:hit = $h }
        }
        return $true
    }
    [void][Sc.W]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:hit
}

function Get-LogLen { if (Test-Path $profLog) { (Get-Item $profLog).Length } else { 0 } }
function Get-Recs([long]$from) {
    $fs = [System.IO.File]::Open($profLog, 'Open', 'Read', 'ReadWrite')
    try { if ($fs.Length -le $from) { return @() }
        [void]$fs.Seek($from, [System.IO.SeekOrigin]::Begin)
        $b = New-Object byte[] ($fs.Length - $from); [void]$fs.Read($b, 0, $b.Length) } finally { $fs.Dispose() }
    $o = @(); foreach ($l in ([System.Text.Encoding]::UTF8.GetString($b) -split "`n")) { if ($l) { try { $o += ($l | ConvertFrom-Json) } catch {} } }
    return $o
}
function Move-Pointer([int]$x, [int]$y) {
    [void][Sc.W]::mouse_event([Sc.W]::MOVE -bor [Sc.W]::ABSOLUTE,
        [int](($x * 65535) / ($SW - 1)), [int](($y * 65535) / ($SH - 1)), 0, [IntPtr]::Zero)
}

<#
    The screen as the compositor drew it. CAPTUREBLT is deliberately not used: it would include the layered
    window's own alpha handling and can composite differently from what is on the display.
#>
function Grab-Screen {
    $bmp = New-Object System.Drawing.Bitmap($SW, $SH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    try {
        $src = [Sc.W]::GetWindowDC([Sc.W]::GetDesktopWindow())
        try { [void][Sc.W]::BitBlt($hdc, 0, 0, $SW, $SH, $src, 0, 0, 0x00CC0020) }
        finally { [void][Sc.W]::ReleaseDC([Sc.W]::GetDesktopWindow(), $src) }
    } finally { $g.ReleaseHdc($hdc); $g.Dispose() }
    return $bmp
}

function Describe-Window([System.Drawing.Bitmap]$bmp, [string]$name) {
    # What is actually behind and between the icons: sampled in the band above the icons, at the dock's own
    # margins, and just outside the dock. Identical values inside and outside the dock mean no surface.
    $samples = @{}
    foreach ($pt in $script:probePoints) {
        $p = $bmp.GetPixel($pt.X, $pt.Y)
        $key = "$($p.R),$($p.G),$($p.B)"
        if (-not $samples.ContainsKey($pt.Where)) { $samples[$pt.Where] = @() }
        $samples[$pt.Where] += $key
    }
    Write-Host "  $name — composited screen pixels:" -ForegroundColor Cyan
    foreach ($k in $samples.Keys) {
        $vals = $samples[$k] | Group-Object | Sort-Object Count -Descending | Select-Object -First 2
        Write-Host ("    {0,-26} {1}" -f $k, (($vals | ForEach-Object { "$($_.Name) x$($_.Count)" }) -join '  '))
    }
}

Copy-Item $settingsPath $backup -Force
try {
    $json = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $links = @(Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -Filter *.lnk |
        Where-Object { $_.Name -ne 'Muralis.lnk' } | Select-Object -ExpandProperty FullName)

    foreach ($count in $States) {
        Write-Host "`n=== STATE: $count pinned ===" -ForegroundColor Cyan
        $targets = if ($count -eq 0) { @() } else { $links[0..($count - 1)] }

        $json.Dock.IsVisible = $true
        $json.Dock.BackgroundStyle = 'Transparent'
        $json.Dock.PinnedApps = @($targets | ForEach-Object {
            [pscustomobject]@{ Id = [guid]::NewGuid().ToString('N').Substring(0,12)
                DisplayName = [IO.Path]::GetFileNameWithoutExtension($_)
                LaunchTarget = $_; IconIdentity = $_; Kind = 1; Identity = $_.ToLowerInvariant()
                Arguments = $null; WorkingDirectory = $null } })
        [System.IO.File]::WriteAllText($settingsPath, ($json | ConvertTo-Json -Depth 12), (New-Object System.Text.UTF8Encoding($false)))

        $env:MURALIS_DOCK_PROFILE = '1'

        # Exactly one Muralis may exist. The app has a single-instance guard: a second launch wakes the first
        # and exits itself with 0x8000FFFF, and an orphan left by an earlier run is a window that answers the
        # search while ignoring the settings this run just wrote. Both look exactly like a dock that failed to
        # appear, so the process table is cleared and waited on before every state.
        Get-Process Muralis -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
        $clear = (Get-Date).AddSeconds(15)
        while ((Get-Date) -lt $clear -and @(Get-Process Muralis -ErrorAction SilentlyContinue).Count -gt 0) {
            Start-Sleep -Milliseconds 300
        }
        if (@(Get-Process Muralis -ErrorAction SilentlyContinue).Count -gt 0) {
            throw 'a Muralis process would not close, so this state cannot be measured cleanly'
        }

        $mark = Get-LogLen
        $p = Start-Process $exe -PassThru
        $h = [IntPtr]::Zero
        for ($i = 0; $i -lt 75 -and $h -eq [IntPtr]::Zero; $i++) {
            Start-Sleep -Milliseconds 400
            if ($p.HasExited) { throw "Muralis exited immediately (code $($p.ExitCode)): another instance held the single-instance lock" }
            $h = Find-Dock $p.Id
        }

        if ($h -eq [IntPtr]::Zero) {
            Write-Host "  no visible dock window (expected only for 0 pinned)" -ForegroundColor Yellow
            if (-not $p.HasExited) { $p.Kill() }
            Start-Sleep -Milliseconds 700
            continue
        }

        # Park the pointer well away so the capture is the resting state.
        Move-Pointer 40 300
        Start-Sleep -Milliseconds 2500

        $wr = New-Object Sc.W+RECT
        [void][Sc.W]::GetWindowRect($h, [ref]$wr)
        $w = $wr.Right - $wr.Left
        $ht = $wr.Bottom - $wr.Top

        # Probe points in SCREEN coordinates: inside the dock's top band, its left margin, its middle, and
        # just outside it on both sides and above. A painted plate makes inside differ from outside.
        $script:probePoints = @(
            [pscustomobject]@{ Where = 'outside-left';  X = [Math]::Max(0, $wr.Left - 40); Y = $wr.Top + 8 }
            [pscustomobject]@{ Where = 'outside-above'; X = $wr.Left + [int]($w / 2);    Y = [Math]::Max(0, $wr.Top - 30) }
            [pscustomobject]@{ Where = 'outside-right'; X = [Math]::Min($SW - 1, $wr.Right + 40); Y = $wr.Top + 8 }
            [pscustomobject]@{ Where = 'inside-top';    X = $wr.Left + [int]($w / 2);    Y = $wr.Top + 6 }
            [pscustomobject]@{ Where = 'inside-left';   X = $wr.Left + 4;                Y = $wr.Top + [int]($ht / 2) }
            [pscustomobject]@{ Where = 'inside-right';  X = $wr.Right - 5;               Y = $wr.Top + [int]($ht / 2) }
            [pscustomobject]@{ Where = 'inside-gap';    X = $wr.Left + [int]($w / 2);    Y = $wr.Top + [int]($ht / 2) }
        )

        $full = Grab-Screen
        $fullName = Join-Path $WorkDir "screen-full-$count.png"
        $full.Save($fullName, [System.Drawing.Imaging.ImageFormat]::Png)

        $pad = 30
        $cx = [Math]::Max(0, $wr.Left - $pad)
        $cy = [Math]::Max(0, $wr.Top - $pad)
        $cw = [Math]::Min($SW - $cx, $w + (2 * $pad))
        $ch = [Math]::Min($SH - $cy, $ht + (2 * $pad))
        $crop = New-Object System.Drawing.Bitmap($cw, $ch)
        $g = [System.Drawing.Graphics]::FromImage($crop)
        $g.DrawImage($full, (New-Object System.Drawing.Rectangle(0, 0, $cw, $ch)), (New-Object System.Drawing.Rectangle($cx, $cy, $cw, $ch)), [System.Drawing.GraphicsUnit]::Pixel)
        $g.Dispose()

        $name = switch ($count) {
            0 { 'dock-screen-01-0apps.png' }
            3 { 'dock-screen-02-3apps.png' }
            6 { 'dock-screen-03-6apps.png' }
            10 { 'dock-screen-04-10apps.png' }
            default { "dock-screen-99-$($count).png" }
        }
        $dest = Join-Path $shotRoot $name
        $crop.Save($dest, [System.Drawing.Imaging.ImageFormat]::Png)

        Write-Host "  window rect: ($($wr.Left),$($wr.Top)) ${w}x${ht}   crop -> $name"
        Describe-Window $full $name
        Write-Host "  full screen -> $fullName"

        $full.Dispose(); $crop.Dispose()
        if (-not $p.HasExited) { $p.Kill() }
        Start-Sleep -Milliseconds 900
    }
}
finally {
    Copy-Item $backup $settingsPath -Force
    Get-Process Muralis -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
}
