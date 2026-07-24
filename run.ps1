$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $projectRoot 'dist\TokenBar.exe'
$hostScript = Join-Path $projectRoot 'src\TokenBar.Host.ps1'

if (-not (Test-Path -LiteralPath $exe)) {
    & (Join-Path $projectRoot 'build.ps1')
}

try {
    $process = Start-Process -FilePath $exe -WorkingDirectory $projectRoot -PassThru
    Start-Sleep -Milliseconds 800
    if (-not $process.HasExited) {
        return
    }
} catch {
    Write-Verbose "TokenBar.exe was not allowed; using the signed PowerShell host."
}

Start-Process -FilePath 'powershell.exe' -ArgumentList @(
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-WindowStyle', 'Hidden',
    '-STA',
    '-File', ('"{0}"' -f $hostScript)
) -WorkingDirectory $projectRoot

