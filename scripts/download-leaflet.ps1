#Requires -Version 5.1
<#
.SYNOPSIS
  Download pre-generated Leaflet minimap tiles from the CDN into the local cache -
  no WoW client or MPQ extraction needed. Zero dependencies (built-in PowerShell,
  5.1 or 7 on Windows, Linux and macOS).

.DESCRIPTION
  Only the download scope in download-common.ps1 is fetched: WoW Classic
  (precata Azeroth + Kalimdor) and WoW: Forever.

  The CDN ships one <era>-tiles.zip per era holding every continent of that era
  (precata also carries Outland and Northrend). Rather than pull the whole
  bundle, this reads the zip's directory with two small range requests and then
  fetches only the byte span of each continent in scope - the bundle stores
  each continent's tiles contiguously, so that is one request per continent and
  nothing outside the scope is downloaded.

  Destination is taken from DataConfig: reads data_config.json (Root) and writes
  to <Root>/leaflet/<era>/<Continent>, so a customized Root is honored and one
  era's art never overwrites another's.

.EXAMPLE
  ./download-leaflet.ps1                 # everything in scope
  ./download-leaflet.ps1 -Continent Azeroth
  ./download-leaflet.ps1 -Force          # re-download even if tiles are present
#>
[CmdletBinding()]
param(
    [string]$Era = "",           # precata or forever; empty = both
    [string]$Continent = "",     # e.g. Azeroth; empty = all in scope
    [string]$DataConfig = "",    # explicit data_config.json path
    [switch]$Force               # re-download even if the cache looks populated
)

. (Join-Path $PSScriptRoot "download-common.ps1")

Assert-ScopeArguments $Era $Continent

# --- zip reading over HTTP ranges --------------------------------------------

$ZipEndRecordSize = 22                       # end-of-central-directory record
$ZipMaxTail = $ZipEndRecordSize + 65535      # ... plus the longest comment
$ZipCentralSignature = 0x02014b50
$ZipLocalSignature = 0x04034b50
$ZipLocalHeaderSize = 30
$ZipStored = 0
$ZipDeflated = 8

# Lists a remote zip's entries without downloading it. Returns $null on 404.
function Read-RemoteZipDirectory([string]$url) {
    try {
        $probe = Invoke-CdnGet $url -From 0 -To 0
    }
    catch {
        if ($_.Exception.Message -match 'HTTP 404') { return $null }
        throw
    }
    $total = [long]$probe.Content.Headers.ContentRange.Length
    $probe.Dispose()

    $tailLength = [Math]::Min($total, [long]$ZipMaxTail)
    $tail = Get-CdnBytes $url ($total - $tailLength) ($total - 1)

    $end = -1
    for ($i = $tail.Length - $ZipEndRecordSize; $i -ge 0; $i--) {
        if ($tail[$i] -eq 0x50 -and $tail[$i + 1] -eq 0x4b -and $tail[$i + 2] -eq 5 -and $tail[$i + 3] -eq 6) {
            $end = $i
            break
        }
    }
    if ($end -lt 0) { throw "$url is not a zip archive." }

    $count = [BitConverter]::ToUInt16($tail, $end + 10)
    $size = [BitConverter]::ToUInt32($tail, $end + 12)
    $offset = [BitConverter]::ToUInt32($tail, $end + 16)
    if ($count -eq 0xFFFF -or $size -eq 0xFFFFFFFF -or $offset -eq 0xFFFFFFFF) {
        throw "$url is a ZIP64 archive, which this script does not read."
    }

    $directory = Get-CdnBytes $url $offset ($offset + $size - 1)
    $entries = New-Object System.Collections.Generic.List[object]
    $p = 0
    for ($n = 0; $n -lt $count; $n++) {
        if ([BitConverter]::ToUInt32($directory, $p) -ne $ZipCentralSignature) {
            throw "$url has a damaged central directory (entry $n)."
        }
        $nameLength = [BitConverter]::ToUInt16($directory, $p + 28)
        $entries.Add([pscustomobject]@{
                Name   = [Text.Encoding]::UTF8.GetString($directory, $p + 46, $nameLength)
                Method = [BitConverter]::ToUInt16($directory, $p + 10)
                Size   = [long][BitConverter]::ToUInt32($directory, $p + 20)
                Offset = [long][BitConverter]::ToUInt32($directory, $p + 42)
            })
        $p += 46 + $nameLength + [BitConverter]::ToUInt16($directory, $p + 30) + [BitConverter]::ToUInt16($directory, $p + 32)
    }

    $sorted = @($entries | Sort-Object Offset)
    return [pscustomobject]@{ Entries = $sorted; CentralOffset = [long]$offset }
}

function Read-Exact([System.IO.Stream]$stream, [int]$count) {
    $buffer = New-Object byte[] $count
    $read = 0
    while ($read -lt $count) {
        $n = $stream.Read($buffer, $read, $count - $read)
        if ($n -le 0) { throw "Unexpected end of downloaded data." }
        $read += $n
    }
    return , $buffer   # keep it a byte[] (see Get-CdnBytes)
}

# Writes the given entries out of a downloaded byte span that starts at $spanStart.
function Expand-ZipSpan([string]$spanFile, [long]$spanStart, $entries, [string]$root) {
    $made = New-Object 'System.Collections.Generic.HashSet[string]'
    $stream = [System.IO.File]::OpenRead($spanFile)
    try {
        foreach ($e in $entries) {
            $segments = $e.Name -split '/'
            if ($segments -contains '..') { throw "Refusing entry with a parent path: $($e.Name)" }

            $stream.Position = $e.Offset - $spanStart
            $header = Read-Exact $stream $ZipLocalHeaderSize
            if ([BitConverter]::ToUInt32($header, 0) -ne $ZipLocalSignature) {
                throw "Damaged local header for $($e.Name)."
            }
            $stream.Position += [BitConverter]::ToUInt16($header, 26) + [BitConverter]::ToUInt16($header, 28)
            $data = Read-Exact $stream $e.Size

            $out = Join-Segments $root $segments
            $dir = Split-Path -Parent $out
            if ($made.Add($dir)) { [void][System.IO.Directory]::CreateDirectory($dir) }

            if ($e.Method -eq $ZipStored) {
                [System.IO.File]::WriteAllBytes($out, $data)
            }
            elseif ($e.Method -eq $ZipDeflated) {
                $inflate = New-Object System.IO.Compression.DeflateStream(
                    (New-Object System.IO.MemoryStream(, $data)), [System.IO.Compression.CompressionMode]::Decompress)
                $file = [System.IO.File]::Create($out)
                try { $inflate.CopyTo($file) } finally { $file.Dispose(); $inflate.Dispose() }
            }
            else {
                throw "$($e.Name) uses zip method $($e.Method), which this script does not read."
            }
        }
    }
    finally {
        $stream.Dispose()
    }
}

# --- main ----------------------------------------------------------------------

$dest = Join-Segments (Resolve-DataRoot $DataConfig) @("leaflet")
Write-Host "leaflet destination: $dest"
Write-Host "scope: $(Format-Scope)"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

# The index names each era's bundle; without one, try the conventional name.
$index = Get-CdnJson "leaflet/index.json"

foreach ($scopeEra in $DownloadScope.Keys) {
    if ($Era -and $scopeEra -ne $Era) { continue }

    $zip = "$scopeEra-tiles.zip"
    if ($index) {
        $published = @($index.eras | Where-Object { $_.era -eq $scopeEra }) | Select-Object -First 1
        $zip = if ($published) { $published.zip } else { $null }
    }

    $bundle = if ($zip) { Read-RemoteZipDirectory (Get-CdnUrl $zip) } else { $null }
    if (-not $bundle) {
        Write-Host "  ${scopeEra}: no tiles published on the CDN yet."
        continue
    }

    $files = @($bundle.Entries | Where-Object { -not $_.Name.EndsWith('/') })
    $byContinent = $files | Group-Object { ($_.Name -split '/')[1] }

    $outOfScope = @($byContinent | Where-Object { -not (Test-InScope $scopeEra $_.Name) } | ForEach-Object Name)
    if ($outOfScope) { Write-Host ("  {0}: skipping {1} (outside the scope)" -f $scopeEra, ($outOfScope -join ", ")) }

    foreach ($group in $byContinent) {
        $name = $group.Name
        if (-not (Test-InScope $scopeEra $name)) { continue }
        if ($Continent -and $name -ne $Continent) { continue }

        $wanted = @($group.Group)
        $targetDir = Join-Segments $dest @($scopeEra, $name)
        $have = 0
        if (Test-Path $targetDir) {
            $have = @(Get-ChildItem $targetDir -Filter *.webp -File -Recurse -ErrorAction SilentlyContinue).Count
        }
        if (-not $Force -and $have -ge $wanted.Count) {
            Write-Host ("  {0}/{1}: present ({2} tiles), skipping" -f $scopeEra, $name, $have)
            continue
        }

        # The continent's span: its first entry up to whatever follows its last.
        $first = $wanted[0].Offset
        $last = $wanted[-1]
        $afterLast = @($bundle.Entries | Where-Object { $_.Offset -gt $last.Offset } | Select-Object -First 1)
        $spanEnd = if ($afterLast) { $afterLast[0].Offset } else { $bundle.CentralOffset }

        $span = New-ScratchFile ("leaflet-{0}-{1}.part" -f $scopeEra, $name)
        Write-Host ("  {0}/{1}: downloading {2} tiles, {3:N1} MB ..." -f $scopeEra, $name, $wanted.Count, (($spanEnd - $first) / 1MB))
        try {
            Save-CdnFile (Get-CdnUrl $zip) $span ($spanEnd - $first) -From $first -To ($spanEnd - 1)

            # Replace the continent wholesale so no stale tile survives a refresh;
            # this discards locally generated tiles for it, which is what -Force is for.
            if (Test-Path $targetDir) { Remove-Item $targetDir -Recurse -Force }
            Write-Host "  extracting ..."
            # arcnames are <era>/<Continent>/*.webp, so extract at the leaflet root.
            Expand-ZipSpan $span $first $wanted $dest
        }
        finally {
            Remove-Item $span -Force -ErrorAction SilentlyContinue
        }
        Write-Host ("  -> {0} ({1} tiles)" -f $targetDir, $wanted.Count)
    }
}

Write-Host "Done."
