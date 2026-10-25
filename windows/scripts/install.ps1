#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Installs (or upgrades) the Screen Time Enforcer service on this PC.

.DESCRIPTION
  - Copies ScreenTimeService.exe and ScreenTimeHelper.exe to
    C:\Program Files\ScreenTimeEnforcer (standard users can't change it).
  - Creates C:\ProgramData\ScreenTimeEnforcer with config.json from the
    sample on first install, readable only by SYSTEM and Administrators,
    since it holds the MQTT password.
  - Registers the service as LocalSystem, automatic start, restarted on
    failure.
  - Prints this PC's Mosquitto ACL block, built from config.json.

  On first install it stops before starting the service, so config.json can
  be filled in. Run it again afterwards (it's safe to re-run any time).

.EXAMPLE
  .\scripts\install.ps1
#>
param(
    [string]$Source = (Join-Path (Split-Path $PSScriptRoot -Parent) 'publish')
)
$ErrorActionPreference = 'Stop'
$serviceName = 'ScreenTimeEnforcer'
$installDir = Join-Path $env:ProgramFiles 'ScreenTimeEnforcer'
$dataDir = Join-Path $env:ProgramData 'ScreenTimeEnforcer'
$configPath = Join-Path $dataDir 'config.json'
$sampleConfig = Join-Path (Split-Path $PSScriptRoot -Parent) 'config\config.sample.json'

foreach ($exe in 'ScreenTimeService.exe', 'ScreenTimeHelper.exe') {
    if (-not (Test-Path (Join-Path $Source $exe))) {
        throw "$exe not found in $Source. Run .\scripts\build.ps1 first."
    }
}

$existing = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Write-Host 'Stopping the running service...'
    Stop-Service $serviceName -Force
}

New-Item -ItemType Directory -Force $installDir, $dataDir | Out-Null
Copy-Item (Join-Path $Source 'ScreenTimeService.exe'), (Join-Path $Source 'ScreenTimeHelper.exe'), (Join-Path $Source 'ScreenTimeHelper.exe.config') $installDir -Force

$newConfig = $false
if (-not (Test-Path $configPath)) {
    Copy-Item $sampleConfig $configPath
    $newConfig = $true
}

# SYSTEM (S-1-5-18) and Administrators (S-1-5-32-544) only. SIDs, not
# names, so this works on non-English Windows too.
icacls $dataDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null

if (-not $existing) {
    New-Service -Name $serviceName `
        -BinaryPathName "`"$installDir\ScreenTimeService.exe`"" `
        -DisplayName 'Screen Time Enforcer' `
        -Description 'Tracks screen time for managed accounts and enforces limits set in Home Assistant.' `
        -StartupType Automatic | Out-Null
}
# Restart 5 s after a crash (or a non-zero exit, e.g. a bad config.json),
# twice, then every 30 s; the failure count resets daily.
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
sc.exe failureflag $serviceName 1 | Out-Null

if ($newConfig) {
    Write-Host ''
    Write-Host "Created $configPath from the sample. Fill in managed_users (each kid's"
    Write-Host 'windows_user_account is what `whoami` prints after the backslash when'
    Write-Host 'signed in as them), device_id, and the MQTT settings, then run this'
    Write-Host 'script again to start the service and print the broker ACL block.'
    Write-Host "  notepad $configPath"
    return
}

# --- Mosquitto ACL block, generated from config.json ----------------------
$config = Get-Content $configPath -Raw | ConvertFrom-Json
$deviceId = ([string]$config.device_id).ToLowerInvariant() -replace '[^\p{L}\p{Nd}_-]', '-'
if (-not $deviceId) { $deviceId = 'pc' }
$mqttUser = if ($config.mqtt_username) { $config.mqtt_username } else { '<SET mqtt_username IN config.json FIRST>' }
$lines = @("user $mqttUser", '')
foreach ($entry in $config.managed_users) {
    $child = ([string]$entry.child_name).Trim()
    if (-not $child) { continue }
    $prefix = if ($entry.topic_prefix) { ([string]$entry.topic_prefix).Trim().TrimEnd('/') } else { "screen/$child" }
    $lines += "# --- $child on this machine ---"
    if (-not ($prefix -eq "screen/$child" -or $prefix.StartsWith("screen/$child/"))) {
        $lines += "# !! WARNING: topic_prefix '$prefix' doesn't match child_name '$child'."
        $lines += "# !! HA won't see this kid. Fix topic_prefix in config.json and rerun."
    }
    $lines += "topic readwrite $prefix/win/$deviceId/#"
    $lines += "topic read $prefix/allowed"
    $lines += "topic read $prefix/mac/+/minutes_today"
    $lines += "topic read $prefix/win/+/minutes_today"
    $lines += "topic write $prefix/total_minutes_today"
    $lines += "topic read homeassistant/${child}_shared/+/state"
    $lines += ''
}
$lines += 'topic write homeassistant/+/+/config'
$snippet = $lines -join "`r`n"
$snippetPath = Join-Path $dataDir 'mosquitto_acl_snippet.txt'
Set-Content -Path $snippetPath -Value $snippet

Write-Host ''
Write-Host "Add this PC's block to the broker's ACL file (one MQTT user per machine):"
Write-Host ''
Write-Host $snippet
Write-Host ''
Write-Host "Saved at $snippetPath."

Start-Service $serviceName
Write-Host ''
Write-Host "Service started. Log: $dataDir\logs\service.log"
