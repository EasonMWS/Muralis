<#
    Builds and runs the Muralis test suites.

    Two things about this repository's toolchain are worked around here, both of them properties of the
    machine rather than of the code:

      * `Muralis.slnx` restores nothing under SDK 10.0.401 and reports the failure as "0 errors", so the
        projects are built one at a time instead of through the solution.
      * MSBuild's incremental up-to-date check misfires, leaving stale assemblies in `bin` after a source
        edit. Every build here therefore deletes the assembly it is about to produce and then checks that
        a new one appeared.

    Usage:  ./tools/phase4d-verify.ps1 [-Configuration Debug|Release] [-Filter <expression>]
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',

    [string] $Filter = ''
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_ROOT = 'C:\Users\Eason\.dotnet'
$env:PATH = "C:\Users\Eason\.dotnet;$env:PATH"
$env:DOTNET_CLI_UI_LANGUAGE = 'en-US'
$env:MSBUILDDISABLENODEREUSE = '1'

$root = Split-Path -Parent $PSScriptRoot
$dotnet = 'C:\Users\Eason\.dotnet\dotnet.exe'

function Get-BuiltAssemblies {
    param([string] $ProjectDirectory, [string] $Name)

    Get-ChildItem -Path (Join-Path $root $ProjectDirectory) -Recurse -Filter "$Name.dll" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\bin\\[^\\]+\\" }
}

function Build-Project {
    param([string] $Project, [string] $Directory, [string] $Name)

    Write-Host "=== build $Name ($Configuration) ===" -ForegroundColor Cyan

    foreach ($file in (Get-BuiltAssemblies $Directory $Name)) {
        Remove-Item $file.FullName -Force
    }

    # --no-incremental: this machine's up-to-date check decides a project is current when it is not, and
    # a stale assembly silently makes a passing test meaningless. A full compile every time is the only
    # way the result means anything.
    $arguments = @(
        'build', $Project, '-c', $Configuration, '-p:Platform=x64', '-p:RuntimeIdentifier=win-x64',
        '-m:1', '--no-incremental', '-v', 'minimal', '--nologo'
    )
    $output = & $dotnet @arguments 2>&1
    $text = $output -join "`n"

    if ($LASTEXITCODE -ne 0 -or $text -notmatch 'Build succeeded') {
        $output | ForEach-Object { Write-Host $_ }
        throw "$Name failed to build."
    }

    if ($text -notmatch '(\d+) Warning\(s\)') {
        $output | ForEach-Object { Write-Host $_ }
        throw "$Name did not report its warning count."
    }

    if ($Matches[1] -ne '0') {
        $output | ForEach-Object { Write-Host $_ }
        throw "$Name built with $($Matches[1]) warnings."
    }

    $produced = @(Get-BuiltAssemblies $Directory $Name)
    if ($produced.Count -eq 0) {
        throw "$Name produced no assembly: the build did not really run."
    }

    Write-Host "  -> $($produced[0].FullName)" -ForegroundColor DarkGray
    Write-Host "     $($produced[0].LastWriteTime)"
    return $produced[0].FullName
}

function Invoke-Tests {
    param([string] $Assembly, [string] $Label)

    Write-Host "=== test $Label ===" -ForegroundColor Cyan
    $arguments = @('test', $Assembly, '--nologo')
    if ($Filter) { $arguments += @('--filter', $Filter) }

    # `dotnet test` writes its failures to stderr, which PowerShell would otherwise turn into a
    # terminating error before the summary could be read.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $output = & $dotnet @arguments 2>&1
    $exit = $LASTEXITCODE
    $ErrorActionPreference = $previous

    $output | Select-String -Pattern 'Passed!|Failed!' | ForEach-Object { Write-Host $_.Line }

    if ($exit -ne 0) {
        $output |
            Select-String -Pattern 'Error Message|Assert|\[FAIL\]' |
            Select-Object -First 40 |
            ForEach-Object { Write-Host $_.Line }
        throw "$Label failed."
    }
}

Build-Project 'src/Muralis.Core/Muralis.Core.csproj' 'src/Muralis.Core' 'Muralis.Core' | Out-Null
Build-Project 'src/Muralis.Desktop/Muralis.Desktop.csproj' 'src/Muralis.Desktop' 'Muralis.Desktop' | Out-Null
Build-Project 'src/Muralis.App/Muralis.App.csproj' 'src/Muralis.App' 'Muralis' | Out-Null

# The core suite targets plain net10.0, so the tests are run from the framework-specific output rather
# than the RID-specific one the desktop and app assemblies use.
$coreTests = Build-Project 'tests/Muralis.Core.Tests/Muralis.Core.Tests.csproj' 'tests/Muralis.Core.Tests' 'Muralis.Core.Tests'
$desktopTests = Build-Project 'tests/Muralis.Desktop.Tests/Muralis.Desktop.Tests.csproj' 'tests/Muralis.Desktop.Tests' 'Muralis.Desktop.Tests'

Invoke-Tests $coreTests 'Muralis.Core.Tests'
Invoke-Tests $desktopTests 'Muralis.Desktop.Tests'

Write-Host "All suites passed ($Configuration)." -ForegroundColor Green
