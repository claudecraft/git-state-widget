<#
.SYNOPSIS
    Stops GitStateWidget and removes its start-at-logon entry. Config is left in place.

.PARAMETER RemoveConfig
    Also delete %LOCALAPPDATA%\GitStateWidget (the repo list and panel position).
#>
[CmdletBinding()]
param(
    [switch]$RemoveConfig
)

$ErrorActionPreference = 'Continue'

Get-Process GitStateWidget -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'GitStateWidget' -ErrorAction SilentlyContinue
Write-Host "Stopped and removed from startup." -ForegroundColor Green

# The first version ran as a scheduled task; remove it if it is still registered. Needs elevation
# if it was registered at the task root by an administrator.
if (Get-ScheduledTask -TaskName 'GitStateWidget' -ErrorAction SilentlyContinue) {
    try {
        Unregister-ScheduledTask -TaskName 'GitStateWidget' -Confirm:$false -ErrorAction Stop
        Write-Host "Removed the legacy GitStateWidget scheduled task." -ForegroundColor Green
    } catch {
        Write-Host "Legacy scheduled task still present; remove it from an elevated prompt:" -ForegroundColor Yellow
        Write-Host "  schtasks /Delete /TN GitStateWidget /F" -ForegroundColor Yellow
    }
}

if ($RemoveConfig) {
    Remove-Item -Recurse -Force (Join-Path $env:LOCALAPPDATA 'GitStateWidget') -ErrorAction SilentlyContinue
    Write-Host "Config removed." -ForegroundColor DarkGray
}
