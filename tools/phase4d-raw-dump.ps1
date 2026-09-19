<#
    Drives the running Muralis app into Muralis Mode, injects pointer motion, and asks the raw pointer window
    itself to report its counters.

    The counters come from inside the window procedure, so a reading of reports=0 is the procedure saying it was
    never given a WM_INPUT, not an outside observer failing to see one.

    Usage: ./tools/phase4d-raw-dump.ps1 [-SkipEnter] [-SkipInject]
#>
[CmdletBinding()]
param(
    [switch] $SkipEnter,
    [switch] $SkipInject,
    [int] $DumpDelayMs = 700
)

$ErrorActionPreference = 'Stop'
$native = Join-Path $env:USERPROFILE '.dsh\profiles\web-computer-test\node_modules\dsh-computer-use-windows\native\publish\DshComputerUse.Native.exe'
$log = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
$out = 'D:\AI\temp\dsh-cu-eval\stageB'

Add-Type -Namespace P4d -Name W -MemberDefinition @'
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
[DllImport("user32.dll", SetLastError=true)] public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
[DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SendMessageTimeoutW(IntPtr h, uint m, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
public const uint MOVE = 0x0001, ABSOLUTE = 0x8000, DUMP = 0x8000 + 13, NULLMSG = 0x0000, ABORTIFHUNG = 0x0002;
'@

function Move-To([int] $x, [int] $y) {
    $sw = [P4d.W]::GetSystemMetrics(0); $sh = [P4d.W]::GetSystemMetrics(1)
    [P4d.W]::mouse_event([P4d.W]::MOVE -bor [P4d.W]::ABSOLUTE, [int](($x * 65535) / ($sw - 1)), [int](($y * 65535) / ($sh - 1)), 0, [IntPtr]::Zero)
}

$app = Get-Process -Name Muralis -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $app) { throw 'the Muralis app is not running' }

if (-not $SkipEnter) {
    $mode = (Get-Content "$env:LOCALAPPDATA\Muralis\settings.json" -Raw | ConvertFrom-Json).DesktopExperience.Mode
    if ($mode -ne 'Muralis') {
        $obs = (& $native observe --hwnd $app.MainWindowHandle --maxElements 400 --outputDir $out 2>&1 | Out-String | ConvertFrom-Json)
        $btn = $obs.elements | Where-Object { $_.automationId -eq 'HeroPrimaryAction' } | Select-Object -First 1
        & $native click --hwnd $app.MainWindowHandle --x ([int]($btn.bounds.x + $btn.bounds.width / 2)) --y ([int]($btn.bounds.y + $btn.bounds.height / 2)) 2>&1 | Out-Null
        Start-Sleep -Seconds 9
    }
}

$dock = ((& $native list 2>&1 | Out-String | ConvertFrom-Json) | Where-Object { $_.title -eq 'Muralis Dock' } | Select-Object -First 1)
if (-not $dock) { throw 'the dock window is not up' }
$dock.id | Set-Content "$out\dock-hwnd.txt"
Write-Host "dock: hwnd=$($dock.id) at ($($dock.bounds.x),$($dock.bounds.y)) $($dock.bounds.width)x$($dock.bounds.height)"

$before = (Get-Content $log).Count
$raw = [P4d.W]::FindWindowW('MuralisRawPointerWindow', 'Muralis Raw Pointer')
Write-Host "raw window: $raw"
if ($raw -eq [IntPtr]::Zero) { throw 'the raw pointer window does not exist' }

function Dump([string] $label) {
    # The receiving thread answers on its own schedule, and the first reading lands while the app is still
    # settling, so the answer is waited for rather than sampled once.
    for ($attempt = 1; $attempt -le 12; $attempt++) {
        [P4d.W]::PostMessageW($raw, [P4d.W]::DUMP, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        Start-Sleep -Milliseconds 250
        $last = Get-Content $log | Select-String 'raw\.dump' | Select-Object -Last 1
        if ($last -and ((Get-Content $log).Count -gt $before)) { break }
    }

    $last = Get-Content $log | Select-String 'raw\.dump' | Select-Object -Last 1
    if ($last) { Write-Host "  $label -> $($last.Line)" } else { Write-Host "  $label -> (no dump written after $attempt attempts)" }
}

# A hung receiving thread and a thread that is simply never given a message look identical from outside, so the
# thread is asked directly. A null message is dispatched to the window procedure like any other, and the answer
# says whether that procedure is reachable at all.
function Ping([string] $label) {
    $pingResult = [IntPtr]::Zero
    $ping = [P4d.W]::SendMessageTimeoutW($raw, [P4d.W]::NULLMSG, [IntPtr]::Zero, [IntPtr]::Zero, [P4d.W]::ABORTIFHUNG, 2000, [ref]$pingResult)
    $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    Write-Host "  ping $label -> answered=$($ping -ne [IntPtr]::Zero) result=$pingResult lastError=$err"
}

Ping 'before'
Dump 'at open'

if (-not $SkipInject) {
    $bandY = $dock.bounds.y + $dock.bounds.height - 40
    Write-Host "injecting: sweep across the screen, then along the dock band y=$bandY"
    foreach ($x in 200, 500, 800, 1100, 1400, 1700, 2000) { Move-To $x 700; Start-Sleep -Milliseconds 40 }
    Dump 'after off-dock motion'

    foreach ($x in 900, 1000, 1100, 1200, 1300, 1400, 1500) { Move-To $x $bandY; Start-Sleep -Milliseconds 40 }
    Dump 'after on-band motion'
    Ping 'after'
}

Write-Host '=== motion traces for this process ==='
$start = Get-Process -Name Muralis -ErrorAction SilentlyContinue | Select-Object -First 1
Get-Content $log | Select-String 'motion\.' | Select-Object -Last 10 | ForEach-Object { $_.Line }
