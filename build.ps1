<#
.SYNOPSIS
Compile Aria Cockpit et son banc d'essai dans .work\sortie\.

.DESCRIPTION
Produit la version autonome : Aria-Cockpit.exe et Aria-Bench.exe, sans rien d'autre à installer.
Ne demande pas les droits administrateur et ne lance rien : pour compiler puis lancer d'un double-clic,
« Lancer Aria Cockpit.cmd » reste là.

    .\build.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$racine = $PSScriptRoot
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "dotnet est introuvable : installe le SDK .NET 10." }

$sortie = Join-Path $racine '.work\sortie'
# Une jonction à la place de .work ou de .work\sortie mènerait la suppression qui suit hors du dépôt.
foreach ($d in (Join-Path $racine '.work'), $sortie) {
    if ((Test-Path -LiteralPath $d) -and ((Get-Item -LiteralPath $d -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "$d est un lien ou une jonction : rien n'est supprimé. Retire-le, puis relance."
    }
}
if (Test-Path -LiteralPath $sortie) { Remove-Item -LiteralPath $sortie -Recurse -Force }

& dotnet publish (Join-Path $racine 'src\Aria.Gui') -c Release -o $sortie
if ($LASTEXITCODE -ne 0) { throw "La compilation d'Aria Cockpit a échoué." }
& dotnet publish (Join-Path $racine 'src\Aria.Bench') -c Release -r win-x64 '-p:SelfContained=true' -o $sortie
if ($LASTEXITCODE -ne 0) { throw "La compilation du banc d'essai a échoué." }

foreach ($exe in 'Aria-Cockpit.exe', 'Aria-Bench.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $sortie $exe))) { throw "$exe est absent de .work\sortie\ après la compilation." }
}
Write-Host "Prêt : $sortie"