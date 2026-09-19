<#
    Shared helpers for the Stage B acceptance runs.

    Two things this file exists to get right, because both produced wrong numbers before:

      * the dock profiler log is read by file position. Re-reading it whole on every step stalls the run for
        hundreds of milliseconds and that stall shows up later as latency the dock never had;
      * pointers are aimed with the engine's own cached centres and screen origin — taken from motion.rebuild and
        motion.pointer — never with a UIA element sequence. The dock's tree holds virtualized shelf items laid
        out far outside the window, so a UIA sequence is not dock geometry.

    Nothing here touches the product; it only drives the mouse and reads what the dock already writes.
#>

$script:LogPath = Join-Path $env:LOCALAPPDATA 'Muralis\logs\dock-drop-profile.jsonl'
$script:AppLog = Join-Path $env:LOCALAPPDATA 'Muralis\logs\muralis-20260918.log'
$script:LogPos = 0
$script:LogTail = ''
$script:Lines = New-Object System.Collections.Generic.List[string]

Add-Type -Namespace P4D -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll", SetLastError=true)] public static extern int GetWindowLongW(IntPtr h, int i);
public const uint MOVE=0x0001, ABSOLUTE=0x8000, LEFTDOWN=0x0002, LEFTUP=0x0004, WHEEL=0x0800;
public const int GWL_EXSTYLE=-20, WS_EX_TOPMOST=0x00000008;
'@
$script:ScreenW = [P4D.Win]::GetSystemMetrics(0)
$script:ScreenH = [P4D.Win]::GetSystemMetrics(1)

function Reset-MotionLog {
    <#
        Starts a fresh tail: the reader is put at the end of the file so only records written from now on are
        collected. Returns how many records were already there.

        State that already exists — the current rail, the last transition — is not in that tail. Use
        Seed-MotionLog for it, or a dock that has stopped writing will look like a dock that never wrote.
    #>
    $script:LogPos = 0
    $script:LogTail = ''
    $script:Lines.Clear()
    Read-MotionLog
    $n = $script:Lines.Count
    $script:Lines.Clear()
    return $n
}

function Seed-MotionLog {
    <#
        Loads the whole file into the buffer so the current state can be read, leaving the increment position at
        the end. New records are appended to the same buffer, so a reading and a tail can be mixed.
    #>
    $script:LogPos = 0
    $script:LogTail = ''
    $script:Lines.Clear()
    if (Test-Path -LiteralPath $script:LogPath) {
        $script:LogPos = (Get-Item -LiteralPath $script:LogPath).Length
        foreach ($line in [System.IO.File]::ReadAllLines($script:LogPath, [System.Text.Encoding]::UTF8)) {
            if ($line.Length -gt 0) { $script:Lines.Add($line) }
        }
    }
    return $script:Lines.Count
}

function Read-MotionLog {
    if (-not (Test-Path -LiteralPath $script:LogPath)) { return }
    try {
        $fs = [System.IO.File]::Open($script:LogPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    } catch { return }

    try {
        if ($fs.Length -lt $script:LogPos) { $script:LogPos = 0; $script:LogTail = '' }
        if ($fs.Length -eq $script:LogPos) { return }
        $fs.Seek($script:LogPos, [System.IO.SeekOrigin]::Begin) | Out-Null
        $buffer = New-Object byte[] ($fs.Length - $script:LogPos)
        $read = $fs.Read($buffer, 0, $buffer.Length)
        $script:LogPos += $read
        $text = $script:LogTail + [System.Text.Encoding]::UTF8.GetString($buffer, 0, $read)
        $parts = $text -split "`n"
        $script:LogTail = $parts[-1]
        for ($i = 0; $i -lt $parts.Count - 1; $i++) {
            if ($parts[$i].Length -gt 0) { $script:Lines.Add($parts[$i]) }
        }
    } finally { $fs.Dispose() }
}

function Pump-MotionLog([int] $milliseconds = 0) {
    $deadline = (Get-Date).AddMilliseconds($milliseconds)
    do {
        Read-MotionLog
        if ($milliseconds -le 0) { break }
        Start-Sleep -Milliseconds 30
    } while ((Get-Date) -lt $deadline)
}

function Get-Marks([string] $name) {
    @($script:Lines | Select-String $name | ForEach-Object { $_.Line | ConvertFrom-Json })
}

function Get-LastRecord {
    # The last of a record type anywhere in the file, not just since the last read: used for values that are
    # written once and stay true, such as the broker's consumer count.
    @(Get-Content -LiteralPath $script:LogPath | Select-String $args[0] | ForEach-Object { $_.Line | ConvertFrom-Json }) | Select-Object -Last 1
}

function Move-Pointer([int]$x, [int]$y) {
    [P4D.Win]::mouse_event([P4D.Win]::MOVE -bor [P4D.Win]::ABSOLUTE,
        [int](($x * 65535) / ($script:ScreenW - 1)), [int](($y * 65535) / ($script:ScreenH - 1)), 0, [IntPtr]::Zero)
}

function Press-Pointer { [P4D.Win]::mouse_event([P4D.Win]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero) }
function Release-Pointer { [P4D.Win]::mouse_event([P4D.Win]::LEFTUP, 0, 0, 0, [IntPtr]::Zero) }

function Scroll-Wheel([int] $delta) {
    # A wheel delta is signed and the API takes an unsigned word, so the two's complement is passed through
    # rather than cast: a cast throws on every negative delta.
    $unsigned = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$delta), 0)
    [P4D.Win]::mouse_event([P4D.Win]::WHEEL, 0, 0, $unsigned, [IntPtr]::Zero)
}

function Build-Path {
    <#
        Builds an explicit list of integer positions. Written as a loop into a typed list on purpose: the
        expression form this replaced mixed a range operator into an addition, and PowerShell parsed it into
        something that was not the intended path at all — the sweep then ran over a few points and reported
        that nothing happened.
    #>
    param(
        [Parameter(Mandatory)][int] $From,
        [Parameter(Mandatory)][int] $To,
        [Parameter(Mandatory)][int] $Step
    )

    if ($Step -le 0) { throw 'step must be positive' }
    $list = New-Object System.Collections.Generic.List[int]
    if ($From -le $To) {
        for ($x = $From; $x -le $To; $x += $Step) { $list.Add($x) }
    } else {
        for ($x = $From; $x -ge $To; $x -= $Step) { $list.Add($x) }
    }
    return $list
}

function Show-Path {
    param([Parameter(Mandatory)] $Path, [Parameter(Mandatory)][string] $Label)
    $xs = @($Path)
    if ($xs.Count -eq 0) { throw "$Label is empty" }
    $min = ($xs | Measure-Object -Minimum).Minimum
    $max = ($xs | Measure-Object -Maximum).Maximum
    Write-Host ("    {0}: count={1} first={2} last={3} minX={4} maxX={5}" -f $Label, $xs.Count, $xs[0], $xs[-1], $min, $max)
    return [pscustomobject]@{ Label=$Label; Count=$xs.Count; First=$xs[0]; Last=$xs[-1]; Min=$min; Max=$max }
}
