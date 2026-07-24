[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $projectRoot '.codex-tmp\tests'
$runtimeRoot = Join-Path $testRoot 'runtime'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'Windows .NET Framework C# compiler was not found.'
}

if (Test-Path -LiteralPath $testRoot) {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $runtimeRoot | Out-Null
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$env:TEMP = $testRoot
$env:TMP = $testRoot
$env:TOKENBAR_REGRESSION_ROOT = $runtimeRoot

$insideProcess = $null
$siblingProcess = $null
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$hadStartupValue = $false
$startupValue = $null
try {
    try {
        $startupValue = Get-ItemPropertyValue -LiteralPath $runKey -Name TokenBar `
            -ErrorAction Stop
        $hadStartupValue = $true
    } catch {
        $hadStartupValue = $false
    }

    $sources = @(
        Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs'
        Get-Item -LiteralPath (Join-Path $PSScriptRoot 'RegressionTests.cs')
    ) | Sort-Object Name | ForEach-Object FullName
    $testExe = Join-Path $testRoot 'TokenBar.RegressionTests.exe'
    $arguments = @(
        '/nologo',
        '/target:exe',
        '/platform:anycpu',
        '/optimize+',
        '/main:TokenBar.Tests.RegressionTests',
        ('/out:' + $testExe),
        '/reference:System.dll',
        '/reference:System.Core.dll',
        '/reference:System.Drawing.dll',
        '/reference:System.Windows.Forms.dll',
        '/reference:System.Web.Extensions.dll'
    ) + $sources
    & $compiler $arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Regression test build failed with exit code $LASTEXITCODE."
    }

    & $testExe
    if ($LASTEXITCODE -ne 0) {
        throw "C# regression tests failed with exit code $LASTEXITCODE."
    }

    $fakeLocalAppData = Join-Path $runtimeRoot 'localappdata'
    $installRoot = Join-Path $fakeLocalAppData 'TokenBar'
    $siblingRoot = Join-Path $fakeLocalAppData 'TokenBarBackup'
    New-Item -ItemType Directory -Force -Path $installRoot, $siblingRoot | Out-Null
    $insideExe = Join-Path $installRoot 'inside.exe'
    $siblingExe = Join-Path $siblingRoot 'sibling.exe'
    Copy-Item -LiteralPath $testExe -Destination $insideExe
    Copy-Item -LiteralPath $testExe -Destination $siblingExe
    $insideProcess = Start-Process -FilePath $insideExe -ArgumentList '--hang' `
        -WindowStyle Hidden -PassThru
    $siblingProcess = Start-Process -FilePath $siblingExe -ArgumentList '--hang' `
        -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 500

    $previousLocalAppData = $env:LOCALAPPDATA
    try {
        $env:LOCALAPPDATA = $fakeLocalAppData
        & (Join-Path $projectRoot 'uninstall.ps1') -InstallRoot $installRoot
    } finally {
        $env:LOCALAPPDATA = $previousLocalAppData
    }
    $insideProcess.Refresh()
    $siblingProcess.Refresh()
    if (-not $insideProcess.HasExited) {
        throw 'Uninstall did not stop a process inside the install directory.'
    }
    if ($siblingProcess.HasExited) {
        throw 'Uninstall stopped a process in the TokenBarBackup sibling directory.'
    }
    if (Test-Path -LiteralPath $installRoot) {
        throw 'Uninstall did not remove the test install directory.'
    }
    Write-Host 'Uninstall boundary test passed.'
    Write-Host 'All regression tests passed.'
} finally {
    if ($insideProcess -and -not $insideProcess.HasExited) {
        Stop-Process -Id $insideProcess.Id -Force -ErrorAction SilentlyContinue
    }
    if ($siblingProcess -and -not $siblingProcess.HasExited) {
        Stop-Process -Id $siblingProcess.Id -Force -ErrorAction SilentlyContinue
    }
    if ($hadStartupValue) {
        Set-ItemProperty -LiteralPath $runKey -Name TokenBar -Value $startupValue
    } else {
        Remove-ItemProperty -LiteralPath $runKey -Name TokenBar `
            -ErrorAction SilentlyContinue
    }
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    Remove-Item Env:TOKENBAR_REGRESSION_ROOT -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
