<#
    Captures the visual evidence for the readiness milestone.

    Every capture is a region of the real desktop, taken with GDI BitBlt from the desktop window, so each shows
    what the display actually composited — including the wallpaper, the taskbar and any window behind the dock.
    The region is cropped tight and scaled with nearest-neighbour so the pixels being judged stay the pixels that
    were captured.

    Usage:
      ./tools/clear-ready-captures.ps1 -Exe <path> [-AppArgs '--paths p.txt']
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
    [int] $Zoom = 3,
    [string] $OutDir = 'D:\AI\temp\dsh-cu-eval\clear-dock\ready-shots'
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
if (-not (Test-Path $Exe)) { throw "no executable at $Exe" }

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace Cr -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
[DllImport("user32.dll")] public static extern IntPtr GetWindowDC(IntPtr h);
[DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
[DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool BitBlt(IntPtr d,int x,int y,int w,int h,IntPtr s,int sx,int sy,int rop);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int ht, uint flags);
public delegate bool EnumProc(IntPtr h, IntPtr p);
public struct RECT { public int Left, Top, Right, Bottom; }
public static readonly IntPtr HWND_TOP = IntPtr.Zero;
public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
public const uint MOVE = 0x0001, ABSOLUTE = 0x8000, LEFTDOWN = 0x0002, LEFTUP = 0x0004;
'@

$SW = [Cr.W]::GetSystemMetrics(0)
$SH = [Cr.W]::GetSystemMetrics(1)

function Move-To([int] $x, [int] $y) {
    [Cr.W]::mouse_event([Cr.W]::MOVE -bor [Cr.W]::ABSOLUTE,
        [int](([long]$x * 65535) / ($SW - 1)), [int](([long]$y * 65535) / ($SH - 1)), 0, [IntPtr]::Zero)
}

function Find-Candidate([int] $owner) {
    $hits = New-Object System.Collections.Generic.List[IntPtr]
    $cb = [Cr.W+EnumProc] {
        param([IntPtr]$h, [IntPtr]$p)
        $o = 0; [void][Cr.W]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -eq $owner) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Cr.W]::GetWindowTextW($h, $sb, 256)
            if ($sb.ToString() -like '*Clear Dock*') { $hits.Add($h) }
        }
        return $true
    }
    [void][Cr.W]::EnumWindows($cb, [IntPtr]::Zero)
    return $hits
}

function Grab {
    $bmp = New-Object System.Drawing.Bitmap($SW, $SH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    $src = [Cr.W]::GetWindowDC([Cr.W]::GetDesktopWindow())
    try { [void][Cr.W]::BitBlt($hdc, 0, 0, $SW, $SH, $src, 0, 0, 0x00CC0020) }
    finally { [void][Cr.W]::ReleaseDC([Cr.W]::GetDesktopWindow(), $src); $g.ReleaseHdc($hdc); $g.Dispose() }
    return $bmp
}

# Save the full screen and a zoomed crop of the dock region. Crop bounds are clamped so a dock near an edge
# cannot make the crop exceed the screen, which is what silently failed in an earlier attempt.
function Save-Shot([string] $name, $rect, [int] $margin = 28) {
    $shot = Grab
    $full = Join-Path $OutDir "$name.png"
    $shot.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)

    $x0 = [Math]::Max(0, $rect.Left - $margin)
    $y0 = [Math]::Max(0, $rect.Top - $margin)
    $x1 = [Math]::Min($SW, $rect.Right + $margin)
    $y1 = [Math]::Min($SH, $rect.Bottom + $margin)
    $cw = $x1 - $x0; $ch = $y1 - $y0
    if ($cw -le 0 -or $ch -le 0) { $shot.Dispose(); throw "degenerate crop for $name" }

    $crop = $shot.Clone((New-Object System.Drawing.Rectangle $x0, $y0, $cw, $ch), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $z = New-Object System.Drawing.Bitmap ($cw * $Zoom), ($ch * $Zoom), ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($z)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $g.DrawImage($crop, (New-Object System.Drawing.Rectangle 0, 0, ($cw * $Zoom), ($ch * $Zoom)))
    $g.Dispose()
    $z.Save((Join-Path $OutDir "$name-zoom.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $z.Dispose(); $crop.Dispose(); $shot.Dispose()
    Write-Host "  $name.png + $name-zoom.png  (crop $cw x $ch from $x0,$y0)"
}

function Start-Dock([string[]] $extra) {
    $stdout = Join-Path $OutDir "shot-stdout-$([guid]::NewGuid().ToString('N').Substring(0,6)).txt"
    $runArgs = @($AppArgs) + @('--pins', "$Pins") + $extra
    $startArgs = @{ FilePath = $Exe; PassThru = $true; RedirectStandardOutput = $stdout }
    if ($runArgs.Count -gt 0) { $startArgs['ArgumentList'] = $runArgs }
    $proc = Start-Process @startArgs

    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
        if ($proc.HasExited) { throw "the candidate exited before presenting (code $($proc.ExitCode))" }
        $wins = @(Find-Candidate $proc.Id | Where-Object { [Cr.W]::IsWindowVisible($_) })
        if ($wins.Count -gt 0) { break }
    }
    if ($wins.Count -eq 0) { throw 'no visible candidate window appeared' }
    Start-Sleep -Milliseconds 1200
    return @{ Proc = $proc; Hwnd = $wins[0]; Stdout = $stdout }
}

function Stop-Dock($dock) {
    if (-not $dock.Proc.HasExited) { $dock.Proc.Kill() }
    Start-Sleep -Milliseconds 500
}

Write-Host "=== CLEAR READY CAPTURES ===" -ForegroundColor Cyan
Get-Process ClearDockPoc -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

# --- 100 %, resting -----------------------------------------------------------------------------------------
$dock = Start-Dock @()
$r = New-Object Cr.W+RECT
[void][Cr.W]::GetWindowRect($dock.Hwnd, [ref]$r)
$w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
$bandY = $r.Top + $VerticalReserve + [int](($ht - $VerticalReserve - $PlatePaddingY) / 2)
$slotX = 0..($Pins - 1) | ForEach-Object { $r.Left + $Pad + ($_ * $Cell) + [int]($Cell / 2) }
Write-Host "dock: rect=($($r.Left),$($r.Top)) ${w}x${ht}  bandY=$bandY"

Move-To 60 300
Start-Sleep -Milliseconds 900
Save-Shot 'clear-ready-01-100-rest' $r
Write-Host "01 rest: dock at 100 %, pointer away, five icons at rest"

# --- 100 %, hover -------------------------------------------------------------------------------------------
Move-To $slotX[2] $bandY
Start-Sleep -Milliseconds 700
Save-Shot 'clear-ready-02-100-hover' $r
Write-Host "02 hover: pointer on icon 2, the wave magnified"

# --- 100 %, mid-drag ----------------------------------------------------------------------------------------
Move-To $slotX[0] $bandY
Start-Sleep -Milliseconds 400
[Cr.W]::mouse_event([Cr.W]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 220
foreach ($step in 1..8) {
    Move-To ([int]($slotX[0] + (($slotX[3] - $slotX[0]) * $step / 8))) $bandY
    Start-Sleep -Milliseconds 60
}
Start-Sleep -Milliseconds 200
Save-Shot 'clear-ready-05-drag' $r
Move-To $slotX[0] $bandY
Start-Sleep -Milliseconds 200
[Cr.W]::mouse_event([Cr.W]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 700
Write-Host "05 drag: button held, icon 0 carried towards slot 3"

Stop-Dock $dock

# --- non-100 %: 125 % and 150 % -----------------------------------------------------------------------------
# The higher scales are forced through --scale because this machine has only a 100 % display. The geometry and
# the icon pixel sizes are genuinely recomputed at that scale; what is NOT tested is the OS telling the process
# its DPI through WM_DPICHANGED, and that is recorded as a gap rather than glossed over.
foreach ($forced in @(125, 150)) {
    $dock = Start-Dock @('--scale', "$forced")
    $r = New-Object Cr.W+RECT
    [void][Cr.W]::GetWindowRect($dock.Hwnd, [ref]$r)
    $w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
    $scale = $forced / 100.0
    $iconPx = [int][Math]::Round($IconBox * $scale)
    $cellPx = [int][Math]::Round($Cell * $scale)
    $padPx = [int][Math]::Round($Pad * $scale)
    $reservePx = [int][Math]::Round($VerticalReserve * $scale)
    $platePx = [int][Math]::Round($PlatePaddingY * $scale)
    $bandY = $r.Top + $reservePx + [int](($ht - $reservePx - $platePx) / 2)
    $slotX = 0..($Pins - 1) | ForEach-Object { $r.Left + $padPx + ($_ * $cellPx) + [int]($cellPx / 2) }
    Write-Host "`n${forced}%: rect=($($r.Left),$($r.Top)) ${w}x${ht} icon=${iconPx}px cell=${cellPx}px"

    Move-To 60 300
    Start-Sleep -Milliseconds 900
    Save-Shot "clear-ready-03-non100-rest-$forced" $r

    Move-To $slotX[2] $bandY
    Start-Sleep -Milliseconds 700
    Save-Shot "clear-ready-04-non100-hover-$forced" $r
    Write-Host "  captured rest and hover at $forced %"

    Stop-Dock $dock
}

# --- the magenta backing proof ------------------------------------------------------------------------------
$dock = Start-Dock @()
$r = New-Object Cr.W+RECT
[void][Cr.W]::GetWindowRect($dock.Hwnd, [ref]$r)
$w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
Move-To 60 300
Start-Sleep -Milliseconds 600

$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = 'None'
$form.StartPosition = 'Manual'
$form.ShowInTaskbar = $false
$form.TopMost = $false
$form.BackColor = [System.Drawing.Color]::Magenta
$form.Text = 'Muralis Ready Capture Backing'
$form.SetBounds(0, 0, 200, 200)
$form.Show()
[System.Windows.Forms.Application]::DoEvents()
$margin = 60
$form.SetBounds($r.Left - $margin, $r.Top - $margin, $w + (2 * $margin), $ht + (2 * $margin))
[System.Windows.Forms.Application]::DoEvents()
Start-Sleep -Milliseconds 400
[void][Cr.W]::SetWindowPos($dock.Hwnd, [Cr.W]::HWND_TOP, 0, 0, 0, 0,
    ([Cr.W]::SWP_NOMOVE -bor [Cr.W]::SWP_NOSIZE -bor [Cr.W]::SWP_NOACTIVATE))
[System.Windows.Forms.Application]::DoEvents()
Start-Sleep -Milliseconds 900
Save-Shot 'clear-ready-06-magenta-proof' $r 40
Write-Host "06 magenta: the dock over a background it never painted"
$form.Close(); $form.Dispose()
Stop-Dock $dock

Write-Host "`ncaptures written to $OutDir" -ForegroundColor Cyan
Get-ChildItem $OutDir -Filter '*.png' | Sort-Object Name | ForEach-Object { "  $($_.Name)" }
