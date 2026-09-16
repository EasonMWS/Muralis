<#
.SYNOPSIS
    Phase 3A live verification: the desktop pointer router, driven on a real desktop.

.DESCRIPTION
    The harness moves the real pointer with SendInput (which produces the same raw input reports a
    physical mouse does) and reads the canvas diagnostics panel through UI Automation to see what
    the desktop actually did. It checks the Phase 3A contract:

      - a fast sweep across the row: hover magnification is continuous, never drops to nothing
      - the same sweep 120 DIP above the row, outside the window region: the pointer is read at all
      - leaving the canvas: hover decays to rest, the dock stays retracted
      - the dock trigger band: the rail expands, and retracts after the pointer leaves
      - ordinary application windows and the taskbar: the desktop stops reacting entirely
      - hit testing: the grown item box receives a drag, outside the region nothing is delivered
      - 30 s of continuous fast movement: no anomaly, bounded dispatch rate, CPU and GPU cost
      - Explorer restart: the router rides it out untouched and the canvas re-mounts
      - a graceful exit: the raw input registration and the router window are released

    Everything the harness changes is restored on the way out: settings.json, the prototype layout
    file, the running app.

.PARAMETER Stage
    probe - no app launch, only reports the machine state and what sits under the probe points
    full  - the whole matrix

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/p3a-pointer-verify.ps1 -Stage probe
    powershell -ExecutionPolicy Bypass -File tools/p3a-pointer-verify.ps1 -Stage full
#>
[CmdletBinding()]
param(
    [ValidateSet('probe', 'latency', 'full')] [string]$Stage = 'probe',
    [string]$Exe = 'src/Muralis.App/bin/Debug/net10.0-windows10.0.26100.0/win-x64/Muralis.exe',
    [int]$HoldSeconds = 30,
    [switch]$SkipExplorerRestart,
    [string]$OutDir = 'artifacts/p3a'
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- interop

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class P3Win {
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
  [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, INPUT[] inputs, int size);

  public const uint INPUT_MOUSE = 0;
  public const uint MOUSEEVENTF_MOVE = 0x0001;
  public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
  public const uint MOUSEEVENTF_LEFTUP = 0x0004;
  public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
  public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
  public const uint GA_ROOT = 2;
  public const uint WM_CLOSE = 0x0010;
  public const int SW_MINIMIZE = 6;
  public const int SW_RESTORE = 9;
  public const int SM_CXVIRTUALSCREEN = 78;
  public const int SM_CYVIRTUALSCREEN = 79;
  public const int SM_XVIRTUALSCREEN = 76;
  public const int SM_YVIRTUALSCREEN = 77;

  public struct POINT { public int X; public int Y; }
  public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

  [StructLayout(LayoutKind.Sequential)]
  public struct MOUSEINPUT {
    public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
  }

  [StructLayout(LayoutKind.Sequential)]
  public struct INPUT { public uint type; public MOUSEINPUT mi; }

  private static void SendOne(uint flags, int dx, int dy) {
    INPUT[] inputs = new INPUT[1];
    inputs[0].type = INPUT_MOUSE;
    inputs[0].mi.dx = dx;
    inputs[0].mi.dy = dy;
    inputs[0].mi.dwFlags = flags;
    uint sent = SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
    if (sent != 1) throw new InvalidOperationException("SendInput failed (" + Marshal.GetLastWin32Error() + ")");
  }

  // A move that goes through the input stack, so raw input consumers see it exactly like a mouse.
  public static void MoveTo(int x, int y) {
    int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
    int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
    int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
    int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
    if (width < 2 || height < 2) throw new InvalidOperationException("The virtual screen is not measurable.");
    int nx = (int)Math.Round((x - left) * 65535.0 / (width - 1));
    int ny = (int)Math.Round((y - top) * 65535.0 / (height - 1));
    SendOne(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny);
  }

  public static void LeftDown() { SendOne(MOUSEEVENTF_LEFTDOWN, 0, 0); }
  public static void LeftUp() { SendOne(MOUSEEVENTF_LEFTUP, 0, 0); }

  public static string ClassOf(IntPtr h) {
    var text = new StringBuilder(256);
    GetClassName(h, text, 256);
    return text.ToString();
  }

  public static string TitleOf(IntPtr h) {
    var text = new StringBuilder(256);
    GetWindowText(h, text, 256);
    return text.ToString();
  }

  public static IntPtr RootOf(IntPtr h) { return GetAncestor(h, GA_ROOT); }

  public static int ProcessOf(IntPtr h) {
    uint pid;
    GetWindowThreadProcessId(h, out pid);
    return (int)pid;
  }

  public static IntPtr FindWindowByClass(int processId, string className) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      if (ProcessOf(h) != processId) return true;
      if (!IsWindowVisible(h)) return true;
      if (ClassOf(h) != className) return true;
      found = h;
      return false;
    }, IntPtr.Zero);
    return found;
  }

  // Hidden top level windows included: the router window is never shown. Descendants too: the
  // canvas window is parented to the shell's icon host, so it never shows up as a top level window.
  public static string[] ClassesWithPrefix(string prefix) {
    var list = new System.Collections.Generic.List<string>();
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      string cls = ClassOf(h);
      if (cls.StartsWith(prefix)) list.Add(cls);
      EnumChildWindows(h, delegate(IntPtr child, IntPtr l2) {
        string childCls = ClassOf(child);
        if (childCls.StartsWith(prefix)) list.Add(childCls);
        return true;
      }, IntPtr.Zero);
      return true;
    }, IntPtr.Zero);
    return list.ToArray();
  }

  public static IntPtr[] VisibleTopLevel(int minWidth, int minHeight) {
    var list = new System.Collections.Generic.List<IntPtr>();
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      if (!IsWindowVisible(h)) return true;
      RECT r; GetWindowRect(h, out r);
      if (r.Right - r.Left >= minWidth && r.Bottom - r.Top >= minHeight) list.Add(h);
      return true;
    }, IntPtr.Zero);
    return list.ToArray();
  }

  public static int[] RectOf(IntPtr h) {
    RECT r; GetWindowRect(h, out r);
    return new int[] { r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top };
  }

  public static string Describe(IntPtr h) {
    if (h == IntPtr.Zero) return "(none)";
    return ClassOf(h) + " [" + TitleOf(h) + "]";
  }
}
"@

# ---------------------------------------------------------------- helpers

$repoRoot = Split-Path $PSScriptRoot -Parent
$exePath = if ([System.IO.Path]::IsPathRooted($Exe)) { $Exe } else { Join-Path $repoRoot $Exe }
$outPath = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repoRoot $OutDir }

$appData = Join-Path $env:LOCALAPPDATA 'Muralis'
$settingsPath = Join-Path $appData 'settings.json'
$layoutPath = Join-Path $appData 'desktop-canvas-prototype.json'
$settingsBackup = "$settingsPath.p3a.bak"
$layoutBackup = "$layoutPath.p3a.bak"

# The canvas, as the layout puts it on this display.
$itemCentresX = @(1085, 1215, 1345, 1475)
$rowY = 500
$rowTop = 421   # 500 - (96 * 1.6 / 2 + 2)
$offRowY = 380  # 120 DIP above the centres: inside the influence radius, outside the region
$offRowScale = 1.21
$dockProbe = @(5, 720)
$taskbarProbe = @(1280, 1435)
$blankProbes = @(@(1700, 900), @(700, 1200))
$growProbe = @(1280, 500)     # the blender item's grown box covers this point
$outsideProbe = @(1615, 500)  # right of the region, which ends at 1475 + 78.8

$diagTitle = -join ([int[]](0x753B, 0x5E03, 0x8BCA, 0x65AD, 0xFF08, 0x4EC5, 0x5F00, 0x53D1, 0x7248, 0xFF09) | ForEach-Object { [char]$_ })
$navDynamic = -join ([int[]](0x52A8, 0x6001, 0x58C1, 0x7EB8) | ForEach-Object { [char]$_ })

$script:checks = New-Object System.Collections.ArrayList
$script:samples = @{}
$script:logFile = $null
$script:process = $null
$script:window = [IntPtr]::Zero
$script:diagElement = $null
$script:diagMode = 'minimize'

function Add-Check([string]$name, [bool]$ok, [string]$detail) {
    [void]$script:checks.Add([pscustomobject]@{ Check = $name; Ok = $ok; Detail = $detail })
    $tag = 'FAIL'
    if ($ok) { $tag = 'PASS' }
    Write-Host ("  [{0}] {1} : {2}" -f $tag, $name, $detail)
}

function Refresh-LogFile {
    $dir = Join-Path $appData 'logs'
    $script:logFile = Get-ChildItem $dir -Filter 'muralis-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

function Read-LogText {
    if (-not $script:logFile) { return '' }
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        try { return [IO.File]::ReadAllText($script:logFile.FullName, [Text.Encoding]::UTF8) }
        catch { Start-Sleep -Milliseconds 100 }
    }
    return ''
}

# Serilog's file sink does not have to hit the disk the moment the app writes a line, so the
# on-disk file lags during a run. Counting occurrences of a pattern is still sound: counts from
# previous runs are already flushed, and the whole file is flushed when the app exits. Growth is
# therefore polled with generous timeouts, and the authoritative log checks run after the exit.
$logPatterns = [ordered]@{
    UiIdle         = 'ui idle'
    CanvasShowing  = 'The desktop canvas is showing'
    RouterListening = 'The desktop pointer router is listening'
    RouterReleased = 'released its raw mouse input registration'
    DockExpanded   = 'The desktop dock expanded'
    DockRetracted  = 'The desktop dock retracted'
    ItemDropped    = 'was dropped at'
    ItemClicked    = 'was clicked'
    SurfaceBack    = 'The desktop surface is back on the desktop'
}

function Get-LogCounts {
    Refresh-LogFile
    $text = Read-LogText
    $counts = @{}
    foreach ($key in $logPatterns.Keys) {
        if ([string]::IsNullOrEmpty($text)) { $counts[$key] = 0 }
        else { $counts[$key] = ([regex]::Matches($text, [regex]::Escape($logPatterns[$key]))).Count }
    }
    return $counts
}

function Get-LogCount([string]$pattern) {
    Refresh-LogFile
    $text = Read-LogText
    if ([string]::IsNullOrEmpty($text)) { return 0 }
    return ([regex]::Matches($text, [regex]::Escape($pattern))).Count
}

$script:minimized = New-Object System.Collections.ArrayList

function Get-DesktopPoint([int]$x, [int]$y) {
    $point = New-Object P3Win+POINT
    $point.X = $x
    $point.Y = $y
    $hit = [P3Win]::WindowFromPoint($point)
    $root = [P3Win]::RootOf($hit)
    return @{ Class = [P3Win]::ClassOf($root); Root = $root; Hit = $hit }
}

# The desktop layer must be reachable for any of the pointer checks to mean anything. Whatever
# covers it gets minimised (never our own windows, never the desktop or its taskbar) and put back
# at the end of the run.
function Clear-TheDesktop {
    Write-Host 'Clearing the desktop: minimising the windows that cover the canvas ...'
    $desktopClasses = @('Progman', 'WorkerW', 'SHELLDLL_DefView')
    $keepClasses = @('Shell_TrayWnd', 'Shell_SecondaryTrayWnd', 'Progman', 'WorkerW', 'SHELLDLL_DefView', 'Windows.UI.Core.CoreWindow')
    $points = @()
    foreach ($y in @(380, 500)) {
        for ($x = 1015; $x -le 1615; $x += 60) { $points += , @($x, $y) }
    }
    $points += , @(1215, 300)
    $points += , @(48, 620)
    $points += , @(1700, 900)
    $points += , @(700, 1200)

    $ownId = 0
    if ($null -ne $script:process) { $ownId = $script:process.Id }

    for ($round = 1; $round -le 12; $round++) {
        $offenders = @{}
        foreach ($probe in $points) {
            $found = Get-DesktopPoint $probe[0] $probe[1]
            if ($desktopClasses -contains $found.Class) { continue }
            if ($keepClasses -contains $found.Class) { continue }
            if ([P3Win]::ProcessOf($found.Root) -eq $ownId) { continue }
            if (-not $offenders.ContainsKey($found.Root)) { $offenders[$found.Root] = $found.Class }
        }
        if ($offenders.Count -eq 0) { break }

        foreach ($handle in $offenders.Keys) {
            [P3Win]::ShowWindow($handle, 7) | Out-Null   # SW_SHOWMINNOACTIVE: no focus stealing
            [void]$script:minimized.Add($handle)
            Write-Host ("  minimised {0} [{1}]" -f [P3Win]::ClassOf($handle), [P3Win]::TitleOf($handle))
        }
        Start-Sleep -Milliseconds 600
    }

    $blocked = @()
    foreach ($probe in $points) {
        $found = Get-DesktopPoint $probe[0] $probe[1]
        if (-not ($desktopClasses -contains $found.Class)) {
            $blocked += ("({0},{1}) -> {2}" -f $probe[0], $probe[1], $found.Class)
        }
    }
    return $blocked
}

function Restore-MinimizedWindows {
    if ($script:minimized.Count -eq 0) { return }
    Write-Host 'Restoring the windows the harness minimised ...'
    foreach ($handle in $script:minimized) {
        try {
            if ([P3Win]::IsWindow($handle)) { [P3Win]::ShowWindow($handle, [P3Win]::SW_RESTORE) | Out-Null }
        } catch {
            Write-Host ("  could not restore one window: {0}" -f $_)
        }
    }
    $script:minimized.Clear()
}

function Wait-Until([scriptblock]$predicate, [int]$timeoutSeconds, [string]$what) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (& $predicate) { return $true }
        Start-Sleep -Milliseconds 250
    }
    Write-Host ("  (timed out waiting for {0})" -f $what)
    return $false
}

function Wait-LogCountGrew([string]$pattern, [long]$base, [int]$timeoutSeconds, [string]$what) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ((Get-LogCount $pattern) -gt $base) { return $true }
        Start-Sleep -Milliseconds 300
    }
    Write-Host ("  (timed out waiting for {0} to appear in the log)" -f $what)
    return $false
}

function Move-Pointer([int]$x, [int]$y) {
    [P3Win]::MoveTo($x, $y)
}

function Get-CursorNow {
    $point = New-Object P3Win+POINT
    [void][P3Win]::GetCursorPos([ref]$point)
    return @($point.X, $point.Y)
}

function Move-And-Settle([int]$x, [int]$y, [int]$settleMs) {
    Move-Pointer $x $y
    Start-Sleep -Milliseconds $settleMs
}

function Click-Pointer {
    [P3Win]::LeftDown()
    Start-Sleep -Milliseconds 30
    [P3Win]::LeftUp()
}

function Measure-GpuOnce([int]$processId) {
    try {
        $sample = Get-Counter -Counter '\GPU Engine(*)\Utilization Percentage' -MaxSamples 1 -ErrorAction Stop
        $values = @()
        foreach ($entry in $sample.CounterSamples) {
            if ($entry.InstanceName -like "*pid_$processId*") { $values += $entry.CookedValue }
        }
        if ($values.Count -eq 0) { return 0 }
        return ($values | Measure-Object -Maximum).Maximum
    } catch {
        return $null
    }
}

# ---------------------------------------------------------------- UI Automation

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Find-ByName($root, [string]$name, $scope) {
    if ($null -eq $scope) { $scope = [System.Windows.Automation.TreeScope]::Descendants }
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $root.FindFirst($scope, $condition)
}

function Get-DiagElement($root) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    for ($i = 0; $i -lt $all.Count; $i++) {
        $element = $all.Item($i)
        try {
            $name = $element.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
        } catch { continue }
        if ($name -is [string] -and $name -match '^surface' -and $name -match 'mount') { return $element }
    }
    return $null
}

function Initialize-Diagnostics {
    Write-Host 'Navigating to the dynamic wallpaper page and opening the diagnostics panel ...'
    $window = [P3Win]::FindWindowByClass([int]$script:process.Id, 'WinUIDesktopWin32WindowClass')
    if ($window -eq [IntPtr]::Zero) { throw 'The Muralis main window was not found.' }
    $script:window = $window

    $root = $null
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try { $root = [System.Windows.Automation.AutomationElement]::FromHandle($window); break } catch { Start-Sleep -Milliseconds 250 }
    }
    if ($null -eq $root) { throw 'The Muralis window never became reachable through UI Automation.' }

    [P3Win]::ShowWindow($window, [P3Win]::SW_RESTORE) | Out-Null
    [P3Win]::SetForegroundWindow($window) | Out-Null
    Start-Sleep -Milliseconds 400

    $nav = Find-ByName $root $navDynamic $null
    if ($null -eq $nav) { throw "The navigation item '$navDynamic' was not found." }
    $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 900

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($window)
    $expander = $null
    $named = Find-ByName $root $diagTitle $null
    if ($null -ne $named) {
        try {
            $named.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
            $expander = $named
        } catch {
            $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
            $cursor = $named
            while ($null -ne $cursor) {
                try {
                    $cursor.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
                    $expander = $cursor
                    break
                } catch { $cursor = $walker.GetParent($cursor) }
            }
        }
    }
    if ($null -eq $expander) { throw 'The diagnostics expander was not found.' }
    $script:diagExpander = $expander
    Start-Sleep -Milliseconds 600

    $script:diagElement = Get-DiagElement $root
    if ($null -eq $script:diagElement) { throw 'The diagnostics text was not found after expanding the panel.' }

    # Minimising keeps the desktop clear for the sweeps; if the panel stops answering, the window
    # goes to a corner instead so the reads keep working.
    [P3Win]::ShowWindow($window, [P3Win]::SW_MINIMIZE) | Out-Null
    Start-Sleep -Milliseconds 500
}

function Read-Diag {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            $text = $script:diagElement.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
            if ($text -is [string] -and $text.Length -gt 0) { return $text }
        } catch {
            # The element went stale: resolve it again.
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($script:window)
            $script:diagElement = Get-DiagElement $root
        }
        Start-Sleep -Milliseconds 60
    }

    if ($script:diagMode -eq 'minimize') {
        Write-Host '  (diagnostics went quiet while minimised; switching to corner mode)'
        $script:diagMode = 'corner'
        [P3Win]::ShowWindow($script:window, [P3Win]::SW_RESTORE) | Out-Null
        [P3Win]::MoveWindow($script:window, 1750, 900, 780, 500, $true) | Out-Null
        Start-Sleep -Milliseconds 600
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($script:window)
        $script:diagElement = Get-DiagElement $root
    }

    return ''
}

function Parse-Diag([string]$text) {
    $state = @{
        Inside = $null; Px = $null; Py = $null; HoverId = $null; HoverScale = $null
        DockPhase = $null; Context = $null; Rate = $null; Reports = $null
        Dispatches = $null; Updates = $null; Mount = $null
    }
    if ([string]::IsNullOrEmpty($text)) { return $state }

    if ($text -match 'pointer\s+(inside|outside)\s+(-?[0-9.,]+),\s*(-?[0-9.,]+)') {
        $state.Inside = ($Matches[1] -eq 'inside')
        $state.Px = [double]($Matches[2] -replace ',', '')
        $state.Py = [double]($Matches[3] -replace ',', '')
    }
    if ($text -match 'hovered\s+(\S+)\s+at\s+([0-9.]+)x') { $state.HoverId = $Matches[1]; $state.HoverScale = [double]$Matches[2] }
    if ($text -match 'dock\s+(\w+)\s+at\s+([0-9.]+)x') { $state.DockPhase = $Matches[1]; $state.DockScale = [double]$Matches[2] }
    if ($text -match 'router\s+(\w+)\s+.\s+([0-9.]+)/s\s+.\s+(\d+)\s+reports\s+.\s+(\d+)\s+dispatches') {
        $state.Context = $Matches[1]; $state.Rate = [double]$Matches[2]
        $state.Reports = [long]$Matches[3]; $state.Dispatches = [long]$Matches[4]
    }
    if ($text -match 'updates\s+([0-9.]+)/s\s+.\s+(\d+)\s+total') { $state.Updates = [long]$Matches[2] }
    if ($text -match 'mount\s+(\d+)') { $state.Mount = [int]$Matches[1] }
    return $state
}

function Read-State {
    $text = Read-Diag
    $state = Parse-Diag $text
    $state.Text = $text
    return $state
}

# The panel rewrites its text on a 250 ms timer, so a reading taken straight after a move can still
# describe the previous position. These two wait for the panel to catch up instead of guessing a
# sleep: give up after the timeout and return whatever the panel last said.
function Read-StateUntil([scriptblock]$predicate, [int]$timeoutMs = 1500) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    $state = Read-State
    while ((Get-Date) -lt $deadline) {
        if (& $predicate $state) { return $state }
        Start-Sleep -Milliseconds 60
        $state = Read-State
    }
    return $state
}

function Read-StateAt([int]$x, [int]$y, [int]$timeoutMs = 1500) {
    return Read-StateUntil { param($s) $null -ne $s.Px -and [math]::Abs($s.Px - $x) -le 1.5 -and [math]::Abs($s.Py - $y) -le 1.5 } $timeoutMs
}

# ---------------------------------------------------------------- probe

function Invoke-Probe {
    Write-Host '=== Phase 3A probe ==='
    $running = Get-Process -Name Muralis -ErrorAction SilentlyContinue
    Write-Host ("Muralis running: {0}" -f [bool]$running)
    Write-Host ("Screen: {0}x{1} at {2},{3}" -f `
        [P3Win]::GetSystemMetrics(0), [P3Win]::GetSystemMetrics(1),
        [P3Win]::GetSystemMetrics([P3Win]::SM_XVIRTUALSCREEN), [P3Win]::GetSystemMetrics([P3Win]::SM_YVIRTUALSCREEN))

    if (Test-Path $settingsPath) {
        $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
        $canvasProperty = $settings.PSObject.Properties['DesktopCanvas']
        if ($null -eq $canvasProperty) { Write-Host 'settings.json: no DesktopCanvas key' }
        else { Write-Host ("settings.json: DesktopCanvas.Enabled = {0}" -f $canvasProperty.Value.Enabled) }
        Write-Host ("settings.json: CloseToTray = {0}" -f $settings.CloseToTray)
    }
    Write-Host ("layout file: {0}" -f (Test-Path $layoutPath))

    $probes = @(
        @{ Name = 'row item centre'; X = 1215; Y = 500 },
        @{ Name = 'off-row (outside region)'; X = 1215; Y = 380 },
        @{ Name = 'grown box (inside region)'; X = 1280; Y = 500 },
        @{ Name = 'right of the region'; X = 1615; Y = 500 },
        @{ Name = 'dock trigger'; X = 5; Y = 720 },
        @{ Name = 'taskbar'; X = 1280; Y = 1435 },
        @{ Name = 'blank 1'; X = 1700; Y = 900 },
        @{ Name = 'blank 2'; X = 700; Y = 1200 }
    )

    Write-Host ''
    Write-Host 'Point probes (WindowFromPoint) :'
    foreach ($probe in $probes) {
        $point = New-Object P3Win+POINT
        $point.X = $probe.X
        $point.Y = $probe.Y
        $hit = [P3Win]::WindowFromPoint($point)
        $root = [P3Win]::RootOf($hit)
        $processName = '?'
        $processId = [P3Win]::ProcessOf($root)
        if ($processId -gt 0) {
            try { $processName = (Get-Process -Id $processId).ProcessName } catch { $processName = '?' }
        }
        Write-Host ("  {0,-28} ({1},{2}) -> {3} | root {4} ({5}, pid {6})" -f `
            $probe.Name, $probe.X, $probe.Y, [P3Win]::Describe($hit), [P3Win]::ClassOf($root), $processName, $processId)
    }

    Write-Host ''
    Write-Host 'Visible windows over the canvas area (x 980..1580, y 360..900) and the dock strip (x 0..140, y 540..900):'
    $areas = @(
        @{ Name = 'canvas row'; Left = 980; Top = 360; Right = 1580; Bottom = 900 },
        @{ Name = 'dock strip'; Left = 0; Top = 540; Right = 140; Bottom = 900 }
    )
    foreach ($window in [P3Win]::VisibleTopLevel(60, 40)) {
        $rect = [P3Win]::RectOf($window)
        foreach ($area in $areas) {
            $overlaps = ($rect[0] -lt $area.Right) -and (($rect[0] + $rect[2]) -gt $area.Left) -and
                        ($rect[1] -lt $area.Bottom) -and (($rect[1] + $rect[3]) -gt $area.Top)
            if ($overlaps) {
                $processId = [P3Win]::ProcessOf($window)
                $processName = '?'
                try { $processName = (Get-Process -Id $processId).ProcessName } catch { $processName = '?' }
                Write-Host ("  [{0}] {1} at {2},{3} {4}x{5} ({6}, pid {7}) | {8}" -f `
                    $area.Name, [P3Win]::ClassOf($window), $rect[0], $rect[1], $rect[2], $rect[3], $processName, $processId, [P3Win]::TitleOf($window))
            }
        }
    }

    Write-Host ''
    Write-Host ("Router windows now: {0}" -f (([P3Win]::ClassesWithPrefix('MuralisPointerRouter_')).Count))
    Write-Host ("Canvas host windows now: {0}" -f (([P3Win]::ClassesWithPrefix('MuralisDesktopHostWindow')).Count))
}

# ---------------------------------------------------------------- latency stage

# Moves the pointer to one point and watches how long the diagnostics text takes to reflect it,
# printing every distinct text with its timestamp. Answers the one question the checks depend on:
# how fresh is a UI Automation read of the panel, and does a minimised window update it at all.
function Watch-Point([int]$x, [int]$y, [int]$timeoutSeconds) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    Move-Pointer $x $y
    $cursor = Get-CursorNow
    $under = Get-DesktopPoint $x $y
    Write-Host ("  -> ({0},{1}); cursor now ({2},{3}); under: {4} | root {5}" -f `
        $x, $y, $cursor[0], $cursor[1], [P3Win]::Describe($under.Hit), $under.Class)
    $lastText = ''
    $changes = 0
    $settledAt = -1
    while ($watch.Elapsed.TotalSeconds -lt $timeoutSeconds) {
        $text = Read-Diag
        if ($text -ne $lastText) {
            $state = Parse-Diag $text
            $cursor = Get-CursorNow
            Write-Host ("     +{0,5} ms  px={1} py={2} hover={3}@{4} dock={5} ctx={6} cursor=({7},{8}) reps={9} disps={10} upd={11}" -f `
                [int]$watch.Elapsed.TotalMilliseconds, $state.Px, $state.Py, $state.HoverId, $state.HoverScale,
                $state.DockPhase, $state.Context, $cursor[0], $cursor[1], $state.Reports, $state.Dispatches, $state.Updates)
            $changes++
            $lastText = $text
            if ($settledAt -lt 0 -and $null -ne $state.Px -and [math]::Abs($state.Px - $x) -le 1.5) {
                $settledAt = [int]$watch.Elapsed.TotalMilliseconds
            }
        }
        Start-Sleep -Milliseconds 100
    }
    Write-Host ("     text changes: {0}; pointer seen at the target after {1}" -f $changes, `
        $(if ($settledAt -ge 0) { "$settledAt ms" } else { 'never' }))
}

function Invoke-LatencyStage {
    if (Get-Process -Name Muralis -ErrorAction SilentlyContinue) { throw 'Muralis is already running; stop it first.' }
    if (-not (Test-Path $exePath)) { throw "Executable not found: $exePath" }

    Copy-Item -Force $settingsPath $settingsBackup
    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $settings | Add-Member -NotePropertyName DesktopCanvas -NotePropertyValue ([pscustomobject]@{ Enabled = $true }) -Force
    $settings.CloseToTray = $false
    $settings | ConvertTo-Json -Depth 10 | Set-Content -Path $settingsPath -Encoding UTF8
    if (Test-Path $layoutPath) { Move-Item -Force $layoutPath $layoutBackup }

    Write-Host '=== Phase 3A diagnostics latency probe ==='
    $script:process = Start-Process -FilePath $exePath -PassThru
    $processId = [int]$script:process.Id
    [void](Wait-Until { [P3Win]::FindWindowByClass($processId, 'WinUIDesktopWin32WindowClass') -ne [IntPtr]::Zero } 60 'the main window')
    [void](Wait-Until { ([P3Win]::ClassesWithPrefix('MuralisPointerRouter_')).Count -ge 1 } 60 'the router attach')
    Start-Sleep -Milliseconds 800
    Initialize-Diagnostics
    $blocked = Clear-TheDesktop
    if ($blocked.Count -gt 0) { Write-Host ("  (still covered: {0})" -f ($blocked -join '; ')) }

    Write-Host ''
    Write-Host '--- window minimised ---'
    Watch-Point 1215 500 6
    Watch-Point 1215 380 6
    Watch-Point 1475 500 6
    Watch-Point 700 1200 6

    Write-Host ''
    Write-Host '--- window visible in the corner ---'
    [P3Win]::ShowWindow($script:window, [P3Win]::SW_RESTORE) | Out-Null
    [P3Win]::MoveWindow($script:window, 1750, 900, 780, 500, $true) | Out-Null
    Start-Sleep -Milliseconds 800
    $script:diagMode = 'corner'
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($script:window)
    $script:diagElement = Get-DiagElement $root
    if ($null -eq $script:diagElement) { throw 'The diagnostics text was lost when the window came back.' }
    Watch-Point 1215 500 6
    Watch-Point 1215 440 6
    Watch-Point 1215 380 6
    Watch-Point 1215 300 6
    Watch-Point 1475 500 6
    Watch-Point 700 1200 6
}

# ---------------------------------------------------------------- full run

function Restore-Everything {
    Write-Host ''
    Write-Host 'Restoring settings and layout ...'
    Restore-MinimizedWindows
    Stop-Process -Name Muralis -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    if (Test-Path $settingsBackup) {
        Move-Item -Force $settingsBackup $settingsPath
    }
    if (Test-Path $layoutBackup) {
        Move-Item -Force $layoutBackup $layoutPath
    } elseif (Test-Path $layoutPath) {
        Remove-Item -Force $layoutPath
    }
}

function Invoke-Full {
    if (Get-Process -Name Muralis -ErrorAction SilentlyContinue) { throw 'Muralis is already running; stop it first.' }
    if (-not (Test-Path $exePath)) { throw "Executable not found: $exePath" }
    New-Item -ItemType Directory -Force -Path $outPath | Out-Null

    # --- prepare: back up settings, enable the canvas, park any saved layout so the seed geometry
    #     (which every expectation below is computed from) is what actually shows.
    Copy-Item -Force $settingsPath $settingsBackup
    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $settings | Add-Member -NotePropertyName DesktopCanvas -NotePropertyValue ([pscustomobject]@{ Enabled = $true }) -Force
    $settings.CloseToTray = $false   # so the graceful-exit check can close the window for real
    $settings | ConvertTo-Json -Depth 10 | Set-Content -Path $settingsPath -Encoding UTF8
    if (Test-Path $layoutPath) { Move-Item -Force $layoutPath $layoutBackup }

    Write-Host ("Launching {0}" -f $exePath)
    $logBase = Get-LogCounts
    $script:process = Start-Process -FilePath $exePath -PassThru
    $processId = [int]$script:process.Id

    # Direct observables rather than log lines: the on-disk log lags while the app runs.
    $windowUp = Wait-Until { [P3Win]::FindWindowByClass($processId, 'WinUIDesktopWin32WindowClass') -ne [IntPtr]::Zero } 60 'the main window'
    $canvasUp = Wait-Until { ([P3Win]::ClassesWithPrefix('MuralisDesktopHostWindow')).Count -ge 1 } 60 'the canvas mount'
    $routerUp = Wait-Until { ([P3Win]::ClassesWithPrefix('MuralisPointerRouter_')).Count -ge 1 } 60 'the router attach'
    Add-Check 'startup: the main window appears' $windowUp ''
    Add-Check 'startup: the canvas mounts' $canvasUp ''
    Add-Check 'startup: the router attaches' $routerUp ''

    # Never minimise before the first frame: the app crashes if the window goes away that early.
    Start-Sleep -Milliseconds 800
    Initialize-Diagnostics
    Start-Sleep -Milliseconds 400

    $routerWindows = [P3Win]::ClassesWithPrefix('MuralisPointerRouter_')
    Add-Check 'one router window for the whole desktop' ($routerWindows.Count -eq 1) ("count {0}" -f $routerWindows.Count)

    # --- the desktop has to be reachable before any pointer check means anything
    $blocked = Clear-TheDesktop
    Add-Check 'preflight: the canvas area is on the desktop, not under an application' ($blocked.Count -eq 0) ("blocked points: {0}" -f ($blocked -join '; '))
    if ($blocked.Count -gt 0) { throw "The desktop is still covered: $($blocked -join '; ')" }

    # --- preflight: the desktop must be visible where the canvas sits. The *root* class counts:
    # WindowFromPoint over the desktop lands on the icon list view (SysListView32), whose root is
    # the desktop window.
    $desktopHit = Get-DesktopPoint $itemCentresX[1] $offRowY
    $clear = $desktopHit.Class -in @('SHELLDLL_DefView', 'WorkerW', 'Progman')
    Add-Check 'preflight: the off-row probe point is desktop, not a window' $clear ("root class '{0}'" -f $desktopHit.Class)
    if (-not $clear) { throw "Something covers the canvas (root class '$($desktopHit.Class)'); nothing can be swept." }

    $state = Read-State
    Write-Host ''
    Write-Host '--- diagnostics baseline ---'
    Write-Host $state.Text
    Write-Host ''

    $sweep = @()

    # --- 1: sweep along the row, inside the region
    Write-Host 'Test 1: sweep along the row (y=500, inside the window region)'
    # The first sample has no predecessor in the sweep: continuity is between consecutive samples.
    $previousScale = $null
    $maxJump = 0.0
    $minScale = 9.0
    $rowOk = $true
    for ($x = 1015; $x -le 1555; $x += 20) {
        Move-Pointer $x $rowY
        $state = Read-StateAt $x $rowY
        $scale = 1.0
        if ($null -ne $state.HoverScale) { $scale = $state.HoverScale }
        if ($null -ne $previousScale) {
            $jump = [math]::Abs($scale - $previousScale)
            if ($jump -gt $maxJump) { $maxJump = $jump }
        }
        if ($scale -lt $minScale) { $minScale = $scale }
        if (-not $state.Inside) { $rowOk = $false }
        $sweep += [pscustomobject]@{ Phase = 'row'; X = $x; Y = $rowY; Scale = $scale; Item = $state.HoverId; Context = $state.Context; Inside = $state.Inside }
        $previousScale = $scale
    }
    Add-Check 'row sweep: the canvas sees the pointer at every step' $rowOk ''
    Add-Check 'row sweep: hover never falls back to rest' ($minScale -gt 1.05) ("min scale {0:0.00}" -f $minScale)
    Add-Check 'row sweep: scale stays continuous (step jump <= 0.12)' ($maxJump -le 0.12) ("max jump {0:0.000}" -f $maxJump)

    # --- 2: sweep 120 DIP above the row: outside the window region, inside the influence radius
    Write-Host 'Test 2: sweep above the row (y=380, outside the window region)'
    $offRowScales = @()
    $offRowContexts = @()
    for ($x = 1015; $x -le 1555; $x += 20) {
        Move-Pointer $x $offRowY
        $state = Read-StateAt $x $offRowY
        $scale = 0.0
        if ($null -ne $state.HoverScale) { $scale = $state.HoverScale }
        $offRowScales += $scale
        $offRowContexts += $state.Context
        $sweep += [pscustomobject]@{ Phase = 'offrow'; X = $x; Y = $offRowY; Scale = $scale; Item = $state.HoverId; Context = $state.Context; Inside = $state.Inside }
    }
    $offRowMax = ($offRowScales | Measure-Object -Maximum).Maximum
    $overCentre = $offRowScales[[int](($itemCentresX[1] - 1015) / 20)]
    $desktopContexts = ($offRowContexts | Where-Object { $_ -eq 'Desktop' }).Count
    Add-Check 'off-row sweep: the pointer is read beyond the region' ($desktopContexts -gt 0) ("$desktopContexts of $($offRowContexts.Count) samples report the desktop context")
    Add-Check 'off-row sweep: items still magnify (max 1.21x expected)' ($offRowMax -gt 1.10 -and $offRowMax -lt 1.35) ("max scale {0:0.00}" -f $offRowMax)
    Add-Check 'off-row sweep: value above the item centre is the expected 1.21x' ([math]::Abs($overCentre - $offRowScale) -le 0.06) ("{0:0.00} vs 1.21" -f $overCentre)

    $samples['sweep'] = $sweep

    # --- 3: decay back to rest
    Write-Host 'Test 3: decay, pointer leaves the canvas'
    $atRest = { param($s) ($null -eq $s.HoverId -or $s.HoverId -eq 'none') -and ($null -eq $s.HoverScale -or $s.HoverScale -le 1.001) }
    $decayScales = @()
    foreach ($blank in $blankProbes) {
        Move-Pointer $blank[0] $blank[1]
        $state = Read-StateUntil $atRest 2500
        $decayScales += [pscustomobject]@{ X = $blank[0]; Y = $blank[1]; Item = $state.HoverId; Scale = $state.HoverScale; Dock = $state.DockPhase }
    }
    # and on the way out of the row itself: straight up, 200 DIP above the item centres, one
    # influence radius away from the row, so a correct decay ends with nothing hovered
    Move-Pointer 1215 300
    $state = Read-StateUntil $atRest 2500
    $decayScales += [pscustomobject]@{ X = 1215; Y = 300; Item = $state.HoverId; Scale = $state.HoverScale; Dock = $state.DockPhase }
    $allRest = $true
    foreach ($sample in $decayScales) {
        if ($sample.Item -ne 'none' -or $null -eq $sample.Scale -or $sample.Scale -gt 1.001) { $allRest = $false }
    }
    $last = $decayScales[$decayScales.Count - 1]
    Add-Check 'decay: everything returns to rest away from the canvas' $allRest ("last hovered {0} at {1:0.00}x" -f $last.Item, $last.Scale)
    $samples['decay'] = $decayScales

    # --- 4: dock trigger band. The log lines "The desktop dock expanded/retracted" are checked
    #     after the exit, when the log has been flushed; here the diagnostics phase is the evidence.
    Write-Host 'Test 4: dock trigger band and auto-hide'
    Move-Pointer $dockProbe[0] $dockProbe[1]
    $state = Read-StateUntil { param($s) $s.DockPhase -eq 'Shown' } 2500
    $dockShown = ($state.DockPhase -eq 'Shown')
    Add-Check 'dock: the trigger band summons the rail' $dockShown ("phase '{0}'" -f $state.DockPhase)

    Move-Pointer 700 720
    $state = Read-StateUntil { param($s) $s.DockPhase -eq 'Collapsed' } 3000
    Add-Check 'dock: the rail retracts after the pointer leaves' ($state.DockPhase -eq 'Collapsed') ("phase '{0}'" -f $state.DockPhase)

    # --- 5: an ordinary application window and the taskbar stop the desktop
    Write-Host 'Test 5: ordinary application windows'
    Move-Pointer $itemCentresX[1] $offRowY
    $state = Read-StateUntil { param($s) $s.Context -eq 'Desktop' -and $s.HoverScale -gt 1.1 } 2500
    $beforeHover = $state.HoverId
    Add-Check 'foreign: the desktop magnifies before the window comes up' ($null -ne $beforeHover -and $beforeHover -ne 'none' -and $state.HoverScale -gt 1.1) ("hovered {0} at {1:0.00}x" -f $state.HoverId, $state.HoverScale)

    [P3Win]::ShowWindow($script:window, [P3Win]::SW_RESTORE) | Out-Null
    [P3Win]::SetForegroundWindow($script:window) | Out-Null
    Start-Sleep -Milliseconds 700
    $rect = [P3Win]::RectOf($script:window)
    Write-Host ("  main window at {0},{1} {2}x{3}" -f $rect[0], $rect[1], $rect[2], $rect[3])
    $foreignProbe = @($itemCentresX[1], $rowY)
    if ($rect[0] -gt $foreignProbe[0] -or ($rect[0] + $rect[2]) -lt $foreignProbe[0] -or
        $rect[1] -gt $foreignProbe[1] -or ($rect[1] + $rect[3]) -lt $foreignProbe[1]) {
        $foreignProbe = @(($rect[0] + [int]($rect[2] / 2)), ($rect[1] + 40))
        Write-Host ("  (the window does not cover the item; probing its own area at {0},{1})" -f $foreignProbe[0], $foreignProbe[1])
    }
    Move-Pointer $foreignProbe[0] $foreignProbe[1]
    $state = Read-StateUntil { param($s) $s.Context -eq 'Foreign' -and ($null -eq $s.HoverId -or $s.HoverId -eq 'none') } 2500
    $context = $state.Context
    $hoverId = $state.HoverId
    $hoverScale = $state.HoverScale
    Add-Check 'foreign: an application window puts the router out of desktop context' ($context -eq 'Foreign') ("context '$context'")
    Add-Check 'foreign: the canvas stops reacting under an application window' (($null -eq $hoverId -or $hoverId -eq 'none') -and ($null -eq $hoverScale -or $hoverScale -le 1.001)) ("hovered {0} at {1:0.00}x" -f $hoverId, $hoverScale)

    $point = New-Object P3Win+POINT
    $point.X = $foreignProbe[0]; $point.Y = $foreignProbe[1]
    $under = [P3Win]::ClassOf([P3Win]::RootOf([P3Win]::WindowFromPoint($point)))
    Add-Check 'foreign: the probe point really is over the application window' ($under -eq 'WinUIDesktopWin32WindowClass') ("root class '$under'")

    [P3Win]::ShowWindow($script:window, [P3Win]::SW_MINIMIZE) | Out-Null
    Start-Sleep -Milliseconds 500

    # the taskbar hangs off the desktop window but is not the desktop
    Move-Pointer $itemCentresX[1] $offRowY
    $state = Read-StateUntil { param($s) $s.Context -eq 'Desktop' -and $s.HoverScale -gt 1.1 } 2500
    $restored = ($state.Context -eq 'Desktop' -and $state.HoverScale -gt 1.1)
    Add-Check 'foreign: the desktop reacts again once the pointer returns' $restored ("context '{0}', hovered {1} at {2:0.00}x" -f $state.Context, $state.HoverId, $state.HoverScale)

    Move-Pointer $taskbarProbe[0] $taskbarProbe[1]
    $state = Read-StateUntil { param($s) $s.Context -eq 'Foreign' } 2500
    Add-Check 'foreign: the taskbar is not the desktop' ($state.Context -eq 'Foreign') ("context '{0}'" -f $state.Context)
    Add-Check 'foreign: the canvas clears over the taskbar' (($null -eq $state.HoverId -or $state.HoverId -eq 'none') -and $state.HoverScale -le 1.001) ("hovered {0} at {1:0.00}x" -f $state.HoverId, $state.HoverScale)

    # --- 6: hit testing against the visual scale. Evidence here is file system observable: a drag
    #     inside the region commits the drop and writes the layout; a press outside the region
    #     cannot reach the canvas at all. The log lines are checked after the exit.
    Write-Host 'Test 6: hit testing, region and the grown item box'
    $outsideHit = Get-DesktopPoint $outsideProbe[0] $outsideProbe[1]
    Add-Check 'hit test: outside the region the desktop is under the point' ($outsideHit.Class -in @('SHELLDLL_DefView', 'WorkerW', 'Progman')) ("root class '{0}' at {1},{2}" -f $outsideHit.Class, $outsideProbe[0], $outsideProbe[1])

    $layoutBefore = Test-Path $layoutPath
    Move-And-Settle $growProbe[0] $growProbe[1] 450
    [P3Win]::LeftDown()
    Start-Sleep -Milliseconds 40
    for ($step = 1; $step -le 8; $step++) {
        Move-Pointer ($growProbe[0] + $step * 6) $growProbe[1]
        Start-Sleep -Milliseconds 15
    }
    Start-Sleep -Milliseconds 60
    [P3Win]::LeftUp()
    Start-Sleep -Milliseconds 500
    $layoutWritten = Test-Path $layoutPath
    Add-Check 'hit test: the grown box receives the drag (the drop commits the layout)' ($layoutWritten -and -not $layoutBefore) ("layout before: {0}, after: {1}" -f $layoutBefore, $layoutWritten)
    $hashAfterDrag = ''
    if ($layoutWritten) { $hashAfterDrag = (Get-FileHash -Algorithm SHA256 $layoutPath).Hash }

    # A press outside the region is not delivered to the canvas: no click, no drag, no commit.
    Move-And-Settle $outsideProbe[0] $outsideProbe[1] 120
    Click-Pointer
    Start-Sleep -Milliseconds 400
    $hashAfterOutside = ''
    if (Test-Path $layoutPath) { $hashAfterOutside = (Get-FileHash -Algorithm SHA256 $layoutPath).Hash }
    Add-Check 'hit test: a press outside the region changes nothing' ($hashAfterOutside -eq $hashAfterDrag) ''

    # --- 7: 30 s of continuous fast movement
    Write-Host ("Test 7: {0}s of continuous fast movement" -f $HoldSeconds)
    $points = @(
        @(1005, 500), @(1215, 500), @(1475, 500), @(1555, 500), @(1215, 380), @(1085, 380),
        @(215, 380), @(1215, 500), @(1300, 460), @(700, 1200), @(1280, 1435), @(5, 720),
        @(48, 620), @(1700, 900), @(1215, 500)
    )
    $cpuStart = $script:process.TotalProcessorTime
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $gpuValues = @()
    $rateValues = @()
    $reportsSeen = @()
    $dispatchSeen = @()
    $index = 0
    $gpuTick = 0
    $nextSampleAt = 2.5
    while ($watch.Elapsed.TotalSeconds -lt $HoldSeconds) {
        $target = $points[$index % $points.Count]
        $index++
        Move-Pointer $target[0] $target[1]
        Start-Sleep -Milliseconds 4
        # Read on a wall-clock cadence: the diagnostics panel itself writes every 250 ms, so a
        # reading taken faster than that would only repeat the previous sample.
        if ($watch.Elapsed.TotalSeconds -ge $nextSampleAt) {
            $nextSampleAt += 2.5
            $state = Read-State
            if ($null -ne $state.Rate) { $rateValues += $state.Rate }
            if ($null -ne $state.Reports) { $reportsSeen += $state.Reports }
            if ($null -ne $state.Dispatches) { $dispatchSeen += $state.Dispatches }
            $gpuTick++
            if (($gpuTick % 5) -eq 1) {
                $gpu = Measure-GpuOnce $script:process.Id
                if ($null -ne $gpu) { $gpuValues += $gpu }
            }
        }
    }
    $watch.Stop()
    $finalSample = Read-State
    if ($null -ne $finalSample.Reports) { $reportsSeen += $finalSample.Reports }
    if ($null -ne $finalSample.Dispatches) { $dispatchSeen += $finalSample.Dispatches }
    $cpuUsed = ($script:process.TotalProcessorTime - $cpuStart).TotalMilliseconds
    $moves = $index
    $cpuCount = [Environment]::ProcessorCount

    $state = Read-State
    $alive = -not $script:process.HasExited
    Add-Check 'flood: the app is alive after the movement' $alive ''
    Add-Check 'flood: still one mount, one router window' ((($state.Mount) -eq 1) -and (([P3Win]::ClassesWithPrefix('MuralisPointerRouter_')).Count -eq 1)) ("mount {0}" -f $state.Mount)
    $maxRate = 0.0
    if ($rateValues.Count -gt 0) { $maxRate = ($rateValues | Measure-Object -Maximum).Maximum }
    $avgRate = 0.0
    if ($rateValues.Count -gt 0) { $avgRate = ($rateValues | Measure-Object -Average).Average }
    Add-Check 'flood: the dispatch rate stays under the coalescing cap (<=170/s)' ($maxRate -le 170) ("avg {0:0.0}/s, max {1:0.0}/s" -f $avgRate, $maxRate)
    $reportGrowth = 0
    if ($reportsSeen.Count -ge 2) { $reportGrowth = $reportsSeen[$reportsSeen.Count - 1] - $reportsSeen[0] }
    $dispatchGrowth = 0
    if ($dispatchSeen.Count -ge 2) { $dispatchGrowth = $dispatchSeen[$dispatchSeen.Count - 1] - $dispatchSeen[0] }
    Add-Check 'flood: reports arrive and are coalesced' ($reportGrowth -gt 0 -and $dispatchGrowth -gt 0 -and $dispatchGrowth -le $reportGrowth) ("{0} reports -> {1} dispatches in the window" -f $reportGrowth, $dispatchGrowth)

    $cpuPerSecond = $cpuUsed / $watch.Elapsed.TotalSeconds
    $cpuPercent = $cpuPerSecond / 1000 / $cpuCount * 100
    $gpuMax = 0
    if ($gpuValues.Count -gt 0) { $gpuMax = ($gpuValues | Measure-Object -Maximum).Maximum }
    $movesPerSecond = $moves / $watch.Elapsed.TotalSeconds
    Write-Host ("  movement: {0} moves over {1:0.0}s ({2:0.0} moves/s)" -f $moves, $watch.Elapsed.TotalSeconds, $movesPerSecond)
    Write-Host ("  CPU during movement: {0:0.0} ms total, {1:0.00} ms/s, {2:0.000} % of one core ({3} cores)" -f $cpuUsed, $cpuPerSecond, $cpuPercent, $cpuCount)
    Write-Host ("  GPU during movement: max {0:0.0} %" -f $gpuMax)
    $samples['flood'] = [pscustomobject]@{
        Seconds = $watch.Elapsed.TotalSeconds
        Moves = $moves
        MovesPerSecond = $movesPerSecond
        CpuMs = $cpuUsed
        CpuMsPerSecond = $cpuPerSecond
        CpuPercentOfOneCore = $cpuPercent
        GpuMaxPercent = $gpuMax
        DispatchRateAverage = $avgRate
        DispatchRateMax = $maxRate
        Reports = $reportGrowth
        Dispatches = $dispatchGrowth
    }

    # --- 8: idle: nothing moves, nothing happens
    Write-Host 'Test 8: idle while the pointer rests on the desktop'
    Move-Pointer 1215 380
    $state = Read-StateAt 1215 380
    $reportsBefore = $state.Reports
    $cpuStart = $script:process.TotalProcessorTime
    $idleWatch = [System.Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds 5
    $idleWatch.Stop()
    $cpuIdle = ($script:process.TotalProcessorTime - $cpuStart).TotalMilliseconds
    $state = Read-State
    $reportsGrew = $state.Reports - $reportsBefore
    Add-Check 'idle: no reports while the pointer rests' ($reportsGrew -eq 0) ("{0} reports in 5 s" -f $reportsGrew)
    Write-Host ("  CPU while idle, diagnostics panel open: {0:0.0} ms over {1:0.0}s" -f $cpuIdle, $idleWatch.Elapsed.TotalSeconds)
    $samples['idle'] = [pscustomobject]@{
        Seconds = $idleWatch.Elapsed.TotalSeconds
        CpuMs = $cpuIdle
        CpuMsPerSecond = $cpuIdle / $idleWatch.Elapsed.TotalSeconds
        Reports = $reportsGrew
        DispatchesPerSecond = $state.Rate
        UpdatesTotal = $state.Updates
    }

    # and again with the panel closed: that is the app at rest with no development tooling on top
    try {
        $script:diagExpander.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
        Start-Sleep -Milliseconds 800
        $cpuStart = $script:process.TotalProcessorTime
        $quietWatch = [System.Diagnostics.Stopwatch]::StartNew()
        Start-Sleep -Seconds 5
        $quietWatch.Stop()
        $cpuQuiet = ($script:process.TotalProcessorTime - $cpuStart).TotalMilliseconds
        Write-Host ("  CPU while idle, diagnostics panel closed: {0:0.0} ms over {1:0.0}s" -f $cpuQuiet, $quietWatch.Elapsed.TotalSeconds)
        $samples['idleQuiet'] = [pscustomobject]@{
            Seconds = $quietWatch.Elapsed.TotalSeconds
            CpuMs = $cpuQuiet
            CpuMsPerSecond = $cpuQuiet / $quietWatch.Elapsed.TotalSeconds
        }
        $script:diagExpander.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Start-Sleep -Milliseconds 700
    } catch {
        Write-Host ("  (the panel could not be collapsed for the quiet reading: {0})" -f $_)
    }

    # --- 9: the coalescer never drops the last position
    Write-Host 'Test 9: a burst of reports always lands on its final position'
    $matches = 0
    $repetitions = 40
    $misses = @()
    for ($round = 1; $round -le $repetitions; $round++) {
        $targetX = 1215 + (($round * 37) % 400)
        for ($burst = 0; $burst -lt 40; $burst++) {
            $x = 1005 + (($targetX - 1005) * $burst / 40)
            Move-Pointer $x 380
        }
        Move-Pointer $targetX 380
        $state = Read-StateAt $targetX 380
        if ($null -ne $state.Px -and [math]::Abs($state.Px - $targetX) -le 1.5) { $matches++ }
        else { $misses += ("round {0}: wanted x {1}, the panel shows {2}" -f $round, $targetX, $state.Px) }
    }
    $missDetail = ''
    if ($misses.Count -gt 0) { $missDetail = '; ' + ($misses -join '; ') }
    Add-Check 'coalescing: the final position of a burst is never lost' ($matches -eq $repetitions) ("$matches of $repetitions rounds$missDetail")

    # --- 10: Explorer restart. The shell thread outlives Explorer, so the router must ride the
    #     restart out untouched: the same router window, still exactly one, no re-attach.
    if (-not $SkipExplorerRestart) {
        Write-Host 'Test 10: Explorer restart'
        $mountBefore = (Read-State).Mount
        $routerClassBefore = [P3Win]::ClassesWithPrefix('MuralisPointerRouter_')
        Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue

        $back = $false
        $deadline = (Get-Date).AddSeconds(45)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
            $probe = Get-DesktopPoint 1215 380
            if ($probe.Class -in @('SHELLDLL_DefView', 'WorkerW', 'Progman')) {
                $state = Read-State
                if ($null -ne $state.Mount -and $state.Mount -gt $mountBefore) { $back = $true; break }
            }
        }
        $mountAfter = (Read-State).Mount
        Add-Check 'explorer: the shell comes back and the canvas re-mounts' $back ("mount {0} -> {1}" -f $mountBefore, $mountAfter)

        Start-Sleep -Seconds 1
        $routerClassAfter = [P3Win]::ClassesWithPrefix('MuralisPointerRouter_')
        $sameRouter = ($routerClassAfter.Count -eq 1) -and ($routerClassBefore.Count -eq 1) -and ($routerClassAfter[0] -eq $routerClassBefore[0])
        Add-Check 'explorer: the router is untouched (one window, same window)' $sameRouter `
            ("before '{0}' -> after '{1}'" -f ($routerClassBefore -join ','), ($routerClassAfter -join ','))

        Move-Pointer 1215 380
        $state = Read-StateUntil { param($s) $s.Context -eq 'Desktop' -and $s.HoverScale -gt 1.1 } 3000
        Add-Check 'explorer: hover works again through the router' ($state.HoverScale -gt 1.1) ("hovered {0} at {1:0.00}x" -f $state.HoverId, $state.HoverScale)
    } else {
        Write-Host 'Test 10: Explorer restart skipped'
    }

    # --- 11: exit releases everything
    Write-Host 'Test 11: graceful exit releases the raw input registration'
    $window = [P3Win]::FindWindowByClass([int]$script:process.Id, 'WinUIDesktopWin32WindowClass')
    if ($window -eq [IntPtr]::Zero) { $window = $script:window }
    [P3Win]::PostMessage($window, [P3Win]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    $exited = $script:process.WaitForExit(15000)
    if (-not $exited) {
        Stop-Process -Id $script:process.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 800
    }
    Add-Check 'exit: the app closes on the window close' $exited ''
    $released = Wait-LogCountGrew 'released its raw mouse input registration' $logBase.RouterReleased 8 'the release log line'
    Start-Sleep -Milliseconds 700
    $routerWindows = [P3Win]::ClassesWithPrefix('MuralisPointerRouter_')
    $hostWindows = [P3Win]::ClassesWithPrefix('MuralisDesktopHostWindow')
    Add-Check 'exit: the raw input registration is released' $released ''
    Add-Check 'exit: no router window is left behind' ($routerWindows.Count -eq 0) ("count {0}" -f $routerWindows.Count)
    Add-Check 'exit: no canvas window is left behind' ($hostWindows.Count -eq 0) ("count {0}" -f $hostWindows.Count)

    # --- 12: the run's story, read back from the now-flushed log
    Write-Host 'Test 12: the log after the exit (Serilog flushes on shutdown)'
    $logAfter = Get-LogCounts
    $samples['logs'] = [pscustomobject]@{ Base = $logBase; After = $logAfter }
    Add-Check 'log: the first frame settled in this run' ($logAfter.UiIdle -gt $logBase.UiIdle) ("{0} -> {1}" -f $logBase.UiIdle, $logAfter.UiIdle)
    Add-Check 'log: the canvas mounted in this run' ($logAfter.CanvasShowing -gt $logBase.CanvasShowing) ("{0} -> {1}" -f $logBase.CanvasShowing, $logAfter.CanvasShowing)
    Add-Check 'log: the router attached exactly once' ($logAfter.RouterListening -eq ($logBase.RouterListening + 1)) ("{0} -> {1}" -f $logBase.RouterListening, $logAfter.RouterListening)
    Add-Check 'log: the router released exactly once, at the exit' ($logAfter.RouterReleased -eq ($logBase.RouterReleased + 1)) ("{0} -> {1}" -f $logBase.RouterReleased, $logAfter.RouterReleased)
    Add-Check 'log: the dock expanded and retracted' (($logAfter.DockExpanded -gt $logBase.DockExpanded) -and ($logAfter.DockRetracted -gt $logBase.DockRetracted)) `
        ("expanded {0}->{1}, retracted {2}->{3}" -f $logBase.DockExpanded, $logAfter.DockExpanded, $logBase.DockRetracted, $logAfter.DockRetracted)
    Add-Check 'log: exactly one drop reached the canvas, and no click' `
        ((($logAfter.ItemDropped -eq ($logBase.ItemDropped + 1)) -and ($logAfter.ItemClicked -eq $logBase.ItemClicked))) `
        ("dropped {0}->{1}, clicked {2}->{3}" -f $logBase.ItemDropped, $logAfter.ItemDropped, $logBase.ItemClicked, $logAfter.ItemClicked)
    if (-not $SkipExplorerRestart) {
        Add-Check 'log: the shell put the content back' ($logAfter.SurfaceBack -gt $logBase.SurfaceBack) ("{0} -> {1}" -f $logBase.SurfaceBack, $logAfter.SurfaceBack)
    }

    # --- report
    Write-Host ''
    Write-Host '=== checks ==='
    $failed = 0
    foreach ($check in $script:checks) { if (-not $check.Ok) { $failed++ } }
    Write-Host ("{0} checks, {1} failed" -f $script:checks.Count, $failed)

    $report = [pscustomobject]@{
        Stage = 'full'
        At = (Get-Date).ToString('s')
        Exe = $exePath
        Display = @{
            Width = [P3Win]::GetSystemMetrics(0)
            Height = [P3Win]::GetSystemMetrics(1)
        }
        Checks = $script:checks
        Samples = $script:samples
    }
    $reportPath = Join-Path $outPath 'p3a-verify.json'
    $report | ConvertTo-Json -Depth 8 | Set-Content -Path $reportPath -Encoding UTF8
    Write-Host "report written to $reportPath"

    if ($failed -gt 0) { Write-Host "FAILED CHECKS: $failed" } else { Write-Host 'ALL CHECKS PASSED' }
}

# ---------------------------------------------------------------- entry

trap {
    Write-Host ''
    Write-Host ("HARNESS ERROR: {0}" -f $_)
    Write-Host $_.ScriptStackTrace
    try { Restore-Everything } catch { Write-Host ("restore failed: {0}" -f $_) }
    exit 1
}

try {
    if ($Stage -eq 'probe') {
        Invoke-Probe
    } elseif ($Stage -eq 'latency') {
        Invoke-LatencyStage
    } else {
        Invoke-Full
    }
} finally {
    if ($Stage -ne 'probe') {
        Restore-Everything
        Write-Host 'Done; settings and layout restored, app stopped.'
    }
}
