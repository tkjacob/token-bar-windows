[CmdletBinding()]
param(
    [string]$CollectTo
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$sourceRoot = Join-Path $projectRoot 'src'
$runtimeRoot = Join-Path $projectRoot 'dist\.runtime'
$errorLog = Join-Path $projectRoot 'dist\TokenBar.Host.error.log'

try {
    if (Test-Path -LiteralPath $errorLog) {
        Remove-Item -LiteralPath $errorLog -Force
    }
    New-Item -ItemType Directory -Force -Path $runtimeRoot | Out-Null

    Add-Type -AssemblyName Microsoft.CSharp
    Add-Type -AssemblyName System
    Add-Type -AssemblyName System.Core
    Add-Type -AssemblyName System.Drawing
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Web.Extensions

    $provider = New-Object Microsoft.CSharp.CSharpCodeProvider
    $parameters = New-Object System.CodeDom.Compiler.CompilerParameters
    $parameters.GenerateExecutable = $false
    $parameters.GenerateInMemory = $true
    $parameters.IncludeDebugInformation = $false
    $parameters.CompilerOptions = '/optimize+'
    $parameters.TempFiles = New-Object System.CodeDom.Compiler.TempFileCollection($runtimeRoot, $false)

    @(
        [System.Object].Assembly.Location,
        [System.ComponentModel.Component].Assembly.Location,
        [System.Linq.Enumerable].Assembly.Location,
        [System.Drawing.Bitmap].Assembly.Location,
        [System.Windows.Forms.Form].Assembly.Location,
        [System.Web.Script.Serialization.JavaScriptSerializer].Assembly.Location
    ) | Select-Object -Unique | ForEach-Object {
        [void]$parameters.ReferencedAssemblies.Add($_)
    }

    $sources = Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' |
        Sort-Object Name |
        ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) }

    $result = $provider.CompileAssemblyFromSource($parameters, [string[]]$sources)
    if ($result.Errors.HasErrors) {
        $messages = $result.Errors | ForEach-Object ToString
        throw "C# in-memory build failed:`r`n$($messages -join "`r`n")"
    }

    $assembly = $result.CompiledAssembly
    $provider.Dispose()
    $parameters.TempFiles.Delete()
    Remove-Variable provider, parameters, result, sources -ErrorAction SilentlyContinue
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
    [GC]::Collect()

    $env:TOKENBAR_BASE_DIR = Join-Path $projectRoot 'dist'
    $nativeConsole = $assembly.GetType('TokenBar.NativeConsole', $true)
    [void]$nativeConsole.GetMethod(
        'EnableDpiAwareness',
        [Reflection.BindingFlags]'Public, Static'
    ).Invoke($null, @())
    [void]$nativeConsole.GetMethod(
        'FreeCurrent',
        [Reflection.BindingFlags]'Public, Static'
    ).Invoke($null, @())

    $escapedHost = $MyInvocation.MyCommand.Path.Replace('"', '\"')
    $env:TOKENBAR_START_COMMAND = (
        '"{0}" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -STA -File "{1}"' -f
        (Join-Path $PSHOME 'powershell.exe'), $escapedHost
    )

    $program = $assembly.GetType('TokenBar.Program', $true)
    $main = $program.GetMethod('Main', [Reflection.BindingFlags]'NonPublic, Static')
    $programArguments = [string[]]::new(0)
    if (-not [string]::IsNullOrWhiteSpace($CollectTo)) {
        $programArguments = [string[]]@('--collect-to', $CollectTo)
    }
    $invokeArguments = New-Object 'object[]' 1
    $invokeArguments[0] = [string[]]$programArguments
    [void]$main.Invoke($null, $invokeArguments)
} catch {
    $message = "{0:O}`r`n{1}" -f (Get-Date), ($_ | Out-String)
    [System.IO.File]::WriteAllText($errorLog, $message, [Text.Encoding]::UTF8)
}
