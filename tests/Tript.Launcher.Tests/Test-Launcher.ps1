# SPDX-License-Identifier: GPL-2.0-or-later
# Copyright (c) 2026 LeSqueed and the Tript contributors
#
# Drives the real, compiled launcher through its update paths against throwaway install folders.
# The launcher is the one component that can leave an install unable to start, and it had no tests,
# so each scenario here is a way it used to fail or a guard added to stop that.
#
# Stand-in Tript.Shell.exe files are tiny programs that exit with a chosen code, so no .NET runtime,
# OBS or display is needed. Windows only; needs the Visual Studio C++ build tools.

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ("tript-launcher-tests-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work | Out-Null

# vcvars64.bat calls vswhere.exe by name, which is not on PATH on every machine.
$installer = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
$env:PATH = "$installer;$env:PATH"
$vs = & (Join-Path $installer "vswhere.exe") -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ([string]::IsNullOrWhiteSpace($vs)) { throw "The Visual Studio C++ x64 tools were not found." }
$vars = & cmd.exe /d /s /c "`"$vs\VC\Auxiliary\Build\vcvars64.bat`" >nul 2>&1 && set"
foreach ($line in $vars) {
    $i = $line.IndexOf('=')
    if ($i -gt 0) { Set-Item -Path "Env:$($line.Substring(0, $i))" -Value $line.Substring($i + 1) }
}

& rc.exe /nologo /I "$root\src\Tript.Web\public" /fo "$work\launcher.res" "$root\src\Tript.Launcher\launcher.rc"
if ($LASTEXITCODE -ne 0) { throw "Resource compilation failed." }
& cl.exe /nologo /O2 /W4 /WX /MT "$root\src\Tript.Launcher\launcher.c" "$work\launcher.res" `
    "/Fo:$work\launcher.obj" "/Fe:$work\Tript.exe" /link /SUBSYSTEM:WINDOWS user32.lib | Out-Null
if ($LASTEXITCODE -ne 0) { throw "The launcher did not compile cleanly under /W4 /WX." }

foreach ($stub in @(@{ Name = "shell-ok"; Code = 0 }, @{ Name = "shell-broken"; Code = 1 })) {
    Set-Content -Path "$work\$($stub.Name).c" -Value "int main(void) { return $($stub.Code); }" -Encoding ascii
    & cl.exe /nologo /O2 "$work\$($stub.Name).c" "/Fo:$work\$($stub.Name).obj" "/Fe:$work\$($stub.Name).exe" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not build the $($stub.Name) stand-in." }
}

$failures = 0
function Assert-That([bool]$condition, [string]$description) {
    if ($condition) { Write-Output "  ok   $description" }
    else { Write-Output "  FAIL $description"; $script:failures++ }
}

function New-Install([string]$name) {
    $dir = Join-Path $work $name
    New-Item -ItemType Directory -Path (Join-Path $dir ".tript-update") -Force | Out-Null
    Copy-Item "$work\Tript.exe" (Join-Path $dir "Tript.exe")
    return $dir
}

function Add-Shell([string]$folder, [string]$stub, [string]$identity) {
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    Copy-Item "$work\$stub.exe" (Join-Path $folder "Tript.Shell.exe")
    Set-Content (Join-Path $folder "identity.txt") $identity -Encoding ascii
}

# The rollback path shows a message box. With no interactive desktop (CI) it never returns, but
# every file move it reports on has already happened by then, so waiting briefly and killing it is
# enough to inspect the result.
function Invoke-Launcher([string]$dir) {
    $process = Start-Process -FilePath (Join-Path $dir "Tript.exe") -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(10000)) { Stop-Process -Id $process.Id -Force; return $null }
    return $process.ExitCode
}

function Identity([string]$folder) {
    $file = Join-Path $folder "identity.txt"
    if (Test-Path $file) { return (Get-Content $file).Trim() }
    return "<missing>"
}

Write-Output "A broken update is rolled back to the previous install"
$dir = New-Install "rollback"
Add-Shell (Join-Path $dir "App") "shell-ok" "previous"
Add-Shell (Join-Path $dir ".tript-update\staged-020") "shell-broken" "broken"
[IO.File]::WriteAllText((Join-Path $dir ".tript-update\ready.marker"), "1`n0.2.0`nstaged-020`n")
Invoke-Launcher $dir | Out-Null
Assert-That ((Identity (Join-Path $dir "App")) -eq "previous") "the previous install is back in App"
Assert-That ((Identity (Join-Path $dir ".tript-update\failed-App")) -eq "broken") "the broken build was set aside"
$record = Join-Path $dir ".tript-update\rolled-back"
Assert-That ((Test-Path $record) -and ([IO.File]::ReadAllText($record) -eq "0.2.0")) "the failed version is recorded"

Write-Output "A healthy update is kept, with the previous install retained for probation"
$dir = New-Install "healthy"
Add-Shell (Join-Path $dir "App") "shell-ok" "previous"
Add-Shell (Join-Path $dir ".tript-update\staged-020") "shell-ok" "new"
[IO.File]::WriteAllText((Join-Path $dir ".tript-update\ready.marker"), "1`n0.2.0`nstaged-020`n")
$code = Invoke-Launcher $dir
Assert-That ($code -eq 0) "the launcher exits cleanly"
Assert-That ((Identity (Join-Path $dir "App")) -eq "new") "the new build is in App"
Assert-That (Test-Path (Join-Path $dir ".tript-update\old-App\Tript.Shell.exe")) "old-App is kept to roll back to"
Assert-That (-not (Test-Path (Join-Path $dir ".tript-update\rolled-back"))) "nothing is rolled back"

Write-Output "A swap interrupted between its two renames is recovered"
$dir = New-Install "interrupted"
Add-Shell (Join-Path $dir ".tript-update\old-App") "shell-ok" "previous"
$code = Invoke-Launcher $dir
Assert-That ($code -eq 0) "the launcher starts instead of reporting Tript as incomplete"
Assert-That ((Identity (Join-Path $dir "App")) -eq "previous") "the previous install is restored to App"

Write-Output "A version that fails on a normal start, with no update involved, is left alone"
$dir = New-Install "no-update"
Add-Shell (Join-Path $dir "App") "shell-broken" "current"
Add-Shell (Join-Path $dir ".tript-update\old-App") "shell-ok" "older"
$code = Invoke-Launcher $dir
Assert-That ($code -eq 1) "the failure is passed through"
Assert-That ((Identity (Join-Path $dir "App")) -eq "current") "App is not swapped out"

if ($failures -gt 0) {
    Write-Output "$failures launcher check(s) failed; the work folder is kept at $work"
    exit 1
}

Write-Output "All launcher checks passed."
exit 0
