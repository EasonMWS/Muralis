<#
    BACKING PROOF — transparency proved against a background the harness controls.

    The pixel proof compares the empty parts of the dock window against the wallpaper behind it. That is the
    right test, but on this machine the wallpaper where the dock sits happens to be white (249,249,249) — and an
    opaque white plate would read exactly the same. The comparison cannot fail, so it cannot prove anything about
    that particular colour.

    This closes the hole. A solid magenta window is placed directly behind the dock, covering it entirely. Now
    the empty parts of the dock must read MAGENTA, which no plate the dock could paint would ever be. A white
    plate, a grey plate, a Mica plate and an Acrylic plate are all distinguishable from magenta by a mile.

    Sampling happens twice against the same magenta backing:
      * dock shown   -> the empty area must be magenta, and the icons must cover it with ink
      * dock hidden  -> magenta everywhere, proving the backing really is behind the dock and the grab is honest

    Usage:
      ./tools/backing-proof.ps1 -Exe <path> [-AppArgs '--pins 5'] [-OutDir <dir>]
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Exe,
    [string[]] $AppArgs = @(),
    [int] $IconBox = 52,
    [int] $Cell = 56,
    [int] $Pad = 12,
    [int] $PlatePaddingY = 10,
    [int] $VerticalReserve = 44,
    [string] $BackingColour = 'Magenta',
    [string] $OutDir = 'D:\AI\temp\dsh-cu-eval\clear-dock\backing'
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace Bp -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
[DllImport("user32.dll")] public static extern IntPtr GetWindowDC(IntPtr h);
[DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
[DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool BitBlt(IntPtr d,int x,int y,int w,int h,IntPtr s,int sx,int sy,int rop);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ShowWindow(IntPtr h, int cmd);
[DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int ht, uint flags);
public delegate bool EnumProc(IntPtr h, IntPtr p);
public struct RECT { public int Left, Top, Right, Bottom; }
public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
'@

$SW = [Bp.W]::GetSystemMetrics(0)
$SH = [Bp.W]::GetSystemMetrics(1)

function Find-Top([int]$owner, [string]$titleLike) {
    $hits = New-Object System.Collections.Generic.List[IntPtr]
    $cb = [Bp.W+EnumProc] {
        param([IntPtr]$h, [IntPtr]$p)
        $o = 0; [void][Bp.W]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -eq $owner) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Bp.W]::GetWindowTextW($h, $sb, 256)
            if ($sb.ToString() -like $titleLike) { $hits.Add($h) }
        }
        return $true
    }
    [void][Bp.W]::EnumWindows($cb, [IntPtr]::Zero)
    return $hits
}

function Grab {
    $bmp = New-Object System.Drawing.Bitmap($SW, $SH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $src = [Bp.W]::GetWindowDC([Bp.W]::GetDesktopWindow())
    try { [void][Bp.W]::BitBlt($hdc, 0, 0, $SW, $SH, $src, 0, 0, 0x00CC0020) }
    finally { [void][Bp.W]::ReleaseDC([Bp.W]::GetDesktopWindow(), $src); $g.ReleaseHdc($hdc); $g.Dispose() }
    return $bmp
}
function Rgb([System.Drawing.Bitmap]$b, [int]$x, [int]$y) { $c = $b.GetPixel($x,$y); return @($c.R, $c.G, $c.B) }
function Dist($a, $b) { return [math]::Abs($a[0]-$b[0]) + [math]::Abs($a[1]-$b[1]) + [math]::Abs($a[2]-$b[2]) }

if (-not (Test-Path $Exe)) { throw "no executable at $Exe" }

$want = [System.Drawing.Color]::FromName($BackingColour)
if ($want.IsEmpty) { throw "unknown backing colour $BackingColour" }
$wantRgb = @([int]$want.R, [int]$want.G, [int]$want.B)

Write-Host "=== BACKING PROOF ===" -ForegroundColor Cyan
Write-Host "candidate : $Exe"
Write-Host "backing   : $BackingColour = $($wantRgb -join ',')  (a colour no dock plate would ever be)"

# --- the backing window: a plain WinForms window, solid magenta, not topmost, never activated ---
$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = 'None'
$form.StartPosition = 'Manual'
$form.ShowInTaskbar = $false
$form.TopMost = $false
$form.BackColor = $want
$form.Text = 'Muralis Backing Proof'
$form.SetBounds(0, 0, 400, 300)
$form.Show()
[System.Windows.Forms.Application]::DoEvents()
Write-Host "  backing window shown: $((New-Object System.Drawing.Rectangle $form.Left,$form.Top,$form.Width,$form.Height))"

# --- the candidate ---
$stdoutPath = Join-Path $OutDir 'poc-stdout.txt'
if (Test-Path $stdoutPath) { Remove-Item $stdoutPath -Force }
$startArgs = @{ FilePath = $Exe; PassThru = $true; RedirectStandardOutput = $stdoutPath }
if ($AppArgs.Count -gt 0) { $startArgs['ArgumentList'] = $AppArgs }
$proc = Start-Process @startArgs

$wins = @()
$deadline = (Get-Date).AddSeconds(25)
while ((Get-Date) -lt $deadline) {
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 250
    if ($proc.HasExited) { throw "the candidate exited before presenting (code $($proc.ExitCode))" }
    $wins = @(Find-Top $proc.Id '*Clear Dock*' | Where-Object { [Bp.W]::IsWindowVisible($_) })
    if ($wins.Count -gt 0) { break }
}
if ($wins.Count -eq 0) { throw 'no visible candidate window appeared' }
$h = $wins[0]

$r = New-Object Bp.W+RECT
[void][Bp.W]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
Write-Host "  candidate window: hwnd=$h rect=($($r.Left),$($r.Top)) ${w}x${ht}"

# --- put the magenta window exactly behind the dock ---
# Generously larger than the dock so the dock sits inside it with a margin on every side, which also proves the
# backing covers ground the dock does not.
$margin = 60
$form.SetBounds($r.Left - $margin, $r.Top - $margin, $w + (2 * $margin), $ht + (2 * $margin))
[System.Windows.Forms.Application]::DoEvents()
Start-Sleep -Milliseconds 300
# The dock is topmost in its default mode; the backing stays in the normal band, so the dock is above it while
# the desktop is below it. Neither window is allowed to activate.
[void][Bp.W]::SetWindowPos($h, [Bp.W]::HWND_TOPMOST, 0, 0, 0, 0, ([Bp.W]::SWP_NOMOVE -bor [Bp.W]::SWP_NOSIZE -bor [Bp.W]::SWP_NOACTIVATE -bor [Bp.W]::SWP_SHOWWINDOW))
[System.Windows.Forms.Application]::DoEvents()
Start-Sleep -Milliseconds 700

# The dock magnifies under the pointer, so park it away before measuring the resting state.
[void][Bp.W]::SetCursorPos(40, 200)
Start-Sleep -Milliseconds 700

# --- probe points, derived from the window the candidate really made ---
$pad = $Pad; $cellPx = $Cell; $iconPx = $IconBox
$gapPx = $cellPx - $iconPx
$pins = [int][Math]::Round(($w - (2 * $pad) + $gapPx) / [double]$cellPx)
if ($pins -lt 1 -or $pins -gt 40) { throw "the window width ${w}px does not describe a dock (derived $pins icons)" }
$bandCentre = $r.Top + $VerticalReserve + [int](($ht - $VerticalReserve - $PlatePaddingY) / 2)
Write-Host "  derived: pins=$pins cell=${cellPx}px icon=${iconPx}px gap=${gapPx}px pad=${pad}px"

$gaps = @()
for ($i = 0; $i -lt ($pins - 1); $i++) {
    $gaps += @{ X = $r.Left + $pad + (($i + 1) * $cellPx) - [int]($gapPx / 2); Y = $bandCentre }
}
$reserve = @{ X = $r.Left + [int]($w / 2); Y = $r.Top + 3 }
# Just outside the dock but still on the magenta backing: proves the backing is really there and really behind.
$flankL = @{ X = $r.Left - 30; Y = $bandCentre }
$flankR = @{ X = $r.Right + 30; Y = $bandCentre }
$iconPoints = @()
for ($i = 0; $i -lt $pins; $i++) {
    $iconPoints += @{ X = $r.Left + $pad + ($i * $cellPx) + [int]($cellPx / 2); Y = $bandCentre }
}

# --- reading 1: dock shown over magenta ---
$shown = Grab
$shown.Save((Join-Path $OutDir 'backing-shown.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# --- reading 2: dock hidden, magenta alone ---
[void][Bp.W]::ShowWindow($h, [Bp.W]::SW_HIDE)
Start-Sleep -Milliseconds 900
$hidden = Grab
$hidden.Save((Join-Path $OutDir 'backing-hidden.png'), [System.Drawing.Imaging.ImageFormat]::Png)
[void][Bp.W]::ShowWindow($h, [Bp.W]::SW_SHOWNOACTIVATE)
Start-Sleep -Milliseconds 600

$samples = New-Object System.Collections.Generic.List[string]
$failures = New-Object System.Collections.Generic.List[string]
$tolerance = 6

function Check-Magenta([string]$label, $point, [System.Drawing.Bitmap]$bmp) {
    $px = Rgb $bmp $point.X $point.Y
    $d = Dist $px $wantRgb
    $ok = $d -le $tolerance
    Write-Host ("    {0,-12} ({1},{2}) = {3,-13} -> {4}" -f $label, $point.X, $point.Y, ($px -join ','), $(if ($ok) { 'MAGENTA (transparent)' } else { "NOT MAGENTA (delta $d)" }))
    $script:samples.Add("$label x=$($point.X) y=$($point.Y) rgb=$($px -join ',') delta=$d ok=$ok")
    return $ok
}

Write-Host "`n  --- the empty parts of the dock, over a magenta background ---" -ForegroundColor Yellow
foreach ($g in $gaps) { if (-not (Check-Magenta 'dock-gap' $g $shown)) { $failures.Add("dock gap at $($g.X),$($g.Y) is not the backing") } }
if (-not (Check-Magenta 'dock-reserve' $reserve $shown)) { $failures.Add('the dock reserve band is not the backing') }

Write-Host "`n  --- the backing itself, beside the dock ---" -ForegroundColor Yellow
if (-not (Check-Magenta 'beside-left' $flankL $shown)) { $failures.Add('the backing is not visible to the left of the dock') }
if (-not (Check-Magenta 'beside-right' $flankR $shown)) { $failures.Add('the backing is not visible to the right of the dock') }

Write-Host "`n  --- the dock hidden: the backing alone ---" -ForegroundColor Yellow
foreach ($g in $gaps) { if (-not (Check-Magenta 'hidden-gap' $g $hidden)) { $failures.Add("with the dock hidden the gap at $($g.X),$($g.Y) is not the backing") } }

# The icons must still be drawn on top of the backing, or "transparent" could be satisfied by drawing nothing.
$totalInk = 0; $totalProbes = 0; $drawnIcons = 0
Write-Host "`n  --- the icons, drawn over the backing ---" -ForegroundColor Yellow
for ($i = 0; $i -lt $iconPoints.Count; $i++) {
    $ink = 0; $probes = 0
    for ($dx = -1; $dx -le 1; $dx++) {
        for ($dy = -1; $dy -le 1; $dy++) {
            $px = $iconPoints[$i].X + [int]($dx * $iconPx * 0.3)
            $py = $iconPoints[$i].Y + [int]($dy * $iconPx * 0.3)
            $probes++
            if ((Dist (Rgb $shown $px $py) $wantRgb) -gt $tolerance) { $ink++ }
        }
    }
    $totalInk += $ink; $totalProbes += $probes
    if ($ink -gt 0) { $drawnIcons++ }
    Write-Host ("    icon {0}: {1} of {2} points are NOT the backing  ({3})" -f $i, $ink, $probes, $(if ($ink -gt 0) { 'DRAWN' } else { 'NOTHING DRAWN' }))
    $samples.Add("ICON $i x=$($iconPoints[$i].X) y=$($iconPoints[$i].Y) notBacking=$ink/$probes")
}
if ($drawnIcons -lt $iconPoints.Count) { $failures.Add("only $drawnIcons of $($iconPoints.Count) icons were drawn over the backing") }
if ($totalInk -lt [int]($totalProbes * 0.5)) { $failures.Add("only $totalInk of $totalProbes icon points differ from the backing") }

Write-Host "`n=== VERDICT ===" -ForegroundColor Cyan
if ($failures.Count -eq 0) {
    Write-Host "  PASS - the empty parts of the dock window show a background colour the dock never painted," -ForegroundColor Green
    Write-Host "         so they are genuinely see-through. The icons are drawn over it." -ForegroundColor Green
    $exit = 0
} else {
    Write-Host "  FAIL" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
    $exit = 1
}
$samples.Add("VERDICT pins=$pins gaps=$($gaps.Count) icons=$drawnIcons ink=$totalInk/$totalProbes failures=$($failures.Count)")
$samples | Set-Content -Path (Join-Path $OutDir 'backing-samples.txt') -Encoding UTF8

if (-not $proc.HasExited) { $proc.Kill() }
$form.Close(); $form.Dispose()
$shown.Dispose(); $hidden.Dispose()
Write-Host "  captures: $OutDir\backing-shown.png , backing-hidden.png"
exit $exit
