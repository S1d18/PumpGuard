#Requires -RunAsAdministrator
<#
  Устанавливает PumpGuard: служба Windows (автозапуск при загрузке, до входа пользователя)
  + виджет рабочего стола (автозапуск при входе текущего пользователя).
  Повторный запуск обновляет программу, config.json в C:\ProgramData\PumpGuard не трогается.
#>
param(
    [string]$InstallDir = "$env:ProgramFiles\PumpGuard",
    [switch]$NoWidgetAutostart
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dataDir = Join-Path $env:ProgramData 'PumpGuard'
$serviceExe = Join-Path $InstallDir 'service\PumpGuard.Service.exe'
$widgetExe = Join-Path $InstallDir 'widget\PumpGuard.Widget.exe'

if (-not (Test-Path (Join-Path $env:ProgramFiles 'PawnIO'))) {
    Write-Warning 'Драйвер PawnIO не найден: без него не будут видны датчики материнской платы (помпа, вентиляторы, CPU). Установите его с https://pawnio.eu'
}

$existing = Get-Service PumpGuard -ErrorAction SilentlyContinue
if ($existing) { Stop-Service PumpGuard -Force }
Get-Process PumpGuard.Widget -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host 'Сборка...'
dotnet publish (Join-Path $root 'src\PumpGuard.Service') -c Release -o (Join-Path $InstallDir 'service') --nologo -v q
if ($LASTEXITCODE) { throw 'Не удалось собрать службу' }
dotnet publish (Join-Path $root 'src\PumpGuard.Widget') -c Release -o (Join-Path $InstallDir 'widget') --nologo -v q
if ($LASTEXITCODE) { throw 'Не удалось собрать виджет' }

New-Item -ItemType Directory -Force $dataDir | Out-Null
$config = Join-Path $dataDir 'config.json'
if (-not (Test-Path $config)) {
    Copy-Item (Join-Path $InstallDir 'service\config.default.json') $config
    Write-Host "Создан $config — впишите в него датчик помпы (см. README)."
}
# The config decides when the PC gets switched off: only admins and SYSTEM may change it.
icacls $dataDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null

if (-not $existing) {
    New-Service -Name PumpGuard -BinaryPathName "`"$serviceExe`"" -DisplayName 'PumpGuard' `
        -Description 'Мониторинг помпы, вентиляторов и температур; аварийное выключение ПК при отказе охлаждения.' `
        -StartupType Automatic | Out-Null
}
# If the service ever crashes, Windows restarts it: protection must not silently stay off.
sc.exe failure PumpGuard reset= 86400 actions= restart/5000/restart/5000/restart/10000 | Out-Null
Start-Service PumpGuard

if (-not $NoWidgetAutostart) {
    Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name PumpGuardWidget -Value "`"$widgetExe`""
}
# Through explorer, so the widget runs as the normal (non-elevated) user.
Start-Process explorer.exe -ArgumentList "`"$widgetExe`""

Start-Sleep 3
try {
    $s = Invoke-RestMethod http://127.0.0.1:8765/api/status -TimeoutSec 5
    Write-Host "Служба работает, состояние: $($s.state)" -ForegroundColor Green
    if (-not $s.pump.sensorId) {
        Write-Warning "Помпа ещё не настроена. Найдите её датчик:`n  & '$serviceExe' --list-sensors`nи впишите Id в $config (Pump:SensorId), затем: Restart-Service PumpGuard"
    }
} catch {
    Write-Warning "Служба не отвечает на http://127.0.0.1:8765 — смотрите журнал: Просмотр событий → Журналы Windows → Приложение"
}
