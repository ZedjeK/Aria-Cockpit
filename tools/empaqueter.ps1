<#
.SYNOPSIS
Fabrique les deux zips d'une version d'Aria Cockpit et leurs empreintes, dans .work\release\.

.DESCRIPTION
Produit exactement ce que la release publie :

    Aria-Cockpit-<version>-autonome.zip   rien à installer (runtime .NET inclus)
    Aria-Cockpit-<version>-leger.zip      pour les PC qui ont déjà le runtime .NET 10 Desktop

Chaque zip contient, à sa racine, Aria-Cockpit.exe, Aria-Bench.exe, LICENSE et THIRD-PARTY-NOTICES.txt, et rien
d'autre : la mise à jour intégrée à l'appli cherche ces noms-là. À côté de chaque zip, un fichier .sha256
(« empreinte  nom »).

La version est celle de tools\version.ps1. Ne demande pas les droits administrateur et ne publie rien.

    .\tools\empaqueter.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$racine = Split-Path -Parent $PSScriptRoot
$version = "$(& (Join-Path $PSScriptRoot 'version.ps1') | Select-Object -Last 1)".Trim()

$travail = Join-Path $racine '.work'
$autonome = Join-Path $travail 'sortie'
$leger = Join-Path $travail 'leger'
$release = Join-Path $travail 'release'

# Une jonction à la place d'un de ces dossiers mènerait les suppressions qui suivent hors du dépôt.
foreach ($d in $travail, $leger, $release) {
    if ((Test-Path -LiteralPath $d) -and ((Get-Item -LiteralPath $d -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "$d est un lien ou une jonction : rien n'est supprimé. Retire-le, puis relance."
    }
}

# Variante autonome : c'est ce que fait build.ps1, dans .work\sortie\
& (Join-Path $racine 'build.ps1')

# Variante légère : les mêmes programmes, sans le runtime .NET
if (Test-Path -LiteralPath $leger) { Remove-Item -LiteralPath $leger -Recurse -Force }
& dotnet publish (Join-Path $racine 'src\Aria.Gui') -c Release '-p:SelfContained=false' -o $leger
if ($LASTEXITCODE -ne 0) { throw "La compilation légère d'Aria Cockpit a échoué." }
& dotnet publish (Join-Path $racine 'src\Aria.Bench') -c Release -r win-x64 '-p:SelfContained=false' -o $leger
if ($LASTEXITCODE -ne 0) { throw "La compilation légère du banc d'essai a échoué." }

if (Test-Path -LiteralPath $release) { Remove-Item -LiteralPath $release -Recurse -Force }
New-Item -ItemType Directory -Path $release | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem
$attendus = 'Aria-Bench.exe', 'Aria-Cockpit.exe', 'LICENSE', 'THIRD-PARTY-NOTICES.txt'

foreach ($variante in @{ Nom = 'autonome'; Dossier = $autonome }, @{ Nom = 'leger'; Dossier = $leger }) {
    $fichiers = @(
        (Join-Path $variante.Dossier 'Aria-Cockpit.exe'),
        (Join-Path $variante.Dossier 'Aria-Bench.exe'),
        (Join-Path $racine 'LICENSE'),
        (Join-Path $racine 'THIRD-PARTY-NOTICES.txt')
    )
    foreach ($f in $fichiers) {
        if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { throw "Fichier manquant pour le zip $($variante.Nom) : $f" }
    }

    $nom = "Aria-Cockpit-$version-$($variante.Nom).zip"
    $zip = Join-Path $release $nom
    Compress-Archive -LiteralPath $fichiers -DestinationPath $zip

    # Le zip est relu : quatre fichiers à sa racine, ceux que la mise à jour intégrée attend, et rien d'autre
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try { $contenu = @($archive.Entries | ForEach-Object { $_.FullName } | Sort-Object) }
    finally { $archive.Dispose() }
    if (($contenu -join '|') -ne ($attendus -join '|')) {
        throw "$nom ne contient pas ce qui est attendu. Trouvé : $($contenu -join ', ')."
    }

    $empreinte = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$zip.sha256" -Value "$empreinte  $nom`n" -Encoding ascii -NoNewline
    Write-Host ("{0}  {1}  {2:N1} Mo" -f $empreinte, $nom, ((Get-Item -LiteralPath $zip).Length / 1MB))
}

Write-Host "Prêt : $release"
