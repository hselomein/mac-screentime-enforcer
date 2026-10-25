<#
.SYNOPSIS
  Builds the service and helper into windows\publish\. The service is a
  self-contained single .exe (no .NET install needed on the PC); the helper
  is a small .NET Framework 4.8 exe, which every Windows 10/11 already has.

.EXAMPLE
  .\scripts\build.ps1                    # Intel/AMD PCs
  .\scripts\build.ps1 -Runtime win-arm64 # ARM PCs (e.g. Snapdragon)
#>
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'publish'

dotnet publish (Join-Path $root 'src\ScreenTimeService\ScreenTimeService.csproj') -c Release -r $Runtime -o $out
if ($LASTEXITCODE -ne 0) { throw 'Building ScreenTimeService failed.' }
# The helper targets .NET Framework 4.8 (built into Windows), so no runtime
# identifier: the same small exe runs on x64 and ARM.
dotnet publish (Join-Path $root 'src\ScreenTimeHelper\ScreenTimeHelper.csproj') -c Release -o $out
if ($LASTEXITCODE -ne 0) { throw 'Building ScreenTimeHelper failed.' }
Write-Host "Built into $out. Next: .\scripts\install.ps1 from an elevated PowerShell."
