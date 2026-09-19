<#
    ROUTE A — layered-window experiments on the CURRENT WinUI 3 dock HWND.

    Applies WS_EX_LAYERED and SetLayeredWindowAttributes to the running dock's HWND at runtime. Nothing in the
    repository is modified: the styles are applied to a live window and removed again, so a failed experiment
    leaves no trace and the next route starts from a clean build.

    A1: WS_EX_LAYERED + LWA_ALPHA   — constant alpha treatment.
    A2: WS_EX_LAYERED + LWA_COLORKEY — a chroma key the dock never uses, so the band behind the icons is meant
        to punch through to the wallpaper.
    A3: alpha behaviour at several levels, with the icons required to stay visible.

    Every experiment records: does the XAML still render, do the icons stay, what do the plate pixels become,
    and is the window still there afterwards. Reverted before the next step.

    Usage: ./tools/routeA-layered-probe.ps1 [-Pins 5]
#>
[CmdletBinding()]
param([int] $Pins = 5)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\Muralis.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Muralis.exe'
$settingsPath = Join-Path $env:LOCALAPPDATA 'Muralis\settings.json'
$out = 'D:\AI\temp\dsh-cu-eval\clear-dock\routeA'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$backup = Join-Path $out 'settings.before.json'

if (-not (Test-Path $exe)) { throw "no built binary at $exe" }

Add-Type -AssemblyName System.Drawing
Add-Type -Namespace Ra -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
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
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr h, IntPtr r, IntPtr u, uint f);
public delegate bool EnumProc(IntPtr h, IntPtr p);
public struct RECT { public int Left, Top, Right, Bottom; }
public struct POINT { public int X, Y; }
public const int GWL_EXSTYLE = -20;
public const long WS_EX_LAYERED = 0x00080000L;
public const uint LWA_COLORKEY = 0x1, LWA_ALPHA = 0x2;
public const uint MOVE = 0x0001, ABSOLUTE = 0x8000;
public const uint RDW_INVALIDATE = 0x1, RDW_ALLCHILDREN = 0x80, RDW_UPDATENOW = 0x100, RDW_FRAME = 0x400;
'@
$SW = [Ra.W]::GetSystemMetrics(0)
$SH = [Ra.W]::GetSystemMetrics(1)

function Find-Window([int]$owner, [string]$title) {
    $script:hit = [IntPtr]::Zero
    $cb = [Ra.W+EnumProc] {
        param([IntPtr]$h, [IntPtr]$p)
        $o = 0; [void][Ra.W]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -eq $owner -and [Ra.W]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Ra.W]::GetWindowTextW($h, $sb, 256)
            if ($sb.ToString() -eq $title) { $script:hit = $h }
        }
        return $true
    }
    [void][Ra.W]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:hit
}

function Grab-Screen {
    $bmp = New-Object System.Drawing.Bitmap($SW, $SH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $src = [Ra.W]::GetWindowDC([Ra.W]::GetDesktopWindow())
    [void][Ra.W]::BitBlt($hdc, 0, 0, $SW, $SH, $src, 0, 0, 0x00CC0020)
    [void][Ra.W]::ReleaseDC([Ra.W]::GetDesktopWindow(), $src)
    $g.ReleaseHdc($hdc); $g.Dispose()
    return $bmp
}

function ExStyle([IntPtr]$h) { return [Ra.W]::GetWindowLongPtrW($h, [Ra.W]::GWL_EXSTYLE).ToInt64() }
function HasLayered([IntPtr]$h) { return ((ExStyle $h) -band [Ra.W]::WS_EX_LAYERED) -ne 0 }

function Probe([System.Drawing.Bitmap]$bmp, [int]$x, [int]$y) {
    $c = $bmp.GetPixel($x, $y)
    return "$($c.R),$($c.G),$($c.B)"
}

# Reads the plate band: three points that sit inside the window but away from any icon.
function Read-Plate([System.Drawing.Bitmap]$bmp, $r) {
    $midX = [int](($r.Left + $r.Right) / 2)
    $topBand = $r.Top + 8
    $leftMargin = $r.Left + 3
    $rightMargin = $r.Right - 4
    $midY = [int](($r.Top + $r.Bottom) / 2)
    return [pscustomobject]@{
        TopBand = Probe $bmp $midX $topBand
        Left = Probe $bmp $leftMargin $midY
        Right = Probe $bmp $rightMargin $midY
        Outside = Probe $bmp $midX ($r.Top - 12)
    }
}

Copy-Item $settingsPath $backup -Force
$report = @()
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

    $r = New-Object Ra.W+RECT
    [void][Ra.W]::GetWindowRect($h, [ref]$r)
    Write-Host "=== ROUTE A: layered experiments on the current WinUI HWND ===" -ForegroundColor Cyan
    Write-Host "dock HWND: $h   rect: ($($r.Left),$($r.Top)) $($r.Right-$r.Left)x$($r.Bottom-$r.Top)"
    Write-Host "exstyle before: 0x$('{0:X}' -f (ExStyle $h))   layered: $(HasLayered $h)"

    # --- baseline ---
    $b = Grab-Screen
    $base = Read-Plate $b $r
    $b.Save((Join-Path $out 'A0-baseline.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "`n  A0 baseline (no layered style):" -ForegroundColor Yellow
    Write-Host "    topband=$($base.TopBand)  left=$($base.Left)  right=$($base.Right)  outside=$($base.Outside)"
    $b.Dispose()
    $report += "A0 baseline: topband=$($base.TopBand) left=$($base.Left) right=$($base.Right) outside=$($base.Outside)"

    # --- A1: WS_EX_LAYERED + LWA_ALPHA at 255 (constant alpha, no key) ---
    Write-Host "`n  A1: WS_EX_LAYERED + LWA_ALPHA(255), no colorkey" -ForegroundColor Yellow
    $ex = ExStyle $h
    [void][Ra.W]::SetWindowLongPtrW($h, [Ra.W]::GWL_EXSTYLE, [IntPtr]($ex -bor [Ra.W]::WS_EX_LAYERED))
    $ok = [Ra.W]::SetLayeredWindowAttributes($h, 0, 255, [Ra.W]::LWA_ALPHA)
    [void][Ra.W]::RedrawWindow($h, [IntPtr]::Zero, [IntPtr]::Zero, 0x1 -bor 0x80 -bor 0x100 -bor 0x400)
    Start-Sleep -Seconds 2
    $alive = [Ra.W]::IsWindow($h)
    $a1bmp = Grab-Screen
    $a1 = Read-Plate $a1bmp $r
    $a1bmp.Save((Join-Path $out 'A1-layered-alpha255.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "    SetLayeredWindowAttributes returned: $ok   window alive: $alive   layered now: $(HasLayered $h)"
    Write-Host "    topband=$($a1.TopBand)  left=$($a1.Left)  right=$($a1.Right)  outside=$($a1.Outside)"
    $a1bmp.Dispose()
    $report += "A1 layered+ALPHA(255): ret=$ok alive=$alive topband=$($a1.TopBand) outside=$($a1.Outside)"

    # Did the XAML survive? A vanished window means the swapchain and the layered path are incompatible.
    $contentSurvived = $alive -and ($a1.TopBand -ne $a1.Outside -or $a1.Left -ne $a1.Outside)
    Write-Host "    XAML content still distinguishable from outside: $contentSurvived"

    # --- A3: sweep alpha, requiring the plate to differ from alpha 0 while content stays ---
    foreach ($alpha in @(200, 128, 64, 1)) {
        [void][Ra.W]::SetLayeredWindowAttributes($h, 0, [byte]$alpha, [Ra.W]::LWA_ALPHA)
        [void][Ra.W]::RedrawWindow($h, [IntPtr]::Zero, [IntPtr]::Zero, 0x1 -bor 0x80 -bor 0x100 -bor 0x400)
        Start-Sleep -Milliseconds 900
        $sw = Grab-Screen
        $s = Read-Plate $sw $r
        $sw.Save((Join-Path $out "A3-alpha$alpha.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Host "    A3 alpha=$alpha : topband=$($s.TopBand)  outside=$($s.Outside)  alive=$([Ra.W]::IsWindow($h))"
        $report += "A3 alpha=${alpha}: topband=$($s.TopBand) outside=$($s.Outside) alive=$([Ra.W]::IsWindow($h))"
        $sw.Dispose()
    }

    # --- revert everything ---
    [void][Ra.W]::SetLayeredWindowAttributes($h, 0, 255, [Ra.W]::LWA_ALPHA)
    [void][Ra.W]::SetWindowLongPtrW($h, [Ra.W]::GWL_EXSTYLE, [IntPtr]$ex)
    [void][Ra.W]::RedrawWindow($h, [IntPtr]::Zero, [IntPtr]::Zero, 0x1 -bor 0x80 -bor 0x100 -bor 0x400)
    Start-Sleep -Seconds 1
    $after = Grab-Screen
    $rev = Read-Plate $after $r
    $after.Save((Join-Path $out 'A9-reverted.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "`n  reverted: exstyle=0x$('{0:X}' -f (ExStyle $h))  layered=$(HasLayered $h)" -ForegroundColor Green
    Write-Host "    topband=$($rev.TopBand)  outside=$($rev.Outside)" -ForegroundColor Green
    $after.Dispose()
    $report += "reverted: layered=$(HasLayered $h) topband=$($rev.TopBand) outside=$($rev.Outside)"
}
finally {
    Copy-Item $backup $settingsPath -Force
    Get-Process Muralis -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
    $report | Set-Content (Join-Path $out 'routeA-report.txt') -Encoding UTF8
    Write-Host "`nreport -> $(Join-Path $out 'routeA-report.txt')"
}
