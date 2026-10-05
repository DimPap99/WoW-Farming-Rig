#Requires -Version 5.1
<#
.SYNOPSIS
  Download pre-baked navmesh tiles from the CDN into the local cache - no WoW
  client or bake needed. Zero dependencies (built-in PowerShell, 5.1 or 7 on
  Windows, Linux and macOS).

.DESCRIPTION
  Only the download scope in download-common.ps1 is fetched: WoW Classic
  (precata Azeroth + Kalimdor) and WoW: Forever. Other eras and continents on
  the CDN are listed as skipped.

  Destination is taken from DataConfig: reads data_config.json (Root) and writes
  to <Root>/PathInfo/navmesh/<era>/<continent>/<hash>, so a customized Root is
  honored. The era and per-continent settings-hash come from the CDN index.json,
  i.e. they match the DEFAULT agent config. If you run a custom agent config
  (different hash) you must bake instead.

.EXAMPLE
  ./download-navmesh.ps1                 # everything in scope
  ./download-navmesh.ps1 -Continent Kalimdor
  ./download-navmesh.ps1 -Era forever
  ./download-navmesh.ps1 -Force          # re-download even if present
#>
[CmdletBinding()]
param(
    [string]$Continent = "",     # e.g. Kalimdor; empty = all in scope
    [string]$Era = "",           # precata or forever; empty = both
    [string]$DataConfig = "",    # explicit data_config.json path
    [switch]$Force               # re-download even if the cache looks complete
)

. (Join-Path $PSScriptRoot "download-common.ps1")

Assert-ScopeArguments $Era $Continent

$dest = Join-Segments (Resolve-DataRoot $DataConfig) @("PathInfo", "navmesh")
Write-Host "navmesh destination: $dest"
Write-Host "scope: $(Format-Scope)"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

Write-Host "fetching $CdnBase/navmesh/index.json ..."
$index = Get-CdnJson "navmesh/index.json"
if (-not $index) { throw "The CDN has no navmesh/index.json." }

# An older index carries no era; those bundles were all precata.
$all = @($index.continents | ForEach-Object {
        $e = if ($_.era) { $_.era } else { "precata" }
        $_ | Add-Member -NotePropertyName era -NotePropertyValue $e -Force -PassThru
    })

$items = @($all | Where-Object { Test-InScope $_.era $_.name })
$skipped = @($all | Where-Object { -not (Test-InScope $_.era $_.name) })
if ($skipped) {
    Write-Host ("skipping {0} bundle(s) outside the scope: {1}" -f $skipped.Count,
        (($skipped | ForEach-Object { "$($_.era)/$($_.name)" }) -join ", "))
}

foreach ($scopeEra in $DownloadScope.Keys) {
    if ($Era -and $scopeEra -ne $Era) { continue }
    if (-not @($items | Where-Object { $_.era -eq $scopeEra })) {
        $hint = if ($scopeEra -eq "forever") { " - bake it locally, see docs/forever-port.md" } else { "" }
        Write-Host "  ${scopeEra}: nothing published on the CDN yet$hint."
    }
}

if ($Continent) { $items = @($items | Where-Object { $_.name -eq $Continent }) }
if ($Era) { $items = @($items | Where-Object { $_.era -eq $Era }) }
if (-not $items) {
    Write-Host "Nothing to download."
    return
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

foreach ($it in $items) {
    $label = "{0}/{1}/{2}" -f $it.era, $it.name, $it.hash
    $targetDir = Join-Segments $dest @($it.era, $it.name, $it.hash)

    $have = 0
    if (Test-Path $targetDir) {
        $have = @(Get-ChildItem $targetDir -Filter *.dnm -File -ErrorAction SilentlyContinue).Count
    }
    if (-not $Force -and $have -ge $it.tiles) {
        Write-Host ("  {0}: present ({1} tiles), skipping" -f $label, $have)
        continue
    }

    $zip = New-ScratchFile ("navmesh-{0}-{1}-{2}.zip" -f $it.era, $it.name, $it.hash)
    Write-Host ("  {0}: downloading {1:N0} MB ..." -f $label, ($it.bytes / 1MB))
    try {
        Save-CdnFile (Get-CdnUrl $it.zip) $zip $it.bytes

        # ExtractToDirectory cannot merge, so the old tiles go first.
        if (Test-Path $targetDir) { Remove-Item $targetDir -Recurse -Force }
        Write-Host "  extracting ..."
        # zip arcnames are <era>/<continent>/<hash>/tile_*.dnm, so extract at the navmesh root.
        [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $dest)
    }
    finally {
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
    }
    Write-Host ("  -> {0} ({1} tiles)" -f $targetDir, $it.tiles)
}

Write-Host "Done."
