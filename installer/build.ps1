<#
.SYNOPSIS
    Arma el instalador de NexScreen: dist\NexScreen-Setup-<versión>.exe
.DESCRIPTION
    1. Publica la app de bandeja y la consola (.NET 8 self-contained, win-x64).
    2. Descarga (con caché y SHA-256 fijo) ffmpeg LGPL, adb, el driver del monitor virtual y nefcon.
    3. Junta todo en installer\staging\app con el APK de la tablet y las licencias.
    4. Compila installer\nexscreen.iss con Inno Setup (ISCC).
.EXAMPLE
    installer\build.ps1
    installer\build.ps1 -Apk android\app\build\outputs\apk\release\app-release.apk -Version 0.5.0
#>
param(
    # APK de la app Android que el host instala solo al enchufar la tablet.
    [string]$Apk = 'android\app\build\outputs\apk\debug\app-debug.apk',

    # Por defecto, la de host\Directory.Build.props.
    [string]$Version,

    # Ruta a ISCC.exe si no está en los lugares habituales.
    [string]$Iscc
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path $PSScriptRoot
Set-Location $root

# Dependencias con versión y hash fijos. Para actualizar una: cambiar URL y SHA-256 juntos.
$Pins = @{
    ffmpeg = @{
        Url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-14-13-03/ffmpeg-n9.0.2-27-g7e93014c5b-win64-lgpl-shared-9.0.zip'
        Sha256 = '75943DD3BB603E49FFE01360AC683E2F17D13C0C4193D68CBD1D11448FA32891'
    }
    adb = @{
        Url = 'https://dl.google.com/android/repository/platform-tools_r37.0.1-win.zip'
        Sha256 = '45F4D63113E895EBDE0C90F194099A4676B6AC653BD28D54314A9E022BBC1A99'
    }
    driver = @{
        Url = 'https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip'
        Sha256 = 'E24210692B442B39AF763536330CE78B423F19342B7A7792C26DE3944E418B3A'
    }
    nefcon = @{
        Url = 'https://github.com/nefarius/nefcon/releases/download/v1.14.0/nefcon_v1.14.0.zip'
        Sha256 = 'A15557DA24A9EFCA203158DE3B43B0EAF982DB231F0194031F1ED428BC13E669'
    }
}

function Step([string]$text) { Write-Host "`n== $text" -ForegroundColor Cyan }

function Get-Pinned([string]$name) {
    $pin = $Pins[$name]
    $cache = Join-Path $PSScriptRoot '.cache'
    New-Item -ItemType Directory -Force $cache | Out-Null
    $file = Join-Path $cache "$name.zip"
    if (-not (Test-Path $file) -or (Get-FileHash $file -Algorithm SHA256).Hash -ne $pin.Sha256) {
        Write-Host "Descargando $name..."
        Invoke-WebRequest $pin.Url -OutFile $file -UseBasicParsing
        $actual = (Get-FileHash $file -Algorithm SHA256).Hash
        if ($actual -ne $pin.Sha256) { throw "Hash inválido para $name`n  esperado $($pin.Sha256)`n  obtenido $actual" }
    }
    return $file
}

function Expand-Pinned([string]$name) {
    $destination = Join-Path $PSScriptRoot ".cache\$name"
    if (-not (Test-Path $destination)) { Expand-Archive (Get-Pinned $name) $destination }
    return $destination
}

function Find-Iscc {
    if ($Iscc) { return $Iscc }
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $found) { $found = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
    if (-not $found) { throw 'No se encontró Inno Setup. Instálalo con: winget install JRSoftware.InnoSetup' }
    return $found
}

if (-not $Version) {
    $Version = ([xml](Get-Content host\Directory.Build.props)).Project.PropertyGroup.Version | Select-Object -First 1
}
if (-not (Test-Path $Apk)) { throw "No se encontró el APK: $Apk (compílalo con android\gradlew.bat -p android assembleDebug)" }

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = "$env:ProgramFiles\dotnet\dotnet.exe" }

$staging = Join-Path $PSScriptRoot 'staging'
$app = Join-Path $staging 'app'
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force "$app\tools", "$app\driver", "$app\licenses" | Out-Null

Step "Publicando NexScreen $Version"
foreach ($project in 'host\src\NexScreen.Tray', 'host\src\NexScreen.Host') {
    & $dotnet publish $project -c Release -r win-x64 --self-contained true -p:Version=$Version -o $app --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló para $project" }
}

Step 'ffmpeg (LGPL)'
$ffmpeg = Get-ChildItem (Expand-Pinned 'ffmpeg') -Recurse -Filter ffmpeg.exe | Select-Object -First 1
Copy-Item $ffmpeg.FullName, "$($ffmpeg.DirectoryName)\*.dll" "$app\tools"
Copy-Item (Join-Path (Split-Path $ffmpeg.DirectoryName) 'LICENSE.txt') "$app\licenses\ffmpeg-LICENSE.txt"

Step 'adb'
$platformTools = Join-Path (Expand-Pinned 'adb') 'platform-tools'
Copy-Item "$platformTools\adb.exe", "$platformTools\AdbWinApi.dll", "$platformTools\AdbWinUsbApi.dll" "$app\tools"
Copy-Item "$platformTools\NOTICE.txt" "$app\licenses\adb-NOTICE.txt"

Step 'App de la tablet'
Copy-Item $Apk "$app\tools\NexScreen.apk"

Step 'Monitor virtual'
Copy-Item (Get-Pinned 'driver') "$app\driver\VirtualDisplayDriver.zip"
Copy-Item (Get-Pinned 'nefcon') "$app\driver\nefcon.zip"
Copy-Item scripts\install-virtual-display.ps1, scripts\allow-firewall.ps1 "$app\driver"

Copy-Item "$PSScriptRoot\THIRD-PARTY-NOTICES.md" "$app\licenses"

Step 'Compilando el instalador'
$iscc = Find-Iscc
& $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$app" "/DOutputDir=$root\dist" "$PSScriptRoot\nexscreen.iss"
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup falló' }

$setup = Get-Item "$root\dist\NexScreen-Setup-$Version.exe"
Write-Host ("`nInstalador listo: {0} ({1:N0} MB)" -f $setup.FullName, ($setup.Length / 1MB)) -ForegroundColor Green
