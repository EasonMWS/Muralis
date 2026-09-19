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
      ./tools/pixel-proof.ps1 -Exe <path> [-Args '--pins 5'] [-Cell 56] [-IconBox 52] [-Pad 12]

    The probe points are derived from the window the candidate actually created rather than assumed, because a
    harness that guesses the geometry reports its own guess as a finding: with three pinned apps a fourth "gap"
    point lands inside a real icon and reads as an opaque plate that does not exist.
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
    [double] $DpiScale = 1.0,
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
# ArgumentList rejects an empty collection, so it is only passed when there is something to pass.
$startArgs = @{ FilePath = $Exe; PassThru = $true; RedirectStandardOutput = $stdoutPath }
if ($AppArgs.Count -gt 0) { $startArgs['ArgumentList'] = $AppArgs }
$proc = Start-Process @startArgs

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
# Everything is derived from the window the candidate actually created, in physical pixels. The pinned count is
# read back out of the width, so the probe points describe the dock that exists rather than the one the harness
# expected: a gap point that lands inside a real icon would be reported as an opaque plate.
$pad = [int][Math]::Round($Pad * $DpiScale)
$plateY = [int][Math]::Round($PlatePaddingY * $DpiScale)
$reserveH = [int][Math]::Round($VerticalReserve * $DpiScale)
$cellPx = [int][Math]::Round($Cell * $DpiScale)
$iconPx = [int][Math]::Round($IconBox * $DpiScale)
$gapPx = $cellPx - $iconPx

# The pinned count is read back out of the width: width = pins*cell - gap + 2*pad, so the count follows from the
# window the candidate really created. Guarded against a width that cannot be a dock at all.
$pins = [int][Math]::Round(($w - (2 * $pad) + $gapPx) / [double]$cellPx)
if ($pins -lt 1 -or $pins -gt 40) { throw "the window width ${w}px does not describe a dock (derived $pins icons)" }
Write-Host "  derived: pins=$pins cell=${cellPx}px icon=${iconPx}px gap=${gapPx}px pad=${pad}px"

# The vertical middle of the icon band, which is where a gap between two icons is genuinely empty.
$bandCentre = $r.Top + $reserveH + [int](($ht - $reserveH - $plateY) / 2)

# Between-icon gaps: the centre of each gap, which is exactly empty by construction.
$gaps = @()
for ($i = 0; $i -lt ($pins - 1); $i++) {
    $gaps += @{ X = $r.Left + $pad + (($i + 1) * $cellPx) - [int]($gapPx / 2); Y = $bandCentre }
}
# The reserve band above the icons: must also be desktop, and is the largest single empty area.
$reserve = @{ X = $r.Left + [int]($w / 2); Y = $r.Top + 3 }
# The centre of every icon cell. These are opaque wherever an icon actually has ink, and reporting all of them
# is what makes the check usable for real artwork: an icon whose centre happens to be white would otherwise
# look like "nothing was drawn", which is a false alarm rather than a finding.
$iconPoints = @()
for ($i = 0; $i -lt $pins; $i++) {
    $iconPoints += @{ X = $r.Left + $pad + ($i * $cellPx) + [int]($cellPx / 2); Y = $bandCentre }
}
# A point just outside the window, which is desktop in both readings and validates the grab itself.
$outside = @{ X = $r.Left + [int]($w / 2); Y = $r.Top - 20 }

# --- reading 1: window hidden ---
[void][Pp.W]::ShowWindow($h, [Pp.W]::SW_HIDE)
Start-Sleep -Milliseconds 1200
$hidden = Grab
$hidden.Save((Join-Path $OutDir 'proof-hidden.png'), [System.Drawing.Imaging.ImageFormat]::Png)
# The unary comma matters and is not decoration. PowerShell flattens the three-channel array that Rgb returns
# when it is emitted from ForEach-Object, so without it $hGaps becomes one long array of scalars rather than an
# array of triples — and then $hGaps[$i] is a single channel, Dist() finds no G or B to compare, and the gate
# silently checks only the red channel while printing coordinates that belong to other points. That defect was
# found by an independent review of this file, not by running it, which is the uncomfortable part.
$hGaps = @($gaps | ForEach-Object { ,(Rgb $hidden $_.X $_.Y) })
$hReserve = Rgb $hidden $reserve.X $reserve.Y
$hOutside = Rgb $hidden $outside.X $outside.Y
$hIcon0 = Rgb $hidden $iconPoints[0].X $iconPoints[0].Y
Write-Host "`n  --- dock HIDDEN (wallpaper reference) ---" -ForegroundColor Yellow
Write-Host "    gaps     : $((($hGaps | ForEach-Object { $_ -join ',' }) -join '  '))"
Write-Host "    reserve  : $($hReserve -join ',')"
Write-Host "    outside  : $($hOutside -join ',')"

# --- reading 2: window shown ---
[void][Pp.W]::ShowWindow($h, [Pp.W]::SW_SHOWNOACTIVATE)
Start-Sleep -Milliseconds 1400
$shown = Grab
$shown.Save((Join-Path $OutDir 'proof-shown.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$sGaps = @($gaps | ForEach-Object { ,(Rgb $shown $_.X $_.Y) })
$sReserve = Rgb $shown $reserve.X $reserve.Y
$sOutside = Rgb $shown $outside.X $outside.Y
$sIcon0 = Rgb $shown $iconPoints[0].X $iconPoints[0].Y
Write-Host "`n  --- dock SHOWN ---" -ForegroundColor Yellow
Write-Host "    gaps     : $((($sGaps | ForEach-Object { $_ -join ',' }) -join '  '))"
Write-Host "    reserve  : $($sReserve -join ',')"
Write-Host "    outside  : $($sOutside -join ',')"
Write-Host "    icon 0   : $($sIcon0 -join ',')  (where the first icon is drawn; must NOT be the wallpaper if it has ink)"

# --- the gate ---
$failures = New-Object System.Collections.Generic.List[string]
$tolerance = 6   # compositor rounding only; a visible plate is off by hundreds

# One machine-readable line per sample, with its coordinates. A harness whose evidence cannot be read back is a
# harness whose verdict cannot be checked, and a human-readable dump has already been misread once here.
$samples = New-Object System.Collections.Generic.List[string]
$samples.Add("SAMPLE window rect=($($r.Left),$($r.Top)) ${w}x${ht} pins=$pins cell=${cellPx} icon=${iconPx} pad=${pad}")
foreach ($g in $gaps) { $samples.Add("SAMPLE gap x=$($g.X) y=$($g.Y)") }
$samples.Add("SAMPLE reserve x=$($reserve.X) y=$($reserve.Y)")
$samples.Add("SAMPLE outside x=$($outside.X) y=$($outside.Y)")
foreach ($p in $iconPoints) { $samples.Add("SAMPLE icon x=$($p.X) y=$($p.Y)") }

for ($i = 0; $i -lt $gaps.Count; $i++) {
    $d = Dist $hGaps[$i] $sGaps[$i]
    $verdict = if ($d -le $tolerance) { 'TRANSPARENT' } else { "OPAQUE (delta $d)" }
    Write-Host ("    gap {0}: hidden={1} shown={2}  -> {3}" -f $i, ($hGaps[$i] -join ','), ($sGaps[$i] -join ','), $verdict)
    $samples.Add("GAP $i x=$($gaps[$i].X) y=$($gaps[$i].Y) hidden=$($hGaps[$i] -join ',') shown=$($sGaps[$i] -join ',') delta=$d")
    if ($d -gt $tolerance) { $failures.Add("gap $i differs by $d") }
}
$dReserve = Dist $hReserve $sReserve
Write-Host ("    reserve : hidden={0} shown={1}  -> {2}" -f ($hReserve -join ','), ($sReserve -join ','), $(if ($dReserve -le $tolerance) { 'TRANSPARENT' } else { "OPAQUE (delta $dReserve)" }))
$samples.Add("RESERVE x=$($reserve.X) y=$($reserve.Y) hidden=$($hReserve -join ',') shown=$($sReserve -join ',') delta=$dReserve")
if ($dReserve -gt $tolerance) { $failures.Add("reserve band differs by $dReserve") }

$dOutside = Dist $hOutside $sOutside
Write-Host ("    outside : delta {0} (grab sanity; must be ~0)" -f $dOutside)
$samples.Add("OUTSIDE x=$($outside.X) y=$($outside.Y) hidden=$($hOutside -join ',') shown=$($sOutside -join ',') delta=$dOutside")
if ($dOutside -gt $tolerance) { $failures.Add("the grab itself is unstable: outside differs by $dOutside") }

# Every icon cell is sampled on a grid, not at its centre. A single centre pixel cannot tell a drawn icon from a
# hole: the wallpaper here is white where the dock sits, and an icon whose artwork is white at its centre would
# read as "nothing was drawn" — a false alarm. Sampling a grid counts coverage instead, which is the property
# that actually distinguishes an icon from an empty cell.
$drawnIcons = 0
$totalInk = 0
$totalProbes = 0
for ($i = 0; $i -lt $iconPoints.Count; $i++) {
    $cx = $iconPoints[$i].X
    $cy = $iconPoints[$i].Y
    $ink = 0
    $probes = 0
    for ($dx = -1; $dx -le 1; $dx++) {
        for ($dy = -1; $dy -le 1; $dy++) {
            $px = $cx + [int]($dx * $iconPx * 0.3)
            $py = $cy + [int]($dy * $iconPx * 0.3)
            $probes++
            if ((Dist (Rgb $hidden $px $py) (Rgb $shown $px $py)) -gt $tolerance) { $ink++ }
        }
    }
    $totalInk += $ink
    $totalProbes += $probes
    if ($ink -gt 0) { $drawnIcons++ }
    Write-Host ("    icon {0}: {1} of {2} sampled points changed  ({3})" -f $i, $ink, $probes, $(if ($ink -gt 0) { 'DRAWN' } else { 'NOTHING DRAWN' }))
    $samples.Add("ICON $i x=$cx y=$cy ink=$ink/$probes")
}
Write-Host ("    icons drawn: {0} of {1}   ink coverage {2} of {3} sampled points" -f $drawnIcons, $iconPoints.Count, $totalInk, $totalProbes)
if ($drawnIcons -eq 0) {
    $failures.Add('no icon cell changed anywhere, so nothing was actually drawn')
} elseif ($totalInk -lt [int]($totalProbes * 0.5)) {
    $failures.Add("only $totalInk of $totalProbes icon points changed, which is too little to be a drawn icon")
}

$samples.Add("VERDICT pins=$pins gaps=$($gaps.Count) icons=$drawnIcons ink=$totalInk/$totalProbes failures=$($failures.Count)")
$samples | Set-Content -Path (Join-Path $OutDir 'proof-samples.txt') -Encoding UTF8

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
