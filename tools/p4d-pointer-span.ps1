<#
    Diagnostic: does the dock's pointer space line up with the screen? Drives the pointer across the run and
    reports, for each position, the peak scale the engine produced and the dock-space pointer it used.
    If the reported pointer does not match the same screen position twice, the space is inconsistent.
#>
[CmdletBinding()]
param([int] $Pins = 6, [string] $Prefix = 'mark')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\Muralis.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Muralis.exe'
$settingsPath = Join-Path $env:LOCALAPPDATA 'Muralis\settings.json'
$profLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
$out = 'D:\AI\temp\dsh-cu-eval\product'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$backup = Join-Path $out 'settings.span.before.json'

Add-Type -Namespace Sp -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
public delegate bool EnumProc(IntPtr h, IntPtr p);
public struct RECT { public int Left, Top, Right, Bottom; }
public struct POINT { public int X, Y; }
public const uint MOVE = 0x0001, ABSOLUTE = 0x8000;
'@
$SW = [Sp.W]::GetSystemMetrics(0); $SH = [Sp.W]::GetSystemMetrics(1)

function Find-Dock([int]$owner) {
    $script:hit = [IntPtr]::Zero
    $cb = [Sp.W+EnumProc] {
        param([IntPtr]$h, [IntPtr]$p)
        $o = 0; [void][Sp.W]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -eq $owner -and [Sp.W]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Sp.W]::GetWindowTextW($h, $sb, 256)
            if ($sb.ToString() -eq 'Muralis Dock') { $script:hit = $h }
        }
        return $true
    }
    [void][Sp.W]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:hit
}
function Get-LogLen { if (Test-Path $profLog) { (Get-Item $profLog).Length } else { 0 } }
function Get-Recs([long]$from) {
    $fs = [System.IO.File]::Open($profLog, 'Open', 'Read', 'ReadWrite')
    try { if ($fs.Length -le $from) { return @() }
        [void]$fs.Seek($from, [System.IO.SeekOrigin]::Begin)
        $b = New-Object byte[] ($fs.Length - $from); [void]$fs.Read($b, 0, $b.Length) } finally { $fs.Dispose() }
    $o = @(); foreach ($l in ([System.Text.Encoding]::UTF8.GetString($b) -split "`n")) { if ($l) { try { $o += ($l | ConvertFrom-Json) } catch {} } }
    return $o
}
function Move-To([int]$x, [int]$y) {
    [void][Sp.W]::mouse_event([Sp.W]::MOVE -bor [Sp.W]::ABSOLUTE,
        [int](($x * 65535) / ($SW - 1)), [int](($y * 65535) / ($SH - 1)), 0, [IntPtr]::Zero)
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
    $launchMark = Get-LogLen
    $p = Start-Process $exe -PassThru
    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 60 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 400; $h = Find-Dock $p.Id }
    if ($h -eq [IntPtr]::Zero) { throw 'no dock window' }
    $ready = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $ready) {
        Start-Sleep -Milliseconds 300
        if (@((Get-Recs $launchMark) | Where-Object { $_.label -eq 'motion.pointer.source' }).Count -gt 0) { break }
    }
    Start-Sleep -Milliseconds 800

    $cr = New-Object Sp.W+RECT; [void][Sp.W]::GetClientRect($h, [ref]$cr)
    $pt = New-Object Sp.W+POINT; [void][Sp.W]::ClientToScreen($h, [ref]$pt)
    $wr = New-Object Sp.W+RECT; [void][Sp.W]::GetWindowRect($h, [ref]$wr)
    Write-Host "window ($($wr.Left),$($wr.Top)) $($wr.Right-$wr.Left)x$($wr.Bottom-$wr.Top)  client $($cr.Right)x$($cr.Bottom) origin ($($pt.X),$($pt.Y))" -ForegroundColor Cyan

    # The resting run, from the dock's own reading of itself.
    Move-To 40 400
    Start-Sleep -Milliseconds 400
    $recs = Get-Recs $launchMark
    $o = @($recs | Where-Object { $_.name -eq 'motion.outside' })
    if (-not $o.Count) { throw 'no resting bounds recorded' }
    $o = $o[-1]
    $runLeftScreen = [double]$o.originX + [double]$o.left
    Write-Host "run: left=$($o.left) right=$($o.right) top=$($o.top) bottom=$($o.bottom)  origin=($($o.originX),$($o.originY))" -ForegroundColor Cyan

    $y = [int]([double]$o.originY + (([double]$o.top + [double]$o.bottom) / 2))
    $targets = @(
        @{ Label = 'restingLeft'; X = [int]$runLeftScreen + 1 },
        @{ Label = 'icon0'; X = [int]$runLeftScreen + 38 },
        @{ Label = 'gap01'; X = [int]$runLeftScreen + 66 },
        @{ Label = 'icon1'; X = [int]$runLeftScreen + 94 },
        @{ Label = 'icon5'; X = [int]$runLeftScreen + 318 },
        @{ Label = 'restingRight'; X = [int]$runLeftScreen + [int]$o.right - 1 }
    )

    $rows = @()
    foreach ($t in $targets) {
        $mark = Get-LogLen
        Move-To $t.X $y
        Start-Sleep -Milliseconds 650
        $recs = Get-Recs $mark
        $ptr = @($recs | Where-Object { $_.name -eq 'motion.pointer' })
        $rb = @($recs | Where-Object { $_.name -eq 'motion.rebuild' })
        $peak = if ($ptr.Count) { [double]$ptr[-1].peak } else { $null }
        $dockPtr = if ($ptr.Count) { [double]$ptr[-1].pointer } else { $null }
        $originX = if ($rb.Count) { [double]$rb[-1].originX } elseif ($ptr.Count) { [double]$ptr[-1].originX } else { $null }
        $dockLeft = if ($rb.Count) { [double]$rb[-1].dockLeft } else { $null }
        $row = [pscustomobject]@{
            Label = $t.Label; ScreenX = $t.X; ScreenY = $y
            Peak = $peak; EnginePointer = $dockPtr; EngineOriginX = $originX
            ImpliedDockPointer = if ($null -ne $originX) { [math]::Round($t.X - $originX, 2) } else { $null }
            DockLeft = $dockLeft
        }
        $rows += $row
        $row | Format-List
    }
    $rows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path (Join-Path $out 'span.csv')
}
finally {
    Copy-Item $backup $settingsPath -Force
    Get-Process Muralis -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
}
