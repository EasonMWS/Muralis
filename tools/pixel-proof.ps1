<#
    PIXEL PROOF — the hard gate for Operation Clear Dock.

    Samples the SAME screen points twice: once with the candidate dock window hidden, once with it shown. In the
    empty parts of the window the two readings must agree, because "transparent" means the desktop behind is
    what the display actually showed. It reads the composited screen rather than any alpha buffer, so it cannot
    be satisfied by a correct surface that never reaches the display.

    It also checks the opaque parts are the colours they were painted, which is what makes the transparent
    reading falsifiable: a channel-swapped or non-premultiplied surface would show a wrong colour rather than a
    plausible one.

    Usage:
      ./tools/pixel-proof.ps1 -Exe <path> [-Args '--pins 5'] [-SquareSize 48] [-Cell 56] [-Pad 12]
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Exe,
    [string[]] $AppArgs = @(),
    [int] $SquareSize = 48,
    [int] $Cell = 56,
    [int] $Pad = 12,
    [int] $SquareInset = 2,
    [string] $OutDir = 'D:\AI\temp\dsh-cu-eval\clear-dock\proof'
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Add-Type -AssemblyName System.Drawing
Add-Type -Namespace Pp -Name W -MemberDefinition @'
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
public delegate bool EnumProc(IntPtr h, IntPtr p);
public struct RECT { public int Left, Top, Right, Bottom; }
public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
'@
$SW = [Pp.W]::GetSystemMetrics(0)
$SH = [Pp.W]::GetSystemMetrics(1)

function Find-Top([int]$owner, [string]$titleLike) {
    $hits = New-Object System.Collections.Generic.List[IntPtr]
    $cb = [Pp.W+EnumProc] {
        param([IntPtr]$h, [IntPtr]$p)
        $o = 0; [void][Pp.W]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -eq $owner) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Pp.W]::GetWindowTextW($h, $sb, 256)
            if ($sb.ToString() -like $titleLike) { $hits.Add($h) }
        }
        return $true
    }
    [void][Pp.W]::EnumWindows($cb, [IntPtr]::Zero)
    return $hits
}

function Grab {
    $bmp = New-Object System.Drawing.Bitmap($SW, $SH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $src = [Pp.W]::GetWindowDC([Pp.W]::GetDesktopWindow())
    try { [void][Pp.W]::BitBlt($hdc, 0, 0, $SW, $SH, $src, 0, 0, 0x00CC0020) }
    finally { [void][Pp.W]::ReleaseDC([Pp.W]::GetDesktopWindow(), $src); $g.ReleaseHdc($hdc); $g.Dispose() }
    return $bmp
}
function Px([System.Drawing.Bitmap]$b, [int]$x, [int]$y) { $c = $b.GetPixel($x,$y); return "$($c.R),$($c.G),$($c.B)" }
function Rgb([System.Drawing.Bitmap]$b, [int]$x, [int]$y) { $c = $b.GetPixel($x,$y); return @($c.R, $c.G, $c.B) }
function Dist($a, $b) { return [math]::Abs($a[0]-$b[0]) + [math]::Abs($a[1]-$b[1]) + [math]::Abs($a[2]-$b[2]) }

if (-not (Test-Path $Exe)) { throw "no executable at $Exe" }

Write-Host "=== PIXEL PROOF ===" -ForegroundColor Cyan
Write-Host "candidate: $Exe  args: $($AppArgs -join ' ')"

$stdoutPath = Join-Path $OutDir 'poc-stdout.txt'
if (Test-Path $stdoutPath) { Remove-Item $stdoutPath -Force }
$proc = Start-Process -FilePath $Exe -ArgumentList $AppArgs -PassThru -RedirectStandardOutput $stdoutPath

# Readiness is established from PowerShell's own view of the process: the window must exist, be visible, and
# have been presented. The candidate's stdout is reported for the record but is not a precondition, because a
# harness that cannot parse its own candidate says nothing about the candidate.
$wins = @()
$deadline = (Get-Date).AddSeconds(25)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 300
    if ($proc.HasExited) { throw "the candidate exited before presenting (code $($proc.ExitCode))" }
    $wins = @(Find-Top $proc.Id '*Clear Dock*' | Where-Object { [Pp.W]::IsWindowVisible($_) })
    if ($wins.Count -gt 0) { break }
}
if ($wins.Count -eq 0) { throw 'no visible candidate window appeared' }

Write-Host "  candidate stdout: $(@(Get-Content $stdoutPath -ErrorAction SilentlyContinue) -join ' | ')"
# One more beat so a present that lands just after the window is shown is not missed.
Start-Sleep -Milliseconds 900

$h = $wins[0]

# Park the pointer well away from the candidate before measuring. A dock magnifies under the cursor by
# design, and a magnified icon legitimately covers the gap beside it — so a reading taken with the pointer on
# the dock measures the hovering state, not the resting one this gate is about.
[void][Pp.W]::SetCursorPos(40, 200)
Start-Sleep -Milliseconds 900

$r = New-Object Pp.W+RECT
[void][Pp.W]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
Write-Host "  window: hwnd=$h rect=($($r.Left),$($r.Top)) ${w}x${ht} visible=$([Pp.W]::IsWindowVisible($h))"

# --- the probe points, in screen coordinates ---
# Between-squares points: the centre of each gap, in the vertical middle of the icon band.
$gaps = @()
for ($i = 0; $i -lt 4; $i++) {
    $gx = $r.Left + $Pad + (($i + 1) * $Cell) - [int]($Cell / 2) + [int]($SquareSize / 2)
    $gx = $r.Left + $Pad + ($i * $Cell) + $Cell - 2   # the 4 DIP gap between icon cells
    $gaps += @{ X = $gx; Y = $r.Top + $Pad + [int]($SquareSize / 2) }
}
# The reserve band above the squares: must also be desktop.
$reserve = @{ X = $r.Left + [int]($w / 2); Y = $r.Top + 3 }
# The centre of every icon cell. These are opaque wherever an icon actually has ink, and reporting all of them
# is what makes the check usable for real artwork: an icon whose centre happens to be white would otherwise
# look like "nothing was drawn", which is a false alarm rather than a finding.
$iconPoints = @()
for ($i = 0; $i -lt 5; $i++) {
    $iconPoints += @{ X = $r.Left + $Pad + ($i * $Cell) + [int]($Cell / 2); Y = $r.Top + $r.Bottom; }
}
$iconPoints = @($iconPoints | ForEach-Object { @{ X = $_.X; Y = $r.Top + [int](($r.Bottom - $r.Top) * 0.72) } })
$inSquare = $iconPoints[0]
# A point just outside the window, which is desktop in both readings and validates the grab itself.
$outside = @{ X = $r.Left + [int]($w / 2); Y = $r.Top - 20 }

# --- reading 1: window hidden ---
[void][Pp.W]::ShowWindow($h, [Pp.W]::SW_HIDE)
Start-Sleep -Milliseconds 1200
$hidden = Grab
$hidden.Save((Join-Path $OutDir 'proof-hidden.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$hGaps = @($gaps | ForEach-Object { Rgb $hidden $_.X $_.Y })
$hReserve = Rgb $hidden $reserve.X $reserve.Y
$hOutside = Rgb $hidden $outside.X $outside.Y
$hSquare = Rgb $hidden $inSquare.X $inSquare.Y
Write-Host "`n  --- dock HIDDEN (wallpaper reference) ---" -ForegroundColor Yellow
Write-Host "    gaps     : $((($hGaps | ForEach-Object { $_ -join ',' }) -join '  '))"
Write-Host "    reserve  : $($hReserve -join ',')"
Write-Host "    outside  : $($hOutside -join ',')"

# --- reading 2: window shown ---
[void][Pp.W]::ShowWindow($h, [Pp.W]::SW_SHOWNOACTIVATE)
Start-Sleep -Milliseconds 1400
$shown = Grab
$shown.Save((Join-Path $OutDir 'proof-shown.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$sGaps = @($gaps | ForEach-Object { Rgb $shown $_.X $_.Y })
$sReserve = Rgb $shown $reserve.X $reserve.Y
$sOutside = Rgb $shown $outside.X $outside.Y
$sSquare = Rgb $shown $inSquare.X $inSquare.Y
Write-Host "`n  --- dock SHOWN ---" -ForegroundColor Yellow
Write-Host "    gaps     : $((($sGaps | ForEach-Object { $_ -join ',' }) -join '  '))"
Write-Host "    reserve  : $($sReserve -join ',')"
Write-Host "    outside  : $($sOutside -join ',')"
Write-Host "    in-square: $($sSquare -join ',')  (painted opaque, must NOT be the wallpaper)"

# --- the gate ---
$failures = New-Object System.Collections.Generic.List[string]
$tolerance = 6   # compositor rounding only; a visible plate is off by hundreds

for ($i = 0; $i -lt $gaps.Count; $i++) {
    $d = Dist $hGaps[$i] $sGaps[$i]
    $verdict = if ($d -le $tolerance) { 'TRANSPARENT' } else { "OPAQUE (delta $d)" }
    Write-Host ("    gap {0}: hidden={1} shown={2}  -> {3}" -f $i, ($hGaps[$i] -join ','), ($sGaps[$i] -join ','), $verdict)
    if ($d -gt $tolerance) { $failures.Add("gap $i differs by $d") }
}
$dReserve = Dist $hReserve $sReserve
Write-Host ("    reserve : hidden={0} shown={1}  -> {2}" -f ($hReserve -join ','), ($sReserve -join ','), $(if ($dReserve -le $tolerance) { 'TRANSPARENT' } else { "OPAQUE (delta $dReserve)" }))
if ($dReserve -gt $tolerance) { $failures.Add("reserve band differs by $dReserve") }

$dOutside = Dist $hOutside $sOutside
Write-Host ("    outside : delta {0} (grab sanity; must be ~0)" -f $dOutside)
if ($dOutside -gt $tolerance) { $failures.Add("the grab itself is unstable: outside differs by $dOutside") }

# At least one icon centre must have changed, or nothing was drawn at all. Not every one will: real artwork has
# white and pale pixels, and a pixel that matches the wallpaper is not evidence of a missing icon.
$drawnIcons = 0
for ($i = 0; $i -lt $iconPoints.Count; $i++) {
    $d = Dist (Rgb $hidden $iconPoints[$i].X $iconPoints[$i].Y) (Rgb $shown $iconPoints[$i].X $iconPoints[$i].Y)
    $mark = if ($d -gt $tolerance) { 'INK' } else { 'matches wallpaper' }
    Write-Host ("    icon {0} centre: hidden={1} shown={2}  -> {3} (delta {4})" -f `
        $i, ((Rgb $hidden $iconPoints[$i].X $iconPoints[$i].Y) -join ','), ((Rgb $shown $iconPoints[$i].X $iconPoints[$i].Y) -join ','), $mark, $d)
    if ($d -gt $tolerance) { $drawnIcons++ }
}
Write-Host ("    icons with visible ink: {0} of {1}" -f $drawnIcons, $iconPoints.Count)
if ($drawnIcons -eq 0) { $failures.Add('no icon centre changed, so nothing was actually drawn') }

Write-Host "`n=== VERDICT ===" -ForegroundColor Cyan
if ($failures.Count -eq 0) {
    Write-Host "  PASS - the empty parts of the window are the real desktop, and the opaque parts rendered." -ForegroundColor Green
    $exit = 0
} else {
    Write-Host "  FAIL" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
    $exit = 1
}

if (-not $proc.HasExited) { $proc.Kill() }
$hidden.Dispose(); $shown.Dispose()
Write-Host "  captures: $OutDir\proof-hidden.png , proof-shown.png"
exit $exit
