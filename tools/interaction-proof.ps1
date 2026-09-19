<#
    INTERACTION PROOF for a clear-dock candidate.

    Checks the two semantics the dock cannot lose: showing and clicking the candidate must not activate it or
    take the foreground away from whatever the user was using, and it must be a passive tool window rather
    than a normal one.

    It reads those from the window's own extended style and from the foreground window before and after a
    click at the candidate's centre, so the answer does not depend on anything the candidate claims about
    itself.

    Usage: ./tools/interaction-proof.ps1 -Exe <path> [-AppArgs '--pins 5']
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Exe,
    [string[]] $AppArgs = @(),
    [string] $OutDir = 'D:\AI\temp\dsh-cu-eval\clear-dock\interaction'
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Add-Type -Namespace Ip -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
public delegate bool EnumProc(IntPtr h, IntPtr p);
public struct RECT { public int Left, Top, Right, Bottom; }
public const int GWL_EXSTYLE = -20;
public const long WS_EX_TOOLWINDOW = 0x00000080L;
public const long WS_EX_NOACTIVATE = 0x08000000L;
public const long WS_EX_LAYERED = 0x00080000L;
public const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;
'@

function Find-Candidate {
    param([int] $Owner, [string] $TitleLike)
    $hits = New-Object System.Collections.Generic.List[IntPtr]
    $cb = [Ip.W+EnumProc] {
        param([IntPtr]$h, [IntPtr]$p)
        $o = 0
        [void][Ip.W]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -eq $Owner) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Ip.W]::GetWindowTextW($h, $sb, 256)
            if ($sb.ToString() -like $TitleLike) { $hits.Add($h) }
        }
        return $true
    }
    [void][Ip.W]::EnumWindows($cb, [IntPtr]::Zero)
    return $hits
}

function Get-WindowTitle {
    param([IntPtr] $Handle)
    $sb = New-Object System.Text.StringBuilder 256
    [void][Ip.W]::GetWindowTextW($Handle, $sb, 256)
    return $sb.ToString()
}

function Get-WinClass {
    param([IntPtr] $Handle)
    $sb = New-Object System.Text.StringBuilder 256
    [void][Ip.W]::GetClassNameW($Handle, $sb, 256)
    return $sb.ToString()
}

if (-not (Test-Path $Exe)) { throw "no executable at $Exe" }
Write-Host "=== INTERACTION PROOF ===" -ForegroundColor Cyan

$so = Join-Path $OutDir 'poc-stdout.txt'
if (Test-Path $so) { Remove-Item $so -Force }
$proc = Start-Process -FilePath $Exe -ArgumentList $AppArgs -PassThru -RedirectStandardOutput $so

$wins = @()
$deadline = (Get-Date).AddSeconds(25)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 300
    if ($proc.HasExited) { throw "candidate exited early (code $($proc.ExitCode))" }
    $wins = @(Find-Candidate -Owner $proc.Id -TitleLike '*Clear Dock*' | Where-Object { [Ip.W]::IsWindowVisible($_) })
    if ($wins.Count -gt 0) { break }
}
if ($wins.Count -eq 0) { throw 'no visible candidate window appeared' }

$h = $wins[0]
Start-Sleep -Milliseconds 900

$r = New-Object Ip.W+RECT
[void][Ip.W]::GetWindowRect($h, [ref]$r)
$ex = [Ip.W]::GetWindowLongPtrW($h, [Ip.W]::GWL_EXSTYLE).ToInt64()

$failures = New-Object System.Collections.Generic.List[string]
Write-Host ("  window : hwnd={0} class='{1}' title='{2}'" -f $h, (Get-WinClass $h), (Get-WindowTitle $h))
Write-Host ("  rect   : ({0},{1}) {2}x{3}" -f $r.Left, $r.Top, ($r.Right - $r.Left), ($r.Bottom - $r.Top))
Write-Host ("  exstyle: 0x{0:X}" -f $ex)

$styleChecks = @(
    @{ Name = 'WS_EX_TOOLWINDOW'; Present = (($ex -band [Ip.W]::WS_EX_TOOLWINDOW) -ne 0) }
    @{ Name = 'WS_EX_NOACTIVATE'; Present = (($ex -band [Ip.W]::WS_EX_NOACTIVATE) -ne 0) }
    @{ Name = 'WS_EX_LAYERED';    Present = (($ex -band [Ip.W]::WS_EX_LAYERED) -ne 0) }
)
foreach ($c in $styleChecks) {
    Write-Host ("  {0}: {1}" -f $c.Name, $(if ($c.Present) { 'present' } else { 'MISSING' }))
    if (-not $c.Present) { $failures.Add("$($c.Name) is missing") }
}

$fgBefore = [Ip.W]::GetForegroundWindow()
Write-Host ("  foreground before click: hwnd={0} '{1}'" -f $fgBefore, (Get-WindowTitle $fgBefore))

$cx = [int](($r.Left + $r.Right) / 2)
$cy = [int](($r.Top + $r.Bottom) / 2)
[void][Ip.W]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 250
[void][Ip.W]::mouse_event([Ip.W]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 80
[void][Ip.W]::mouse_event([Ip.W]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 600

$fgAfter = [Ip.W]::GetForegroundWindow()
Write-Host ("  foreground after click : hwnd={0} '{1}'" -f $fgAfter, (Get-WindowTitle $fgAfter))
if ($fgAfter -ne $fgBefore) { $failures.Add("the click changed the foreground window ($fgBefore -> $fgAfter)") }
if ($fgAfter -eq $h) { $failures.Add('the candidate itself became foreground, so it activates') }

Write-Host "`n=== VERDICT ===" -ForegroundColor Cyan
if ($failures.Count -eq 0) {
    Write-Host '  PASS - passive tool window, layered, and clicking it did not take the foreground.' -ForegroundColor Green
    $code = 0
} else {
    Write-Host '  FAIL' -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
    $code = 1
}
if (-not $proc.HasExited) { $proc.Kill() }
exit $code
