<#
    Decides the one question the UI-Automation capture could not: does the dock paint a dock-wide surface?

    The `observe` capture renders the accessibility tree onto a synthetic background, so a transparent window
    over the wallpaper comes out opaque whatever the dock does. This grabs the composited screen instead and
    reads the pixels in the band a plate would occupy — the dock's own top band, its left and right margins,
    and the area just outside the window on all four sides. A painted plate makes inside differ from outside;
    no plate makes the band identical to its surroundings.

    Usage: ./tools/p4d-surface-proof.ps1 [-Pins 6] [-Style Transparent|Glass]
#>
[CmdletBinding()]
param(
    [int] $Pins = 6,
    [ValidateSet('Transparent', 'Glass')]
    [string] $Style = 'Transparent'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\Muralis.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Muralis.exe'
$settingsPath = Join-Path $env:LOCALAPPDATA 'Muralis\settings.json'
$profLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
$out = 'D:\AI\temp\dsh-cu-eval\product'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$backup = Join-Path $out "settings.surface.$Style.before.json"

if (-not (Test-Path $exe)) { throw "no built binary at $exe" }

Add-Type -AssemblyName System.Drawing
Add-Type -Namespace Pf -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
[DllImport("user32.dll")] public static extern IntPtr GetWindowDC(IntPtr h);
[DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
[DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr d,int x,int y,int w,int h,IntPtr s,int sx,int sy,int rop);
public delegate bool EnumProc(IntPtr h, IntPtr p);
public struct RECT { public int Left, Top, Right, Bottom; }
'@
$SW = [Pf.W]::GetSystemMetrics(0)
$SH = [Pf.W]::GetSystemMetrics(1)

function Find-Dock([int]$owner) {
    $script:hit = [IntPtr]::Zero
    $cb = [Pf.W+EnumProc] {
        param([IntPtr]$h, [IntPtr]$p)
        $o = 0; [void][Pf.W]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -eq $owner -and [Pf.W]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Pf.W]::GetWindowTextW($h, $sb, 256)
            if ($sb.ToString() -eq 'Muralis Dock') { $script:hit = $h }
        }
        return $true
    }
    [void][Pf.W]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:hit
}

function Grab {
    $bmp = New-Object System.Drawing.Bitmap($SW, $SH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $src = [Pf.W]::GetWindowDC([Pf.W]::GetDesktopWindow())
    [void][Pf.W]::BitBlt($hdc, 0, 0, $SW, $SH, $src, 0, 0, 0x00CC0020)
    [void][Pf.W]::ReleaseDC([Pf.W]::GetDesktopWindow(), $src)
    $g.ReleaseHdc($hdc); $g.Dispose()
    return $bmp
}

function Classify([System.Drawing.Bitmap]$bmp, [int]$x, [int]$y) {
    $c = $bmp.GetPixel($x, $y)
    return "$($c.R),$($c.G),$($c.B)"
}

Copy-Item $settingsPath $backup -Force
try {
    $json = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $links = @(Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -Filter *.lnk |
        Where-Object { $_.Name -ne 'Muralis.lnk' } | Select-Object -ExpandProperty FullName)
    $json.Dock.IsVisible = $true
    $json.Dock.BackgroundStyle = $Style
    $json.Dock.PinnedApps = @($links[0..($Pins - 1)] | ForEach-Object {
        [pscustomobject]@{ Id = [guid]::NewGuid().ToString('N').Substring(0,12)
            DisplayName = [IO.Path]::GetFileNameWithoutExtension($_)
            LaunchTarget = $_; IconIdentity = $_; Kind = 1; Identity = $_.ToLowerInvariant()
            Arguments = $null; WorkingDirectory = $null } })
    [System.IO.File]::WriteAllText($settingsPath, ($json | ConvertTo-Json -Depth 12), (New-Object System.Text.UTF8Encoding($false)))

    # One instance only: a second launch just wakes the first and exits.
    Get-Process Muralis -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
    $clear = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $clear -and @(Get-Process Muralis -ErrorAction SilentlyContinue).Count -gt 0) { Start-Sleep -Milliseconds 300 }
    if (@(Get-Process Muralis -ErrorAction SilentlyContinue).Count -gt 0) { throw 'a Muralis process would not close' }

    $env:MURALIS_DOCK_PROFILE = '1'
    $p = Start-Process $exe -PassThru
    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 75 -and $h -eq [IntPtr]::Zero; $i++) {
        Start-Sleep -Milliseconds 400
        if ($p.HasExited) { throw "Muralis exited immediately (code $($p.ExitCode))" }
        $h = Find-Dock $p.Id
    }
    if ($h -eq [IntPtr]::Zero) { throw 'no visible dock window appeared' }
    Start-Sleep -Milliseconds 3000

    $wr = New-Object Pf.W+RECT
    [void][Pf.W]::GetWindowRect($h, [ref]$wr)
    $w = $wr.Right - $wr.Left
    $ht = $wr.Bottom - $wr.Top
    $midX = $wr.Left + [int]($w / 2)

    $bmp = Grab
    Write-Host "=== STYLE: $Style   ($Pins pinned) ===" -ForegroundColor Cyan
    Write-Host "dock window rect: ($($wr.Left),$($wr.Top)) ${w}x${ht}"
    Write-Host ""
    Write-Host "  band a dock-wide plate would occupy, INSIDE the window:"
    Write-Host "    top band above the icons        ($midX,$($wr.Top + 8))   = $(Classify $bmp $midX ($wr.Top + 8))"
    Write-Host "    left margin inside the window   ($($wr.Left + 3),$($wr.Top + 60)) = $(Classify $bmp ($wr.Left + 3) ($wr.Top + 60))"
    Write-Host "    right margin inside the window  ($($wr.Right - 4),$($wr.Top + 60)) = $(Classify $bmp ($wr.Right - 4) ($wr.Top + 60))"
    Write-Host "  the same surroundings, OUTSIDE the window:"
    Write-Host "    just above                      ($midX,$($wr.Top - 8))   = $(Classify $bmp $midX ($wr.Top - 8))"
    Write-Host "    just left                       ($($wr.Left - 8),$($wr.Top + 60)) = $(Classify $bmp ($wr.Left - 8) ($wr.Top + 60))"
    Write-Host "    just right                      ($($wr.Right + 8),$($wr.Top + 60)) = $(Classify $bmp ($wr.Right + 8) ($wr.Top + 60))"

    $shot = Join-Path $root "screenshots\dock-surface-$Style.png"
    $bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)

    $pad = 40
    $cx = [Math]::Max(0, $wr.Left - $pad); $cy = [Math]::Max(0, $wr.Top - $pad)
    $cw = [Math]::Min($SW - $cx, $w + (2 * $pad)); $ch = [Math]::Min($SH - $cy, $ht + (2 * $pad))
    $mag = New-Object System.Drawing.Bitmap(($cw * 2), ($ch * 2))
    $g = [System.Drawing.Graphics]::FromImage($mag)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g.DrawImage($bmp, (New-Object System.Drawing.Rectangle(0,0,($cw*2),($ch*2))), (New-Object System.Drawing.Rectangle($cx,$cy,$cw,$ch)), [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    $magShot = Join-Path $root "screenshots\dock-surface-$Style-crop.png"
    $mag.Save($magShot, [System.Drawing.Imaging.ImageFormat]::Png)
    $g2 = $null
    Write-Host ""
    Write-Host "  full screen  -> $shot"
    Write-Host "  2x crop      -> $magShot  (${cw}x${ch} magnified)"

    $bmp.Dispose(); $mag.Dispose()
}
finally {
    Copy-Item $backup $settingsPath -Force
    Get-Process Muralis -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
}
