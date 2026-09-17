<#
.SYNOPSIS
    What the Phase 3 live verification harnesses share: interop, desktop helpers, the diagnostics
    reader and the check list.

.DESCRIPTION
    Dot-source this file from a harness:

        . (Join-Path $PSScriptRoot 'p3-common.ps1')
        Set-BackupPaths 'p3b'

    It brings in the Win32 and UI Automation interop, the pointer helpers, the desktop clearing
    pass, the diagnostics panel reader, the log helpers and the check list. The harnesses define
    the stages; this file defines how they touch the machine.

    Everything a harness changes on the way in is restored by Restore-Everything on the way out,
    and it is the harness' job to call that from a trap.
#>

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- interop

if (-not ('P3Win' -as [type])) {
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
  [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr hWnd);
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
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageW(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SetWindowTextW(IntPtr hWnd, string text);
  [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
  [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
  [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)] public static extern uint SendInputKeys(uint count, INPUTK[] inputs, int size);
  [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr process, uint flags);

  public const uint INPUT_MOUSE = 0;
  public const uint INPUT_KEYBOARD = 1;
  public const uint MOUSEEVENTF_MOVE = 0x0001;
  public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
  public const uint MOUSEEVENTF_LEFTUP = 0x0004;
  public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
  public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
  public const uint KEYEVENTF_KEYUP = 0x0002;
  public const uint KEYEVENTF_UNICODE = 0x0004;
  public const uint GA_ROOT = 2;
  public const uint WM_CLOSE = 0x0010;
  public const uint WM_SETTEXT = 0x000C;
  public const uint WM_CHAR = 0x0102;
  public const uint WM_COMMAND = 0x0111;
  public const uint BM_CLICK = 0x00F5;

  /// <summary>The shell's own dialog puts its file name box at control id 1148 and its button at 1.</summary>
  public const int FileNameControlId = 1148;
  public const int DialogButtonId = 1;
  public const int SW_MINIMIZE = 6;
  public const int SW_RESTORE = 9;
  public const int SM_CXVIRTUALSCREEN = 78;
  public const int SM_CYVIRTUALSCREEN = 79;
  public const int SM_XVIRTUALSCREEN = 76;
  public const int SM_YVIRTUALSCREEN = 77;
  public const uint GR_GDIOBJECTS = 0;
  public const uint GR_USEROBJECTS = 1;

  public struct POINT { public int X; public int Y; }
  public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

  [StructLayout(LayoutKind.Sequential)]
  public struct MOUSEINPUT {
    public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
  }

  [StructLayout(LayoutKind.Sequential)]
  public struct KEYBDINPUT {
    public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
  }

  [StructLayout(LayoutKind.Explicit)]
  public struct INPUTUNION {
    [FieldOffset(0)] public MOUSEINPUT mi;
    [FieldOffset(0)] public KEYBDINPUT ki;
  }

  [StructLayout(LayoutKind.Sequential)]
  public struct INPUT { public uint type; public MOUSEINPUT mi; }

  [StructLayout(LayoutKind.Sequential)]
  public struct INPUTK { public uint type; public INPUTUNION u; }

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

  private static void SendKey(ushort virtualKey, ushort scan, uint flags) {
    INPUTK[] inputs = new INPUTK[1];
    inputs[0].type = INPUT_KEYBOARD;
    inputs[0].u.ki.wVk = virtualKey;
    inputs[0].u.ki.wScan = scan;
    inputs[0].u.ki.dwFlags = flags;
    uint sent = SendInputKeys(1, inputs, Marshal.SizeOf(typeof(INPUTK)));
    if (sent != 1) throw new InvalidOperationException("SendInput (keyboard) failed (" + Marshal.GetLastWin32Error() + ")");
  }

  // Types text through the input stack, one unicode code unit at a time: layout independent, and it
  // reaches the window the user's own typing would reach.
  public static void TypeText(string text) {
    foreach (char c in text) {
      SendKey(0, c, KEYEVENTF_UNICODE);
      SendKey(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP);
    }
  }

  // A real key press. A virtual key goes in wVk, and wScan stays empty: with neither the Unicode nor
  // the scancode flag set it is wVk the system reads, and an event that carries the key in wScan
  // instead is an event with no key in it at all.
  public static void PressKey(ushort virtualKey) {
    SendKey(virtualKey, 0, 0);
    SendKey(virtualKey, 0, KEYEVENTF_KEYUP);
  }

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

  // A classic dialog's controls are children of it, and a control that has been drawn is addressed by
  // class and control id however the dialog is localised: the shell's own picker exposes its file name
  // box as Edit 1148 and its own button as Button 1 in every language.
  public static IntPtr FindChild(IntPtr parent, string className, int controlId) {
    IntPtr found = IntPtr.Zero;
    EnumChildWindows(parent, delegate(IntPtr h, IntPtr l) {
      if (className != null && ClassOf(h) != className) return true;
      if (controlId >= 0 && GetDlgCtrlID(h) != controlId) return true;
      if (!IsWindowVisible(h)) return true;
      found = h;
      return false;
    }, IntPtr.Zero);
    return found;
  }

  public static bool SetText(IntPtr h, string text) { return SetWindowTextW(h, text); }

  public static IntPtr Send(IntPtr h, uint message, IntPtr wParam, IntPtr lParam) {
    return SendMessageW(h, message, wParam, lParam);
  }

  // Typing into a control is not the same as setting its text: every keystroke is a WM_CHAR the control
  // and the dialog behind it see, so the dialog's own idea of the box changes with it. The keystrokes
  // are posted, not sent from a keyboard, so no focus and no foreground window is involved.
  public static void TypeInto(IntPtr h, string text) {
    foreach (char c in text) {
      PostMessage(h, WM_CHAR, (IntPtr)c, IntPtr.Zero);
      System.Threading.Thread.Sleep(12);
    }
  }

  // Asking a button to click itself is what a dialog's own button does for a mouse, without any of the
  // focus, z-order or foreground state a real click would depend on.
  public static void ClickButton(IntPtr button) { SendMessageW(button, BM_CLICK, IntPtr.Zero, IntPtr.Zero); }

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

  // Top level windows of one process, class and all, whether visible or not: pickers and dialogs are
  // owned by the app and often do not show up in the shell's own lists.
  public static IntPtr[] WindowsOfProcess(int processId) {
    var list = new System.Collections.Generic.List<IntPtr>();
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      if (ProcessOf(h) == processId) list.Add(h);
      return true;
    }, IntPtr.Zero);
    return list.ToArray();
  }

  // The first window of a process whose class starts with a prefix. The canvas window is a child of
  // Explorer's icon host, so the search descends into every top level window's children, not only
  // into the ones this process owns.
  public static IntPtr FindWindowByPrefix(int processId, string prefix) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      if (ProcessOf(h) == processId && ClassOf(h).StartsWith(prefix)) { found = h; return false; }
      EnumChildWindows(h, delegate(IntPtr child, IntPtr l2) {
        if (ProcessOf(child) == processId && ClassOf(child).StartsWith(prefix)) { found = child; return false; }
        return true;
      }, IntPtr.Zero);
      return found == IntPtr.Zero;
    }, IntPtr.Zero);
    return found;
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

  public static IntPtr[] WindowsOfClass(string className) {
    var list = new System.Collections.Generic.List<IntPtr>();
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      if (ClassOf(h) == className) list.Add(h);
      return true;
    }, IntPtr.Zero);
    return list.ToArray();
  }

  public static int[] RectOf(IntPtr h) {
    RECT r; GetWindowRect(h, out r);
    return new int[] { r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top };
  }

  public static int[] ClientSizeOf(IntPtr h) {
    RECT r; GetClientRect(h, out r);
    return new int[] { r.Right - r.Left, r.Bottom - r.Top };
  }

  public static int[] ClientOriginOf(IntPtr h) {
    POINT p; p.X = 0; p.Y = 0;
    ClientToScreen(h, ref p);
    return new int[] { p.X, p.Y };
  }

  public static uint GdiObjectsOf(int processId) {
    var process = System.Diagnostics.Process.GetProcessById(processId);
    return GetGuiResources(process.Handle, GR_GDIOBJECTS);
  }

  public static uint UserObjectsOf(int processId) {
    var process = System.Diagnostics.Process.GetProcessById(processId);
    return GetGuiResources(process.Handle, GR_USEROBJECTS);
  }

  public static string Describe(IntPtr h) {
    if (h == IntPtr.Zero) return "(none)";
    return ClassOf(h) + " [" + TitleOf(h) + "]";
  }
}
"@
}

# ---------------------------------------------------------------- paths

$appData = Join-Path $env:LOCALAPPDATA 'Muralis'
$settingsPath = Join-Path $appData 'settings.json'

# The current desktop document, and the Phase 2 prototype file next to it (read once for migration).
$layoutPath = Join-Path $appData 'desktop\layout.json'
$prototypePath = Join-Path $appData 'desktop-canvas-prototype.json'

$script:backupSuffix = $null
$script:layoutParkRan = $false
$script:layoutHadDocument = $false
$script:settingsBackedUp = $false

function Set-BackupPaths([string]$Suffix) {
    $script:backupSuffix = $Suffix
    $script:settingsBackup = "$script:settingsPath.$Suffix.bak"
    $script:layoutBackup = "$script:layoutPath.$Suffix.bak"
    $script:prototypeBackup = "$script:prototypePath.$Suffix.bak"
    $script:settingsBackedUp = $false
    $script:layoutParkRan = $false
    $script:layoutHadDocument = $false
}

# The nav item and the diagnostics expander are found by name, and the app runs in Chinese on this
# machine: building the strings from code points keeps this file readable.
$navDynamic = -join ([int[]](0x52A8, 0x6001, 0x58C1, 0x7EB8) | ForEach-Object { [char]$_ })
$diagTitle = -join ([int[]](0x753B, 0x5E03, 0x8BCA, 0x65AD, 0xFF08, 0x4EC5, 0x5F00, 0x53D1, 0x7248, 0xFF09) | ForEach-Object { [char]$_ })

$script:checks = New-Object System.Collections.ArrayList
$script:samples = @{}
$script:logFile = $null
$script:process = $null
$script:window = [IntPtr]::Zero
$script:diagElement = $null
$script:diagExpander = $null
$script:diagMode = 'minimize'
$script:minimized = New-Object System.Collections.ArrayList

function Add-Check([string]$name, [bool]$ok, [string]$detail) {
    [void]$script:checks.Add([pscustomobject]@{ Check = $name; Ok = $ok; Detail = $detail })
    $tag = 'FAIL'
    if ($ok) { $tag = 'PASS' }
    Write-Host ("  [{0}] {1} : {2}" -f $tag, $name, $detail)
}

function Add-Sample([string]$name, $value) {
    $script:samples[$name] = $value
}

function Write-CheckReport([string]$stage, [string]$outPath, [string]$fileName = '') {
    Write-Host ''
    Write-Host '=== checks ==='
    $failed = 0
    foreach ($check in $script:checks) { if (-not $check.Ok) { $failed++ } }
    Write-Host ("{0} checks, {1} failed" -f $script:checks.Count, $failed)

    $report = [pscustomobject]@{
        Stage = $stage
        At = (Get-Date).ToString('s')
        Display = @{
            Width = [P3Win]::GetSystemMetrics(0)
            Height = [P3Win]::GetSystemMetrics(1)
        }
        Checks = $script:checks
        Samples = $script:samples
    }
    if ([string]::IsNullOrEmpty($fileName)) { $fileName = "p3-$stage-verify.json" }
    New-Item -ItemType Directory -Force -Path $outPath | Out-Null
    $reportPath = Join-Path $outPath $fileName
    $report | ConvertTo-Json -Depth 8 | Set-Content -Path $reportPath -Encoding UTF8
    Write-Host "report written to $reportPath"

    if ($failed -gt 0) { Write-Host "FAILED CHECKS: $failed" } else { Write-Host 'ALL CHECKS PASSED' }
    return $failed
}

# ---------------------------------------------------------------- log

function Refresh-LogFile {
    $dir = Join-Path $appData 'logs'
    $script:logFile = Get-ChildItem $dir -Filter 'muralis-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

function Read-LogText {
    if (-not $script:logFile) { return '' }
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        try {
            # Serilog keeps the file open for writing, and a plain read is refused by the sharing
            # rules while that goes on. The reader shares the file with the writer instead, so the
            # log can be counted mid-run as well as after the app has exited.
            $stream = New-Object IO.FileStream($script:logFile.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
            try {
                $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)
                try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
            }
            finally { $stream.Dispose() }
        }
        catch { Start-Sleep -Milliseconds 100 }
    }
    return ''
}

# Serilog's file sink does not have to hit the disk the moment the app writes a line, so the on-disk
# file lags during a run. Counting occurrences of a pattern is still sound: counts from previous runs
# are already flushed, and the whole file is flushed when the app exits. Growth is therefore polled
# with generous timeouts, and the authoritative log checks run after the exit.
function Get-LogCount([string]$pattern) {
    Refresh-LogFile
    $text = Read-LogText
    if ([string]::IsNullOrEmpty($text)) { return 0 }
    return ([regex]::Matches($text, [regex]::Escape($pattern))).Count
}

function Get-LogCounts($patterns) {
    $counts = @{}
    foreach ($key in $patterns.Keys) { $counts[$key] = Get-LogCount $patterns[$key] }
    return $counts
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

function Get-LogLines([string]$pattern) {
    Refresh-LogFile
    $text = Read-LogText
    if ([string]::IsNullOrEmpty($text)) { return @() }
    return @($text -split "`r?`n" | Where-Object { $_ -match [regex]::Escape($pattern) })
}

# ---------------------------------------------------------------- desktop

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
# at the end of the run. PointsX and PointsY are crossed; ExtraPoints adds single probes a harness
# needs outside that grid.
function Clear-TheDesktop([int[]]$PointsX, [int[]]$PointsY, [string]$Label, $ExtraPoints = $null) {
    Write-Host ("Clearing the desktop{0}: minimising the windows in the way ..." -f $Label)
    $desktopClasses = @('Progman', 'WorkerW', 'SHELLDLL_DefView')
    $keepClasses = @('Shell_TrayWnd', 'Shell_SecondaryTrayWnd', 'Progman', 'WorkerW', 'SHELLDLL_DefView', 'Windows.UI.Core.CoreWindow')

    $points = @()
    foreach ($y in $PointsY) {
        foreach ($x in $PointsX) { $points += , @($x, $y) }
    }
    if ($null -ne $ExtraPoints) {
        foreach ($probe in $ExtraPoints) { $points += , @($probe[0], $probe[1]) }
    }

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

function Wait-ForSeconds([double]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 50 }
}

# ---------------------------------------------------------------- pointer

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

# A double click is two clicks close enough in time and place for the system to call it one, which is
# what the canvas' own gesture machine reads: the gap has to be inside GetDoubleClickTime.
function DoubleClick-Pointer([int]$gapMs = 60) {
    [P3Win]::LeftDown()
    Start-Sleep -Milliseconds 20
    [P3Win]::LeftUp()
    Start-Sleep -Milliseconds $gapMs
    [P3Win]::LeftDown()
    Start-Sleep -Milliseconds 20
    [P3Win]::LeftUp()
}

# A press, a move in steps and a release: a drag as the gesture machine sees one, well past the
# system drag rectangle.
function Drag-Pointer([int]$fromX, [int]$fromY, [int]$toX, [int]$toY, [int]$steps = 12) {
    Move-Pointer $fromX $fromY
    Start-Sleep -Milliseconds 120
    [P3Win]::LeftDown()
    Start-Sleep -Milliseconds 120
    for ($i = 1; $i -le $steps; $i++) {
        $x = [int]($fromX + (($toX - $fromX) * $i / $steps))
        $y = [int]($fromY + (($toY - $fromY) * $i / $steps))
        Move-Pointer $x $y
        Start-Sleep -Milliseconds 25
    }
    Start-Sleep -Milliseconds 80
    [P3Win]::LeftUp()
    Start-Sleep -Milliseconds 250
}

# The notepads already running, so the ones a launch starts are told apart from the user's own. A
# harness that is about to launch something records the baseline first, and closes only what it
# started.
$script:notepadBaseline = @()

function Get-NotepadIds {
    return @(Get-Process -Name notepad -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
}

function Wait-NewNotepad([int]$timeoutSeconds = 15) {
    $baseline = $script:notepadBaseline
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $now = Get-NotepadIds
        $new = @($now | Where-Object { $baseline -notcontains $_ })
        if ($new.Count -gt 0) { return $new }
        Start-Sleep -Milliseconds 300
    }
    return @()
}

function Close-FixtureNotepads([int[]]$ids) {
    foreach ($id in $ids) {
        try { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue } catch { }
    }
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

function Get-CpuMilliseconds([int]$processId) {
    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $null }
    return $process.TotalProcessorTime.TotalMilliseconds
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

function Find-ById($root, [string]$id, $scope) {
    if ($null -eq $scope) { $scope = [System.Windows.Automation.TreeScope]::Descendants }
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $root.FindFirst($scope, $condition)
}

function Find-AllByIdPrefix($root, [string]$prefix) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    $found = @()
    for ($i = 0; $i -lt $all.Count; $i++) {
        $element = $all.Item($i)
        try {
            $id = $element.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::AutomationIdProperty)
        } catch { continue }
        if ($id -is [string] -and $id.StartsWith($prefix)) { $found += , $element }
    }
    return $found
}

function Invoke-Element($element, [string]$what) {
    if ($null -eq $element) { throw "The element '$what' was not found." }
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Set-ElementText($element, [string]$text, [string]$what) {
    if ($null -eq $element) { throw "The element '$what' was not found." }
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($text)
}

function Get-TextElementNames($root, [int]$limit = 40) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    $names = @()
    for ($i = 0; $i -lt $all.Count -and $names.Count -lt $limit; $i++) {
        try {
            $name = $all.Item($i).GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
        } catch { continue }
        if ($name -is [string] -and $name.Length -gt 0) { $names += $name }
    }
    return $names
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

function Get-AppRoot {
    $root = $null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        try { return [System.Windows.Automation.AutomationElement]::FromHandle($script:window) }
        catch { Start-Sleep -Milliseconds 250 }
    }
    throw 'The Muralis window never became reachable through UI Automation.'
}

function Open-DynamicPage {
    Write-Host 'Navigating to the dynamic wallpaper page and opening the diagnostics panel ...'
    $window = [P3Win]::FindWindowByClass([int]$script:process.Id, 'WinUIDesktopWin32WindowClass')
    if ($window -eq [IntPtr]::Zero) { throw 'The Muralis main window was not found.' }
    $script:window = $window

    $root = Get-AppRoot

    [P3Win]::ShowWindow($window, [P3Win]::SW_RESTORE) | Out-Null
    [P3Win]::SetForegroundWindow($window) | Out-Null
    Start-Sleep -Milliseconds 400

    $nav = Find-ByName $root $navDynamic $null
    if ($null -eq $nav) { throw "The navigation item '$navDynamic' was not found." }
    $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 900

    Open-DiagnosticsPanel
}

# The panel the canvas reports through is opened separately from the page it sits on: a stage that only
# reads the document has no use for it, and one that reads item counts, icon caches or the router's rate
# has no other way in.
function Open-DiagnosticsPanel {
    $root = Get-AppRoot
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
}

# Minimising keeps the desktop clear for the pointer work; if the panel stops answering, the window
# goes to a corner instead so the reads keep working.
function Minimize-AppWindow {
    [P3Win]::ShowWindow($script:window, [P3Win]::SW_MINIMIZE) | Out-Null
    $script:diagMode = 'minimize'
    Start-Sleep -Milliseconds 500
}

function Show-AppWindow([int]$x, [int]$y, [int]$w, [int]$h) {
    [P3Win]::ShowWindow($script:window, [P3Win]::SW_RESTORE) | Out-Null
    [P3Win]::MoveWindow($script:window, $x, $y, $w, $h, $true) | Out-Null
    [P3Win]::SetForegroundWindow($script:window) | Out-Null
    $script:diagMode = 'corner'

    # A window coming back from being minimised rebuilds its visual tree, so the panel inside it takes a
    # moment to answer again — a fixed pause before the first look is a guess, and a wrong one whenever
    # the page is a little slower than last time. The look is repeated instead, and where it still finds
    # nothing the panel is opened again the same way it was opened the first time: that path knows how
    # to find the expander, and a panel that came back collapsed is the other way this can go wrong.
    $deadline = (Get-Date).AddSeconds(10)
    while ($true) {
        Start-Sleep -Milliseconds 400
        $script:diagElement = Get-DiagElement (Get-AppRoot)
        if ($null -ne $script:diagElement) { return }
        if ((Get-Date) -ge $deadline) { throw 'The diagnostics text was lost when the window came back.' }
        try { Open-DiagnosticsPanel; return } catch { }
    }
}

function Read-Diag {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            $text = $script:diagElement.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
            if ($text -is [string] -and $text.Length -gt 0) { return $text }
        } catch {
            # The element went stale: resolve it again.
            $script:diagElement = Get-DiagElement (Get-AppRoot)
        }
        Start-Sleep -Milliseconds 60
    }

    if ($script:diagMode -eq 'minimize') {
        Write-Host '  (diagnostics went quiet while minimised; switching to corner mode)'
        Show-AppWindow 1750 900 780 500

        # The reading that found the panel quiet is the reading that had to restore the window, so it is
        # not the reading the caller asked for: what it gets back is what the panel says now, or the
        # caller is told there is nothing rather than being handed a state the panel never reported.
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            try {
                $text = $script:diagElement.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
                if ($text -is [string] -and $text.Length -gt 0) { return $text }
            } catch {
                $script:diagElement = Get-DiagElement (Get-AppRoot)
            }
            Start-Sleep -Milliseconds 100
        }
    }

    return ''
}

function Parse-Diag([string]$text) {
    $state = @{
        Inside = $null; Px = $null; Py = $null; HoverId = $null; HoverScale = $null
        DockPhase = $null; Context = $null; Rate = $null; Reports = $null
        Dispatches = $null; Updates = $null; Mount = $null
        ItemCount = $null; MissingCount = $null; SelectedId = $null
        LaunchId = $null; LaunchOutcome = $null; IconEntries = $null; IconMb = $null
        MonitorW = $null; MonitorH = $null; MonitorX = $null; MonitorY = $null; ScaleFactor = $null
        DockItems = $null; DockEnabled = $null; DockEdge = $null
    }
    if ([string]::IsNullOrEmpty($text)) { return $state }

    if ($text -match 'pointer\s+(inside|outside)\s+(-?[0-9.,]+),\s*(-?[0-9.,]+)') {
        $state.Inside = ($Matches[1] -eq 'inside')
        $state.Px = [double]($Matches[2] -replace ',', '')
        $state.Py = [double]($Matches[3] -replace ',', '')
    }
    if ($text -match 'items\s+(\d+)\s+.\s+hovered\s+(\S+)\s+at\s+([0-9.]+)x') {
        $state.ItemCount = [int]$Matches[1]; $state.HoverId = $Matches[2]; $state.HoverScale = [double]$Matches[3]
    }
    if ($text -match 'missing\s+(\d+)\s+.\s+selected\s+(\S+)') {
        $state.MissingCount = [int]$Matches[1]; $state.SelectedId = $Matches[2]
    }
    if ($text -match 'launch\s+(\S+?)\s+.\s+(\S+)') {
        $state.LaunchId = $Matches[1]; $state.LaunchOutcome = $Matches[2]
    }
    if ($text -match 'icons\s+(\d+)\s+cached\s+.\s+([0-9.]+)\s+MB') {
        $state.IconEntries = [int]$Matches[1]; $state.IconMb = [double]$Matches[2]
    }
    if ($text -match 'dock\s+(\w+)\s+.\s+(\d+)\s+items\s+.\s+(on|off)\s+.\s+(\w+)\s+edge\s+.\s+reveal\s+([0-9.]+)') {
        $state.DockPhase = $Matches[1]
        $state.DockItems = [int]$Matches[2]
        $state.DockEnabled = ($Matches[3] -eq 'on')
        $state.DockEdge = $Matches[4]
        $state.DockScale = [double]$Matches[5]
    }
    if ($text -match 'router\s+(\w+)\s+.\s+([0-9.]+)/s\s+.\s+(\d+)\s+reports\s+.\s+(\d+)\s+dispatches') {
        $state.Context = $Matches[1]; $state.Rate = [double]$Matches[2]
        $state.Reports = [long]$Matches[3]; $state.Dispatches = [long]$Matches[4]
    }
    if ($text -match 'updates\s+([0-9.]+)/s\s+.\s+(\d+)\s+total') { $state.Updates = [long]$Matches[2] }
    if ($text -match 'mount\s+(\d+)') { $state.Mount = [int]$Matches[1] }
    if ($text -match 'monitor\s+\S+\s+.\s+(\d+)x(\d+)\s+at\s+(-?\d+),(-?\d+)') {
        $state.MonitorW = [int]$Matches[1]; $state.MonitorH = [int]$Matches[2]
        $state.MonitorX = [int]$Matches[3]; $state.MonitorY = [int]$Matches[4]
    }
    if ($text -match 'dpi\s*\(\s*([0-9.]+)\s*x\s*\)') { $state.ScaleFactor = [double]$Matches[1] }
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

function Wait-Diag([scriptblock]$predicate, [int]$timeoutSeconds = 20, [string]$what = 'the diagnostics panel') {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $state = Read-State
        if (& $predicate $state) { return $state }
        Start-Sleep -Milliseconds 300
    }
    Write-Host ("  (timed out waiting for {0})" -f $what)
    return $null
}

# ---------------------------------------------------------------- the canvas' geometry

# Until the panel describes a display, the canvas' geometry is the primary one at its own scale.
$script:scale = 1.0
$script:monitor = @(0, 0, [P3Win]::GetSystemMetrics(0), [P3Win]::GetSystemMetrics(1))

# The canvas resolves every item from the display's centre: with the anchor it is given, the item's
# centre in pixels is the display centre plus its DIP offset times the scale. Which display, and at what
# scale, is what the panel says — and a display the panel has not described yet is the primary one.
function Read-Geometry {
    $state = Read-State
    if ($null -ne $state.ScaleFactor) { $script:scale = $state.ScaleFactor }
    if ($null -ne $state.MonitorW) {
        $script:monitor = @($state.MonitorX, $state.MonitorY, $state.MonitorW, $state.MonitorH)
    }
    return $state
}

function Get-ItemCentre($item) {
    $x = $script:monitor[0] + ($script:monitor[2] / 2) + ($item.OffsetXDip * $script:scale)
    $y = $script:monitor[1] + ($script:monitor[3] / 2) + ($item.OffsetYDip * $script:scale)
    return @([int][math]::Round($x), [int][math]::Round($y))
}

# ---------------------------------------------------------------- settings, layout, restore

# Backs the user's settings up, once per run. Every stage of a run changes the same file, so a second
# backup would take the copy of the file this run had already changed, and the way back would hand the
# user the harness's own settings instead of their own.
function Backup-Settings {
    if ($script:settingsBackedUp) { return }

    # A backup already on disk was made by a run that never put it back, which means that run died before
    # its restore: the copy is older than anything this run could take, and the file on disk is whatever
    # that run left. Adopted rather than overwritten, because overwriting it would put a harness's own
    # settings where the user's are restored from, and the user would never get them back.
    if (Test-Path $script:settingsBackup) {
        Write-Host ("  (a settings backup from an earlier run was left behind and is being kept: {0})" -f $script:settingsBackup)
        $script:settingsBackedUp = $true
        return
    }

    if (-not (Test-Path $script:settingsPath)) { throw "settings.json was not found at $script:settingsPath" }
    Copy-Item -Force $script:settingsPath $script:settingsBackup
    $script:settingsBackedUp = $true
}

# Keeps the window closing for real, so the harness can close it and wait for the exit. Every other
# setting is left exactly as the user had it. The desktop mode is not a setting: it lives in the
# desktop layout document, and Write-Layout is what asks for the one these runs need.
function Enable-CanvasInSettings {
    $settings = Get-Content $script:settingsPath -Raw | ConvertFrom-Json
    $settings.CloseToTray = $false
    $settings | ConvertTo-Json -Depth 10 | Set-Content -Path $script:settingsPath -Encoding UTF8
}

# Parks the layout documents: the user's own desktop layout must come back untouched, and the Phase 2
# prototype file must not be there while a harness plants a document of its own. What was found is
# remembered, because the way back has to tell the user's own document from one the harness planted.
#
# Once per run, for the same reason the settings are backed up once: a second park would take the
# document this run had already planted, and the way back would hand the user that instead of their own.
function Park-LayoutFiles {
    if ($script:layoutParkRan) { return }
    New-Item -ItemType Directory -Force -Path (Split-Path $script:layoutPath -Parent) | Out-Null
    $script:layoutParkRan = $true
    if (Test-Path $script:layoutPath) {
        Move-Item -Force $script:layoutPath $script:layoutBackup
        $script:layoutHadDocument = $true
    }
    if (Test-Path $script:prototypePath) { Move-Item -Force $script:prototypePath $script:prototypeBackup }
}

function Read-Layout {
    if (-not (Test-Path $script:layoutPath)) { return $null }
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        try { return (Get-Content $script:layoutPath -Raw -Encoding UTF8 | ConvertFrom-Json) }
        catch { Start-Sleep -Milliseconds 100 }
    }
    return $null
}

function Get-LayoutItem($layout, [string]$id) {
    if ($null -eq $layout) { return $null }
    foreach ($item in $layout.Items) { if ($item.Id -eq $id) { return $item } }
    return $null
}

# The document as the harness plants it: the same shape the app writes, one object per item.
function New-LayoutItem {
    param(
        [string]$Id,
        [string]$Name,
        [string]$Kind,
        [string]$Path,
        [double]$OffsetX = 0,
        [double]$OffsetY = 0,
        [string]$Placement = 'Free',
        [string]$IconKey = '',
        [int]$Z = 0
    )

    $target = [pscustomobject]@{ kind = $Kind }
    if ($Kind -eq 'url') { $target | Add-Member -NotePropertyName url -NotePropertyValue $Path }
    else { $target | Add-Member -NotePropertyName path -NotePropertyValue $Path }

    return [pscustomobject]@{
        Id = $Id
        Name = $Name
        Target = $target
        IconKey = $IconKey
        Placement = $Placement
        Anchor = 'Center'
        OffsetXDip = $OffsetX
        OffsetYDip = $OffsetY
        SizeDip = 96
        Z = $Z
        IsVisible = $true
    }
}

# The document as the harness plants it: the shape the app writes, and the desktop mode asked for on
# the way in. Preview is the mode the canvas runs under while Explorer's own icons are still the
# user's, which is what most of these runs are about: the canvas and the dock, not the takeover. A
# stage that wants to watch a mode change happen asks for the mode it is starting from instead.
#
# Everything the sections can be left out of is left out, because an absent node is read as the app's
# own default and a section written here by hand would be a second copy of those defaults to keep in
# step. Adoption is the exception: it is off by default so a run starts from exactly the items it
# planted, and a stage that wants the user's own desktop brought in alongside the fixture asks for it.
function Write-Layout($items, [string]$Mode = 'Preview', [bool]$AdoptDesktopItems = $false) {
    $layout = [pscustomobject]@{
        SchemaVersion = 4
        Kind = 'muralis.desktopLayout'
        Proximity = [pscustomobject]@{ MaxScale = 1.6; InfluenceRadiusDip = 200; Falloff = 'Smoothstep' }
        Motion = [pscustomobject]@{ Hover = [pscustomobject]@{ PeriodSeconds = 0.30; DampingRatio = 0.85 } }
        Dock = [pscustomobject]@{ Enabled = $false; Entries = @() }
        Takeover = [pscustomobject]@{
            Mode = $Mode
            AdoptDesktopItems = $AdoptDesktopItems
            IgnoredSourcePaths = @()
        }
        Items = @($items)
    }
    $json = $layout | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText($script:layoutPath, $json, [Text.UTF8Encoding]::new($false))
}

# Real items of every kind the app knows, the way its own importer would bring them in: programs from
# the system folder, folders that exist, and addresses — laid out on the grid the app's own placer
# walks, ten columns around the display centre, so a hundred of them land the way a hundred imported
# ones would and a hover sweep can walk the whole set.
function New-GridFixtureItems([int]$count) {
    $programs = [math]::Max($count - 6, 1)
    $exes = @(Get-ChildItem (Join-Path $env:SystemRoot 'System32') -Filter '*.exe' |
        Where-Object { $_.Length -gt 4096 } | Sort-Object Name | Select-Object -First $programs)
    $folders = @($env:SystemRoot, (Join-Path $env:SystemRoot 'System32'), $env:TEMP)
    $urls = @('https://example.com/', 'https://example.org/', 'https://example.net/')

    $items = @()
    $index = 0
    $z = 0
    foreach ($exe in $exes) {
        $items += New-LayoutItem -Id ("app_p{0:d2}" -f $index) -Name $exe.BaseName -Kind 'application' -Path $exe.FullName `
            -OffsetX 0 -OffsetY 0 -Z $z
        $index++; $z++
    }
    foreach ($folder in $folders) {
        $items += New-LayoutItem -Id ("dir_p{0:d2}" -f $index) -Name (Split-Path $folder -Leaf) -Kind 'folder' -Path $folder `
            -OffsetX 0 -OffsetY 0 -IconKey 'folder' -Z $z
        $index++; $z++
    }
    foreach ($url in $urls) {
        $items += New-LayoutItem -Id ("url_p{0:d2}" -f $index) -Name ([Uri]$url).Host -Kind 'url' -Path $url `
            -OffsetX 0 -OffsetY 0 -IconKey 'url' -Z $z
        $index++; $z++
    }

    $columns = 10
    $rows = [int][math]::Ceiling($count / $columns)
    for ($i = 0; $i -lt $items.Count; $i++) {
        $column = $i % $columns
        $row = [int][math]::Floor($i / $columns)
        $items[$i].OffsetXDip = (($column - (($columns - 1) / 2)) * 130)
        $items[$i].OffsetYDip = (($row - (($rows - 1) / 2)) * 130)
    }

    return $items
}

function Restore-Everything {
    Write-Host ''
    Write-Host 'Restoring settings and the desktop documents ...'
    Restore-MinimizedWindows
    Stop-Process -Name Muralis -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
    if (Test-Path $script:settingsBackup) { Move-Item -Force $script:settingsBackup $script:settingsPath }
    Restore-LayoutFile
    if (Test-Path $script:prototypeBackup) { Move-Item -Force $script:prototypeBackup $script:prototypePath }
    if (Test-Path "$script:layoutPath.bad") { Remove-Item -Force "$script:layoutPath.bad" }
}

# The desktop layout is the one thing a run must never lose. A document that is there at the end is
# either the user's own, put back from the parked copy, or one the harness planted in the space where
# the user had none — and only a park that really ran and really found nothing tells those apart.
# Anything else leaves the file exactly where it is: a verification run that eats a desktop layout has
# cost more than it can ever prove.
function Restore-LayoutFile {
    if (Test-Path $script:layoutBackup) {
        Move-Item -Force $script:layoutBackup $script:layoutPath
        return
    }

    if (-not (Test-Path $script:layoutPath)) { return }

    if ($script:layoutHadDocument) {
        Write-Host ("  WARNING: the parked desktop layout is missing and the user had one; leaving {0} untouched" -f $script:layoutPath)
        return
    }

    if ($script:layoutParkRan) {
        Remove-Item -Force $script:layoutPath
        return
    }

    Write-Host '  WARNING: no layout park ran this time; leaving the desktop layout untouched'
}
