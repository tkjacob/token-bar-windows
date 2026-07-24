[CmdletBinding()]
param(
    [switch]$DebugBuild
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceRoot = Join-Path $projectRoot 'src'
$outputRoot = Join-Path $projectRoot 'dist'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'Windows .NET Framework C# compiler was not found.'
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

$sources = Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' |
    Sort-Object Name |
    ForEach-Object FullName

$arguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    ('/win32manifest:' + (Join-Path $sourceRoot 'app.manifest')),
    ('/out:' + (Join-Path $outputRoot 'TokenBar.exe')),
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Web.Extensions.dll'
)

if ($DebugBuild) {
    $arguments += @('/debug+', '/optimize-')
}

$arguments += $sources
& $compiler $arguments
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

$size = (Get-Item -LiteralPath (Join-Path $outputRoot 'TokenBar.exe')).Length
Write-Host ('Built dist\TokenBar.exe ({0:N0} bytes)' -f $size)
