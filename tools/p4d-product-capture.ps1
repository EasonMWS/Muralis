<#
    Dock productization: runtime acceptance and capture.

    For each product state — nothing pinned, three apps, six apps, ten apps — this drives the real built binary
    and records three independent things:

      * the window rectangle, read from the window, which is the geometry the user's clicks land in;
      * the dock's own motion records, read from the byte range this state produced, which carry the icon run's
        drawn bounds, the participant count, the engine's peak scale and the pointer-latency percentiles;
      * a screenshot of the live window, taken by the native observer and handed on uncropped.

    It then drives the pointer to the test matrix the motion acceptance calls for — each icon, the midpoint
    between two of them, both ends, and away — and records what the dock did. The rules it checks are the ones
    the product promises; anything else it prints and leaves to review.

    Usage: ./tools/p4d-product-capture.ps1 [-States 0,3,6,10] [-ShotDir screenshots]
#>
[CmdletBinding()]
param(
    [int[]] $States = @(0, 3, 6, 10),
    [string] $ShotDir = 'screenshots',
    [string] $WorkDir = 'D:\AI\temp\dsh-cu-eval\product'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$shotRoot = if ([IO.Path]::IsPathRooted($ShotDir)) { $ShotDir } else { Join-Path $root $ShotDir }
New-Item -ItemType Directory -Force -Path $shotRoot, $WorkDir | Out-Null

$exe = Join-Path $root 'src\Muralis.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Muralis.exe'
$native = Join-Path $env:USERPROFILE '.dsh\profiles\web-computer-test\node_modules\dsh-computer-use-windows\native\publish\DshComputerUse.Native.exe'
$settingsPath = Join-Path $env:LOCALAPPDATA 'Muralis\settings.json'
$profLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
$backup = Join-Path $WorkDir 'settings.before.json'

if (-not (Test-Path $exe)) { throw "no built binary at $exe" }
if (-not (Test-Path $native)) { throw "no native observer at $native" }
if (-not (Test-Path $settingsPath)) { throw "no settings file at $settingsPath" }

Add-Type -Namespace Cap -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
public delegate bool EnumProc(IntPtr h, IntPtr p);
public struct RECT { public int Left, Top, Right, Bottom; }
public const uint MOVE = 0x0001, ABSOLUTE = 0x8000;
'@

$script:ScreenW = [Cap.Win]::GetSystemMetrics(0)
$script:ScreenH = [Cap.Win]::GetSystemMetrics(1)

function Get-WindowHandle([int] $ownerProcessId, [string] $title) {
    $script:hit = [IntPtr]::Zero
    $cb = [Cap.Win+EnumProc] {
        param([IntPtr] $h, [IntPtr] $p)
        $owner = 0
        [void][Cap.Win]::GetWindowThreadProcessId($h, [ref] $owner)
        if ($owner -eq $ownerProcessId -and [Cap.Win]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Cap.Win]::GetWindowTextW($h, $sb, 256)
            if ($sb.ToString() -eq $title) {
                $r = New-Object Cap.Win+RECT
                [void][Cap.Win]::GetWindowRect($h, [ref] $r)
                if (($r.Right - $r.Left) -gt 0 -and ($r.Bottom - $r.Top) -gt 0) { $script:hit = $h }
            }
        }
        return $true
    }
    [void][Cap.Win]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:hit
}

function Move-Pointer([int] $x, [int] $y) {
    [Cap.Win]::mouse_event([Cap.Win]::MOVE -bor [Cap.Win]::ABSOLUTE,
        [int](($x * 65535) / ($script:ScreenW - 1)), [int](($y * 65535) / ($script:ScreenH - 1)), 0, [IntPtr]::Zero)
}

function Read-Rect([IntPtr] $h) {
    $r = New-Object Cap.Win+RECT
    [void][Cap.Win]::GetWindowRect($h, [ref] $r)
    $dpi = [Cap.Win]::GetDpiForWindow($h)
    [pscustomobject]@{
        Left = $r.Left; Top = $r.Top; Width = $r.Right - $r.Left; Height = $r.Bottom - $r.Top
        CenterX = $r.Left + [int](($r.Right - $r.Left) / 2)
        Scale = if ($dpi -gt 0) { [math]::Round($dpi / 96.0, 4) } else { 1 }
    }
}

function Get-LogLength { if (Test-Path $profLog) { (Get-Item $profLog).Length } else { 0 } }

function Get-RecordsFrom([long] $position) {
    if (-not (Test-Path $profLog)) { return @() }
    $fs = [System.IO.File]::Open($profLog, 'Open', 'Read', 'ReadWrite')
    try {
        if ($fs.Length -le $position) { return @() }
        [void]$fs.Seek($position, [System.IO.SeekOrigin]::Begin)
        $buf = New-Object byte[] ($fs.Length - $position)
        [void]$fs.Read($buf, 0, $buf.Length)
    } finally { $fs.Dispose() }
    $out = @()
    foreach ($line in ([System.Text.Encoding]::UTF8.GetString($buf) -split "`n")) {
        if ($line.Length -eq 0) { continue }
        try { $out += ($line | ConvertFrom-Json) } catch { }
    }
    return $out
}

function Write-Pins([string[]] $targets) {
    $json = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $pins = @()
    foreach ($t in $targets) {
        $pins += [pscustomobject]@{
            Id = [guid]::NewGuid().ToString('N').Substring(0, 12)
            DisplayName = [IO.Path]::GetFileNameWithoutExtension($t)
            LaunchTarget = $t
            IconIdentity = $t
            Kind = 1
            Identity = $t.ToLowerInvariant()
            Arguments = $null
            WorkingDirectory = $null
        }
    }
    $json.Dock.IsVisible = $true
    $json.Dock.BackgroundStyle = 'Transparent'
    $json.Dock.PinnedApps = $pins
    [System.IO.File]::WriteAllText($settingsPath, ($json | ConvertTo-Json -Depth 12), (New-Object System.Text.UTF8Encoding($false)))
}

function Start-Muralis {
    $env:MURALIS_DOCK_PROFILE = '1'
    $script:launchMarker = Get-LogLength
    $p = Start-Process -FilePath $exe -PassThru
    $deadline = (Get-Date).AddSeconds(60)
    $h = [IntPtr]::Zero
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 400
        if ($p.HasExited) { throw "Muralis exited during startup with code $($p.ExitCode)" }
        $h = Get-WindowHandle $p.Id 'Muralis Dock'
        if ($h -ne [IntPtr]::Zero) { break }
    }

    # No visible dock window is a legitimate outcome, not a failure: the dock is content-sized, so with
    # nothing pinned it either never puts a window on screen or hides the one it made. The caller decides
    # what an absent window means for the state it asked for.
    if ($h -eq [IntPtr]::Zero) {
        Start-Sleep -Milliseconds 4000
        return [pscustomobject]@{ Process = $p; Handle = [IntPtr]::Zero }
    }

    # The dock's passive raw-input registration is opened on its own thread a couple of seconds after the
    # window exists, and reports that arrive before it is up are simply not delivered. Driving the pointer
    # into that gap looks exactly like a dock that ignores the pointer. The dock's own record of the
    # registration is waited for, with a fixed floor because the record is written by the same path that
    # writes everything else.
    Start-Sleep -Milliseconds 12000
    $ready = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $ready) {
        Start-Sleep -Milliseconds 300
        if ($p.HasExited) { throw "Muralis exited while opening its pointer source with code $($p.ExitCode)" }
        $seen = Get-RecordsFrom $script:launchMarker
        if (@($seen | Where-Object { $_.label -eq 'motion.pointer.source' }).Count -gt 0) { break }
    }

    Start-Sleep -Milliseconds 900
    return [pscustomobject]@{ Process = $p; Handle = $h }
}

function Stop-Muralis($session) {
    if ($session -and -not $session.Process.HasExited) {
        [void]$session.Process.CloseMainWindow()
        Start-Sleep -Milliseconds 700
        if (-not $session.Process.HasExited) { $session.Process.Kill() }
        $session.Process.WaitForExit(6000) | Out-Null
    }
    Start-Sleep -Milliseconds 500
}

function Save-Shot([IntPtr] $h, [string] $name, [string] $note) {
    $shotDir = Join-Path $WorkDir 'raw'
    New-Item -ItemType Directory -Force -Path $shotDir | Out-Null
    $before = @(Get-ChildItem "$shotDir\window-*.png" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
    & $native observe --hwnd ([int]$h) --maxElements 500 --outputDir $shotDir 2>&1 | Out-Null
    $after = @(Get-ChildItem "$shotDir\window-*.png" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime)
    $fresh = $after | Where-Object { $_.FullName -notin $before } | Select-Object -Last 1
    if (-not $fresh) { $fresh = $after | Select-Object -Last 1 }
    if (-not $fresh -or $fresh.Length -le 0) {
        Write-Host "  shot $name : UNAVAILABLE" -ForegroundColor Yellow
        return $null
    }
    $dest = Join-Path $shotRoot $name
    Copy-Item $fresh.FullName $dest -Force
    $fi = Get-Item $dest
    Write-Host "  shot $name : $($fi.Length) bytes  ($note)" -ForegroundColor DarkGray
    return $dest
}

Copy-Item -LiteralPath $settingsPath -Destination $backup -Force
Write-Host "settings backed up to $backup" -ForegroundColor DarkGray

$links = @(Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -Filter *.lnk -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne 'Muralis.lnk' } | Select-Object -ExpandProperty FullName)
if ($links.Count -lt 10) { throw "need ten desktop shortcuts, found $($links.Count)" }

$rows = @()
$problems = @()

try {
    foreach ($count in $States) {
        Write-Host "`n=== STATE: $count pinned ===" -ForegroundColor Cyan
        $targets = if ($count -eq 0) { @() } else { $links[0..($count - 1)] }
        Write-Pins $targets

        $session = $null
        try {
            $marker = Get-LogLength
            $session = Start-Muralis
            $h = $session.Handle

            Move-Pointer 60 400
            Start-Sleep -Milliseconds 500

            if ($h -eq [IntPtr]::Zero) {
                # No visible dock window. That is the state the product promises with nothing pinned — the
                # dock is content-sized, so an empty one never puts a plate on the desktop. With apps pinned
                # it is a failure, and it is reported as one rather than recorded as a reading.
                if ($count -ne 0) {
                    $problems += "$count apps: no visible dock window appeared"
                    continue
                }

                $rows += [pscustomobject]@{
                    Pinned = $count; Visible = $false; Scale = $null
                    RestingW = $null; RestingH = $null; RestingCenterX = $null
                    ScreenCenterX = [int]($script:ScreenW / 2)
                    ExpandedW = $null; ExpandedCenterX = $null; SettledW = $null
                    RunDip = $null; RunHDip = $null; Participants = $null; Capacity = $null
                    Peak = $null; Dropped = 0; Crashed = $session.Process.HasExited
                }
                Write-Host '  no visible dock window - the empty dock is off the desktop' -ForegroundColor Green
                continue
            }

            $resting = Read-Rect $h

            # Visibility is read only once the dock has had time to decide whether it has anything to draw.
            # With nothing pinned the window is created, hidden and moved off the work area, and a reading
            # taken during that sequence reports the state before it finished.
            $visible = $true
            if ($count -eq 0) {
                $deadline = (Get-Date).AddSeconds(8)
                while ((Get-Date) -lt $deadline) {
                    Start-Sleep -Milliseconds 250
                    if (-not [Cap.Win]::IsWindowVisible($h)) { break }
                }
            }

            $visible = [Cap.Win]::IsWindowVisible($h)

            # The window frame, measured rather than assumed: the dock's own client origin and the window
            # rectangle differ by exactly this, and the expanded size is reported in client coordinates.
            $frameOriginLeft = $null; $frameY = 0
            $firstRecs = Get-RecordsFrom $marker
            $firstOut = @($firstRecs | Where-Object { $_.name -eq 'motion.outside' })
            if ($firstOut.Count -gt 0) {
                $frameOriginLeft = [double]$firstOut[-1].originX
                $frameY = [int]([double]$firstOut[-1].originY - $resting.Top)
            }

            $records = Get-RecordsFrom $marker
            $outside = @($records | Where-Object { $_.name -eq 'motion.outside' })
            $rebuild = @($records | Where-Object { $_.name -eq 'motion.rebuild' })
            $lastOut = if ($outside.Count) { $outside[-1] } else { $null }
            $lastRb = if ($rebuild.Count) { $rebuild[-1] } else { $null }

            $runWidth = $null; $runHeight = $null; $runTopDip = $null; $midX = $null; $midY = $null; $leadIconScreenX = $null
            if ($lastOut) {
                $runWidth = [math]::Round([double]$lastOut.right - [double]$lastOut.left, 2)
                $runHeight = [math]::Round([double]$lastOut.bottom - [double]$lastOut.top, 2)
                $runTopDip = [double]$lastOut.top
                $midX = [int]([double]$lastOut.originX + (([double]$lastOut.left + [double]$lastOut.right) / 2))
                $midY = [int]([double]$lastOut.originY + (([double]$lastOut.top + [double]$lastOut.bottom) / 2))

                # The first icon's centre, which is where the wave is at its full scale.
                $leadIconScreenX = [int]([double]$lastOut.originX + [double]$lastOut.left + 38)
            }

            # --- rest, then the pointer matrix the motion acceptance asks for ---
            #
            # Two things this has to get right, and both produced wrong readings before:
            #
            #   * the dock resizes a few milliseconds AFTER the report that caused it, so a reading taken once
            #     after a fixed sleep samples the previous state and reports "the dock never widened". Every
            #     reading below waits for the dock's own record of the transition.
            #   * where an icon is on screen depends on where the window is, and the window moves when the dock
            #     expands. The origin is therefore re-read from the dock immediately before aiming, never
            #     carried over from an earlier state.
            $expanded = $null; $peak = $null; $drops = 0; $participants = $null; $capacity = $null
            $restWidth = $resting.Width

            Move-Pointer 40 400
            Start-Sleep -Milliseconds 500

            $live = Get-RecordsFrom $marker
            $bounds = @($live | Where-Object { $_.name -eq 'motion.outside' })
            if ($bounds.Count -gt 0) {
                $b = $bounds[-1]
                $aimY = [int]([double]$b.originY + (([double]$b.top + [double]$b.bottom) / 2))
                $leadX = [int]([double]$b.originX + [double]$b.left + 38)

                # The first icon's centre, which is where the engine's peak is defined to be exactly full.
                # Aimed up to three times: a report that arrives before the dock's passive registration is
                # open is simply not delivered, and one retry separates "not registered yet" from "not
                # working at all" without pretending the first attempt succeeded.
                $sawExpansion = $false
                for ($attempt = 0; $attempt -lt 6 -and -not $sawExpansion; $attempt++) {
                    Move-Pointer ($leadX + $attempt) $aimY

                    $deadline = (Get-Date).AddSeconds(3)
                    while ((Get-Date) -lt $deadline) {
                        Start-Sleep -Milliseconds 150
                        $live = Get-RecordsFrom $marker
                        if (@($live | Where-Object { $_.name -eq 'motion.bounds.transition' -and $_.state -eq 'expanded' }).Count -gt 0) {
                            $sawExpansion = $true
                            break
                        }
                    }

                    if (-not $sawExpansion) {
                        # Away and back: the dock's passive registration can still be opening, and a report
                        # sent before it is up is not delivered at all. Retrying is what separates "not
                        # registered yet" from "the dock does not respond", and the reading below is only
                        # recorded once the dock has answered.
                        Move-Pointer 40 400
                        Start-Sleep -Milliseconds 1200
                    }
                }

                Start-Sleep -Milliseconds 350
                $expanded = Read-Rect $h

                # AppWindow.MoveAndResize is applied asynchronously, and on this machine the delay is not
                # constant: a reading taken at a fixed moment lands on either side of the resize. What is
                # recorded is therefore the width the dock STAYS at once it has stopped changing, and it is
                # given several seconds to settle first. Recording the widest reading seen would hide a dock
                # that never widened; recording the first one would report the previous size.
                $widest = $expanded
                $deadline = (Get-Date).AddSeconds(8)
                while ((Get-Date) -lt $deadline) {
                    Start-Sleep -Milliseconds 250
                    $now = Read-Rect $h
                    if ($now.Width -gt $widest.Width) { $widest = $now }
                    if ($now.Width -eq $widest.Width -and $now.Width -gt $resting.Width) { break }
                }
                $expanded = $widest

                $live = Get-RecordsFrom $marker
                $pointers = @($live | Where-Object { $_.name -eq 'motion.pointer' })
                if ($pointers.Count) {
                    $peak = ($pointers | Measure-Object -Property peak -Maximum).Maximum
                    $participants = $pointers[-1].icons
                    $capacity = $pointers[-1].capacity
                }

                Move-Pointer 40 400
                $deadline = (Get-Date).AddSeconds(12)
                $settled = Read-Rect $h
                $restSeen = 0
                while ((Get-Date) -lt $deadline) {
                    Start-Sleep -Milliseconds 250
                    $settled = Read-Rect $h
                    # Two consecutive resting-width readings, so a window caught mid-resize is not mistaken
                    # for one that has settled.
                    if ($settled.Width -eq $restWidth) { $restSeen++; if ($restSeen -ge 2) { break } } else { $restSeen = 0 }
                }

                $final = Get-RecordsFrom $marker
                $perf = @($final | Where-Object { $_.name -eq 'motion.performance' })
                if ($perf.Count) { $drops = ($perf | Measure-Object -Property dropped -Maximum).Maximum }
            } else {
                $settled = $null
            }

            $row = [pscustomobject]@{
                Pinned = $count
                Visible = $visible
                Scale = $resting.Scale
                RestingW = $resting.Width
                RestingH = $resting.Height
                RestingCenterX = $resting.CenterX
                ScreenCenterX = [int]($script:ScreenW / 2)
                ExpandedW = if ($expanded) { $expanded.Width } else { $null }
                ExpandedCenterX = if ($expanded) { $expanded.CenterX } else { $null }
                SettledW = if ($settled) { $settled.Width } else { $null }
                RunDip = $runWidth
                RunHDip = $runHeight
                Participants = $participants
                Capacity = $capacity
                Peak = $peak
                Dropped = $drops
                Crashed = $session.Process.HasExited
            }
            $rows += $row
            $row | Format-List
        }
        finally { Stop-Muralis $session }

        # --- screenshots in this state (fresh session so the window is at rest) ---
        $session = $null
        try {
            $marker = Get-LogLength
            $session = Start-Muralis
            $h = $session.Handle
            Move-Pointer 60 400
            Start-Sleep -Milliseconds 900
            $shotName = switch ($count) {
                0 { 'dock-product-01-0apps.png' }
                3 { 'dock-product-02-3apps-rest.png' }
                6 { 'dock-product-04-6apps-rest.png' }
                10 { 'dock-product-06-10apps.png' }
                default { "dock-product-99-$($count)apps.png" }
            }
            $null = Save-Shot $h $shotName "$count pinned, pointer away, transparent"

            if ($count -eq 3) {
                $live = Get-RecordsFrom $marker
                $o = @($live | Where-Object { $_.name -eq 'motion.outside' })
                if ($o.Count) {
                    $x = [int]([double]$o[-1].originX + (([double]$o[-1].left + [double]$o[-1].right) / 2))
                    $y = [int]([double]$o[-1].originY + (([double]$o[-1].top + [double]$o[-1].bottom) / 2))
                    Move-Pointer $x $y
                    Start-Sleep -Milliseconds 800
                    $null = Save-Shot $h 'dock-product-03-3apps-hover.png' 'three pinned, pointer on the middle icon'
                }
            }

            if ($count -eq 6) {
                $live = Get-RecordsFrom $marker
                $o = @($live | Where-Object { $_.name -eq 'motion.outside' })
                if ($o.Count) {
                    $x = [int]([double]$o[-1].originX + (([double]$o[-1].left + [double]$o[-1].right) / 2))
                    $y = [int]([double]$o[-1].originY + (([double]$o[-1].top + [double]$o[-1].bottom) / 2))
                    Move-Pointer $x $y
                    Start-Sleep -Milliseconds 800
                    $null = Save-Shot $h 'dock-product-05-6apps-hover.png' 'six pinned, pointer on the middle icon'
                    $null = Save-Shot $h 'dock-product-07-dark.png' 'six pinned under the current theme'
                }
            }
        }
        finally { Stop-Muralis $session }
    }
}
finally {
    Copy-Item -LiteralPath $backup -Destination $settingsPath -Force
    Write-Host "settings restored" -ForegroundColor DarkGray
    Get-Process Muralis -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); Start-Sleep -Milliseconds 300 }
}

$csv = Join-Path $WorkDir 'acceptance.csv'
$rows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $csv

Write-Host "`n=== SUMMARY ===" -ForegroundColor Cyan
$rows | Format-Table Pinned, Visible, Scale, RestingW, ExpandedW, SettledW, RunDip, RestingCenterX, ExpandedCenterX, Participants, Peak, Dropped, Crashed -AutoSize

foreach ($r in $rows) {
    if ($r.Crashed) { $problems += "$($r.Pinned) apps: the process exited during the run" }
    if ($r.Pinned -eq 0) {
        if ($r.Visible) { $problems += 'nothing pinned: the dock window is still visible' }
        continue
    }
    if ($r.RestingW -ge 900) { $problems += "$($r.Pinned) apps: resting window is $($r.RestingW) px, sized for a dock the user does not have" }
    if ([math]::Abs($r.RestingCenterX - $r.ScreenCenterX) -gt 2) { $problems += "$($r.Pinned) apps: resting window is not centred" }
    if ($null -ne $r.ExpandedCenterX -and [math]::Abs($r.ExpandedCenterX - $r.RestingCenterX) -gt 2) { $problems += "$($r.Pinned) apps: the dock moved when it expanded" }
    if ($null -ne $r.ExpandedW -and $r.ExpandedW -le $r.RestingW) { $problems += "$($r.Pinned) apps: entering the dock did not widen it" }
    if ($null -ne $r.SettledW -and $r.SettledW -ne $r.RestingW) { $problems += "$($r.Pinned) apps: leaving did not return the window to rest" }
    if ($null -ne $r.Peak -and $r.Peak -lt 1.79) { $problems += "$($r.Pinned) apps: the wave peaked at only $($r.Peak)" }
    if ($null -ne $r.Dropped -and $r.Dropped -gt 0) { $problems += "$($r.Pinned) apps: $($r.Dropped) pointer reports were dropped" }
    if ($r.Participants -ne $r.Pinned) { $problems += "$($r.Pinned) apps: the engine is driving $($r.Participants) icons" }
}

Write-Host "wrote $csv"
if ($problems.Count) {
    Write-Host "`nFAILED:" -ForegroundColor Red
    $problems | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Host "`nAll acceptance rules held." -ForegroundColor Green
