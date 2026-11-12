<#
.SYNOPSIS
Lance les tests d'Aria Cockpit. La CI appelle ce même script.

.DESCRIPTION
Les tests n'utilisent ni le matériel ni le système : ils passent sans droits administrateur et sans carte NVIDIA.
Code de retour non nul si un test échoue.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$racine = Split-Path -Parent $PSScriptRoot
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "dotnet est introuvable : installe le SDK .NET 10." }

& dotnet test (Join-Path $racine 'tests\Aria.Tests') -c Release --logger 'console;verbosity=normal'
exit $LASTEXITCODE