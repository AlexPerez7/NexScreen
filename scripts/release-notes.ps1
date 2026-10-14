<#
.SYNOPSIS
    Arma el texto de un release de GitHub: los cambios de la versión (de CHANGELOG.md) y las
    instrucciones de instalación.

.DESCRIPTION
    Falla si CHANGELOG.md no tiene una sección "## [X.Y.Z]" con contenido para la versión: así
    ningún release sale sin la lista de cambios. Lo usa .github/workflows/release.yml; también se
    puede correr en el PC para revisar el texto antes de crear el tag.

.EXAMPLE
    scripts\release-notes.ps1 -Version 0.5.0
#>
param(
    [Parameter(Mandatory)] [string]$Version,
    [string]$Changelog = (Join-Path $PSScriptRoot '..\CHANGELOG.md'),
    # Si se indica, escribe el texto en este archivo (UTF-8) además de devolverlo.
    [string]$OutFile
)

$ErrorActionPreference = 'Stop'

$text = [IO.File]::ReadAllText((Resolve-Path $Changelog)) -replace "`r`n", "`n"
$pattern = "(?ms)^## \[$([regex]::Escape($Version))\][^\n]*\n(.*?)(?=^## \[|\z)"
$match = [regex]::Match($text, $pattern)
if (-not $match.Success -or -not $match.Groups[1].Value.Trim()) {
    throw "CHANGELOG.md no tiene una sección '## [$Version]' con los cambios. Agrégala antes de publicar."
}
$changes = $match.Groups[1].Value.Trim()

$notes = @"
$changes

## Instalación

1. Descarga **NexScreen-Setup-$Version.exe** e instálalo en el PC. Si ya tienes NexScreen 0.5.0 o
   posterior, la bandeja te avisa sola y lo instala con un clic.
2. En la tablet activa *Opciones de desarrollador → Depuración USB* y enchúfala: NexScreen le
   instala la app (o la actualiza) y se conecta solo. También puedes instalar
   **NexScreen-$Version.apk** a mano.

Por WiFi: abre la app en la tablet y toca tu PC en "PCs en la red" (red marcada como Privada). La
primera vez la tablet te pide el código que muestra el PC.
"@

if ($OutFile) { [IO.File]::WriteAllText($OutFile, $notes, [Text.UTF8Encoding]::new($false)) }
$notes
