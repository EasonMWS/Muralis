<#
    Diagnostic: sweep the pointer across and around the dock window and report which positions the dock's raw
    pointer source actually saw. Exists because "the dock ignored the pointer" and "the pointer never moved"
    look identical from the dock's own log.
#>
[CmdletBinding()]
param([int] $Pins = 6)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\Muralis.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Muralis.exe'
$settingsPath = Join-Path $env:LOCALAPPDATA 'Muralis\settings.json'
$profLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
$backup = 'D:\AI\temp\dsh-cu-eval\product\settings.sweep.before.json'
New-Item -ItemType Directory -Force -Path (Split-Path $backup) | Out-Null

Add-Type -Namespace Sw -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
public delegate bool EnumProc(IntPtr h, IntPtr p);
public struct RECT { public int Left, Top, Right, Bottom; }
public struct POINT { public int X, Y; }
public const uint MOVE = 0x0001, ABSOLUTE = 0x8000;
'@
$SW = [Sw.W]::GetSystemMetrics(0); $SH = [Sw.W]::GetSystemMetrics(1)

function Find([int]$owner, [string]$title) {
    $script:hit = [IntPtr]::Zero
    $cb = [Sw.W+EnumProc] {
        param([IntPtr]$h, [IntPtr]$p)
        $o = 0; [void][Sw.W]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -eq $owner -and [Sw.W]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Sw.W]::GetWindowTextW($h, $sb, 256)
            if ($sb.ToString() -eq $title) { $script:hit = $h }
        }
        return $true
    }
    [void][Sw.W]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:hit
}
function LogLen { if (Test-Path $profLog) { (Get-Item $profLog).Length } else { 0 } }
function Records([long]$from) {
    $fs = [System.IO.File]::Open($profLog, 'Open', 'Read', 'ReadWrite')
    try {
        if ($fs.Length -le $from) { return @() }
        [void]$fs.Seek($from, [System.IO.SeekOrigin]::Begin)
        $b = New-Object byte[] ($fs.Length - $from); [void]$fs.Read($b, 0, $b.Length)
    } finally { $fs.Dispose() }
    $o = @(); foreach ($l in ([System.Text.Encoding]::UTF8.GetString($b) -split "`n")) { if ($l) { try { $o += ($l | ConvertFrom-Json) } catch {} } }
    return $o
}

Copy-Item $settingsPath $backup -Force
try {
    $json = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $links = @(Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -Filter *.lnk |
        Where-Object { $_.Name -ne 'Muralis.lnk' } | Select-Object -ExpandProperty FullName)[0..($Pins - 1)]
    $json.Dock.IsVisible = $true
    $json.Dock.BackgroundStyle = 'Transparent'
    $json.Dock.PinnedApps = @($links | ForEach-Object {
        [pscustomobject]@{ Id = [guid]::NewGuid().ToString('N').Substring(0,12); DisplayName = [IO.Path]::GetFileNameWithoutExtension($_)
            LaunchTarget = $_; IconIdentity = $_; Kind = 1; Identity = $_.ToLowerInvariant(); Arguments = $null; WorkingDirectory = $null } })
    [System.IO.File]::WriteAllText($settingsPath, ($json | ConvertTo-Json -Depth 12), (New-Object System.Text.UTF8Encoding($false)))

    $env:MURALIS_DOCK_PROFILE = '1'
    $p = Start-Process $exe -PassThru
    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 60 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 400; $h = Find $p.Id 'Muralis Dock' }
    if ($h -eq [IntPtr]::Zero) { throw 'no dock window' }
    Start-Sleep -Milliseconds 2500

    $wr = New-Object Sw.W+RECT; [void][Sw.W]::GetWindowRect($h, [ref]$wr)
    "dock window: ($($wr.Left),$($wr.Top)) $($wr.Right-$wr.Left)x$($wr.Bottom-$wr.Top)"
    $points = @(
        [pscustomobject]@{ X = 60; Y = 400 },
        [pscustomobject]@{ X = 2000; Y = 300 },
        [pscustomobject]@{ X = $wr.Left + 30; Y = $wr.Top + 60 },
        [pscustomobject]@{ X = [int](($wr.Left + $wr.Right) / 2); Y = [int](($wr.Top + $wr.Bottom) / 2) },
        [pscustomobject]@{ X = $wr.Right - 30; Y = $wr.Top + 60 }
    )

    foreach ($pt in $points) {
        $x = $pt.X; $y = $pt.Y
        $marker = LogLen
        [void][Sw.W]::mouse_event([Sw.W]::MOVE -bor [Sw.W]::ABSOLUTE,
            [int](($x * 65535) / ($SW - 1)), [int](($y * 65535) / ($SH - 1)), 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 700
        $cur = New-Object Sw.W+POINT; [void][Sw.W]::GetCursorPos([ref]$cur)
        $recs = Records $marker
        $names = ($recs | Where-Object { $_.name -like 'motion.*' } | ForEach-Object { $_.name }) -join ','
        $isIn = ($x -ge $wr.Left -and $x -le $wr.Right -and $y -ge $wr.Top -and $y -le $wr.Bottom)
        "  ask ($x,$y) inWindow=$isIn  cursorNow=($($cur.X),$($cur.Y))  dockSaw=[$names]"
    }
}
finally {
    Copy-Item $backup $settingsPath -Force
    Get-Process Muralis -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
}
