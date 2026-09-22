# SPDX-License-Identifier: GPL-2.0-or-later
# Copyright (c) 2026 LeSqueed and the Tript contributors
#
# Starts a built Tript.App.exe headless and waits for its READY line. CI builds on Linux and ships
# a Windows bundle, so without this nothing ever started the Windows build before it was released.
#
# Every path, port and the log folder point into a temp directory. Without --log-dir the host would
# write into, and prune, the real user's log folder, which matters when this is run on a dev machine.
# Written for Windows PowerShell 5.1 as well as pwsh: no ArgumentList, no ?? operator.

param(
    [Parameter(Mandatory = $true)][string]$AppHost,
    [Parameter(Mandatory = $true)][string]$WebRoot,
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path $AppHost)) { throw "The app host was not found at $AppHost." }

$work = Join-Path ([IO.Path]::GetTempPath()) ("tript-smoke-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path (Join-Path $work "content"), (Join-Path $work "logs") | Out-Null

function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    return $port
}

$arguments = @(
    "--fake-recorder", "--disable-updater",
    "--content-root", "`"$(Join-Path $work "content")`"",
    "--settings-path", "`"$(Join-Path $work "settings.json")`"",
    "--log-dir", "`"$(Join-Path $work "logs")`"",
    "--web-root", "`"$WebRoot`"",
    "--ui-port", (Get-FreePort), "--content-port", (Get-FreePort), "--control-port", (Get-FreePort)
) -join " "

$start = New-Object System.Diagnostics.ProcessStartInfo
$start.FileName = $AppHost
$start.Arguments = $arguments
$start.UseShellExecute = $false
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$process = [System.Diagnostics.Process]::Start($start)
$stderr = $process.StandardError.ReadToEndAsync()

$ready = $false
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline -and -not $process.HasExited) {
    $line = $process.StandardOutput.ReadLineAsync()
    if ($line.Wait(2000) -and $line.Result -and $line.Result.StartsWith("READY")) {
        $ready = $true
        break
    }
}

if (-not $process.HasExited) {
    $process.Kill()
    $process.WaitForExit(10000) | Out-Null
}

if (-not $ready) {
    Write-Output "The app host did not reach READY within $TimeoutSeconds s (exit code: $($process.ExitCode))."
    Write-Output "--- stderr ---"
    Write-Output $stderr.Result
    $log = Get-ChildItem (Join-Path $work "logs") -Filter "tript-*.log" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($log) {
        Write-Output "--- log ---"
        Get-Content $log.FullName
    }
    exit 1
}

Write-Output "The app host reached READY."
exit 0
