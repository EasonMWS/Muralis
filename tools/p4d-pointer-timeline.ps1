<#
    Diagnostic: poll the dock window rectangle while the pointer enters, so the resize's own timeline is
    measured rather than sampled once. Exists because a single reading cannot tell "the dock never widened"
    from "the dock widened after the reading".
#>
[CmdletBinding()]
param([int] $Pins = 6, [int] $Seconds = 4)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\Muralis.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Muralis.exe'
$settingsPath = Join-Path $env:LOCALAPPDATA 'Muralis\settings.json'
$profLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
$backup = 'D:\AI\temp\dsh-cu-eval\product\settings.timeline.before.json'
New-Item -ItemType Directory -Force -Path (Split-Path $backup) | Out-Null

Add-Type -Namespace Tl -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
public delegate bool EnumProc(IntPtr h, IntPtr p);
public struct RECT { public int Left, Top, Right, Bottom; }
public const uint MOVE = 0x0001, ABSOLUTE = 0x8000;
'@
$SW = [Tl.W]::GetSystemMetrics(0)
$SH = [Tl.W]::GetSystemMetrics(1)

function Find-Dock([int]$owner) {
    $script:hit = [IntPtr]::Zero
    $cb = [Tl.W+EnumProc] {
        param([IntPtr]$h, [IntPtr]$p)
        $o = 0
        [void][Tl.W]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -eq $owner -and [Tl.W]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Tl.W]::GetWindowTextW($h, $sb, 256)
            if ($sb.ToString() -eq 'Muralis Dock') { $script:hit = $h }
        }
        return $true
    }
    [void][Tl.W]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:hit
}

function Get-LogLen {
    if (Test-Path $profLog) { return (Get-Item $profLog).Length }
    return 0
}

function Get-Recs([long]$from) {
    $fs = [System.IO.File]::Open($profLog, 'Open', 'Read', 'ReadWrite')
    try {
        if ($fs.Length -le $from) { return @() }
        [void]$fs.Seek($from, [System.IO.SeekOrigin]::Begin)
        $b = New-Object byte[] ($fs.Length - $from)
        [void]$fs.Read($b, 0, $b.Length)
    } finally { $fs.Dispose() }
    $o = @()
    foreach ($l in ([System.Text.Encoding]::UTF8.GetString($b) -split "`n")) {
        if ($l) { try { $o += ($l | ConvertFrom-Json) } catch { } }
    }
    return $o
}

function Get-RectText([IntPtr]$h) {
    $r = New-Object Tl.W+RECT
    [void][Tl.W]::GetWindowRect($h, [ref]$r)
    $w = $r.Right - $r.Left
    $ht = $r.Bottom - $r.Top
    return ('{0}x{1} at {2},{3}' -f $w, $ht, $r.Left, $r.Top)
}

Copy-Item $settingsPath $backup -Force
try {
    $json = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $links = @(Get-ChildItem ([Environment]::GetFolderPath('Desktop')) -Filter *.lnk |
        Where-Object { $_.Name -ne 'Muralis.lnk' } | Select-Object -ExpandProperty FullName)[0..($Pins - 1)]
    $json.Dock.IsVisible = $true
    $json.Dock.BackgroundStyle = 'Transparent'
    $json.Dock.PinnedApps = @($links | ForEach-Object {
        [pscustomobject]@{
            Id = [guid]::NewGuid().ToString('N').Substring(0, 12)
            DisplayName = [IO.Path]::GetFileNameWithoutExtension($_)
            LaunchTarget = $_; IconIdentity = $_; Kind = 1; Identity = $_.ToLowerInvariant()
            Arguments = $null; WorkingDirectory = $null
        }
    })
    [System.IO.File]::WriteAllText($settingsPath, ($json | ConvertTo-Json -Depth 12), (New-Object System.Text.UTF8Encoding($false)))

    $env:MURALIS_DOCK_PROFILE = '1'
    $launchMark = Get-LogLen
    $p = Start-Process $exe -PassThru
    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 60 -and $h -eq [IntPtr]::Zero; $i++) {
        Start-Sleep -Milliseconds 400
        $h = Find-Dock $p.Id
    }
    if ($h -eq [IntPtr]::Zero) { throw 'no dock window' }

    $ready = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $ready) {
        Start-Sleep -Milliseconds 300
        $seen = Get-Recs $launchMark
        if (@($seen | Where-Object { $_.label -eq 'motion.pointer.source' }).Count -gt 0) { break }
    }
    Start-Sleep -Milliseconds 800
    Write-Host "registration seen; resting window = $(Get-RectText $h)" -ForegroundColor Cyan

    $wr = New-Object Tl.W+RECT
    [void][Tl.W]::GetWindowRect($h, [ref]$wr)
    $cx = [int](($wr.Left + $wr.Right) / 2)
    $cy = [int](($wr.Top + $wr.Bottom) / 2)
    Write-Host "aim at $cx,$cy" -ForegroundColor Cyan

    $mark = Get-LogLen
    $started = Get-Date
    [void][Tl.W]::mouse_event([Tl.W]::MOVE -bor [Tl.W]::ABSOLUTE,
        [int](($cx * 65535) / ($SW - 1)), [int](($cy * 65535) / ($SH - 1)), 0, [IntPtr]::Zero)

    $shown = @{}
    while (((Get-Date) - $started).TotalSeconds -lt $Seconds) {
        Start-Sleep -Milliseconds 200
        $elapsed = [int]((Get-Date) - $started).TotalMilliseconds
        foreach ($r in (Get-Recs $mark)) {
            $key = "$($r.name):$($r.t)"
            if ($r.name -and -not $shown.ContainsKey($key)) {
                $shown[$key] = $true
                Write-Host ("  t={0,6}ms  {1,-26} {2}" -f $elapsed, $r.name, ($r | ConvertTo-Json -Compress))
            }
        }
        Write-Host ("  t={0,6}ms  window={1}" -f $elapsed, (Get-RectText $h)) -ForegroundColor DarkGray
    }

    Write-Host "final: $(Get-RectText $h)" -ForegroundColor Cyan
}
finally {
    Copy-Item $backup $settingsPath -Force
    Get-Process Muralis -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
}
