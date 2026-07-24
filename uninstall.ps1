[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'TokenBar')
)

$ErrorActionPreference = 'Stop'
$installRootFull = [IO.Path]::GetFullPath($InstallRoot)

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Remove-ItemProperty -LiteralPath $runKey -Name TokenBar -ErrorAction SilentlyContinue

Get-CimInstance Win32_Process | Where-Object {
    ($_.ExecutablePath -and
        [IO.Path]::GetFullPath($_.ExecutablePath).StartsWith(
            $installRootFull, [StringComparison]::OrdinalIgnoreCase)) -or
    ($_.Name -eq 'powershell.exe' -and $_.CommandLine -and
        $_.CommandLine.IndexOf(
            (Join-Path $installRootFull 'src\TokenBar.Host.ps1'),
            [StringComparison]::OrdinalIgnoreCase) -ge 0)
} | ForEach-Object {
    Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path -LiteralPath $installRootFull)) {
    Write-Host 'Token Bar is already removed.'
    return
}

$localAppDataFull = [IO.Path]::GetFullPath($env:LOCALAPPDATA)
if (-not $installRootFull.StartsWith(
    $localAppDataFull + [IO.Path]::DirectorySeparatorChar,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "For safety, automatic removal is limited to LOCALAPPDATA: $installRootFull"
}

$currentScript = [IO.Path]::GetFullPath($MyInvocation.MyCommand.Path)
if ($currentScript.StartsWith(
    $installRootFull + [IO.Path]::DirectorySeparatorChar,
    [StringComparison]::OrdinalIgnoreCase)) {
    $escapedRoot = $installRootFull.Replace("'", "''")
    $cleanup = "Start-Sleep -Milliseconds 500; Remove-Item -LiteralPath '$escapedRoot' -Recurse -Force"
    Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') `
        -ArgumentList @('-NoProfile', '-WindowStyle', 'Hidden', '-Command', $cleanup) `
        -WindowStyle Hidden
} else {
    Remove-Item -LiteralPath $installRootFull -Recurse -Force
}

Write-Host "Token Bar removed from: $installRootFull"

