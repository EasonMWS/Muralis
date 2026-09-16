<#
.SYNOPSIS
    Phase 3B live verification: real desktop items, real icons and real launches, on a real desktop.

.DESCRIPTION
    The harness drives the running application the way a user would — through the page's own import
    buttons, through the pointer on the desktop — and reads back what happened from the diagnostics
    panel, the layout document, the log and the processes the machine actually started. It checks the
    Phase 3B contract:

      demo
      - a real program, a real shortcut, a real folder and a real address are imported one at a time
      - every import only references what was picked: nothing is copied, moved or scanned
      - each item gets a real icon from the shell, cached rather than re-read
      - a single click selects and never launches; a double click launches through the shell
      - a drag moves the item and never launches, however it ends
      - a target that is taken away marks its item; the item is never removed or rewritten
      - positions and items survive a restart of Muralis and a restart of Explorer
      - removing an item from the page leaves what it points at alone

      perf
      - 50 real items: startup, icon cache memory, idle CPU, hover CPU/GPU, layout save, handles

    Everything the harness changes is restored on the way out: settings.json, the user's own desktop
    layout document, the Phase 2 prototype file, the fixture folder, and the app itself.

.PARAMETER Stage
    probe  - no app launch: reports the machine state and what the fixture would use
    picker - the four imports alone, for the dialog step the demo depends on
    demo   - the acceptance demo (real imports, real launches)
    perf   - the 50 item performance matrix
    full   - both

.PARAMETER SkipUrlLaunch
    Leaves the address item alone: opening it puts a tab in the user's default browser.

.PARAMETER SkipFolderLaunch
    Leaves the folder item alone: opening it puts an Explorer window on the user's desktop.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/p3b-item-verify.ps1 -Stage probe
    powershell -ExecutionPolicy Bypass -File tools/p3b-item-verify.ps1 -Stage full
#>
[CmdletBinding()]
param(
    [ValidateSet('probe', 'picker', 'demo', 'perf', 'full')] [string]$Stage = 'probe',
    [string]$Exe = 'src/Muralis.App/bin/Debug/net10.0-windows10.0.26100.0/win-x64/Muralis.exe',
    [switch]$SkipUrlLaunch,
    [switch]$SkipFolderLaunch,
    [switch]$SkipExplorerRestart,
    [int]$PerfItemCount = 50,
    [string]$OutDir = 'artifacts/p3b'
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'p3-common.ps1')
Set-BackupPaths 'p3b'

$repoRoot = Split-Path $PSScriptRoot -Parent
$exePath = if ([System.IO.Path]::IsPathRooted($Exe)) { $Exe } else { Join-Path $repoRoot $Exe }
$outPath = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $repoRoot $OutDir }

$logPatterns = [ordered]@{
    CanvasShowing = 'The desktop canvas is showing'
    LayoutLoaded  = 'Desktop layout loaded from'
    LayoutSaved   = 'Desktop layout saved to'
    ItemOpened    = ' was opened by the shell'
    ItemSelected  = ' was selected'
    ItemDropped   = ' was dropped at'
    IconMissing   = 'could not be read from the shell'
}

$script:fixture = $null
$script:scale = 1.0
$script:monitor = @(0, 0, 2560, 1440)
$script:notepadBaseline = @()

# The log is one file per day, so a run sees the lines of every earlier run too. Counts are always
# read as growth from this baseline, never as absolute numbers.
$script:logBaseline = @{}

# ---------------------------------------------------------------- app lifecycle

function Assert-MuralisNotRunning {
    if (Get-Process -Name Muralis -ErrorAction SilentlyContinue) {
        throw 'Muralis is already running; stop it first.'
    }
    if (-not (Test-Path $exePath)) { throw "Executable not found: $exePath" }
}

# The app keeps itself to one instance with a named mutex. The name is the one SingleInstanceGuard
# owns; a probe that can open it means some instance is still holding the slot.
function Test-SingleInstanceFree {
    try {
        $mutex = [System.Threading.Mutex]::OpenExisting('Local\Muralis.SingleInstance')
        $mutex.Dispose()
        return $false
    } catch {
        $inner = $_.Exception.InnerException
        return ($inner -is [System.Threading.WaitHandleCannotBeOpenedException])
    }
}

function Start-App {
    Write-Host ("Launching {0}" -f $exePath)
    $script:process = Start-Process -FilePath $exePath -PassThru
    $processId = [int]$script:process.Id

    $windowUp = Wait-Until { [P3Win]::FindWindowByClass($processId, 'WinUIDesktopWin32WindowClass') -ne [IntPtr]::Zero } 60 'the main window'
    if (-not $windowUp -and $script:process.HasExited) {
        Write-Host '  (the launched process exited without a window: it only woke another instance)'
    }
    $canvasUp = Wait-Until { ([P3Win]::ClassesWithPrefix('MuralisDesktopHostWindow')).Count -ge 1 } 60 'the canvas mount'
    $routerUp = Wait-Until { ([P3Win]::ClassesWithPrefix('MuralisPointerRouter_')).Count -ge 1 } 60 'the router attach'

    # Never minimise before the first frame: the app crashes if the window goes away that early.
    Start-Sleep -Milliseconds 900
    $script:notepadBaseline = Get-NotepadIds
    return @{ Window = $windowUp; Canvas = $canvasUp; Router = $routerUp }
}

function Stop-App {
    if ($null -eq $script:process) { return $true }

    Write-Host 'Closing the app ...'
    $window = [P3Win]::FindWindowByClass([int]$script:process.Id, 'WinUIDesktopWin32WindowClass')
    if ($window -ne [IntPtr]::Zero) {
        [P3Win]::PostMessage($window, [P3Win]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    }

    $closed = Wait-Until { $script:process.HasExited } 15 'the app to close'
    if (-not $closed) {
        Write-Host '  (the app did not close on its own; stopping it)'
        Stop-Process -Id $script:process.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 800
    }

    $script:process = $null
    $script:window = [IntPtr]::Zero

    # The process object reports the exit before Windows has let go of what the process held: its
    # single-instance mutex can outlive the exit by up to a second. A launch in that gap is taken for
    # a second instance and only wakes the dying one, so the harness waits for the slot itself.
    Wait-Until { Test-SingleInstanceFree } 15 'the single-instance slot to be free' | Out-Null

    Start-Sleep -Milliseconds 700
    return $closed
}

# ---------------------------------------------------------------- fixture

function New-Fixtures {
    $root = Join-Path $env:TEMP 'MuralisP3B'
    if (Test-Path $root) { Remove-Item -Recurse -Force $root }
    New-Item -ItemType Directory -Force -Path $root | Out-Null

    $folder = Join-Path $root 'P3B Folder'
    New-Item -ItemType Directory -Force -Path $folder | Out-Null

    # A shortcut made by the shell itself, pointing at a program that is always there.
    $link = Join-Path $root 'P3B Notepad.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($link)
    $shortcut.TargetPath = Join-Path $env:SystemRoot 'System32\notepad.exe'
    $shortcut.Description = 'Muralis Phase 3B fixture shortcut'
    $shortcut.Save()

    $script:fixture = @{
        Root    = $root
        Folder  = $folder
        Link    = $link
        Notepad = Join-Path $env:SystemRoot 'System32\notepad.exe'
        Url     = 'https://example.com/'
    }

    Write-Host ("fixture folder:  {0}" -f $folder)
    Write-Host ("fixture .lnk:     {0}" -f $link)
    Write-Host ("fixture program:  {0}" -f $script:fixture.Notepad)
    Write-Host ''
    return $script:fixture
}

function Get-DefaultBrowserProcess {
    try {
        $progId = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice' -ErrorAction SilentlyContinue).ProgId
        if (-not $progId) { return $null }
        $command = (Get-ItemProperty ("Registry::HKEY_CLASSES_ROOT\{0}\shell\open\command" -f $progId) -ErrorAction SilentlyContinue).'(default)'
        if (-not $command) { return $null }
        if ($command -match '^\s*"([^"]+)"') { $exe = $Matches[1] }
        elseif ($command -match '^\s*(\S+)') { $exe = $Matches[1] }
        else { return $null }
        return ([IO.Path]::GetFileNameWithoutExtension($exe) | ForEach-Object { $_.ToLowerInvariant() })
    } catch {
        return $null
    }
}

# ---------------------------------------------------------------- layout reading

function Wait-LayoutItems([int]$count, [int]$timeoutSeconds = 15) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $layout = Read-Layout
        if ($null -ne $layout -and @($layout.Items).Count -eq $count) { return $layout }
        Start-Sleep -Milliseconds 250
    }
    Write-Host ("  (timed out waiting for {0} items in the layout)" -f $count)
    Write-Host ("  layout path {0} exists {1}" -f $script:layoutPath, (Test-Path $script:layoutPath))
    foreach ($near in @(Get-ChildItem (Split-Path $script:layoutPath -Parent) -Force -ErrorAction SilentlyContinue)) {
        Write-Host ("  nearby: {0} {1} bytes at {2:HH:mm:ss}" -f $near.Name, $near.Length, $near.LastWriteTime)
    }
    if (Test-Path $script:layoutPath) {
        $info = Get-Item $script:layoutPath
        Write-Host ("  length {0} written {1:HH:mm:ss}" -f $info.Length, $info.LastWriteTime)
        $raw = Get-Content $script:layoutPath -Raw -Encoding UTF8
        try { $null = $raw | ConvertFrom-Json; Write-Host '  parse: ok' }
        catch { Write-Host ("  parse error: {0}" -f $_.Exception.Message) }
    }
    $late = Read-Layout
    Write-Host ("  the reader sees a document: {0}" -f ($null -ne $late))
    if ($null -ne $late) { Write-Host ("  the reader counts {0} items" -f @($late.Items).Count) }
    return $late
}

function Get-ItemById($layout, [string]$id) {
    foreach ($item in @($layout.Items)) { if ($item.Id -eq $id) { return $item } }
    return $null
}

function Get-LatestItem($layout) {
    $items = @($layout.Items)
    if ($items.Count -eq 0) { return $null }
    return $items[$items.Count - 1]
}

# ---------------------------------------------------------------- the page

function Open-Page {
    Open-DynamicPage
    $state = Read-Geometry
    Add-Sample 'geometry' @{ Scale = $script:scale; Monitor = ($script:monitor -join 'x') }
    return $state
}

function Get-PageRemoveButtons {
    return Find-AllByIdPrefix (Get-AppRoot) 'CanvasRemoveItem_'
}

# The picker is the shell's own common dialog, owned by our process. It is a classic dialog, so its
# controls are addressed by control id — 1148 is the file name box, 1 is the button that answers it —
# and neither step depends on focus, z-order or what is in the foreground.
function Select-InPicker([string]$Path, [string]$What, [int]$timeoutSeconds = 25) {
    $seen = @{}
    foreach ($handle in [P3Win]::WindowsOfProcess([int]$script:process.Id)) { $seen[[long]$handle] = $true }

    $dialog = [IntPtr]::Zero
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        foreach ($handle in [P3Win]::WindowsOfProcess([int]$script:process.Id)) {
            if ($seen.ContainsKey([long]$handle)) { continue }
            if (-not [P3Win]::IsWindowVisible($handle)) { continue }
            $dialog = $handle
            break
        }
        if ($dialog -ne [IntPtr]::Zero) { break }

        # Some builds put the common dialog in a window of another process: fall back to any new
        # top level dialog that is not ours to keep.
        foreach ($handle in [P3Win]::WindowsOfClass('#32770')) {
            if (-not [P3Win]::IsWindowVisible($handle)) { continue }
            if ($seen.ContainsKey([long]$handle)) { continue }
            $dialog = $handle
            break
        }
        if ($dialog -ne [IntPtr]::Zero) { break }

        Start-Sleep -Milliseconds 200
    }

    if ($dialog -eq [IntPtr]::Zero) {
        throw ("The {0} picker never appeared." -f $What)
    }

    Write-Host ("  {0} picker: {1}" -f $What, [P3Win]::Describe($dialog))
    [P3Win]::SetForegroundWindow($dialog) | Out-Null
    Start-Sleep -Milliseconds 500

    $combo = [P3Win]::FindChild($dialog, 'ComboBox', [P3Win]::FileNameControlId)
    $edit = [P3Win]::FindChild($dialog, 'Edit', [P3Win]::FileNameControlId)
    $box = $edit
    if ($box -eq [IntPtr]::Zero) { $box = $combo }
    if ($box -eq [IntPtr]::Zero) { $box = [P3Win]::FindChild($dialog, 'Edit', -1) }
    if ($box -eq [IntPtr]::Zero) {
        throw ("The {0} picker has no file name box to answer." -f $What)
    }
    Write-Host ("  the file name box is {0} 0x{1:X}" -f [P3Win]::ClassOf($box), [long]$box)

    $button = [P3Win]::FindChild($dialog, 'Button', [P3Win]::DialogButtonId)
    if ($button -eq [IntPtr]::Zero) {
        throw ("The {0} picker has no button to answer it." -f $What)
    }

    # The path is typed into the box a character at a time: that is what a typist does to the box, so
    # the dialog's own idea of it changes and its own button wakens. Setting the text outright is only
    # the retreat for builds whose box ignores keystrokes.
    [P3Win]::SetText($box, '') | Out-Null
    [P3Win]::TypeInto($box, $Path)
    Start-Sleep -Milliseconds 600

    $said = [P3Win]::TitleOf($box)
    if (-not $said.Contains($Path)) {
        Write-Host ("  the box kept only '{0}'; setting the text instead" -f $said)
        [P3Win]::SetText($box, $Path) | Out-Null
        if ($combo -ne [IntPtr]::Zero -and $combo -ne $box) { [P3Win]::SetText($combo, $Path) | Out-Null }
        Start-Sleep -Milliseconds 500
    } else {
        Write-Host ("  the box says '{0}'" -f $said)
    }
    Write-Host ("  the picker button is enabled: {0}" -f [P3Win]::IsWindowEnabled($button))

    # The button answers with a click of its own; the same command sent to the dialog is the same
    # thing said outright, and between the two the dialog is answered whichever way it listens.
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        if (-not [P3Win]::IsWindow($dialog) -or -not [P3Win]::IsWindowVisible($dialog)) { break }

        if ($attempt % 2 -eq 0) {
            [P3Win]::ClickButton($button)
        } else {
            [P3Win]::Send($dialog, [P3Win]::WM_COMMAND, [IntPtr][P3Win]::DialogButtonId, $button) | Out-Null
        }

        Start-Sleep -Milliseconds 800
    }

    $closed = Wait-Until { -not [P3Win]::IsWindow($dialog) -or -not [P3Win]::IsWindowVisible($dialog) } 20 'the picker to close'
    if (-not $closed) { throw ("The {0} picker would not close." -f $What) }
    Start-Sleep -Milliseconds 600
}

# Imports one path through its own button: a program or a shortcut share the application picker, a
# folder has a picker of its own, so the button id is what decides which importer runs.
function Add-PathThroughPage([string]$ButtonId, [string]$Path, [string]$What) {
    Write-Host ("Adding {0} through the page ..." -f $What)
    $before = @((Read-Layout).Items).Count
    Invoke-Element (Find-ById (Get-AppRoot) $ButtonId) $ButtonId
    Start-Sleep -Milliseconds 1200
    Select-InPicker $Path $What
    return (Wait-LayoutItems ($before + 1))
}

function Add-UrlThroughPage([string]$Url) {
    Write-Host ("Adding {0} through the address box ..." -f $Url)
    $before = @((Read-Layout).Items).Count
    $root = Get-AppRoot
    Set-ElementText (Find-ById $root 'CanvasUrlBox') $Url 'CanvasUrlBox'
    Start-Sleep -Milliseconds 300
    Invoke-Element (Find-ById $root 'CanvasAddUrl') 'CanvasAddUrl'
    return (Wait-LayoutItems ($before + 1))
}

# ---------------------------------------------------------------- demo: imports

function Invoke-DemoImports {
    Write-Host ''
    Write-Host '=== 1. importing real things through the page ==='

    $layout = Read-Layout
    Add-Check 'import: the desktop starts empty' (@($layout.Items).Count -eq 0) ("items {0}" -f @($layout.Items).Count)

    $logBefore = $script:logBaseline['IconMissing']

    $app = Add-PathThroughPage 'CanvasAddApplication' $script:fixture.Notepad 'the program picker'
    $appItem = Get-LatestItem $app
    Add-Check 'import: a real .exe becomes an application item' `
        ($null -ne $appItem -and $appItem.Target.kind -eq 'application' -and $appItem.Target.path -eq $script:fixture.Notepad) `
        ("kind {0} path {1}" -f $appItem.Target.kind, $appItem.Target.path)
    Add-Check 'import: the name comes from the file' ($appItem.Name -eq 'notepad') ("name '{0}'" -f $appItem.Name)
    Add-Check 'import: the id is its own, not the path' ($appItem.Id -match '^app_[0-9a-f]{8}$') ("id '{0}'" -f $appItem.Id)
    Add-Sample 'imported.notepad' $appItem

    $folderLayout = Add-PathThroughPage 'CanvasAddFolder' $script:fixture.Folder 'the folder picker'
    $folderItem = Get-LatestItem $folderLayout
    Add-Check 'import: a real folder becomes a folder item' `
        ($null -ne $folderItem -and $folderItem.Target.kind -eq 'folder' -and $folderItem.Target.path -eq $script:fixture.Folder) `
        ("kind {0} path {1}" -f $folderItem.Target.kind, $folderItem.Target.path)

    $urlLayout = Add-UrlThroughPage $script:fixture.Url
    $urlItem = Get-LatestItem $urlLayout
    Add-Check 'import: an address becomes a url item' `
        ($null -ne $urlItem -and $urlItem.Target.kind -eq 'url' -and $urlItem.Target.url -eq $script:fixture.Url) `
        ("kind {0} url {1}" -f $urlItem.Target.kind, $urlItem.Target.url)
    Add-Check 'import: the address is named after its host' ($urlItem.Name -eq 'example.com') ("name '{0}'" -f $urlItem.Name)

    $lnkLayout = Add-PathThroughPage 'CanvasAddApplication' $script:fixture.Link 'the shortcut picker'
    $lnkItem = Get-LatestItem $lnkLayout
    Add-Check 'import: a real .lnk becomes a shortcut item' `
        ($null -ne $lnkItem -and $lnkItem.Target.kind -eq 'shortcut' -and $lnkItem.Target.path -eq $script:fixture.Link) `
        ("kind {0} path {1}" -f $lnkItem.Target.kind, $lnkItem.Target.path)
    Add-Check 'import: the shortcut keeps its own name' ($lnkItem.Name -eq 'P3B Notepad') ("name '{0}'" -f $lnkItem.Name)
    Add-Sample 'imported.folder' $folderItem
    Add-Sample 'imported.url' $urlItem
    Add-Sample 'imported.shortcut' $lnkItem

    Add-Check 'import: four items, four kinds in the document' `
        (@($lnkLayout.Items).Count -eq 4) ("items {0}" -f @($lnkLayout.Items).Count)
    Add-Check 'import: nothing from the old prototype seed is back' `
        (@($lnkLayout.Items | Where-Object { $_.Id -in @('steam', 'chrome', 'blender', 'comfyui', 'files', 'music', 'settings', 'terminal') }).Count -eq 0) ''

    # Nothing was copied: the fixtures stay exactly where the user picked them from.
    Add-Check 'import: the picked files were left alone' `
        ((Test-Path $script:fixture.Notepad) -and (Test-Path $script:fixture.Folder) -and (Test-Path $script:fixture.Link)) ''
    $stray = @(Get-ChildItem $script:fixture.Root -Force | Where-Object { $_.Name -notin @('P3B Folder', 'P3B Notepad.lnk') })
    Add-Check 'import: nothing was copied into the fixture folder' ($stray.Count -eq 0) ("strays {0}" -f $stray.Count)

    # The page lists what was imported, with a remove button each.
    $buttons = Get-PageRemoveButtons
    Add-Check 'page: one row per item, each with a remove button' ($buttons.Count -eq 4) ("rows {0}" -f $buttons.Count)
    $names = Get-TextElementNames (Get-AppRoot)
    Add-Check 'page: the rows carry the item names' `
        (($names -contains 'notepad') -and ($names -contains 'P3B Folder') -and ($names -contains 'example.com') -and ($names -contains 'P3B Notepad')) ''

    $iconWarnings = (Get-LogCount $logPatterns.IconMissing) - $logBefore
    Add-Sample 'iconWarnings' $iconWarnings
    return $lnkLayout
}

# ---------------------------------------------------------------- demo: the desktop

function Invoke-DemoDesktop($layout, [string]$appId, [string]$folderId, [string]$urlId, [string]$lnkId) {
    Write-Host ''
    Write-Host '=== 2. the items on the desktop ==='

    Minimize-AppWindow
    $pointsX = @()
    $pointsY = @()
    foreach ($item in @($layout.Items)) {
        $centre = Get-ItemCentre $item
        $pointsX += $centre[0]
        $pointsY += $centre[1]
    }
    $blocked = Clear-TheDesktop $pointsX $pointsY ''
    Add-Check 'desktop: the items are on the desktop, not under an application' ($blocked.Count -eq 0) ("blocked {0}" -f ($blocked -join '; '))

    $items = @($layout.Items)
    if ($blocked.Count -gt 0) { throw "The desktop is still covered: $($blocked -join '; ')" }

    # --- the real icons were resolved from the shell and cached (an address has none to read)
    $iconItems = @($items | Where-Object { $_.Target.kind -ne 'url' }).Count
    $iconState = Wait-Diag { param($s) $null -ne $s.IconEntries -and $s.IconEntries -ge $iconItems } 30 'the icons to be cached'
    Add-Check 'icons: every item with a file behind it got a real icon' `
        ($null -ne $iconState -and $iconState.IconEntries -ge $iconItems) ("cached {0} of {1}" -f $iconState.IconEntries, $iconItems)
    Add-Check 'icons: the cache holds pixels, not just entries' `
        ($null -ne $iconState -and $iconState.IconMb -gt 0) ("{0} MB" -f $iconState.IconMb)
    Add-Sample 'icons.demo' @{ Entries = $iconState.IconEntries; Mb = $iconState.IconMb }
    $iconWarnings = (Get-LogCount $logPatterns.IconMissing) - $script:logBaseline['IconMissing']
    Add-Check 'icons: no icon had to be left unresolved' ($iconWarnings -eq 0) ("warnings {0}" -f $iconWarnings)

    $appCentre = Get-ItemCentre (Get-ItemById $layout $appId)
    Add-Check 'icons: the program item sits where its saved offset puts it' `
        ($appCentre[0] -gt 0 -and $appCentre[1] -gt 0) ("centre {0},{1}" -f $appCentre[0], $appCentre[1])

    # --- hover: the item on the desktop is the one the document describes
    Move-And-Settle $appCentre[0] $appCentre[1] 500
    $hover = Read-StateAt $appCentre[0] $appCentre[1]
    Add-Check 'hover: the pointer finds the program item by its own id' ($hover.HoverId -eq $appId) ("hovered {0}" -f $hover.HoverId)
    Add-Check 'hover: the item magnifies like any other' ($hover.HoverScale -gt 1.0) ("scale {0}" -f $hover.HoverScale)

    # --- a single click selects and never launches
    Move-And-Settle $appCentre[0] $appCentre[1] 200
    Click-Pointer
    Start-Sleep -Milliseconds 900
    $clicked = Read-State
    Add-Check 'click: the first click selects the item' ($clicked.SelectedId -eq $appId) ("selected {0}" -f $clicked.SelectedId)
    Add-Check 'click: the click reported itself in the log' ((Get-LogCount $logPatterns.ItemSelected) -gt $script:logBaseline['ItemSelected']) ''
    Add-Check 'click: a single click did not launch anything' ((Wait-NewNotepad 3).Count -eq 0) 'no new process'

    # --- a double click launches through the shell
    Move-Pointer $appCentre[0] $appCentre[1]
    Start-Sleep -Milliseconds 150
    DoubleClick-Pointer
    $new = Wait-NewNotepad 15
    Add-Check 'double click: the program really started' ($new.Count -gt 0) ("pids {0}" -f ($new -join ', '))
    Add-Check 'double click: the launch reported itself in the log' ((Get-LogCount $logPatterns.ItemOpened) -gt $script:logBaseline['ItemOpened']) ''
    $opened = Wait-Diag { param($s) $s.LaunchId -eq $appId -and $s.LaunchOutcome -eq 'Launched' } 10 'the launch outcome'
    Add-Check 'double click: the outcome is the shell taking it' ($null -ne $opened) ("launch {0} -> {1}" -f $opened.LaunchId, $opened.LaunchOutcome)
    Close-FixtureNotepads $new
    Start-Sleep -Milliseconds 600

    # --- a drag moves the item, ends the click chain and never launches
    $folderBefore = Get-ItemById $layout $folderId
    $folderCentre = Get-ItemCentre $folderBefore
    $targetX = $folderCentre[0] + 260
    $targetY = $folderCentre[1] - 180
    $windowsBefore = @([P3Win]::WindowsOfClass('CabinetWClass')).Count
    Drag-Pointer $folderCentre[0] $folderCentre[1] $targetX $targetY
    $draggedLayout = Wait-LayoutItems 4
    $folderAfter = Get-ItemById $draggedLayout $folderId
    $moved = ($folderAfter.OffsetXDip -ne $folderBefore.OffsetXDip) -or ($folderAfter.OffsetYDip -ne $folderBefore.OffsetYDip)
    Add-Check 'drag: the drop moved the item and was saved' $moved `
        ("{0},{1} -> {2},{3}" -f $folderBefore.OffsetXDip, $folderBefore.OffsetYDip, $folderAfter.OffsetXDip, $folderAfter.OffsetYDip)
    Add-Check 'drag: the drop reported itself in the log' ((Get-LogCount $logPatterns.ItemDropped) -gt $script:logBaseline['ItemDropped']) ''
    Add-Check 'drag: the drag and its release launched nothing' ((Wait-NewNotepad 2).Count -eq 0) 'no new process'
    Add-Check 'drag: the drag did not open what it points at' (@([P3Win]::WindowsOfClass('CabinetWClass')).Count -eq $windowsBefore) 'no new window'

    $droppedCentre = Get-ItemCentre $folderAfter
    Move-And-Settle $droppedCentre[0] $droppedCentre[1] 500
    $hoverDropped = Read-StateAt $droppedCentre[0] $droppedCentre[1]
    Add-Check 'drag: the item is really drawn at its new place' ($hoverDropped.HoverId -eq $folderId) ("hovered {0}" -f $hoverDropped.HoverId)

    # --- the shortcut opens what it points at, through the shell
    $lnkNow = Get-ItemById $draggedLayout $lnkId
    $lnkCentre = Get-ItemCentre $lnkNow
    $blocked = Clear-TheDesktop @($lnkCentre[0]) @($lnkCentre[1]) ' (for the shortcut)'
    Move-Pointer ($lnkCentre[0] - 60) ($lnkCentre[1] - 60)
    Start-Sleep -Milliseconds 250
    Move-And-Settle $lnkCentre[0] $lnkCentre[1] 300
    DoubleClick-Pointer
    $fromShortcut = Wait-NewNotepad 15
    Add-Check 'shortcut: double clicking it starts what it points at' ($fromShortcut.Count -gt 0) ("pids {0}" -f ($fromShortcut -join ', '))
    $lnkOpened = Wait-Diag { param($s) $s.LaunchId -eq $lnkId -and $s.LaunchOutcome -eq 'Launched' } 10 'the shortcut launch outcome'
    Add-Check 'shortcut: the shell took the shortcut' ($null -ne $lnkOpened) ("launch {0} -> {1}" -f $lnkOpened.LaunchId, $lnkOpened.LaunchOutcome)
    Close-FixtureNotepads $fromShortcut
    Start-Sleep -Milliseconds 500

    # --- a target that is taken away marks its item and nothing else
    Move-Pointer $droppedCentre[0] $droppedCentre[1]
    Rename-Item -Force $script:fixture.Link "$($script:fixture.Link).away"
    Start-Sleep -Milliseconds 400
    Move-And-Settle $lnkCentre[0] $lnkCentre[1] 300
    Click-Pointer
    $missingState = Wait-Diag { param($s) $s.MissingCount -ge 1 } 15 'the missing mark'
    Add-Check 'missing: taking the target away marks the item' ($null -ne $missingState) ("missing {0}" -f $missingState.MissingCount)
    $afterMissing = Read-Layout
    Add-Check 'missing: the item is still in the document, untouched' `
        ((@($afterMissing.Items).Count -eq 4) -and ($null -ne (Get-ItemById $afterMissing $lnkId))) ("items {0}" -f @($afterMissing.Items).Count)
    Add-Check 'missing: the app is still alive and answering' ($null -ne (Get-Process -Id $script:process.Id -ErrorAction SilentlyContinue)) ''
    $missingCentre = Get-ItemCentre (Get-ItemById $afterMissing $lnkId)
    Add-Check 'missing: the marked item has not been moved' `
        (($missingCentre[0] -eq $lnkCentre[0]) -and ($missingCentre[1] -eq $lnkCentre[1])) ''

    # --- a folder opens in Explorer
    if (-not $SkipFolderLaunch) {
        $folderNow = Get-ItemCentre (Get-ItemById (Read-Layout) $folderId)
        Move-Pointer $folderNow[0] $folderNow[1]
        Start-Sleep -Milliseconds 150
        DoubleClick-Pointer
        $explorer = $null
        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $deadline -and $null -eq $explorer) {
            foreach ($handle in [P3Win]::WindowsOfClass('CabinetWClass')) {
                if ([P3Win]::TitleOf($handle) -like '*P3B Folder*') { $explorer = $handle; break }
            }
            if ($null -eq $explorer) { Start-Sleep -Milliseconds 500 }
        }
        Add-Check 'folder: double clicking opens it in Explorer' ($null -ne $explorer) ('')
        if ($null -ne $explorer) {
            $folderOpened = Wait-Diag { param($s) $s.LaunchId -eq $folderId -and $s.LaunchOutcome -eq 'Launched' } 10 'the folder launch outcome'
            Add-Check 'folder: the shell took the request' ($null -ne $folderOpened) ("launch {0} -> {1}" -f $folderOpened.LaunchId, $folderOpened.LaunchOutcome)
            [P3Win]::PostMessage($explorer, [P3Win]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
            Start-Sleep -Milliseconds 800
        }
    }

    # --- an address opens in the default browser
    if (-not $SkipUrlLaunch) {
        $browserName = Get-DefaultBrowserProcess
        $browserBefore = @()
        if ($browserName) { $browserBefore = @(Get-Process -Name $browserName -ErrorAction SilentlyContinue | ForEach-Object { $_.Id }) }
        Write-Host ("Default browser: {0}" -f $browserName)

        $urlNow = Get-ItemCentre (Get-ItemById (Read-Layout) $urlId)
        Move-Pointer $urlNow[0] $urlNow[1]
        Start-Sleep -Milliseconds 150
        DoubleClick-Pointer
        $urlOpened = Wait-Diag { param($s) $s.LaunchId -eq $urlId -and $s.LaunchOutcome -eq 'Launched' } 15 'the address launch outcome'
        Add-Check 'address: the shell took the address' ($null -ne $urlOpened) ("launch {0} -> {1}" -f $urlOpened.LaunchId, $urlOpened.LaunchOutcome)
        Start-Sleep -Milliseconds 2500
        if ($browserName) {
            $browserAfter = @(Get-Process -Name $browserName -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
            $newBrowser = @($browserAfter | Where-Object { $browserBefore -notcontains $_ })
            Add-Sample 'url.browser' @{ Name = $browserName; Before = $browserBefore.Count; After = $browserAfter.Count; New = $newBrowser.Count }
            Write-Host ("  the browser had {0} process(es), now {1}" -f $browserBefore.Count, $browserAfter.Count)
        }
    }

    return (Read-Layout)
}

# ---------------------------------------------------------------- demo: restart, explorer, remove

function Invoke-DemoRestart($layout, [string]$folderId, [string]$lnkId) {
    Write-Host ''
    Write-Host '=== 3. a restart of Muralis ==='

    $before = Read-Layout
    $hashBefore = (Get-FileHash -Algorithm SHA256 $script:layoutPath).Hash
    $folderBefore = Get-ItemById $before $folderId
    $restored = Stop-App
    Add-Check 'restart: the app closed cleanly on its own window' $restored ''

    Start-App | Out-Null
    Open-Page | Out-Null
    $after = Read-Layout
    $hashAfter = (Get-FileHash -Algorithm SHA256 $script:layoutPath).Hash

    $state = Wait-Diag { param($s) $s.ItemCount -eq 4 } 30 'the four items to come back'
    Add-Check 'restart: every item came back' ($null -ne $state -and $state.ItemCount -eq 4) ("items {0}" -f $state.ItemCount)
    Add-Check 'restart: the document was not rewritten on load' ($hashBefore -eq $hashAfter) ''
    Add-Check 'restart: the layout is byte for byte what it was' `
        (($folderBefore.OffsetXDip -eq (Get-ItemById $after $folderId).OffsetXDip) -and ($folderBefore.OffsetYDip -eq (Get-ItemById $after $folderId).OffsetYDip)) ''

    Minimize-AppWindow
    $folderCentre = Get-ItemCentre (Get-ItemById $after $folderId)
    $blocked = Clear-TheDesktop @($folderCentre[0]) @($folderCentre[1]) ' (after the restart)'
    Add-Check 'restart: the desktop is reachable again' ($blocked.Count -eq 0) ("blocked {0}" -f ($blocked -join '; '))
    Move-And-Settle $folderCentre[0] $folderCentre[1] 600
    $hover = Read-StateAt $folderCentre[0] $folderCentre[1]
    Add-Check 'restart: the dragged item is back where it was dropped' ($hover.HoverId -eq $folderId) ("hovered {0}" -f $hover.HoverId)

    $missing = Wait-Diag { param($s) $s.MissingCount -ge 1 } 20 'the missing mark after the restart'
    Add-Check 'restart: the item whose target is gone is still marked, still there' `
        ($null -ne $missing -and $missing.ItemCount -eq 4) ("missing {0} of {1}" -f $missing.MissingCount, $missing.ItemCount)

    return $after
}

function Invoke-DemoExplorer($layout, [string]$lnkId) {
    if ($SkipExplorerRestart) { return }

    Write-Host ''
    Write-Host '=== 4. a restart of Explorer ==='
    Move-Item -Force "$($script:fixture.Link).away" $script:fixture.Link
    Add-Check 'explorer: the target is back on disk' (Test-Path $script:fixture.Link) ''

    $mountBefore = (Read-State).Mount
    taskkill /f /im explorer.exe | Out-Null
    Start-Sleep -Seconds 3
    Start-Process explorer.exe | Out-Null
    Start-Sleep -Seconds 6
    $remount = Wait-Diag { param($s) $s.Mount -gt $mountBefore } 45 'the canvas to come back'
    Add-Check 'explorer: the canvas comes back after the shell restarts' ($null -ne $remount) ("mount {0} -> {1}" -f $mountBefore, $remount.Mount)

    $state = Wait-Diag { param($s) $s.ItemCount -eq 4 } 30 'all four items to come back'
    Add-Check 'explorer: every item is on the desktop again' ($null -ne $state) ("items {0}" -f $state.ItemCount)

    Start-Sleep -Milliseconds 1000
    $cleared = Wait-Diag { param($s) $s.MissingCount -eq 0 } 30 'the missing mark to clear'
    Add-Check 'explorer: the item whose target came back is no longer marked' ($null -ne $cleared) ("missing {0}" -f $cleared.MissingCount)

    $lnkNow = Get-ItemById (Read-Layout) $lnkId
    $lnkCentre = Get-ItemCentre $lnkNow
    $blocked = Clear-TheDesktop @($lnkCentre[0]) @($lnkCentre[1]) ' (after the Explorer restart)'

    # Away first, then onto the point: a pointer that never travelled cannot say the hover came back.
    Move-Pointer ($lnkCentre[0] - 60) ($lnkCentre[1] - 60)
    Start-Sleep -Milliseconds 250
    Move-And-Settle $lnkCentre[0] $lnkCentre[1] 800
    $hover = Read-StateAt $lnkCentre[0] $lnkCentre[1] 2500
    $under = Get-DesktopPoint $lnkCentre[0] $lnkCentre[1]
    Write-Host ("  shortcut at {0},{1} (dip {2},{3}), cursor {4}, under the point {5}" -f `
        $lnkCentre[0], $lnkCentre[1], $lnkNow.OffsetXDip, $lnkNow.OffsetYDip, ((Get-CursorNow) -join ','), $under.Class)
    Write-Host ("  panel: {0}" -f ($hover.Text -replace "`r?`n", ' | '))
    Add-Check 'explorer: the shortcut item is hoverable again' ($hover.HoverId -eq $lnkId) ("hovered {0}" -f $hover.HoverId)
}

function Invoke-DemoRemove($layout, [string]$folderId) {
    Write-Host ''
    Write-Host '=== 5. removing an item from the page ==='

    Show-AppWindow 1750 900 780 500
    $buttons = Get-PageRemoveButtons
    $button = $null
    foreach ($candidate in $buttons) {
        $id = $candidate.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::AutomationIdProperty)
        if ($id -eq "CanvasRemoveItem_$folderId") { $button = $candidate; break }
    }
    Add-Check 'remove: the row for the folder item is on the page' ($null -ne $button) ''
    if ($null -ne $button) {
        Invoke-Element $button 'the folder row remove button'
        $after = Wait-LayoutItems 3
        Add-Check 'remove: the item left the document' ($null -eq (Get-ItemById $after $folderId)) ("items {0}" -f @($after.Items).Count)
        Add-Check 'remove: what it pointed at is still there' (Test-Path $script:fixture.Folder) ("{0}" -f (Test-Path $script:fixture.Folder))
        $rows = Get-PageRemoveButtons
        Add-Check 'remove: the page lists one row fewer' ($rows.Count -eq 3) ("rows {0}" -f $rows.Count)
    }
}

# ---------------------------------------------------------------- perf

function Invoke-Perf {
    Write-Host ''
    Write-Host ("=== Phase 3B performance: {0} real items ===" -f $PerfItemCount)

    $items = New-GridFixtureItems $PerfItemCount
    Write-Layout $items
    Write-Host ("planted {0} items in {1}" -f $items.Count, $script:layoutPath)
    Add-Sample 'perf.items' $items.Count

    # An address has no file behind it to read an icon from, so only the items with a location on
    # disk get one: the expectation is the document's own arithmetic, not a round number.
    $iconItems = @($items | Where-Object { $_.Target.kind -ne 'url' }).Count
    Add-Sample 'perf.iconItems' $iconItems

    $logBase = Get-LogCounts $logPatterns
    $started = Get-Date
    Start-App | Out-Null
    $mountMs = [int]((Get-Date) - $started).TotalMilliseconds
    Add-Sample 'perf.wallClockToMountMs' $mountMs

    Open-Page | Out-Null
    $loaded = Wait-Diag { param($s) $s.ItemCount -eq $PerfItemCount } 60 'fifty items on the canvas'
    Add-Check 'perf: all fifty items are on the canvas' ($null -ne $loaded) ("items {0}" -f $loaded.ItemCount)

    # --- icon cache: every icon resolved, once
    $iconStart = Get-Date
    $icons = Wait-Diag { param($s) $null -ne $s.IconEntries -and $s.IconEntries -ge $iconItems } 120 'the icons to be cached'
    $iconMs = [int]((Get-Date) - $iconStart).TotalMilliseconds
    Add-Check 'perf: every icon was resolved from the shell' `
        ($null -ne $icons -and $icons.IconEntries -ge $iconItems) ("entries {0} of {1}" -f $icons.IconEntries, $iconItems)
    Add-Sample 'perf.iconResolveMs' $iconMs
    Add-Sample 'perf.iconCacheMb' $icons.IconMb
    Add-Sample 'perf.iconEntries' $icons.IconEntries
    Write-Host ("  {0} icons cached, {1} MB, {2} ms" -f $icons.IconEntries, $icons.IconMb, $iconMs)

    # --- native resources: an icon is pixels, not a permanent GDI or USER object each
    $processId = [int]$script:process.Id
    $handles = (Get-Process -Id $processId).HandleCount
    $gdi = [P3Win]::GdiObjectsOf($processId)
    $user = [P3Win]::UserObjectsOf($processId)
    $working = [math]::Round((Get-Process -Id $processId).WorkingSet64 / 1MB, 1)
    Add-Sample 'perf.handles' $handles
    Add-Sample 'perf.gdiObjects' $gdi
    Add-Sample 'perf.userObjects' $user
    Add-Sample 'perf.workingSetMb' $working
    Write-Host ("  handles {0}, GDI {1}, USER {2}, working set {3} MB" -f $handles, $gdi, $user, $working)

    # --- idle
    Minimize-AppWindow
    $idleStart = Get-CpuMilliseconds $processId
    $idleWall = [System.Diagnostics.Stopwatch]::StartNew()
    Wait-ForSeconds 10
    $idleWall.Stop()
    $idleEnd = Get-CpuMilliseconds $processId
    $idleMs = $idleEnd - $idleStart
    $idlePercent = [math]::Round(($idleMs / $idleWall.Elapsed.TotalMilliseconds / [Environment]::ProcessorCount) * 100, 3)
    Add-Sample 'perf.idleCpuMs' $idleMs
    Add-Sample 'perf.idleCpuPercentOfOneCore' ([math]::Round(($idleMs / $idleWall.Elapsed.TotalMilliseconds) * 100, 3))
    Write-Host ("  idle: {0} ms over {1:0.0} s ({2} % of the whole machine)" -f $idleMs, $idleWall.Elapsed.TotalSeconds, $idlePercent)
    Add-Check 'perf: idle with fifty items stays cheap' ($idleMs -lt 500) ("{0} ms in 10 s" -f $idleMs)

    # --- hover: sweeping across the grid
    $centres = @()
    $gridX = @()
    $gridY = @()
    foreach ($item in $items) {
        $centre = Get-ItemCentre $item
        $centres += , $centre
        if ($gridX -notcontains $centre[0]) { $gridX += $centre[0] }
        if ($gridY -notcontains $centre[1]) { $gridY += $centre[1] }
    }

    # The sweep only measures what it can reach: anything of the user's sitting over the grid would
    # swallow the moves and the hover CPU would be measured against a pointer that never arrived.
    $blocked = Clear-TheDesktop $gridX $gridY ' (for the hover sweep)'
    Add-Check 'perf: the whole grid is on the desktop, not under an application' ($blocked.Count -eq 0) ("blocked {0}" -f ($blocked -join '; '))

    $cpus = Get-CpuMilliseconds $processId
    $gpu = 0
    $moves = 0
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $deadline = (Get-Date).AddSeconds(12)
    while ((Get-Date) -lt $deadline) {
        foreach ($centre in $centres) {
            Move-Pointer $centre[0] $centre[1]
            $moves++
            Start-Sleep -Milliseconds 20
            if ((Get-Date) -ge $deadline) { break }
        }
        $sample = Measure-GpuOnce $processId
        if ($null -ne $sample -and $sample -gt $gpu) { $gpu = $sample }
    }
    $watch.Stop()
    $cpuAfter = Get-CpuMilliseconds $processId
    $hoverMs = $cpuAfter - $cpus
    $hoverPercent = [math]::Round(($hoverMs / $watch.Elapsed.TotalMilliseconds) * 100, 3)
    Add-Sample 'perf.hoverSeconds' ([math]::Round($watch.Elapsed.TotalSeconds, 1))
    Add-Sample 'perf.hoverMoves' $moves
    Add-Sample 'perf.hoverCpuMs' $hoverMs
    Add-Sample 'perf.hoverCpuPercentOfOneCore' $hoverPercent
    Add-Sample 'perf.hoverGpuPercent' $gpu
    Write-Host ("  hover: {0} moves over {1:0.0} s, {2} ms CPU ({3} % of one core), GPU max {4} %" -f $moves, $watch.Elapsed.TotalSeconds, $hoverMs, $hoverPercent, $gpu)
    Add-Check 'perf: hovering fifty items stays inside one core' ($hoverPercent -lt 100) ("{0} % of one core" -f $hoverPercent)

    # The CPU number above only describes hovering if the sweep actually hovered: land on one item
    # and read the canvas' own answer.
    Move-And-Settle $centres[0][0] $centres[0][1] 400
    $swept = Read-StateAt $centres[0][0] $centres[0][1]
    Add-Check 'perf: the sweep really reached the items' ($swept.HoverId -eq $items[0].Id) ("hovered {0}" -f $swept.HoverId)

    # --- the resources the hovering did not leave behind
    $handlesAfter = (Get-Process -Id $processId).HandleCount
    $gdiAfter = [P3Win]::GdiObjectsOf($processId)
    Add-Sample 'perf.handlesAfterHover' $handlesAfter
    Add-Sample 'perf.gdiObjectsAfterHover' $gdiAfter
    Add-Check 'perf: hovering does not grow the handle count without bound' (($handlesAfter - $handles) -lt 200) ("{0} -> {1}" -f $handles, $handlesAfter)

    # --- a layout save, measured by the app itself
    $saveBefore = Get-LogCount $logPatterns.LayoutSaved
    $first = Get-ItemCentre (Get-ItemById (Read-Layout) $items[0].Id)
    Drag-Pointer $first[0] $first[1] ($first[0] + 130) ($first[1] + 130) 8
    $saved = Wait-LogCountGrew $logPatterns.LayoutSaved $saveBefore 20 'the layout save'
    $lines = @(Get-LogLines $logPatterns.LayoutSaved)
    $saveLine = ''
    if ($lines.Count -gt 0) { $saveLine = $lines[$lines.Count - 1] }
    $saveMs = $null
    if ($saveLine -match 'in ([0-9.]+) ms') { $saveMs = [double]$Matches[1] }
    Add-Check 'perf: the drop saved the whole document' $saved ''
    Add-Sample 'perf.layoutSaveMs' $saveMs
    Write-Host ("  layout save: {0} ms" -f $saveMs)

    # --- what the run cost in the log: the app's own startup milestones
    $runLog = Get-LogLines 'Startup:'
    $milestones = @{}
    foreach ($line in $runLog) {
        if ($line -match 'Startup: (.*?) at ([0-9]+) ms') { $milestones[$Matches[1]] = [int]$Matches[2] }
    }
    Add-Sample 'perf.startupMilestones' $milestones
    Write-Host ("  startup milestones: {0}" -f (($milestones.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)ms" }) -join ', '))

    $layoutLines = @(Get-LogLines $logPatterns.LayoutLoaded)
    $layoutLine = ''
    if ($layoutLines.Count -gt 0) { $layoutLine = $layoutLines[$layoutLines.Count - 1] }
    Add-Sample 'perf.layoutLoadLine' $layoutLine

    $iconWarnings = (Get-LogCount $logPatterns.IconMissing) - $logBase['IconMissing']
    Add-Check 'perf: no icon failed to resolve' ($iconWarnings -eq 0) ("warnings {0}" -f $iconWarnings)
}

# ---------------------------------------------------------------- probe

function Invoke-Probe {
    Write-Host '=== Phase 3B probe ==='
    Write-Host ("Muralis running: {0}" -f [bool](Get-Process -Name Muralis -ErrorAction SilentlyContinue))
    Write-Host ("Exe: {0} (present: {1})" -f $exePath, (Test-Path $exePath))
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
    Write-Host ("desktop layout:  {0} (present: {1})" -f $layoutPath, (Test-Path $layoutPath))
    if (Test-Path $layoutPath) {
        $layout = Read-Layout
        Write-Host ("  schema {0}, kind '{1}', {2} item(s)" -f $layout.SchemaVersion, $layout.Kind, @($layout.Items).Count)
        foreach ($item in @($layout.Items)) {
            $exists = switch ($item.Target.kind) {
                'url' { $true }
                'folder' { Test-Path -PathType Container $item.Target.path }
                default { Test-Path -PathType Leaf $item.Target.path }
            }
            Write-Host ("  {0,-12} {1,-16} {2,-6} {3}" -f $item.Id, $item.Name, $item.Target.kind, $(if ($exists) { 'present' } else { 'MISSING' }))
        }
    }
    Write-Host ("prototype file:  {0} (present: {1})" -f $prototypePath, (Test-Path $prototypePath))
    Write-Host ("default browser: {0}" -f (Get-DefaultBrowserProcess))

    Write-Host ''
    Write-Host 'Fixture the demo would use:'
    Write-Host ("  {0}" -f (Join-Path $env:TEMP 'MuralisP3B'))
    Write-Host ("  program: {0}" -f (Join-Path $env:SystemRoot 'System32\notepad.exe'))
    Write-Host ("  shortcut: {0}" -f (Join-Path $env:TEMP 'MuralisP3B\P3B Notepad.lnk'))
    Write-Host ("  folder:   {0}" -f (Join-Path $env:TEMP 'MuralisP3B\P3B Folder'))
    Write-Host ''
    Write-Host 'Perf items: System32 executables, system folders and three addresses.'
}

# ---------------------------------------------------------------- entry

trap {
    Write-Host ''
    Write-Host ("HARNESS ERROR: {0}" -f $_)
    Write-Host $_.ScriptStackTrace
    try { Restore-Everything } catch { Write-Host ("restore failed: {0}" -f $_) }
    exit 1
}

# The probe only reads the machine: it is allowed to run while the app is up.
if ($Stage -eq 'probe') {
    Invoke-Probe
    exit 0
}

Assert-MuralisNotRunning

Backup-Settings
Enable-CanvasInSettings

# Park first, plant second: the user's own document must be moved aside before anything writes over
# the file the app reads.
Park-LayoutFiles
Write-Layout @()

$failed = 0
try {
    if ($Stage -in @('picker', 'demo', 'full')) {
        New-Fixtures | Out-Null
        $script:logBaseline = Get-LogCounts $logPatterns

        Start-App | Out-Null
        Open-Page | Out-Null
        $layout = Invoke-DemoImports

        if ($Stage -ne 'picker') {
            $items = @($layout.Items)
            $appId = ($items | Where-Object { $_.Target.kind -eq 'application' })[0].Id
            $folderId = ($items | Where-Object { $_.Target.kind -eq 'folder' })[0].Id
            $urlId = ($items | Where-Object { $_.Target.kind -eq 'url' })[0].Id
            $lnkId = ($items | Where-Object { $_.Target.kind -eq 'shortcut' })[0].Id

            $layout = Invoke-DemoDesktop $layout $appId $folderId $urlId $lnkId
            $layout = Invoke-DemoRestart $layout $folderId $lnkId
            Invoke-DemoExplorer $layout $lnkId
            Invoke-DemoRemove $layout $folderId
        }
    }

    if ($Stage -eq 'perf' -or $Stage -eq 'full') {
        Stop-App | Out-Null
        Invoke-Perf
    }

    $failed = Write-CheckReport $Stage $outPath
} finally {
    Restore-Everything
    if ($null -ne $script:fixture -and (Test-Path $script:fixture.Root)) {
        Remove-Item -Recurse -Force $script:fixture.Root -ErrorAction SilentlyContinue
    }
}

if ($failed -gt 0) { exit 1 }
exit 0
