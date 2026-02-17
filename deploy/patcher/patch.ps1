param(
    [Parameter(Mandatory=$true)]
    [string]$ClientDir
)

$ErrorActionPreference = 'Stop'

# --- Read config ---
$configPath = Join-Path $PSScriptRoot 'patcher.config'
if (-not (Test-Path $configPath)) {
    Write-Host "ERROR: patcher.config not found at $configPath" -ForegroundColor Red
    exit 1
}

$patchUrl = $null
foreach ($line in Get-Content $configPath) {
    if ($line -match '^PatchUrl=(.+)$') {
        $patchUrl = $Matches[1].Trim()
    }
}

if (-not $patchUrl) {
    Write-Host "ERROR: PatchUrl not found in patcher.config" -ForegroundColor Red
    exit 1
}

# Ensure trailing slash
if (-not $patchUrl.EndsWith('/')) { $patchUrl += '/' }

Write-Host "Mimir Client Patcher"
Write-Host "  Patch server: $patchUrl"
Write-Host "  Client dir:   $ClientDir"
Write-Host ""

# --- Ensure client dir exists ---
if (-not (Test-Path $ClientDir)) {
    New-Item -ItemType Directory -Path $ClientDir -Force | Out-Null
}

# --- Read local version ---
$versionFile = Join-Path $ClientDir '.mimir-version'
$currentVersion = 0
if (Test-Path $versionFile) {
    $currentVersion = [int](Get-Content $versionFile -Raw).Trim()
}
Write-Host "Current version: $currentVersion"

# --- Download patch index ---
$indexUrl = "${patchUrl}patch-index.json"
Write-Host "Fetching patch index from $indexUrl ..."

try {
    $indexJson = (Invoke-WebRequest -Uri $indexUrl -UseBasicParsing).Content
} catch {
    Write-Host "ERROR: Failed to download patch index: $_" -ForegroundColor Red
    exit 1
}

$index = $indexJson | ConvertFrom-Json
$latestVersion = $index.latestVersion
Write-Host "Latest version:  $latestVersion"

if ($currentVersion -ge $latestVersion) {
    Write-Host ""
    Write-Host "Client is up to date!" -ForegroundColor Green
    exit 0
}

# --- Apply patches ---
$patchesToApply = $index.patches | Where-Object { $_.version -gt $currentVersion } | Sort-Object version

Write-Host ""
Write-Host "Applying $($patchesToApply.Count) patch(es)..."
Write-Host ""

foreach ($patch in $patchesToApply) {
    $version = $patch.version
    $url = $patch.url
    $expectedHash = $patch.sha256
    $fileCount = $patch.fileCount
    $sizeKB = [math]::Round($patch.sizeBytes / 1024, 1)

    # Resolve relative URLs against base
    if (-not ($url -match '^https?://') -and -not ($url -match '^file:///')) {
        $url = "${patchUrl}${url}"
    }

    Write-Host "Patch v${version}: $fileCount files, ${sizeKB} KB"
    Write-Host "  Downloading $url ..."

    $tempZip = Join-Path $env:TEMP "mimir-patch-v${version}.zip"

    try {
        Invoke-WebRequest -Uri $url -OutFile $tempZip -UseBasicParsing
    } catch {
        Write-Host "  ERROR: Download failed: $_" -ForegroundColor Red
        exit 1
    }

    # Verify SHA-256
    $actualHash = (Get-FileHash -Path $tempZip -Algorithm SHA256).Hash.ToLower()
    if ($actualHash -ne $expectedHash) {
        Write-Host "  ERROR: SHA-256 mismatch!" -ForegroundColor Red
        Write-Host "    Expected: $expectedHash"
        Write-Host "    Actual:   $actualHash"
        Remove-Item $tempZip -Force
        exit 1
    }
    Write-Host "  Checksum verified."

    # Extract over client directory
    Write-Host "  Extracting..."
    Expand-Archive -Path $tempZip -DestinationPath $ClientDir -Force

    # Update version
    Set-Content -Path $versionFile -Value $version
    Write-Host "  Applied v${version} successfully." -ForegroundColor Green

    # Cleanup
    Remove-Item $tempZip -Force
}

Write-Host ""
Write-Host "Patching complete! Client is now at version $latestVersion." -ForegroundColor Green
