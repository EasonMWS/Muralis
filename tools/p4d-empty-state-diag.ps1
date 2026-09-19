<#
    Diagnostic: with nothing pinned, is the dock window hidden?
    Prints the window's visibility, its rectangle, and its extended style, then waits and prints them again.
    Exists because "the window is hidden" and "the window is off the work area" are different facts and only
    one of them is what the product promises.
#>
[CmdletBinding()]
param([int] $WaitSeconds = 8)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src\Muralis.App\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\Muralis.exe'
$settingsPath = Join-Path $env:LOCALAPPDATA 'Muralis\settings.json'
$backup = 'D:\AI\temp\dsh-cu-eval\product\settings.empty.before.json'
New-Item -ItemType Directory -Force -Path (Split-Path $backup) | Out-Null

Add-Type -Namespace Em -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
[DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
public delegate bool EnumProc(IntPtr h, IntPtr p);
public struct RECT { public int Left, Top, Right, Bottom; }
'@

function Find-Dock([int]$owner) {
    $script:hit = [IntPtr]::Zero
    $cb = [Em.W+EnumProc] {
        param([IntPtr]$h, [IntPtr]$p)
        $o = 0; [void][Em.W]::GetWindowThreadProcessId($h, [ref]$o)
        if ($o -eq $owner) {
            $sb = New-Object System.Text.StringBuilder 256
            [void][Em.W]::GetWindowTextW($h, $sb, 256)
            if ($sb.ToString() -eq 'Muralis Dock') { $script:hit = $h }
        }
        return $true
    }
    [void][Em.W]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:hit
}

function Report([IntPtr]$h, [string]$when) {
    $r = New-Object Em.W+RECT
    [void][Em.W]::GetWindowRect($h, [ref]$r)
    $w = $r.Right - $r.Left
    $ht = $r.Bottom - $r.Top
    $ex = [Em.W]::GetWindowLongPtr($h, -20).ToInt64()
    $topmost = if ($ex -band 0x8) { 'TOPMOST' } else { 'not-topmost' }
    $tool = if ($ex -band 0x80) { 'toolwindow' } else { 'no-toolwindow' }
    $noact = if ($ex -band 0x08000000) { 'noactivate' } else { 'activatable' }
    Write-Host ("  {0,-10} visible={1,-5} iconic={2,-5} rect=({3},{4}) {5}x{6}  exstyle: {7}, {8}, {9}" -f `
        $when, [Em.W]::IsWindowVisible($h), [Em.W]::IsIconic($h), $r.Left, $r.Top, $w, $ht, $topmost, $tool, $noact)
}

Copy-Item $settingsPath $backup -Force
try {
    $json = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $json.Dock.IsVisible = $true
    $json.Dock.BackgroundStyle = 'Transparent'
    $json.Dock.PinnedApps = @()
    [System.IO.File]::WriteAllText($settingsPath, ($json | ConvertTo-Json -Depth 12), (New-Object System.Text.UTF8Encoding($false)))

    $env:MURALIS_DOCK_PROFILE = '1'
    $p = Start-Process $exe -PassThru
    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 75 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 400; $h = Find-Dock $p.Id }
    if ($h -eq [IntPtr]::Zero) { throw 'no dock window was created at all' }

    Report $h 'immediate'
    for ($i = 1; $i -le $WaitSeconds; $i++) { Start-Sleep -Seconds 1; Report $h "t+${i}s" }
}
finally {
    Copy-Item $backup $settingsPath -Force
    Get-Process Muralis -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
}
