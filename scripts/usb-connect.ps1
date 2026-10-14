<#
.SYNOPSIS
    Expone el puerto del host en la tablet por USB (adb reverse).
.DESCRIPTION
    Con el cable conectado y la depuración USB activa, la tablet puede conectarse a
    127.0.0.1:<puerto> y el tráfico viaja por el cable hasta el PC.
#>
param(
    [int]$Port = 27190
)

$ErrorActionPreference = 'Stop'

$adb = (Get-Command adb -ErrorAction SilentlyContinue).Source
if (-not $adb) {
    $adb = Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'
}
if (-not (Test-Path $adb)) {
    throw "No se encontró adb. Instala Android platform-tools o agrégalo al PATH."
}

$devices = & $adb devices | Select-Object -Skip 1 | Where-Object { $_ -match '\tdevice$' }
if (-not $devices) {
    throw "No hay ninguna tablet conectada por USB (o falta autorizar la depuración USB en la tablet)."
}

& $adb reverse "tcp:$Port" "tcp:$Port"
if ($LASTEXITCODE -ne 0) { throw "adb reverse falló" }

Write-Host "Listo: en la tablet toca 'Conectar por USB' (127.0.0.1:$Port)." -ForegroundColor Green
