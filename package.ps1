[CmdletBinding()]
param(
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (Get-Content -LiteralPath (
        Join-Path $projectRoot 'VERSION') -Raw).Trim()
}
$tempRoot = Join-Path $projectRoot '.codex-tmp\package'
$packageName = "Token-Bar-for-Windows-$Version"
$stageRoot = Join-Path $tempRoot $packageName
$output = Join-Path $projectRoot "dist\$packageName.zip"

if (Test-Path -LiteralPath $tempRoot) {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

& (Join-Path $projectRoot 'build.ps1')

foreach ($file in @(
    'build.ps1', 'run.cmd', 'run.ps1', 'install.ps1', 'uninstall.ps1',
    'README.md', 'LICENSE', 'VERSION'
)) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $file) -Destination $stageRoot
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'src') -Destination $stageRoot -Recurse
New-Item -ItemType Directory -Force -Path (Join-Path $stageRoot 'dist') | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'dist\TokenBar.exe') `
    -Destination (Join-Path $stageRoot 'dist\TokenBar.exe')

if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Force
}
Compress-Archive -LiteralPath $stageRoot -DestinationPath $output `
    -CompressionLevel Optimal
Remove-Item -LiteralPath $tempRoot -Recurse -Force

$size = (Get-Item -LiteralPath $output).Length
$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = Join-Path $projectRoot 'dist\SHA256SUMS.txt'
"$hash *$packageName.zip" | Set-Content -LiteralPath $checksumPath -Encoding Ascii
Write-Host ("Packaged {0} ({1:N0} bytes)" -f $output, $size)
Write-Host ("SHA256 {0}" -f $hash)
