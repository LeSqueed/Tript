param(
    [string]$OutputPath = "dist\Tript.exe"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputPath))
$outputDirectory = Split-Path -Parent $output
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"

if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
    throw "Visual Studio Build Tools were not found."
}

$installation = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ([string]::IsNullOrWhiteSpace($installation)) {
    throw "The Visual Studio C++ x64 tools were not found."
}

$vcvars = Join-Path $installation "VC\Auxiliary\Build\vcvars64.bat"
$environment = & cmd.exe /d /s /c "`"$vcvars`" >nul && set"
foreach ($line in $environment) {
    $separator = $line.IndexOf('=')
    if ($separator -gt 0) {
        Set-Item -Path "Env:$($line.Substring(0, $separator))" -Value $line.Substring($separator + 1)
    }
}

$compiler = (Get-Command cl.exe -ErrorAction Stop).Source
$resourceCompiler = (Get-Command rc.exe -ErrorAction Stop).Source
$temporary = Join-Path ([IO.Path]::GetTempPath()) ("tript-launcher-" + [Guid]::NewGuid().ToString("N"))

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    $resource = Join-Path $temporary "launcher.res"
    $object = Join-Path $temporary "launcher.obj"
    & $resourceCompiler /nologo /I (Join-Path $root "src\Tript.Web\public") /fo $resource (Join-Path $PSScriptRoot "launcher.rc")
    if ($LASTEXITCODE -ne 0) {
        throw "Resource compilation failed with exit code $LASTEXITCODE."
    }

    & $compiler /nologo /O2 /W4 /WX /MT (Join-Path $PSScriptRoot "launcher.c") $resource "/Fo:$object" "/Fe:$output" /link /SUBSYSTEM:WINDOWS user32.lib
    if ($LASTEXITCODE -ne 0) {
        throw "Launcher compilation failed with exit code $LASTEXITCODE."
    }
}
finally {
    Remove-Item -LiteralPath $temporary -Recurse -Force -ErrorAction SilentlyContinue
}

$output
