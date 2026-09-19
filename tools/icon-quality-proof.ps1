<#
    ICON QUALITY PROOF — alpha edges, solved rather than eyeballed.

    Fringing is the classic symptom of getting premultiplication wrong, and it cannot be judged reliably by
    looking at one screenshot: a white fringe and a legitimately pale icon edge look the same over one background.

    So the same dock is captured twice, over two backing colours the dock never paints — magenta and cyan. For a
    pixel the dock composited with coverage a over a backing c, the screen shows

        result = icon * a + c * (1 - a)

    With two known backings and two measurements, that is two equations in two unknowns, so both a and the
    icon's own colour can be recovered exactly:

        a    = 1 - (resultMagenta - resultCyan) / (magenta - cyan)
        icon = (resultMagenta - c * (1 - a)) / a

    The recovered icon colour is then the thing that gets judged, and the two defects have exact signatures:

      * a WHITE fringe  ->  icon channels brighter than the alpha can account for (icon > 255), which is what
                            unpremultiplied or once-too-many-times-multiplied alpha produces;
      * a DARK fringe   ->  icon near zero at a fractional alpha, the signature of multiplying alpha in twice.

    A correct premultiplied edge solves to a colour inside the icon's own range, so the test is falsifiable in
    both directions rather than "looks fine".

    Usage:
      ./tools/icon-quality-proof.ps1 -Exe <path> [-AppArgs '--paths p.txt'] [-Pins 5]
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Exe,
    [string[]] $AppArgs = @(),
    [int] $Pins = 5,
    [int] $IconBox = 52,
    [int] $Cell = 56,
    [int] $Pad = 12,
    [int] $VerticalReserve = 44,
    [int] $PlatePaddingY = 10,
    [string] $OutDir = 'D:\AI\temp\dsh-cu-eval\clear-dock\icon-quality'
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
if (-not (Test-Path $Exe)) { throw "no executable at $Exe" }

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace Iq -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
[DllImport("user32.dll")] public static extern IntPtr GetWindowDC(IntPtr h);
[DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
[DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool BitBlt(IntPtr d,int x,int y,int w,int h,IntPtr s,int sx,int sy,int rop);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int ht, uint flags);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
public delegate bool EnumProc(IntPtr h, IntPtr p);
public struct RECT { public int Left, Top, Right, Bottom; }
public static readonly IntPtr HWND_TOP = IntPtr.Zero;
public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
public const uint MOVE = 0x0001, ABSOLUTE = 0x8000;
'@

$SW = [Iq.W]::GetSystemMetrics(0)
$SH = [Iq.W]::GetSystemMetrics(1)

function Find-Candidate([int] $owner) {
    $hits = New-Object System.Collections.Generic.List[IntPtr]
    $cb = [Iq.W+EnumProc] {
        param([IntPtr]$h, [IntPtr]$p)
        $o = 0; [void][Iq.W]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -eq $owner) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Iq.W]::GetWindowTextW($h, $sb, 256)
            if ($sb.ToString() -like '*Clear Dock*') { $hits.Add($h) }
        }
        return $true
    }
    [void][Iq.W]::EnumWindows($cb, [IntPtr]::Zero)
    return $hits
}

function Grab {
    $bmp = New-Object System.Drawing.Bitmap($SW, $SH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $src = [Iq.W]::GetWindowDC([Iq.W]::GetDesktopWindow())
    try { [void][Iq.W]::BitBlt($hdc, 0, 0, $SW, $SH, $src, 0, 0, 0x00CC0020) }
    finally { [void][Iq.W]::ReleaseDC([Iq.W]::GetDesktopWindow(), $src); $g.ReleaseHdc($hdc); $g.Dispose() }
    return $bmp
}

function Move-To([int] $x, [int] $y) {
    [Iq.W]::mouse_event([Iq.W]::MOVE -bor [Iq.W]::ABSOLUTE,
        [int](([long]$x * 65535) / ($SW - 1)), [int](([long]$y * 65535) / ($SH - 1)), 0, [IntPtr]::Zero)
}

Write-Host "=== ICON QUALITY PROOF ===" -ForegroundColor Cyan
Write-Host "candidate: $Exe"

$stdoutPath = Join-Path $OutDir 'icon-stdout.txt'
if (Test-Path $stdoutPath) { Remove-Item $stdoutPath -Force }

$runArgs = @($AppArgs) + @('--pins', "$Pins")
$startArgs = @{ FilePath = $Exe; PassThru = $true; RedirectStandardOutput = $stdoutPath }
if ($runArgs.Count -gt 0) { $startArgs['ArgumentList'] = $runArgs }
$proc = Start-Process @startArgs

$wins = @()
$deadline = (Get-Date).AddSeconds(25)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 250
    if ($proc.HasExited) { throw "the candidate exited before presenting (code $($proc.ExitCode))" }
    $wins = @(Find-Candidate $proc.Id | Where-Object { [Iq.W]::IsWindowVisible($_) })
    if ($wins.Count -gt 0) { break }
}
if ($wins.Count -eq 0) { throw 'no visible candidate window appeared' }
$h = $wins[0]

$r = New-Object Iq.W+RECT
[void][Iq.W]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left
$ht = $r.Bottom - $r.Top
Start-Sleep -Milliseconds 900

$pinCount = $Pins
$line = @(Get-Content $stdoutPath -ErrorAction SilentlyContinue | Where-Object { $_ -like 'hwnd=*' } | Select-Object -First 1)
if ($line.Count -gt 0 -and ([string]$line[0]) -match 'pins=(\d+)') { $pinCount = [int]$Matches[1] }
$artwork = 0
if ($line.Count -gt 0 -and ([string]$line[0]) -match 'artwork=(\d+)') { $artwork = [int]$Matches[1] }
Write-Host "  window: rect=($($r.Left),$($r.Top)) ${w}x${ht} icons=$pinCount artwork=${artwork}px"

# The dock is raised inside the normal band, and the backing stays below it, so the dock is above the backing
# without being promoted to the topmost band its own reading would then not describe.
$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = 'None'
$form.StartPosition = 'Manual'
$form.ShowInTaskbar = $false
$form.TopMost = $false
$form.Text = 'Muralis Icon Quality Backing'
$form.SetBounds(0, 0, 200, 200)
$form.Show()
[System.Windows.Forms.Application]::DoEvents()

$margin = 40
$form.SetBounds($r.Left - $margin, $r.Top - $margin, $w + (2 * $margin), $ht + (2 * $margin))
[System.Windows.Forms.Application]::DoEvents()
Start-Sleep -Milliseconds 300
[void][Iq.W]::SetWindowPos($h, [Iq.W]::HWND_TOP, 0, 0, 0, 0,
    ([Iq.W]::SWP_NOMOVE -bor [Iq.W]::SWP_NOSIZE -bor [Iq.W]::SWP_NOACTIVATE))
[System.Windows.Forms.Application]::DoEvents()
Start-Sleep -Milliseconds 700

# The pointer must be away, or the magnified icons would not be the resting sizes this is judging.
Move-To 60 300
Start-Sleep -Milliseconds 800

$magenta = @(255, 0, 255)
$cyan = @(0, 255, 255)

$form.BackColor = [System.Drawing.Color]::FromArgb($magenta[0], $magenta[1], $magenta[2])
[System.Windows.Forms.Application]::DoEvents()
Start-Sleep -Milliseconds 900
$overMagenta = Grab
$overMagenta.Save((Join-Path $OutDir 'quality-magenta.png'), [System.Drawing.Imaging.ImageFormat]::Png)

$form.BackColor = [System.Drawing.Color]::FromArgb($cyan[0], $cyan[1], $cyan[2])
[System.Windows.Forms.Application]::DoEvents()
Start-Sleep -Milliseconds 900
$overCyan = Grab
$overCyan.Save((Join-Path $OutDir 'quality-cyan.png'), [System.Drawing.Imaging.ImageFormat]::Png)

Write-Host "  captured over magenta $($magenta -join ',') and cyan $($cyan -join ',')"

# --- solve for alpha and the icon's own colour, over the icon boxes only ---
# Boxes are derived from the layout the candidate reported, in physical pixels at its own scale.
$scale = 1.0
$scaleLine = @(Get-Content $stdoutPath -ErrorAction SilentlyContinue | Where-Object { $_ -like 'hwnd=*' } | Select-Object -First 1)
if ($scaleLine.Count -gt 0 -and ([string]$scaleLine[0]) -match 'scale=([0-9.]+)') { $scale = [double]$Matches[1] }
$iconPx = [int][Math]::Round($IconBox * $scale)
$cellPx = [int][Math]::Round($Cell * $scale)
$padPx = [int][Math]::Round($Pad * $scale)
$reservePx = [int][Math]::Round($VerticalReserve * $scale)
$platePx = [int][Math]::Round($PlatePaddingY * $scale)
$baseline = $r.Top + $ht - $platePx

$stats = New-Object System.Collections.Generic.List[string]
$failures = New-Object System.Collections.Generic.List[string]
$impossibleHigh = 0
$impossibleLow = 0
$edgePixels = 0
$totalEdge = 0
$minRecoveredIcon = 999.0
$maxRecoveredIcon = -999.0

for ($i = 0; $i -lt $pinCount; $i++) {
    $centreX = $r.Left + $padPx + ($i * $cellPx) + [int]($cellPx / 2)
    $left = $centreX - [int]($iconPx / 2)
    $top = $baseline - $iconPx

    for ($y = 0; $y -lt $iconPx; $y++) {
        for ($x = 0; $x -lt $iconPx; $x++) {
            $px = $left + $x
            $py = $top + $y
            if ($px -lt 0 -or $py -lt 0 -or $px -ge $SW -or $py -ge $SH) { continue }

            $m = $overMagenta.GetPixel($px, $py)
            $c = $overCyan.GetPixel($px, $py)
            $mv = @($m.R, $m.G, $m.B)
            $cv = @($c.R, $c.G, $c.B)

            # Alpha, per channel, from the difference against the two known backings. The blue channel carries
            # magenta and cyan identically, so it contributes no equation and is skipped.
            $alphas = @()
            for ($ch = 0; $ch -lt 3; $ch++) {
                $bd = [double]($magenta[$ch] - $cyan[$ch])
                if ([Math]::Abs($bd) -lt 1) { continue }
                $a = 1.0 - (($mv[$ch] - $cv[$ch]) / $bd)
                $alphas += $a
            }
            if ($alphas.Count -eq 0) { continue }

            $alpha = ($alphas | Measure-Object -Average).Average

            # Alpha spread across the channels: a correct composite gives the same coverage in every channel that
            # can see the backing change. A spread means the pixel is not a clean composite of one colour.
            $spread = ($alphas | Measure-Object -Maximum).Maximum - ($alphas | Measure-Object -Minimum).Minimum

            # Only the partially covered pixels are interesting: fully transparent is empty surface and fully
            # opaque is the icon's interior, and neither can show a fringe.
            if ($alpha -le 0.02 -or $alpha -ge 0.985) { continue }
            $totalEdge++

            # Solve for the icon's own colour in the channel that carries the most backing difference.
            $iconValues = @()
            for ($ch = 0; $ch -lt 3; $ch++) {
                $bd = [double]($magenta[$ch] - $cyan[$ch])
                if ([Math]::Abs($bd) -lt 1) { continue }
                $icon = ($mv[$ch] - ($magenta[$ch] * (1.0 - $alpha))) / $alpha
                $iconValues += $icon
                if ($icon -gt 258.0) { $impossibleHigh++ }
                if ($icon -lt -3.0) { $impossibleLow++ }
                $minRecoveredIcon = [Math]::Min($minRecoveredIcon, $icon)
                $maxRecoveredIcon = [Math]::Max($maxRecoveredIcon, $icon)
            }

            if ($spread -gt 0.10) { $edgePixels++ }
        }
    }
}

Write-Host "`n  partially covered pixels examined : $totalEdge"
Write-Host "  recovered icon colour range       : $([Math]::Round($minRecoveredIcon,1)) .. $([Math]::Round($maxRecoveredIcon,1))"
Write-Host "  impossible-bright samples (>258)  : $impossibleHigh   (white fringe)"
Write-Host "  impossible-dark samples (<-3)     : $impossibleLow    (dark fringe)"
Write-Host "  pixels with channel alpha spread  : $edgePixels"

if ($totalEdge -lt 200) { $failures.Add("only $totalEdge partially covered pixels were found; too few to judge edges") }
if ($impossibleHigh -gt 0) { $failures.Add("$impossibleHigh pixels solve to an icon colour brighter than 255, which is a white fringe") }
if ($impossibleLow -gt 0) { $failures.Add("$impossibleLow pixels solve to a negative icon colour, which is a dark fringe") }
if ($totalEdge -gt 0 -and ($edgePixels / [double]$totalEdge) -gt 0.05) {
    $failures.Add("$edgePixels of $totalEdge edge pixels have inconsistent per-channel alpha, so they are not a clean composite")
}

$form.Close(); $form.Dispose()
$overMagenta.Dispose(); $overCyan.Dispose()

Write-Host "`n=== VERDICT ===" -ForegroundColor Cyan
if ($failures.Count -eq 0) {
    Write-Host "  PASS - every partially covered pixel solves to a valid icon colour over two different backings," -ForegroundColor Green
    Write-Host "         so the alpha edges are a clean premultiplied composite: no white fringe, no dark fringe." -ForegroundColor Green
    $exit = 0
} else {
    Write-Host "  FAIL" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
    $exit = 1
}

if (-not $proc.HasExited) { $proc.Kill() }
Write-Host "  captures: $OutDir\quality-magenta.png , quality-cyan.png"
exit $exit
