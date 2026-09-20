# Build and run the Mnemosyne service.
#
# Only one service exists per machine - it holds a Global\MnemosyneService mutex so that four
# game clients racing to autostart it cannot produce two servers on one pipe. A second launch
# just exits, which means "I rebuilt and nothing changed" is the expected failure mode after
# editing service code. Hence -Restart.
#
# Every build goes to its own folder under bin\serve, never over the running service. A running
# service locks its own DLLs, so building in place fails; and stopping first does not help,
# because Ariadne relaunches the service within half a second of the pipe breaking - usually
# mid-build, from the old files. So instead: build beside it, point the marker Ariadne reads at
# the new exe, then stop the old one. Whoever relaunches it - Ariadne or this script - starts
# the new build.
#
# Run it from a normal terminal. From inside a packaged app (the Claude desktop app), writes to
# %APPDATA% are silently redirected to a private copy, so the marker update never reaches Ariadne.
#
#   .\run-service.ps1            build, then start (no-op if already running)
#   .\run-service.ps1 -Restart   replace the running service with a fresh build
#   .\run-service.ps1 -Release   build Release instead of Debug
param(
    [switch]$Restart,
    [switch]$Release
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$config = if ($Release) { 'Release' } else { 'Debug' }
$running = Get-Process Mnemosyne.Service -ErrorAction SilentlyContinue

if ($running -and -not $Restart) {
    Write-Host "service already running (pid $($running.Id -join ', ')). Use -Restart to replace it."
    Write-Host "log: $env:APPDATA\Mnemosyne\service.log"
    exit 0
}

$stage = Join-Path $PSScriptRoot "src\Mnemosyne.Service\bin\serve\$config-$(Get-Date -Format yyyyMMdd-HHmmss)"
dotnet build src\Mnemosyne.Service -c $config -o $stage -v quiet --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$exe = Join-Path $stage 'Mnemosyne.Service.exe'

# the marker is what Ariadne's autostart reads; the service rewrites it on start, but by then
# it is already too late for a relaunch that raced us
$marker = Join-Path $env:APPDATA 'Mnemosyne\service.path'
New-Item -ItemType Directory -Force (Split-Path $marker) | Out-Null
Set-Content -Path $marker -Value $exe -NoNewline -Encoding utf8

if ($running) {
    $running | Stop-Process
    $running | Wait-Process -Timeout 10
}

# old builds nothing is running from; a locked one is still in use and stays
Get-ChildItem (Split-Path $stage) -Directory | Where-Object { $_.FullName -ne $stage } |
    ForEach-Object { try { Remove-Item $_.FullName -Recurse -Force -ErrorAction Stop } catch {} }

Write-Host "starting $exe"
& $exe
