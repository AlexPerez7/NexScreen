<#
.SYNOPSIS
    Abre en el Firewall de Windows los puertos que NexScreen usa por WiFi:
    TCP 27190 (video e input) y UDP 27191 (descubrimiento: la tablet encuentra el PC).
    Requiere ejecutarse como administrador. Solo hace falta una vez.
#>
#Requires -RunAsAdministrator
param(
    [int]$Port = 27190,
    [int]$DiscoveryPort = 27191
)

$ErrorActionPreference = 'Stop'
$rules = @(
    @{ Name = 'NexScreen'; Protocol = 'TCP'; Port = $Port; Description = 'Video e input hacia la tablet (NexScreen)' },
    @{ Name = 'NexScreen (descubrimiento)'; Protocol = 'UDP'; Port = $DiscoveryPort; Description = 'La tablet encuentra el PC en la red (NexScreen)' }
)

foreach ($rule in $rules) {
    if (Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue) {
        Remove-NetFirewallRule -DisplayName $rule.Name
    }

    New-NetFirewallRule -DisplayName $rule.Name -Description $rule.Description `
        -Direction Inbound -Protocol $rule.Protocol -LocalPort $rule.Port -Action Allow `
        -Profile Domain, Private | Out-Null
    Write-Host "Regla '$($rule.Name)' creada para $($rule.Protocol) $($rule.Port) (perfiles Dominio y Privado)." -ForegroundColor Green
}

$public = Get-NetConnectionProfile | Where-Object NetworkCategory -eq 'Public'
if ($public) {
    Write-Host "Atención: la red '$($public.Name -join ', ')' es Pública y estas reglas no aplican ahí." -ForegroundColor Yellow
    Write-Host 'Márcala como Privada en Configuración > Red e Internet si es tu red de confianza.' -ForegroundColor Yellow
}
