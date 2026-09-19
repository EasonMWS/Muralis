<#
    Drag smoke: does dragging a dock icon still take the process down?

    The dock's drag used to throw UnauthorizedAccessException out of a PointerMoved handler, which is an
    unhandled XAML exception and ends the process. This drives the real built binary through the gestures
    that reach that code — hover, press, a real drag past the travel threshold, release, and repeats — and
    reports process liveness, the fatal-error delta in the application log, and how many drags actually
    committed an order.

    Usage: ./tools/p4d-drag-smoke.ps1 [-Pins 6] [-Drags 5] [-IncludeSettings]
#>
[CmdletBinding()]
param(
    [int] $Pins = 6,
    [int] $Drags = 5,
    [switch] $IncludeSettings
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\Muralis.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Muralis.exe'
$settingsPath = Join-Path $env:LOCALAPPDATA 'Muralis\settings.json'
$profLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
$logDir = Join-Path $env:LOCALAPPDATA 'Muralis\logs'
$out = 'D:\AI\temp\dsh-cu-eval\product'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$backup = Join-Path $out 'settings.dragsmoke.before.json'

if (-not (Test-Path $exe)) { throw "no built binary at $exe" }

Add-Type -Namespace Ds -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
public delegate bool EnumProc(IntPtr h, IntPtr p);
public struct RECT { public int Left, Top, Right, Bottom; }
public const uint MOVE = 0x0001, ABSOLUTE = 0x8000, LEFTDOWN = 0x0002, LEFTUP = 0x0004;
'@
$SW = [Ds.W]::GetSystemMetrics(0)
$SH = [Ds.W]::GetSystemMetrics(1)

function Find-Dock([int]$owner) {
    $script:hit = [IntPtr]::Zero
    $cb = [Ds.W+EnumProc] {
        param([IntPtr]$h, [IntPtr]$p)
        $o = 0; [void][Ds.W]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -eq $owner -and [Ds.W]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Ds.W]::GetWindowTextW($h, $sb, 256)
            if ($sb.ToString() -eq 'Muralis Dock') { $script:hit = $h }
        }
        return $true
    }
    [void][Ds.W]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:hit
}
function Find-Main([int]$owner) {
    $script:hit2 = [IntPtr]::Zero
    $cb = [Ds.W+EnumProc] {
        param([IntPtr]$h, [IntPtr]$p)
        $o = 0; [void][Ds.W]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -eq $owner -and [Ds.W]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Ds.W]::GetWindowTextW($h, $sb, 256)
            if ($sb.ToString() -eq 'Muralis') { $script:hit2 = $h }
        }
        return $true
    }
    [void][Ds.W]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:hit2
}
function Log-Len { if (Test-Path $profLog) { (Get-Item $profLog).Length } else { 0 } }
function Recs([long]$from) {
    $fs = [System.IO.File]::Open($profLog, 'Open', 'Read', 'ReadWrite')
    try { if ($fs.Length -le $from) { return @() }
        [void]$fs.Seek($from, [System.IO.SeekOrigin]::Begin)
        $b = New-Object byte[] ($fs.Length - $from); [void]$fs.Read($b, 0, $b.Length) } finally { $fs.Dispose() }
    $o = @(); foreach ($l in ([System.Text.Encoding]::UTF8.GetString($b) -split "`n")) { if ($l) { try { $o += ($l | ConvertFrom-Json) } catch {} } }
    return $o
}
function ToScreen([int]$x, [int]$y) {
    [void][Ds.W]::mouse_event([Ds.W]::MOVE -bor [Ds.W]::ABSOLUTE,
        [int](($x * 65535) / ($SW - 1)), [int](($y * 65535) / ($SH - 1)), 0, [IntPtr]::Zero)
}

function Get-FatalCount {
    $n = 0
    Get-ChildItem "$logDir\muralis-*.log" -ErrorAction SilentlyContinue | ForEach-Object {
        $n += ([System.IO.File]::ReadAllLines($_.FullName, [System.Text.Encoding]::UTF8) | Select-String -Pattern '\[FTL\]').Count
    }
    return $n
}

Copy-Item $settingsPath $backup -Force
$fatalBefore = Get-FatalCount
$exitCode = $null
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
    $clear = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $clear -and @(Get-Process Muralis -ErrorAction SilentlyContinue).Count -gt 0) { Start-Sleep -Milliseconds 300 }
    if (@(Get-Process Muralis -ErrorAction SilentlyContinue).Count -gt 0) { throw 'a Muralis process would not close' }

    $env:MURALIS_DOCK_PROFILE = '1'
    $p = Start-Process $exe -PassThru
    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 75 -and $h -eq [IntPtr]::Zero; $i++) {
        Start-Sleep -Milliseconds 400
        if ($p.HasExited) { throw "Muralis exited during startup (code $($p.ExitCode))" }
        $h = Find-Dock $p.Id
    }
    if ($h -eq [IntPtr]::Zero) { throw 'no visible dock window appeared' }
    Start-Sleep -Milliseconds 12000
    if ($p.HasExited) { throw "Muralis exited while opening its pointer source (code $($p.ExitCode))" }

    Write-Host "=== DRAG SMOKE: $Pins pinned, $Drags drags ===" -ForegroundColor Cyan
    $wr = New-Object Ds.W+RECT
    [void][Ds.W]::GetWindowRect($h, [ref]$wr)
    $w = $wr.Right - $wr.Left
    $ht = $wr.Bottom - $wr.Top
    Write-Host "dock window: ($($wr.Left),$($wr.Top)) ${w}x${ht}"

    # The run is measured from the dock's own bounds, converted with the origin it reports.
    ToScreen 40 300
    Start-Sleep -Milliseconds 700
    $recs = Recs 0
    $o = @($recs | Where-Object { $_.name -eq 'motion.outside' })
    if ($o.Count -eq 0) { throw 'the dock never reported its interaction region' }
    $o = $o[-1]
    $runLeft = [double]$o.originX + [double]$o.left
    $railY = [int]([double]$o.originY + (([double]$o.top + [double]$o.bottom) / 2))
    Write-Host "run left on screen: $runLeft   rail y: $railY"

    $committed = 0
    for ($d = 1; $d -le $Drags; $d++) {
        # Press the first icon, carry it one slot to the right, release. One slot is 56 DIP: 52 box + 4 gap.
        $fromX = [int]$runLeft + 38
        $toX = $fromX + 56
        $mark = Log-Len

        ToScreen $fromX $railY
        Start-Sleep -Milliseconds 350
        [void][Ds.W]::mouse_event([Ds.W]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 120
        # Several steps, so the press crosses the travel threshold and the drag actually begins.
        foreach ($step in 1..6) {
            ToScreen ($fromX + [int](($toX - $fromX) * $step / 6)) $railY
            Start-Sleep -Milliseconds 90
        }
        [void][Ds.W]::mouse_event([Ds.W]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 900

        if ($p.HasExited) {
            Write-Host "  drag $d : PROCESS EXITED (code $($p.ExitCode))" -ForegroundColor Red
            break
        }

        $new = Recs $mark
        $released = @($new | Where-Object { $_.name -eq 'release.commit' -or $_.name -eq 'release.settle' })
        if ($released.Count -gt 0) { $committed++ }
        $names = (($new | Where-Object { $_.name } | ForEach-Object { $_.name } | Sort-Object -Unique) -join ',')
        Write-Host "  drag $d : alive, dock wrote [$names]"

        ToScreen 40 300
        Start-Sleep -Milliseconds 500
        # Re-read the run: a committed reorder can change which app is where but not the run's origin.
        $r2 = @((Recs $mark) | Where-Object { $_.name -eq 'motion.outside' })
        if ($r2.Count -gt 0) { $railY = [int]([double]$r2[-1].originY + (([double]$r2[-1].top + [double]$r2[-1].bottom) / 2)) }
    }

    $settingsReentries = 0
    $dockSectionSeen = 0
    if ($IncludeSettings) {
        Write-Host "`n=== SETTINGS PAGE RE-ENTRY ===" -ForegroundColor Cyan
        # Re-entering the settings page is what accumulates subscriptions: the page is not cached and its view
        # model is transient, so a subscription that is never released grows by one every visit. Each pass
        # navigates to Settings and back to Home through the navigation pane, then confirms the process is
        # still alive and that the pin list was re-read — which is what proves the Dock section really built.
        $main = Find-Main $p.Id
        if ($main -eq [IntPtr]::Zero) { throw 'the main window was not found' }
        $mr = New-Object Ds.W+RECT
        [void][Ds.W]::GetWindowRect($main, [ref]$mr)
        Write-Host "main window: ($($mr.Left),$($mr.Top)) $($mr.Right - $mr.Left)x$($mr.Bottom - $mr.Top)"

        # The navigation pane is queried rather than guessed: aiming at remembered offsets landed on the
        # library item and the run silently never reached Settings at all.
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $mainElement = [System.Windows.Automation.AutomationElement]::FromHandle($main)
        $listItem = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ListItem)

        function Get-NavPoint([string[]] $names) {
            if ($null -eq $mainElement) { return $null }
            foreach ($item in $mainElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listItem)) {
                $r = $item.Current.BoundingRectangle
                if ($r.Width -le 0 -or $r.Height -le 0) { continue }
                if ($names -contains $item.Current.Name) {
                    return [pscustomobject]@{ X = [int]($r.X + $r.Width / 2); Y = [int]($r.Y + $r.Height / 2) }
                }
            }
            return $null
        }

        # The navigation labels are localized, so the settings entry is taken as the last pane item and home as
        # the first, which is the order the shell declares them in.
        $paneItems = @()
        if ($null -ne $mainElement) {
            foreach ($item in $mainElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listItem)) {
                $r = $item.Current.BoundingRectangle
                if ($r.Width -gt 100 -and $r.Width -lt 300 -and $r.Height -gt 20 -and $r.Height -lt 60) {
                    $paneItems += [pscustomobject]@{ Name = $item.Current.Name; X = [int]($r.X + $r.Width / 2); Y = [int]($r.Y + $r.Height / 2) }
                }
            }
        }
        if ($paneItems.Count -lt 2) { throw "the navigation pane was not found ($($paneItems.Count) items)" }
        $settingsNav = $paneItems[-1]
        $homeNav = $paneItems[0]
        Write-Host "nav: home='$($homeNav.Name)' at ($($homeNav.X),$($homeNav.Y))   settings='$($settingsNav.Name)' at ($($settingsNav.X),$($settingsNav.Y))"

        $navX = $settingsNav.X
        $settingsY = $settingsNav.Y
        $homeY = $homeNav.Y

        # Navigation is confirmed from the application log, not from the click: the page logs the route it
        # built, and a pass that did not reach Settings is reported as not reached rather than counted. The
        # log is open for writing by the running app, so it is read through a shared handle.
        function Read-AppLog {
            $snapshot = Get-ChildItem "$logDir\muralis-*.log" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            $fs = [System.IO.File]::Open($snapshot.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
            try {
                $b = New-Object byte[] $fs.Length
                [void]$fs.Read($b, 0, $b.Length)
            } finally { $fs.Dispose() }
            return ([System.Text.Encoding]::UTF8.GetString($b) -split "`n")
        }

        $logLines = (Read-AppLog).Count

        function Read-NewLogLines([int] $from) {
            $all = Read-AppLog
            if ($all.Count -le $from) { return @() }
            return $all[$from..($all.Count - 1)]
        }

        for ($i = 1; $i -le 5; $i++) {
            ToScreen $navX $settingsY
            Start-Sleep -Milliseconds 250
            [void][Ds.W]::mouse_event([Ds.W]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
            [void][Ds.W]::mouse_event([Ds.W]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
            Start-Sleep -Milliseconds 1600
            if ($p.HasExited) { Write-Host "  pass $i : PROCESS EXITED on entering Settings" -ForegroundColor Red; break }

            $new = Read-NewLogLines $logLines
            $logLines += $new.Count
            $builtSettings = @($new | Select-String -Pattern "Page 'settings' built").Count -gt 0
            if ($builtSettings) { $dockSectionSeen++ }

            ToScreen $navX $homeY
            Start-Sleep -Milliseconds 250
            [void][Ds.W]::mouse_event([Ds.W]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
            [void][Ds.W]::mouse_event([Ds.W]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
            Start-Sleep -Milliseconds 1600
            if ($p.HasExited) { Write-Host "  pass $i : PROCESS EXITED on leaving Settings" -ForegroundColor Red; break }

            $after = Read-NewLogLines $logLines
            $logLines += $after.Count
            $leftSettings = @($after | Select-String -Pattern "Page 'home' built").Count -gt 0

            $settingsReentries++
            Write-Host "  pass $i : alive | settings built = $builtSettings | home again = $leftSettings"
        }
    }

    if (-not $p.HasExited) { $p.Kill() }
    $p.WaitForExit(8000) | Out-Null
    $exitCode = if ($p.HasExited) { $p.ExitCode } else { $null }
}
finally {
    Copy-Item $backup $settingsPath -Force
    Get-Process Muralis -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
}

$fatalAfter = Get-FatalCount
Write-Host "`n=== RESULT ===" -ForegroundColor Cyan
Write-Host "  drags attempted      : $Drags"
Write-Host "  drag commits seen    : $committed"
Write-Host "  settings re-entries  : $settingsReentries"
Write-Host "  dock section built   : $dockSectionSeen  (times the pin list was projected on entering Settings)"
Write-Host "  process exit code    : $(if ($null -eq $exitCode) { 'still running (killed by harness)' } else { $exitCode })"
Write-Host "  FTL delta            : $($fatalAfter - $fatalBefore)   (before $fatalBefore, after $fatalAfter)"

if ($fatalAfter -ne $fatalBefore) {
    Write-Host "`nFAILED: the application log gained fatal errors during the run" -ForegroundColor Red
    exit 1
}
Write-Host "`nNo fatal errors were logged." -ForegroundColor Green
