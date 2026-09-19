<#
    One-shot diagnostic: does the pointer actually reach the dock's interaction region now that the dock is
    content-sized? Prints the aim point it computes, then everything the dock recorded afterwards.
#>
[CmdletBinding()]
param([int] $Pins = 6)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\Muralis.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Muralis.exe'
$settingsPath = Join-Path $env:LOCALAPPDATA 'Muralis\settings.json'
$profLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
$work = 'D:\AI\temp\dsh-cu-eval\product'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$backup = Join-Path $work 'settings.diag.before.json'

Add-Type -Namespace Dg -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
[DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
public delegate bool EnumProc(IntPtr h, IntPtr p);
public struct RECT { public int Left, Top, Right, Bottom; }
public struct POINT { public int X, Y; }
public const uint MOVE = 0x0001, ABSOLUTE = 0x8000;
'@
$SW = [Dg.W]::GetSystemMetrics(0); $SH = [Dg.W]::GetSystemMetrics(1)

function Find([int]$owner, [string]$title) {
    $script:hit = [IntPtr]::Zero
    $cb = [Dg.W+EnumProc] {
        param([IntPtr]$h, [IntPtr]$p)
        $o = 0; [void][Dg.W]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -eq $owner -and [Dg.W]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Dg.W]::GetWindowTextW($h, $sb, 256)
            if ($sb.ToString() -eq $title) { $script:hit = $h }
        }
        return $true
    }
    [void][Dg.W]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:hit
}

function Ptr([int]$x, [int]$y) {
    [Dg.W]::mouse_event([Dg.W]::MOVE -bor [Dg.W]::ABSOLUTE,
        [int](($x * 65535) / ($SW - 1)), [int](($y * 65535) / ($SH - 1)), 0, [IntPtr]::Zero)
}

function LogLen { if (Test-Path $profLog) { (Get-Item $profLog).Length } else { 0 } }

function Records([long]$from) {
    $fs = [System.IO.File]::Open($profLog, 'Open', 'Read', 'ReadWrite')
    try {
        if ($fs.Length -le $from) { return @() }
        [void]$fs.Seek($from, [System.IO.SeekOrigin]::Begin)
        $b = New-Object byte[] ($fs.Length - $from)
        [void]$fs.Read($b, 0, $b.Length)
    } finally { $fs.Dispose() }
    $o = @()
    foreach ($l in ([System.Text.Encoding]::UTF8.GetString($b) -split "`n")) {
        if ($l) { try { $o += ($l | ConvertFrom-Json) } catch {} }
    }
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

    $wr = New-Object Dg.W+RECT; [void][Dg.W]::GetWindowRect($h, [ref]$wr)
    $cr = New-Object Dg.W+RECT; [void][Dg.W]::GetClientRect($h, [ref]$cr)
    $pt = New-Object Dg.W+POINT; [void][Dg.W]::ClientToScreen($h, [ref]$pt)
    "window rect   : ($($wr.Left),$($wr.Top))  $($wr.Right - $wr.Left)x$($wr.Bottom - $wr.Top)"
    "client rect   : $($cr.Right)x$($cr.Bottom)  origin ($($pt.X),$($pt.Y))"

    $marker = LogLen
    Ptr 60 400
    Start-Sleep -Milliseconds 900

    $recs = Records $marker
    $out = @($recs | Where-Object { $_.name -eq 'motion.outside' })
    $scr = @($recs | Where-Object { $_.name -eq 'motion.screen' })
    "records after first move: $($recs.Count)  outside=$($out.Count)  screen=$($scr.Count)"
    if ($out.Count) { $o = $out[-1]; "  outside: left=$($o.left) right=$($o.right) top=$($o.top) bottom=$($o.bottom) originX=$($o.originX) originY=$($o.originY) scale=$($o.scale)" }
    if ($scr.Count) { $s = $scr[-1]; "  screen : x=$($s.x) y=$($s.y) originX=$($s.originX) originY=$($s.originY)" }

    if (-not $out.Count) { throw 'no motion.outside record: the dock never saw the pointer leave the region' }
    $o = $out[-1]
    $aimX = [int]([double]$o.originX + (([double]$o.left + [double]$o.right) / 2))
    $aimY = [int]([double]$o.originY + (([double]$o.top + [double]$o.bottom) / 2))
    "AIM at ($aimX,$aimY)  [screen is ${SW}x${SH}]"

    $marker2 = LogLen
    Ptr $aimX $aimY
    Start-Sleep -Milliseconds 1000
    $recs2 = Records $marker2
    "records after aim: $($recs2.Count)"
    $recs2 | ForEach-Object { "  $($_.name)  " + (($_ | ConvertTo-Json -Compress) -replace '^\{"t":[0-9.]+,"ev":"[a-z]+","drop":[0-9]+,"name":"[a-z.]+",?','') }

    $wr2 = New-Object Dg.W+RECT; [void][Dg.W]::GetWindowRect($h, [ref]$wr2)
    "window after aim: ($($wr2.Left),$($wr2.Top))  $($wr2.Right - $wr2.Left)x$($wr2.Bottom - $wr2.Top)"
}
finally {
    Copy-Item $backup $settingsPath -Force
    Get-Process Muralis -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
}
