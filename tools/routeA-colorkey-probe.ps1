<#
    ROUTE A2 — WS_EX_LAYERED + LWA_COLORKEY on the CURRENT WinUI 3 dock HWND.

    The question this answers: if the window is composed through the layered path with a chroma key, does the
    keyed colour actually punch through to the desktop, and does the XAML content survive?

    The key is chosen per run as a colour that appears NOWHERE in the dock's own pixels, which is measured from
    a baseline grab rather than assumed — a key that collides with an icon would delete part of the icon, and
    that collision is itself a finding.

    Nothing in the repository is modified: the styles are applied to the live window and removed afterwards.

    Usage: ./tools/routeA-colorkey-probe.ps1 [-Pins 5]
#>
[CmdletBinding()]
param([int] $Pins = 5)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\Muralis.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Muralis.exe'
$settingsPath = Join-Path $env:LOCALAPPDATA 'Muralis\settings.json'
$out = 'D:\AI\temp\dsh-cu-eval\clear-dock\routeA2'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$backup = Join-Path $out 'settings.before.json'

Add-Type -AssemblyName System.Drawing
Add-Type -Namespace Rb -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);
[DllImport("user32.dll")] public static extern IntPtr SetWindowLongPtrW(IntPtr h, int i, IntPtr v);
[DllImport("user32.dll", SetLastError=true)] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
[DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
[DllImport("user32.dll")] public static extern IntPtr GetWindowDC(IntPtr h);
[DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
[DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr d,int x,int y,int w,int h,IntPtr s,int sx,int sy,int rop);
[DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr h, IntPtr r, IntPtr u, uint f);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
public delegate bool EnumProc(IntPtr h, IntPtr p);
public struct RECT { public int Left, Top, Right, Bottom; }
public const int GWL_EXSTYLE = -20;
public const long WS_EX_LAYERED = 0x00080000L;
public const uint LWA_COLORKEY = 0x1, LWA_ALPHA = 0x2;
public const uint RDW = 0x1 | 0x80 | 0x100 | 0x400;
'@
$SW = [Rb.W]::GetSystemMetrics(0)
$SH = [Rb.W]::GetSystemMetrics(1)

function Find-Window([int]$owner, [string]$title) {
    $script:hit = [IntPtr]::Zero
    $cb = [Rb.W+EnumProc] {
        param([IntPtr]$h, [IntPtr]$p)
        $o = 0; [void][Rb.W]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -eq $owner -and [Rb.W]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Rb.W]::GetWindowTextW($h, $sb, 256)
            if ($sb.ToString() -eq 'Muralis Dock') { $script:hit = $h }
        }
        return $true
    }
    [void][Rb.W]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:hit
}

# The screen as the compositor drew it. Caller disposes.
function Grab-Screen {
    $bmp = New-Object System.Drawing.Bitmap($SW, $SH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $src = [Rb.W]::GetWindowDC([Rb.W]::GetDesktopWindow())
    try { [void][Rb.W]::BitBlt($hdc, 0, 0, $SW, $SH, $src, 0, 0, 0x00CC0020) }
    finally { [void][Rb.W]::ReleaseDC([Rb.W]::GetDesktopWindow(), $src); $g.ReleaseHdc($hdc); $g.Dispose() }
    return $bmp
}

function ExStyle([IntPtr]$h) { return [Rb.W]::GetWindowLongPtrW($h, [Rb.W]::GWL_EXSTYLE).ToInt64() }
function HasLayered([IntPtr]$h) { return ((ExStyle $h) -band [Rb.W]::WS_EX_LAYERED) -ne 0 }

# Every distinct colour in the window rect, so a key can be picked that is provably absent from the dock.
function Get-WindowColours([System.Drawing.Bitmap]$bmp, $r) {
    $seen = New-Object 'System.Collections.Generic.HashSet[int]'
    $w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
    for ($y = 0; $y -lt $ht; $y += 2) {
        for ($x = 0; $x -lt $w; $x += 2) {
            $c = $bmp.GetPixel($r.Left + $x, $r.Top + $y)
            [void]$seen.Add(($c.R -shl 16) -bor ($c.G -shl 8) -bor $c.B)
        }
    }
    return $seen
}

function Show([System.Drawing.Bitmap]$bmp, [int]$x, [int]$y, [string]$label) {
    $c = $bmp.GetPixel($x, $y)
    return "$label=$($c.R),$($c.G),$($c.B)"
}

Copy-Item $settingsPath $backup -Force
$report = New-Object System.Collections.Generic.List[string]
try {
    $json = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $links = @(Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -Filter *.lnk |
        Where-Object { $_.Name -ne 'Muralis.lnk' } | Select-Object -ExpandProperty FullName)
    $json.Dock.IsVisible = $true
    $json.Dock.BackgroundStyle = 'Transparent'
    $json.Dock.PinnedApps = @($links[0..($Pins - 1)] | ForEach-Object {
        [pscustomobject]@{ Id = [guid]::NewGuid().ToString('N').Substring(0,12)
            DisplayName = [IO.Path]::GetFileNameWithoutExtension($_)
            LaunchTarget = $_; IconIdentity = $_; Kind = 1; Identity = $_.ToLowerInvariant()
            Arguments = $null; WorkingDirectory = $null } })
    [System.IO.File]::WriteAllText($settingsPath, ($json | ConvertTo-Json -Depth 12), (New-Object System.Text.UTF8Encoding($false)))

    Get-Process Muralis -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
    Start-Sleep -Seconds 3
    $p = Start-Process $exe -PassThru
    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 75 -and $h -eq [IntPtr]::Zero; $i++) {
        Start-Sleep -Milliseconds 400
        if ($p.HasExited) { throw "Muralis exited during startup (code $($p.ExitCode))" }
        $h = Find-Window $p.Id 'Muralis Dock'
    }
    if ($h -eq [IntPtr]::Zero) { throw 'no visible dock window appeared' }
    Start-Sleep -Seconds 4

    $r = New-Object Rb.W+RECT
    [void][Rb.W]::GetWindowRect($h, [ref]$r)
    $midX = [int](($r.Left + $r.Right) / 2)
    $topBandY = $r.Top + 8
    $midY = [int](($r.Top + $r.Bottom) / 2)
    $outsideY = $r.Top - 12

    Write-Host "=== ROUTE A2: colorkey on the current WinUI HWND ===" -ForegroundColor Cyan
    Write-Host "HWND $h  rect ($($r.Left),$($r.Top)) $($r.Right-$r.Left)x$($r.Bottom-$r.Top)"
    Write-Host "exstyle: 0x$('{0:X}' -f (ExStyle $h))"

    # Pick a key absent from the dock's own pixels. 0x00FF00FF (magenta) is a common candidate but it is
    # tested, not assumed.
    $base = Grab-Screen
    $colours = Get-WindowColours $base $r
    Write-Host "  distinct colours inside the window rect: $($colours.Count)"
    $key = $null; $keyName = $null
    foreach ($candidate in @(
        @{ Name = 'magenta 0xFF00FF'; V = 0xFF00FF }
        @{ Name = 'cyan 0x00FFFF';    V = 0x00FFFF }
        @{ Name = 'lime 0x00FF00';    V = 0x00FF00 }
        @{ Name = 'key 0x010203';     V = 0x010203 }
    )) {
        if (-not $colours.Contains([int]$candidate.V)) { $key = [uint32]$candidate.V; $keyName = $candidate.Name; break }
    }
    if ($null -eq $key) { throw 'no candidate key is absent from the dock pixels; colorkey cannot be tested safely' }
    Write-Host "  chosen key: $keyName (absent from all $($colours.Count) dock colours)" -ForegroundColor Yellow
    $report.Add("key=$keyName distinctDockColours=$($colours.Count)")

    $baseline = "$(Show $base $midX $topBandY 'topband') $(Show $base $midX $outsideY 'outside') $(Show $base ($r.Left+3) $midY 'left')"
    Write-Host "  baseline : $baseline"
    $report.Add("baseline: $baseline")
    $base.Save((Join-Path $out 'B0-baseline.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $base.Dispose()

    # --- apply the colorkey ---
    $ex = ExStyle $h
    [void][Rb.W]::SetWindowLongPtrW($h, [Rb.W]::GWL_EXSTYLE, [IntPtr]($ex -bor [Rb.W]::WS_EX_LAYERED))
    $ok = [Rb.W]::SetLayeredWindowAttributes($h, $key, 0, [Rb.W]::LWA_COLORKEY)
    [void][Rb.W]::RedrawWindow($h, [IntPtr]::Zero, [IntPtr]::Zero, 0x1 -bor 0x80 -bor 0x100 -bor 0x400)
    Start-Sleep -Seconds 2

    $alive = [Rb.W]::IsWindow($h)
    $keyed = Grab-Screen
    $after = "$(Show $keyed $midX $topBandY 'topband') $(Show $keyed $midX $outsideY 'outside') $(Show $keyed ($r.Left+3) $midY 'left')"
    Write-Host "`n  colorkey applied: ret=$ok  alive=$alive  layered=$(HasLayered $h)" -ForegroundColor Yellow
    Write-Host "  after    : $after"
    $report.Add("colorkey ret=$ok alive=$alive layered=$(HasLayered $h): $after")
    $keyed.Save((Join-Path $out 'B1-colorkey.png'), [System.Drawing.Imaging.ImageFormat]::Png)

    # Did the key punch through? The band must become the wallpaper, not stay at its baseline value.
    $bandAfter = $keyed.GetPixel($midX, $topBandY)
    $bandOutside = $keyed.GetPixel($midX, $outsideY)
    $delta = [math]::Abs($bandAfter.R - $bandOutside.R) + [math]::Abs($bandAfter.G - $bandOutside.G) + [math]::Abs($bandAfter.B - $bandOutside.B)
    Write-Host "  |topband - outside| = $delta  (0 would mean the band became the desktop)" -ForegroundColor Yellow
    $report.Add("delta topband-vs-outside=$delta")

    # Is the XAML still there? Compare the icon column against the band.
    $iconPix = $keyed.GetPixel($r.Left + 40, $midY)
    Write-Host "  icon column pixel: $($iconPix.R),$($iconPix.G),$($iconPix.B)"
    $report.Add("iconColumn=$($iconPix.R),$($iconPix.G),$($iconPix.B)")
    $keyed.Dispose()

    # --- revert ---
    [void][Rb.W]::SetLayeredWindowAttributes($h, 0, 255, [Rb.W]::LWA_ALPHA)
    [void][Rb.W]::SetWindowLongPtrW($h, [Rb.W]::GWL_EXSTYLE, [IntPtr]$ex)
    [void][Rb.W]::RedrawWindow($h, [IntPtr]::Zero, [IntPtr]::Zero, 0x1 -bor 0x80 -bor 0x100 -bor 0x400)
    Start-Sleep -Seconds 1
    $rev = Grab-Screen
    Write-Host "`n  reverted: exstyle=0x$('{0:X}' -f (ExStyle $h))  layered=$(HasLayered $h)  $(Show $rev $midX $topBandY 'topband')" -ForegroundColor Green
    $report.Add("reverted: layered=$(HasLayered $h) topband=$(Show $rev $midX $topBandY 'x')")
    $rev.Save((Join-Path $out 'B9-reverted.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $rev.Dispose()
}
finally {
    Copy-Item $backup $settingsPath -Force
    Get-Process Muralis -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
    $report | Set-Content (Join-Path $out 'routeA2-report.txt') -Encoding UTF8
    Write-Host "`nreport -> $(Join-Path $out 'routeA2-report.txt')"
}
