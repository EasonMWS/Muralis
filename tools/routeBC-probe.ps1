<#
    ROUTES B and C — one probe, two independent experiments on the current WinUI 3 dock HWND.

    B: documented DWM frame/background attributes, aimed at TRUE CLEAR rather than blur. Blur, acrylic or any
       tint counts as a failure for this route, and is recorded as one.
    C: a window region covering only the icons, so the HWND is absent between them. The decisive question is
       whether a WinUI 3 window honours a region at all, and if it does, whether the icons survive.

    Nothing in the repository is modified. Each experiment is applied to the live window, measured, and
    reverted before the next. The pixels are read from the real composited screen.

    Usage: ./tools/routeBC-probe.ps1 [-Pins 3]
#>
[CmdletBinding()]
param([int] $Pins = 3)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\Muralis.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Muralis.exe'
$settingsPath = Join-Path $env:LOCALAPPDATA 'Muralis\settings.json'
$out = 'D:\AI\temp\dsh-cu-eval\clear-dock\routeBC'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$backup = Join-Path $out 'settings.before.json'

Add-Type -AssemblyName System.Drawing
Add-Type -Namespace Rc -Name W -MemberDefinition @'
[DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(IntPtr h);
[DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
[DllImport("user32.dll", EntryPoint="SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
[DllImport("user32.dll", SetLastError=true)] public static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
[DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
[DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
[DllImport("gdi32.dll")] public static extern int CombineRgn(IntPtr dst, IntPtr a, IntPtr b, int mode);
[DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
[DllImport("dwmapi.dll", PreserveSig=true)] public static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
[DllImport("dwmapi.dll", PreserveSig=true)] public static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);
[DllImport("dwmapi.dll", PreserveSig=true)] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int s);
[DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
[DllImport("user32.dll")] public static extern IntPtr GetWindowDC(IntPtr h);
[DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
[DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool BitBlt(IntPtr d,int x,int y,int w,int h,IntPtr s,int sx,int sy,int rop);
[DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool RedrawWindow(IntPtr h, IntPtr r, IntPtr u, uint f);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
public delegate bool EnumProc(IntPtr h, IntPtr p);
public struct RECT { public int Left, Top, Right, Bottom; }
public struct MARGINS { public int Left, Right, Top, Bottom; }
public const int GWL_EXSTYLE = -20;
public const long WS_EX_LAYERED = 0x00080000L;
public const int RGN_OR = 2, RGN_DIFF = 4;
public const uint RDW = 0x1 | 0x80 | 0x100 | 0x400;
'@
$SW = [Rc.W]::GetSystemMetrics(0)
$SH = [Rc.W]::GetSystemMetrics(1)

function Find-Window([int]$owner, [string]$title) {
    $script:hit = [IntPtr]::Zero
    $cb = [Rc.W+EnumProc] {
        param([IntPtr]$h, [IntPtr]$p)
        $o = 0; [void][Rc.W]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -eq $owner -and [Rc.W]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Rc.W]::GetWindowTextW($h, $sb, 256)
            if ($sb.ToString() -eq 'Muralis Dock') { $script:hit = $h }
        }
        return $true
    }
    [void][Rc.W]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:hit
}
function Grab-Screen {
    $bmp = New-Object System.Drawing.Bitmap($SW, $SH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $src = [Rc.W]::GetWindowDC([Rc.W]::GetDesktopWindow())
    try { [void][Rc.W]::BitBlt($hdc, 0, 0, $SW, $SH, $src, 0, 0, 0x00CC0020) }
    finally { [void][Rc.W]::ReleaseDC([Rc.W]::GetDesktopWindow(), $src); $g.ReleaseHdc($hdc); $g.Dispose() }
    return $bmp
}
function Pix([System.Drawing.Bitmap]$b, [int]$x, [int]$y) { $c = $b.GetPixel($x,$y); return "$($c.R),$($c.G),$($c.B)" }

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

    # Exactly one Muralis may exist, and the window must belong to the process this run started. A second
    # launch only wakes the first and exits, so without this the probe measures a stale window and every
    # reading after the first is taken from a dead handle.
    Get-Process Muralis -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
    $clear = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $clear -and @(Get-Process Muralis -ErrorAction SilentlyContinue).Count -gt 0) { Start-Sleep -Milliseconds 300 }
    if (@(Get-Process Muralis -ErrorAction SilentlyContinue).Count -gt 0) { throw 'a Muralis process would not close' }

    $p = Start-Process $exe -PassThru
    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 75 -and $h -eq [IntPtr]::Zero; $i++) {
        Start-Sleep -Milliseconds 400
        if ($p.HasExited) { throw "Muralis exited during startup (code $($p.ExitCode))" }
        $h = Find-Window $p.Id 'Muralis Dock'
    }
    if ($h -eq [IntPtr]::Zero) { throw 'no visible dock window appeared' }
    Start-Sleep -Seconds 4
    if ($Pins -ge 2) { Start-Sleep -Seconds 6 }   # let the icons finish arriving from the shell
    if ($p.HasExited) { throw "Muralis exited before the probe began (code $($p.ExitCode))" }

    # The window's owner must be this process, or the handle is stale and the readings are worthless.
    $owner = 0
    [void][Rc.W]::GetWindowThreadProcessId($h, [ref]$owner)
    if ($owner -ne $p.Id) { throw "the dock window belongs to pid $owner, not the process this run started ($($p.Id))" }
    Write-Host "  guarded: hwnd $h owned by pid $($p.Id), process alive=$(-not $p.HasExited)"

    function Resolve-Dock {
        $fresh = Find-Window $p.Id 'Muralis Dock'
        if ($fresh -eq [IntPtr]::Zero) { throw 'the dock window is gone; the reading would be meaningless' }
        $owner2 = 0
        [void][Rc.W]::GetWindowThreadProcessId($fresh, [ref]$owner2)
        if ($owner2 -ne $p.Id) { throw "stale handle: window belongs to pid $owner2" }
        return $fresh
    }
    $h = Resolve-Dock
    $exNow = [Rc.W]::GetWindowLongPtr($h, [Rc.W]::GWL_EXSTYLE).ToInt64()
    if ($exNow -eq 0) { throw 'the dock window reports no extended style, so the handle is not the live dock window' }
    $r = New-Object Rc.W+RECT
    [void][Rc.W]::GetWindowRect($h, [ref]$r)
    $W = $r.Right - $r.Left; $H = $r.Bottom - $r.Top
    $midX = [int]($r.Left + $W / 2)
    Write-Host "=== ROUTES B and C ===" -ForegroundColor Cyan
    Write-Host "HWND $h  rect ($($r.Left),$($r.Top)) ${W}x${H}  pins=$Pins"
    Write-Host "exstyle 0x$('{0:X}' -f ([Rc.W]::GetWindowLongPtr($h,[Rc.W]::GWL_EXSTYLE).ToInt64()))"

    $base = Grab-Screen
    $base.Save((Join-Path $out 'C0-baseline.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    # The band between the first two icons is the place a plate shows and a region would remove.
    $between = $r.Left + 66
    $bandY = $r.Top + 60
    Write-Host "`n  baseline:" -ForegroundColor Yellow
    Write-Host "    gap between icons ($between,$bandY) = $(Pix $base $between $bandY)"
    Write-Host "    top band           ($midX,$($r.Top+8)) = $(Pix $base $midX ($r.Top+8))"
    Write-Host "    just outside       ($midX,$($r.Top-12)) = $(Pix $base $midX ($r.Top-12))"
    $report.Add("baseline gap=$(Pix $base $between $bandY) topband=$(Pix $base $midX ($r.Top+8)) outside=$(Pix $base $midX ($r.Top-12))")
    $base.Dispose()

    # ---------- ROUTE B: DWM ----------
    $h = Resolve-Dock
    Write-Host "`n  --- ROUTE B: DWM frame/background ---" -ForegroundColor Yellow
    $m = New-Object Rc.W+MARGINS
    $m.Left = -1; $m.Right = -1; $m.Top = -1; $m.Bottom = -1
    $bRet = [Rc.W]::DwmExtendFrameIntoClientArea($h, [ref]$m)
    [void][Rc.W]::RedrawWindow($h, [IntPtr]::Zero, [IntPtr]::Zero, 0x1 -bor 0x80 -bor 0x100 -bor 0x400)
    Start-Sleep -Seconds 2
    $bb = Grab-Screen
    $bb.Save((Join-Path $out 'B1-dwm-glass.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "    DwmExtendFrameIntoClientArea(-1,-1,-1,-1) returned 0x$('{0:X}' -f $bRet)"
    Write-Host "    gap = $(Pix $bb $between $bandY)   outside = $(Pix $bb $midX ($r.Top-12))   alive=$([Rc.W]::IsWindow($h))"
    $report.Add("ROUTE B DwmExtendFrame ret=0x$('{0:X}' -f $bRet) gap=$(Pix $bb $between $bandY) outside=$(Pix $bb $midX ($r.Top-12))")
    $bb.Dispose()
    # revert B
    $z = New-Object Rc.W+MARGINS
    [void][Rc.W]::DwmExtendFrameIntoClientArea($h, [ref]$z)
    [void][Rc.W]::RedrawWindow($h, [IntPtr]::Zero, [IntPtr]::Zero, 0x1 -bor 0x80 -bor 0x100 -bor 0x400)
    Start-Sleep -Milliseconds 800

    # ---------- ROUTE C: window region ----------
    $h = Resolve-Dock
    Write-Host "`n  --- ROUTE C: window region over the icons only ---" -ForegroundColor Yellow
    # Icon cell is 56 wide (52 box + 4 gap) with 12 of plate padding; take each icon's own 52-wide box,
    # inset by 2 so the rounded corners of the icon artwork are what the region follows if it works at all.
    $pad = 12
    $cell = 56
    $region = [IntPtr]::Zero
    for ($i = 0; $i -lt $Pins; $i++) {
        $x0 = $pad + ($i * $cell) + 2
        $piece = [Rc.W]::CreateRoundRectRgn($x0, 40, $x0 + 48, 92, 12, 12)
        if ($region -eq [IntPtr]::Zero) { $region = $piece }
        else { [void][Rc.W]::CombineRgn($region, $region, $piece, [Rc.W]::RGN_OR); [void][Rc.W]::DeleteObject($piece) }
    }
    $cRet = [Rc.W]::SetWindowRgn($h, $region, $true)
    [void][Rc.W]::RedrawWindow($h, [IntPtr]::Zero, [IntPtr]::Zero, 0x1 -bor 0x80 -bor 0x100 -bor 0x400)
    Start-Sleep -Seconds 2
    $cb = Grab-Screen
    $cb.Save((Join-Path $out 'C1-region-icons.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "    SetWindowRgn returned $cRet   alive=$([Rc.W]::IsWindow($h))"
    Write-Host "    gap between icons = $(Pix $cb $between $bandY)   outside = $(Pix $cb $midX ($r.Top-12))"
    Write-Host "    inside first icon = $(Pix $cb ($r.Left+40) $bandY)"
    $report.Add("ROUTE C SetWindowRgn ret=$cRet gap=$(Pix $cb $between $bandY) outside=$(Pix $cb $midX ($r.Top-12)) inIcon=$(Pix $cb ($r.Left+40) $bandY) alive=$([Rc.W]::IsWindow($h))")
    $cb.Dispose()

    # revert C
    [void][Rc.W]::SetWindowRgn($h, [IntPtr]::Zero, $true)
    if ($region -ne [IntPtr]::Zero) { [void][Rc.W]::DeleteObject($region) }
    [void][Rc.W]::RedrawWindow($h, [IntPtr]::Zero, [IntPtr]::Zero, 0x1 -bor 0x80 -bor 0x100 -bor 0x400)
    Start-Sleep -Seconds 1
    $rev = Grab-Screen
    Write-Host "`n  reverted: gap = $(Pix $rev $between $bandY)  alive=$([Rc.W]::IsWindow($h))" -ForegroundColor Green
    $report.Add("reverted gap=$(Pix $rev $between $bandY) alive=$([Rc.W]::IsWindow($h))")
    $rev.Dispose()
}
finally {
    Copy-Item $backup $settingsPath -Force
    Get-Process Muralis -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
    $report | Set-Content (Join-Path $out 'routeBC-report.txt') -Encoding UTF8
    Write-Host "`nreport -> $(Join-Path $out 'routeBC-report.txt')"
}
