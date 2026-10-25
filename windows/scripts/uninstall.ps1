#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Removes the Screen Time Enforcer service and program files.

.PARAMETER All
  Also delete C:\ProgramData\ScreenTimeEnforcer (config.json, today's
  minutes, logs). Without it they're kept for a reinstall.
#>
param([switch]$All)
$ErrorActionPreference = 'Stop'
$serviceName = 'ScreenTimeEnforcer'

if (Get-Service $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $serviceName | Out-Null
    Write-Host 'Service removed.'
}
Remove-Item (Join-Path $env:ProgramFiles 'ScreenTimeEnforcer') -Recurse -Force -ErrorAction SilentlyContinue
if ($All) {
    Remove-Item (Join-Path $env:ProgramData 'ScreenTimeEnforcer') -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host 'Config, state and logs removed.'
}
Write-Host 'Done. Home Assistant keeps showing this PC as offline until you delete its device there.'
