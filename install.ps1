[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'TokenBar'),
    [switch]$NoStartup,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$installRootFull = [IO.Path]::GetFullPath($InstallRoot)
$packageRootFull = [IO.Path]::GetFullPath($packageRoot)

if ($installRootFull -ne $packageRootFull) {
    New-Item -ItemType Directory -Force -Path $installRootFull | Out-Null
    foreach ($file in @(
        'build.ps1', 'run.ps1', 'install.ps1', 'uninstall.ps1',
        'README.md', 'LICENSE', 'VERSION'
    )) {
        $source = Join-Path $packageRootFull $file
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination $installRootFull -Force
        }
    }

    foreach ($directory in @('src', 'dist')) {
        $source = Join-Path $packageRootFull $directory
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination $installRootFull -Recurse -Force
        }
    }
}

$runScript = Join-Path $installRootFull 'run.ps1'
if (-not (Test-Path -LiteralPath $runScript)) {
    throw "run.ps1 was not found in $installRootFull"
}

if (-not $NoStartup) {
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $command = '"{0}" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{1}"' -f
        (Join-Path $PSHOME 'powershell.exe'), $runScript
    New-ItemProperty -LiteralPath $runKey -Name TokenBar -PropertyType String `
        -Value $command -Force | Out-Null
}

if (-not $NoLaunch) {
    & $runScript
}

Write-Host "Token Bar installed to: $installRootFull"
if (-not $NoStartup) {
    Write-Host 'Windows startup registration: enabled'
}
