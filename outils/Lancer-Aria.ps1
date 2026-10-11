# Lance Aria sans rien installer à la main :
#   1. demande les droits administrateur (une seule fois pour tout le script)
#   2. installe le SDK .NET 10 via winget s'il est absent
#   3. compile Aria dans le dossier "publish" du projet
#   4. lance l'interface Aria.exe
#      (options avancées : -Console pour la version console Aria.Cli.exe, -Simulation pour n'y rien modifier)
#
# Compatible avec Windows PowerShell 5.1 (présent d'office sur Windows 11).

param([switch] $Console, [switch] $Simulation)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$projet = Split-Path -Parent $PSScriptRoot
$sortie = Join-Path $projet 'publish'
if ($Console) {
    $csproj = Join-Path $projet 'src\Aria.Cli\Aria.Cli.csproj'
    $exe = Join-Path $sortie 'Aria.Cli.exe'
}
else {
    $csproj = Join-Path $projet 'src\Aria.Gui\Aria.Gui.csproj'
    $exe = Join-Path $sortie 'Aria.exe'
}

function Etape([string] $texte) {
    Write-Host ''
    Write-Host "==> $texte" -ForegroundColor Cyan
}

function Arret([string] $texte) {
    Write-Host ''
    Write-Host "[X] $texte" -ForegroundColor Red
    Write-Host 'Rien n''a été modifié sur votre PC.' -ForegroundColor Red
    Read-Host "`nAppuyez sur Entrée pour fermer"
    exit 1
}

function Sdk10Installe {
    try {
        $sdks = & dotnet --list-sdks 2>$null
        return [bool]($sdks | Where-Object { $_ -match '^10\.' })
    }
    catch {
        return $false   # dotnet absent du PATH
    }
}

# --- 1. Droits administrateur -------------------------------------------------------------
$identite = [Security.Principal.WindowsIdentity]::GetCurrent()
$estAdmin = ([Security.Principal.WindowsPrincipal] $identite).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $estAdmin) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Console) { $arguments += '-Console' }
    if ($Simulation) { $arguments += '-Simulation' }
    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $arguments
    }
    catch {
        Arret 'Droits administrateur refusés : Aria en a besoin pour lire les réglages du système.'
    }
    exit 0   # la suite se déroule dans la nouvelle fenêtre administrateur
}

Write-Host 'Aria — préparation' -ForegroundColor White

# --- 2. SDK .NET 10 -----------------------------------------------------------------------
if (Sdk10Installe) {
    Etape 'SDK .NET 10 déjà installé.'
}
else {
    Etape 'Installation du SDK .NET 10 (une seule fois, quelques minutes)…'
    if (-not (Get-Command 'winget' -ErrorAction SilentlyContinue)) {
        Arret 'winget est introuvable. Installez le SDK .NET 10 depuis https://dotnet.microsoft.com/download puis relancez ce fichier.'
    }

    & winget install --id Microsoft.DotNet.SDK.10 --exact --silent --accept-source-agreements --accept-package-agreements

    # Recharge le PATH pour trouver dotnet sans rouvrir de fenêtre
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')

    if (-not (Sdk10Installe)) {
        Arret "L'installation du SDK .NET 10 a échoué (code $LASTEXITCODE). Installez-le depuis https://dotnet.microsoft.com/download puis relancez ce fichier."
    }
}

# --- 3. Compilation -----------------------------------------------------------------------
Etape "Compilation de $(Split-Path -Leaf $exe) (une à deux minutes la première fois)…"
& dotnet publish $csproj -c Release -o $sortie
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe)) {
    Arret 'La compilation a échoué : copiez les messages ci-dessus dans la conversation pour les faire corriger.'
}
Write-Host "Prêt : $exe" -ForegroundColor Green

# --- 4. Lancement -------------------------------------------------------------------------
Etape 'Lancement d''Aria…'
if (-not $Console) {
    # Interface graphique : elle s'ouvre dans sa propre fenêtre, celle-ci peut se fermer
    Start-Process -FilePath $exe
}
elseif ($Simulation) {
    & $exe --simulation
}
else {
    & $exe
}
