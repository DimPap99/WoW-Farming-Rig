#Requires -Version 5.1
<#
.SYNOPSIS
  Shared by download-navmesh.ps1 and download-leaflet.ps1: the download scope,
  the CDN client and where DataConfig puts the files. Dot-source it; it does
  nothing on its own.

.DESCRIPTION
  Runs on Windows PowerShell 5.1 and PowerShell 7 (Windows, Linux, macOS), so it
  builds every path with Join-Path and talks to the CDN through HttpClient - the
  one API whose Range header and streaming behave the same on both.
#>

$ErrorActionPreference = "Stop"

$CdnBase = "https://bot.tortoiseclothing.org"
# Cloudflare answers a default user agent with error 1010.
$CdnUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36"

# What the download scripts fetch: era -> continents ("*" = every continent the
# era publishes). WoW Classic reads the pre-Cataclysm world but only its two
# original continents; Outland (Expansion01) and Northrend sit in the same
# precata era for TBC/Wrath clients and are left out. Add an era or continent
# here to download it again - nothing else in the scripts names one.
$DownloadScope = [ordered]@{
    precata = @("Azeroth", "Kalimdor")   # WoW Classic (vanilla, SoM)
    forever = @("*")                      # WoW: Forever beta
}

function Get-ScopeContinents([string]$era) {
    if ($DownloadScope.Contains($era)) { return @($DownloadScope[$era]) }
    return @()
}

function Test-InScope([string]$era, [string]$continent) {
    $allowed = Get-ScopeContinents $era
    return ($allowed -contains "*") -or ($allowed -contains $continent)
}

function Assert-ScopeArguments([string]$era, [string]$continent) {
    $eras = @($DownloadScope.Keys)
    if ($era -and -not $DownloadScope.Contains($era)) {
        throw "Era '$era' is outside the download scope ($($eras -join ', ')). Edit `$DownloadScope in download-common.ps1 to add it."
    }
    if ($continent) {
        $inAny = @($eras | Where-Object { (-not $era -or $_ -eq $era) -and (Test-InScope $_ $continent) })
        if (-not $inAny) {
            throw "Continent '$continent' is outside the download scope. In scope: $(Format-Scope)."
        }
    }
}

function Format-Scope {
    $parts = foreach ($era in $DownloadScope.Keys) {
        $c = @($DownloadScope[$era])
        if ($c -contains "*") { "$era (all)" } else { "$era ($($c -join ', '))" }
    }
    return ($parts -join "; ")
}

function Join-Segments([string]$base, [string[]]$segments) {
    $path = $base
    foreach ($s in $segments) {
        if ($s) { $path = Join-Path $path $s }
    }
    return $path
}

# data_config.json -> Root, honouring a customised Root the way DataConfig does:
# relative to the folder the config sits in (the project the host runs from).
function Resolve-DataRoot([string]$dataConfig) {
    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

    $candidates = @()
    if ($dataConfig) { $candidates += $dataConfig }
    $candidates += (Join-Path (Get-Location) "data_config.json")
    $candidates += (Join-Path $repoRoot "data_config.json")

    $cfgFile = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if ($cfgFile) {
        $root = (Get-Content $cfgFile -Raw | ConvertFrom-Json).Root
        if ($root) {
            # A config written on Windows says "..\Json"; make it native here.
            $sep = [System.IO.Path]::DirectorySeparatorChar
            $root = $root -replace '[\\/]', $sep
            if ([System.IO.Path]::IsPathRooted($root)) {
                return $root
            }
            $cfgDir = Split-Path -Parent (Resolve-Path $cfgFile)
            $try = [System.IO.Path]::GetFullPath((Join-Path $cfgDir $root))
            if (Test-Path $try) { return $try }
        }
    }

    # The repo's Json folder - where DataConfig's default "../Json" points in dev.
    return (Join-Path $repoRoot "Json")
}

function New-ScratchFile([string]$name) {
    return (Join-Path ([System.IO.Path]::GetTempPath()) $name)
}

# --- CDN client -------------------------------------------------------------

Add-Type -AssemblyName System.Net.Http
# Windows PowerShell 5.1 may still default to TLS 1.0; harmless on 7.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$script:CdnClient = $null

function Get-CdnClient {
    if (-not $script:CdnClient) {
        $handler = New-Object System.Net.Http.HttpClientHandler
        $handler.AutomaticDecompression = [System.Net.DecompressionMethods]::GZip -bor [System.Net.DecompressionMethods]::Deflate
        $client = New-Object System.Net.Http.HttpClient($handler)
        $client.Timeout = [TimeSpan]::FromMinutes(30)
        [void]$client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $CdnUserAgent)
        $script:CdnClient = $client
    }
    return $script:CdnClient
}

function Get-CdnUrl([string]$path) {
    return "$CdnBase/$($path.TrimStart('/'))"
}

# Sends a GET; with -From/-To it is a range request and must come back 206.
function Invoke-CdnGet([string]$url, [Nullable[long]]$From = $null, [Nullable[long]]$To = $null, [switch]$NoCache) {
    $request = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, $url)
    if ($null -ne $From) {
        $request.Headers.Range = New-Object System.Net.Http.Headers.RangeHeaderValue($From, $To)
    }
    if ($NoCache) {
        $request.Headers.CacheControl = New-Object System.Net.Http.Headers.CacheControlHeaderValue
        $request.Headers.CacheControl.NoCache = $true
    }

    $response = (Get-CdnClient).SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) {
        $code = [int]$response.StatusCode
        $response.Dispose()
        throw "GET $url failed: HTTP $code"
    }
    if ($null -ne $From -and [int]$response.StatusCode -ne 206) {
        $response.Dispose()
        throw "GET $url ignored the byte range (HTTP $([int]$response.StatusCode)); the CDN must support range requests."
    }
    return $response
}

# Returns $null for a 404 so callers can tell "not published" from a failure.
function Get-CdnJson([string]$path) {
    $url = Get-CdnUrl $path
    try {
        $response = Invoke-CdnGet $url -NoCache
    }
    catch {
        if ($_.Exception.Message -match 'HTTP 404') { return $null }
        throw
    }
    try {
        return ($response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json)
    }
    finally {
        $response.Dispose()
    }
}

function Get-CdnBytes([string]$url, [long]$from, [long]$to) {
    $response = Invoke-CdnGet $url -From $from -To $to
    try {
        # The comma keeps it a byte[]: a bare return unrolls the array into the
        # pipeline and hands the caller a boxed object[], which every later
        # BitConverter call then converts back - seconds per call on a megabyte.
        return , $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    }
    finally {
        $response.Dispose()
    }
}

# Streams a URL (or one byte range of it) to a file, reporting every 10%.
function Save-CdnFile([string]$url, [string]$outFile, [long]$expectedBytes = 0,
    [Nullable[long]]$From = $null, [Nullable[long]]$To = $null) {
    $response = Invoke-CdnGet $url -From $From -To $To
    try {
        $total = $expectedBytes
        if ($response.Content.Headers.ContentLength) { $total = [long]$response.Content.Headers.ContentLength }

        $source = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $target = [System.IO.File]::Create($outFile)
        try {
            $buffer = New-Object byte[] (1MB)
            $done = 0L
            $nextReport = 10
            while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $target.Write($buffer, 0, $read)
                $done += $read
                if ($total -gt 0) {
                    $pct = [math]::Floor(100 * $done / $total)
                    if ($pct -ge $nextReport -and $pct -lt 100) {
                        Write-Host ("    {0,3}%  {1:N1} / {2:N1} MB" -f $pct, ($done / 1MB), ($total / 1MB))
                        $nextReport = $pct - ($pct % 10) + 10
                    }
                }
            }
        }
        finally {
            $target.Dispose()
            $source.Dispose()
        }
    }
    finally {
        $response.Dispose()
    }
}
