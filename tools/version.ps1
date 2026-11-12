<#
.SYNOPSIS
Affiche la version d'Aria Cockpit, lue dans Directory.Build.props : le seul endroit où elle est écrite.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$props = Join-Path (Split-Path -Parent $PSScriptRoot) 'Directory.Build.props'
[xml]$xml = Get-Content -LiteralPath $props -Raw -Encoding UTF8
$version = @($xml.Project.PropertyGroup | ForEach-Object { $_.SelectSingleNode('Version') } | Where-Object { $_ } | ForEach-Object { $_.InnerText.Trim() })[0]
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Version inattendue dans Directory.Build.props : « $version ». Attendu : X.Y.Z." }
$version