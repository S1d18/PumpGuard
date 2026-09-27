#Requires -RunAsAdministrator
param(
    [string]$InstallDir = "$env:ProgramFiles\PumpGuard",
    # Also delete C:\ProgramData\PumpGuard (config.json and saved fan presets).
    [switch]$Purge
)
$ErrorActionPreference = 'Stop'

if (Get-Service PumpGuard -ErrorAction SilentlyContinue) {
    # Stopping the service hands all fans back to the BIOS.
    Stop-Service PumpGuard -Force
    sc.exe delete PumpGuard | Out-Null
}
Get-Process PumpGuard.Widget -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name PumpGuardWidget -ErrorAction SilentlyContinue
if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
if ($Purge) { Remove-Item (Join-Path $env:ProgramData 'PumpGuard') -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host 'PumpGuard удалён.'
