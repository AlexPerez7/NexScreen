<#
.SYNOPSIS
    Instala (o desinstala con -Uninstall) el monitor virtual que NexScreen usa para extender el escritorio.
.DESCRIPTION
    Instala Virtual Display Driver 25.7.23 (https://github.com/VirtualDrivers/Virtual-Display-Driver,
    MIT, firmado por SignPath Foundation) con nefcon 1.14.0 (Nefarius), igual que el script
    silent-install.ps1 oficial pero con versiones y hashes fijos y verificación de firmas.

    Pasos:
      1. Descarga y verifica SHA-256 y firma Authenticode de ambos paquetes.
      2. Agrega el certificado del driver a "Editores de confianza" del equipo
         (necesario porque el driver está firmado por SignPath y no por Microsoft).
      3. Escribe C:\VirtualDisplayDriver\vdd_settings.xml con las resoluciones de la tablet y
         da permiso de escritura a los usuarios para que nexscreen-host pueda ajustarlas sin admin.
      4. Crea el dispositivo Root\MttVDD e instala el driver.

    Requiere ejecutarse como administrador. Solo hace falta una vez.
#>
#Requires -RunAsAdministrator
param(
    [switch]$Uninstall,

    # Zips ya descargados (el instalador los incluye): se verifican igual que si se descargaran.
    [string]$DriverZip,
    [string]$NefconZip,

    # Si se indica, todo lo que muestra el script queda en este archivo (el instalador lo corre oculto).
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if ($LogPath) {
    New-Item -ItemType Directory -Force (Split-Path $LogPath) | Out-Null
    Start-Transcript -Path $LogPath -Append | Out-Null
}

$DriverUrl = 'https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip'
$DriverSha256 = 'E24210692B442B39AF763536330CE78B423F19342B7A7792C26DE3944E418B3A'
$NefconUrl = 'https://github.com/nefarius/nefcon/releases/download/v1.14.0/nefcon_v1.14.0.zip'
$NefconSha256 = 'A15557DA24A9EFCA203158DE3B43B0EAF982DB231F0194031F1ED428BC13E669'

$HardwareId = 'Root\MttVDD'
$DisplayClassGuid = '4d36e968-e325-11ce-bfc1-08002be10318'
$ConfigDir = 'C:\VirtualDisplayDriver'

function Get-VerifiedArchive([string]$Url, [string]$Sha256, [string]$Destination, [string]$LocalZip) {
    $zip = "$Destination.zip"
    if ($LocalZip) {
        Copy-Item $LocalZip $zip
    } else {
        Invoke-WebRequest -Uri $Url -OutFile $zip -UseBasicParsing
    }
    $actual = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($actual -ne $Sha256) { throw "Hash inválido para $Url`n  esperado $Sha256`n  obtenido $actual" }
    Expand-Archive -Path $zip -DestinationPath $Destination -Force
}

function Assert-Signed([string]$Path, [string]$ExpectedSigner) {
    $signature = Get-AuthenticodeSignature $Path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike "*$ExpectedSigner*") {
        throw "Firma inválida en $Path ($($signature.Status), $($signature.SignerCertificate.Subject))"
    }
}

function Get-VirtualDisplayDevice {
    Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | Where-Object { $_.HardwareID -contains $HardwareId }
}

if (-not $Uninstall -and (Get-VirtualDisplayDevice)) {
    Write-Host 'El monitor virtual ya está instalado; no hay nada que hacer.' -ForegroundColor Green
    if ($LogPath) { Stop-Transcript | Out-Null }
    return
}

$work = Join-Path $env:TEMP "nexscreen-vdd-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $work | Out-Null
try {
    Write-Host 'Preparando nefcon...' -ForegroundColor Cyan
    Get-VerifiedArchive $NefconUrl $NefconSha256 (Join-Path $work 'nefcon') $NefconZip
    $nefcon = Join-Path $work 'nefcon\x64\nefconc.exe'
    Assert-Signed $nefcon 'Nefarius Software Solutions'

    if ($Uninstall) {
        Write-Host 'Quitando el monitor virtual...' -ForegroundColor Cyan
        & $nefcon --remove-device-node --hardware-id $HardwareId --class-guid $DisplayClassGuid
        if ($LASTEXITCODE -notin 0, 3010) { throw "nefcon terminó con código $LASTEXITCODE" }
        Write-Host "Monitor virtual desinstalado. La carpeta $ConfigDir se conserva." -ForegroundColor Green
        return
    }

    Write-Host 'Preparando Virtual Display Driver...' -ForegroundColor Cyan
    Get-VerifiedArchive $DriverUrl $DriverSha256 (Join-Path $work 'vdd') $DriverZip
    $driverDir = Join-Path $work 'vdd\VirtualDisplayDriver'
    $catalog = Join-Path $driverDir 'mttvdd.cat'
    Assert-Signed $catalog 'SignPath Foundation'
    Assert-Signed (Join-Path $driverDir 'MttVDD.dll') 'SignPath Foundation'

    Write-Host 'Confiando en el editor del driver (SignPath Foundation)...' -ForegroundColor Cyan
    $signer = (Get-AuthenticodeSignature $catalog).SignerCertificate
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('TrustedPublisher', 'LocalMachine')
    $store.Open('ReadWrite')
    try { $store.Add($signer) } finally { $store.Close() }

    Write-Host "Escribiendo configuración en $ConfigDir..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Path $ConfigDir -Force | Out-Null
    $settings = Join-Path $ConfigDir 'vdd_settings.xml'
    if (Test-Path $settings) { Copy-Item $settings "$settings.bak" -Force }
    # La primera resolución es la preferida: la nativa de la Galaxy Tab S9 FE.
    @'
<?xml version='1.0' encoding='utf-8'?>
<!-- Generado por NexScreen. nexscreen-host agrega la resolución de cada tablet que se conecta. -->
<vdd_settings>
    <monitors>
        <count>1</count>
    </monitors>
    <gpu>
        <friendlyname>default</friendlyname>
    </gpu>
    <global>
        <g_refresh_rate>60</g_refresh_rate>
    </global>
    <resolutions>
        <resolution><width>2304</width><height>1440</height><refresh_rate>60</refresh_rate></resolution>
        <resolution><width>2560</width><height>1600</height><refresh_rate>60</refresh_rate></resolution>
        <resolution><width>1920</width><height>1200</height><refresh_rate>60</refresh_rate></resolution>
        <resolution><width>1920</width><height>1080</height><refresh_rate>60</refresh_rate></resolution>
    </resolutions>
    <logging>
        <SendLogsThroughPipe>true</SendLogsThroughPipe>
        <logging>false</logging>
        <debuglogging>false</debuglogging>
    </logging>
    <colour>
        <SDR10bit>false</SDR10bit>
        <HDRPlus>false</HDRPlus>
        <ColourFormat>RGB</ColourFormat>
    </colour>
    <cursor>
        <HardwareCursor>true</HardwareCursor>
        <CursorMaxX>128</CursorMaxX>
        <CursorMaxY>128</CursorMaxY>
        <AlphaCursorSupport>true</AlphaCursorSupport>
        <XorCursorSupportLevel>2</XorCursorSupportLevel>
    </cursor>
    <edid>
        <CustomEdid>false</CustomEdid>
        <PreventSpoof>false</PreventSpoof>
        <EdidCeaOverride>false</EdidCeaOverride>
    </edid>
</vdd_settings>
'@ | Set-Content -Path $settings -Encoding UTF8
    # S-1-5-32-545 = Usuarios (independiente del idioma de Windows).
    & icacls $ConfigDir /grant '*S-1-5-32-545:(OI)(CI)M' /T /Q | Out-Null

    Write-Host 'Instalando el driver...' -ForegroundColor Cyan
    Push-Location $driverDir
    try {
        & $nefcon install .\MttVDD.inf $HardwareId
        if ($LASTEXITCODE -notin 0, 3010) { throw "nefcon terminó con código $LASTEXITCODE" }
    } finally {
        Pop-Location
    }

    $device = Get-VirtualDisplayDevice
    if (-not $device) { throw 'El driver se instaló pero no aparece el dispositivo Root\MttVDD' }
    Write-Host "Listo: $($device.FriendlyName) [$($device.Status)]" -ForegroundColor Green
    if ($LASTEXITCODE -eq 3010) { Write-Host 'Windows pide reiniciar para terminar la instalación.' -ForegroundColor Yellow }
} finally {
    Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue
    if ($LogPath) { Stop-Transcript | Out-Null }
}
