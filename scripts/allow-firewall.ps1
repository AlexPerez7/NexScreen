<#
.SYNOPSIS
    Abre el puerto del host en el Firewall de Windows para conectarse por WiFi.
    Requiere ejecutarse como administrador. Solo hace falta una vez.
#>
#Requires -RunAsAdministrator
param(
    [int]$Port = 27190
)

$ErrorActionPreference = 'Stop'
$name = 'NexScreen'

if (Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue) {
    Remove-NetFirewallRule -DisplayName $name
}

New-NetFirewallRule -DisplayName $name `
    -Description 'Streaming de pantalla hacia la tablet (nexscreen-host)' `
    -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow `
    -Profile Domain, Private | Out-Null

Write-Host "Regla '$name' creada para TCP $Port (perfiles Dominio y Privado)." -ForegroundColor Green
