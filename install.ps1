#Requires -RunAsAdministrator
<#
  Устанавливает PumpGuard: служба Windows (автозапуск при загрузке, до входа пользователя)
  + виджет рабочего стола (автозапуск при входе текущего пользователя).
  Повторный запуск обновляет программу, config.json в C:\ProgramData\PumpGuard не трогается
  (кроме генерации ApiToken, если он пустой).
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

# SYSTEM, Administrators: full control. The service runs as SYSTEM, so nothing else needs these folders' write access.
$system = '*S-1-5-18'; $admins = '*S-1-5-32-544'; $users = '*S-1-5-32-545'

function Lock-Folder([string]$Path, [bool]$UsersCanRead) {
    # Take ownership first: a folder pre-created by a normal user would otherwise keep that user's rights.
    icacls $Path /setowner $admins /T /C /Q | Out-Null
    $grants = @("${system}:(OI)(CI)F", "${admins}:(OI)(CI)F")
    if ($UsersCanRead) { $grants += "${users}:(OI)(CI)RX" }
    # Explicit, inheritable rights on the folder only; everything inside is reset to inherit them.
    # (Granting (OI)(CI) with /T would hit files too, where those flags are invalid, and leave them with no access.)
    icacls $Path /inheritance:r /grant:r @grants /C /Q | Out-Null
    if ($LASTEXITCODE) { throw "Не удалось выставить права на $Path" }
    if (Get-ChildItem $Path -Force) { icacls (Join-Path $Path '*') /reset /T /C /Q | Out-Null }
}

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
# The service binary runs as SYSTEM: users may run the widget, never replace files here.
Lock-Folder $InstallDir $true

New-Item -ItemType Directory -Force $dataDir | Out-Null
Lock-Folder $dataDir $false
$config = Join-Path $dataDir 'config.json'
if (-not (Test-Path $config)) {
    Copy-Item (Join-Path $InstallDir 'service\config.default.json') $config
    Write-Host "Создан $config — впишите в него датчик помпы (см. README)."
}

# API token: without it any local process could cancel an emergency shutdown or slow the fans down.
$configText = Get-Content $config -Raw -Encoding utf8
$token = if ($configText -match '"ApiToken"\s*:\s*"([^"]+)"') { $Matches[1] } else { $null }
if (-not $token) {
    $bytes = New-Object byte[] 24   # Windows PowerShell 5.1 compatible (no Convert.ToHexString there)
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $token = ($bytes | ForEach-Object { $_.ToString('x2') }) -join ''
    $configText = $configText -replace '"ApiToken"\s*:\s*"[^"]*"', "`"ApiToken`": `"$token`""
    [IO.File]::WriteAllText($config, $configText, [Text.UTF8Encoding]::new($false))
    Write-Host 'Сгенерирован ApiToken.'
}
# The widget of the installing user gets the token; another user's widget can only read data.
$widgetDir = Join-Path $env:APPDATA 'PumpGuard'
$widgetConfig = Join-Path $widgetDir 'widget.json'
New-Item -ItemType Directory -Force $widgetDir | Out-Null
$widget = if (Test-Path $widgetConfig) { Get-Content $widgetConfig -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
$widget | Add-Member -Force -NotePropertyName Token -NotePropertyValue $token
$widget | ConvertTo-Json | Set-Content $widgetConfig -Encoding utf8

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
