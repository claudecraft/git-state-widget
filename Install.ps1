<#
.SYNOPSIS
    Builds GitStateWidget, registers it to start with Windows (per-user Run key), and launches it.
    No elevation needed.

.DESCRIPTION
    The widget is a WPF tray app, so there is no console to hide and no scheduled task to register.
    Repos live in %LOCALAPPDATA%\GitStateWidget\config.json - add them from the tray menu
    ("Add repo...") or edit the file and choose "Reload config".

.PARAMETER NoStartup
    Build and launch only; do not touch the Run key.
#>
[CmdletBinding()]
param(
    [switch]$NoStartup
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'src'
$exe = Join-Path $src 'bin\Release\net10.0-windows\GitStateWidget.exe'

Write-Host "Building..." -ForegroundColor DarkGray
& dotnet build $src -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed." -ForegroundColor Red
    exit 1
}
if (-not (Test-Path $exe)) {
    Write-Host "Build produced no exe at $exe" -ForegroundColor Red
    exit 1
}

Get-Process GitStateWidget -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if (-not $NoStartup) {
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    New-Item -Path $key -Force | Out-Null
    Set-ItemProperty -Path $key -Name 'GitStateWidget' -Value ('"' + $exe + '"')
    Write-Host "Registered to start at logon (HKCU Run key)." -ForegroundColor DarkGray
}

Start-Process -FilePath $exe
Write-Host "Launched $exe" -ForegroundColor Green
Write-Host "Config: $env:LOCALAPPDATA\GitStateWidget\config.json" -ForegroundColor DarkGray
