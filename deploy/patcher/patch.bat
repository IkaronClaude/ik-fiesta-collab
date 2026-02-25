@echo off
setlocal
:: ================================================================
:: Mimir Client Patcher  --  double-click to update your client.
:: Edit patcher.config to set your patch server URL.
:: ================================================================
set "MIMIR_SELF=%~f0"
set "MIMIR_CLIENT=%~dp0"
if "%MIMIR_CLIENT:~-1%"=="\" set "MIMIR_CLIENT=%MIMIR_CLIENT:~0,-1%"

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$s=[IO.File]::ReadAllText($env:MIMIR_SELF);" ^
    "$p=$s.Substring($s.LastIndexOf('<#')+2);" ^
    "$p=$p.Substring(0,$p.IndexOf('#>'));" ^
    "Invoke-Expression $p"
if %errorlevel% neq 0 (
    echo.
    echo Patching failed. See the error above.
)
pause
exit /b

<#
$ErrorActionPreference = 'Stop'
$patcherDir = Split-Path -Parent $env:MIMIR_SELF
$configPath = Join-Path $patcherDir 'patcher.config'
if (-not (Test-Path $configPath)) {
    Write-Host "ERROR: patcher.config not found at $configPath" -ForegroundColor Red; exit 1
}
$patchUrl = $null
foreach ($line in Get-Content $configPath) {
    if ($line -match '^PatchUrl=(.+)$') { $patchUrl = $Matches[1].Trim() }
}
if (-not $patchUrl) {
    Write-Host "ERROR: PatchUrl not set in patcher.config" -ForegroundColor Red; exit 1
}
$patchUrl = $patchUrl.TrimEnd('/') + '/'
$clientDir = $env:MIMIR_CLIENT

Write-Host 'Mimir Client Patcher'
Write-Host "  Server: $patchUrl"
Write-Host "  Folder: $clientDir"
Write-Host ''

if (-not (Test-Path $clientDir)) { New-Item -ItemType Directory $clientDir -Force | Out-Null }
$versionFile = Join-Path $clientDir '.mimir-version'
$currentVersion = 0
if (Test-Path $versionFile) { $currentVersion = [int](Get-Content $versionFile -Raw).Trim() }
Write-Host "Current version: $currentVersion"

try   { $indexJson = (Invoke-WebRequest -Uri "${patchUrl}patch-index.json" -UseBasicParsing).Content }
catch { Write-Host "ERROR: Cannot reach patch server: $_" -ForegroundColor Red; exit 1 }
$index = $indexJson | ConvertFrom-Json
$latestVersion = [int]$index.latestVersion
Write-Host "Latest version:  $latestVersion"

$minVer = if ($null -ne $index.minIncrementalVersion) { [int]$index.minIncrementalVersion } else { 1 }
$master = $index.masterPatch

if (($null -ne $master) -and ($currentVersion -lt ($minVer - 1))) {
    Write-Host "Version $currentVersion is below minimum incremental v$minVer -- downloading full client..."
    $masterUrl = $master.url
    if ($masterUrl -notmatch '^https?://') { $masterUrl = "${patchUrl}${masterUrl}" }
    Write-Host "  $($master.fileCount) files  $([math]::Round($master.sizeBytes / 1MB, 1)) MB"
    $tmp = Join-Path $env:TEMP 'mimir-master.zip'
    try   { Invoke-WebRequest -Uri $masterUrl -OutFile $tmp -UseBasicParsing }
    catch { Write-Host "ERROR: Download failed: $_" -ForegroundColor Red; exit 1 }
    if ((Get-FileHash $tmp -Algorithm SHA256).Hash.ToLower() -ne $master.sha256) {
        Write-Host 'ERROR: File is corrupted (hash mismatch). Try again or contact support.' -ForegroundColor Red
        Remove-Item $tmp -Force; exit 1
    }
    Expand-Archive $tmp $clientDir -Force
    Set-Content $versionFile $master.version
    Remove-Item $tmp -Force
    Write-Host ''
    Write-Host "Done! Client is now at version $($master.version)." -ForegroundColor Green
    exit 0
}

if ($currentVersion -ge $latestVersion) {
    Write-Host ''
    Write-Host 'Your client is up to date!' -ForegroundColor Green
    exit 0
}

$patches = @($index.patches | Where-Object { $_.version -gt $currentVersion } | Sort-Object version)
Write-Host ''
Write-Host "Applying $($patches.Count) patch(es)..."
foreach ($patch in $patches) {
    $url = $patch.url
    if ($url -notmatch '^https?://') { $url = "${patchUrl}${url}" }
    Write-Host "  v$($patch.version): $($patch.fileCount) files  $([math]::Round($patch.sizeBytes / 1KB, 1)) KB"
    $tmp = Join-Path $env:TEMP "mimir-patch-$($patch.version).zip"
    try   { Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing }
    catch { Write-Host "  ERROR: Download failed: $_" -ForegroundColor Red; exit 1 }
    if ((Get-FileHash $tmp -Algorithm SHA256).Hash.ToLower() -ne $patch.sha256) {
        Write-Host '  ERROR: Patch is corrupted (hash mismatch). Try again or contact support.' -ForegroundColor Red
        Remove-Item $tmp -Force; exit 1
    }
    Expand-Archive $tmp $clientDir -Force
    Set-Content $versionFile $patch.version
    Remove-Item $tmp -Force
    Write-Host '  OK' -ForegroundColor Green
}
Write-Host ''
Write-Host "All done! Client is now at version $latestVersion." -ForegroundColor Green
#>
