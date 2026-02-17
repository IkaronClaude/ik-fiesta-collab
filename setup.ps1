# setup.ps1 — One-stop Mimir project setup
# Prompts for paths, copies server files for Docker, runs the full pipeline.
# After this: `docker compose -f deploy/docker-compose.yml up -d` just works.

param(
    [string]$ServerPath,
    [string]$ClientPath,
    [string]$ProjectDir = "test-project"
)

$ErrorActionPreference = 'Stop'

Write-Host "=== Mimir Project Setup ===" -ForegroundColor Cyan
Write-Host ""

# --- Prompt for paths if not provided ---

if (-not $ServerPath) {
    $ServerPath = Read-Host "Server files path (folder containing 9Data/, Account/, Login/, etc.)"
}
if (-not (Test-Path $ServerPath)) {
    Write-Host "ERROR: Server path does not exist: $ServerPath" -ForegroundColor Red
    exit 1
}
$ServerPath = (Resolve-Path $ServerPath).Path
Write-Host "  Server: $ServerPath" -ForegroundColor Green

if (-not $ClientPath) {
    $ClientPath = Read-Host "Client files path (folder containing ressystem/ with SHN files, or leave blank to skip)"
}
$hasClient = $ClientPath -and (Test-Path $ClientPath)
if ($ClientPath -and -not $hasClient) {
    Write-Host "  WARNING: Client path does not exist, skipping: $ClientPath" -ForegroundColor Yellow
}
if ($hasClient) {
    $ClientPath = (Resolve-Path $ClientPath).Path
    Write-Host "  Client: $ClientPath" -ForegroundColor Green
}

Write-Host ""

# --- Copy server files for Docker ---

$serverFilesDir = Join-Path "deploy" "server-files"
$processDirs = @("Account", "AccountLog", "Character", "GameLog", "Login", "WorldManager", "Zone00", "Zone01", "Zone02", "Zone03", "Zone04")

if (-not (Test-Path $serverFilesDir)) {
    Write-Host "Copying server files to deploy/server-files/ ..."
    Write-Host "  (Docker needs these for server executables and database backups)"
    New-Item -ItemType Directory -Path $serverFilesDir -Force | Out-Null

    # Copy process directories (executables + DLLs)
    foreach ($dir in $processDirs) {
        $src = Join-Path $ServerPath $dir
        $dst = Join-Path $serverFilesDir $dir
        if (Test-Path $src) {
            Write-Host "  Copying $dir/ ..."
            Copy-Item -Path $src -Destination $dst -Recurse -Force
        } else {
            Write-Host "  WARNING: $dir/ not found in server path, skipping" -ForegroundColor Yellow
        }
    }

    # Copy database backups
    $dbSrc = Join-Path $ServerPath "Databases"
    $dbDst = Join-Path $serverFilesDir "Databases"
    if (Test-Path $dbSrc) {
        Write-Host "  Copying Databases/ ..."
        Copy-Item -Path $dbSrc -Destination $dbDst -Recurse -Force
    } else {
        Write-Host "  WARNING: Databases/ not found in server path" -ForegroundColor Yellow
    }

    Write-Host "  Server files copied." -ForegroundColor Green
} else {
    Write-Host "deploy/server-files/ already exists, skipping copy."
}

Write-Host ""

# --- Create project directory ---

if (-not (Test-Path $ProjectDir)) {
    New-Item -ItemType Directory -Path $ProjectDir -Force | Out-Null
    Write-Host "Created project directory: $ProjectDir"
}

# --- Write mimir.json ---

$mimirJson = @{
    version = 2
    environments = @{
        server = @{ importPath = $ServerPath.Replace('\', '/') }
    }
    tables = @{}
}

if ($hasClient) {
    $mimirJson.environments.client = @{ importPath = $ClientPath.Replace('\', '/') }
}

$jsonText = $mimirJson | ConvertTo-Json -Depth 4
Set-Content -Path (Join-Path $ProjectDir "mimir.json") -Value $jsonText
Write-Host "Wrote mimir.json" -ForegroundColor Green

# --- Run Mimir pipeline ---

$cli = "dotnet run --project src/Mimir.Cli --"

Write-Host ""
Write-Host "=== Step 1/3: Init Template ===" -ForegroundColor Cyan
Invoke-Expression "$cli init-template $ProjectDir"
if ($LASTEXITCODE -ne 0) { Write-Host "init-template failed" -ForegroundColor Red; exit 1 }

# Set conflict strategy to split for all merge actions (handles server/client conflicts)
if ($hasClient) {
    Write-Host ""
    Write-Host "Setting conflict strategy to 'split' for merged tables..."
    Invoke-Expression "$cli edit-template $ProjectDir --conflict-strategy split"
}

Write-Host ""
Write-Host "=== Step 2/3: Import ===" -ForegroundColor Cyan
Invoke-Expression "$cli import $ProjectDir"
if ($LASTEXITCODE -ne 0) { Write-Host "import failed" -ForegroundColor Red; exit 1 }

Write-Host ""
Write-Host "=== Step 3/3: Build ===" -ForegroundColor Cyan
Invoke-Expression "$cli build $ProjectDir $ProjectDir/build --all"
if ($LASTEXITCODE -ne 0) { Write-Host "build failed" -ForegroundColor Red; exit 1 }

# --- Done ---

Write-Host ""
Write-Host "=== Setup Complete ===" -ForegroundColor Green
Write-Host ""
Write-Host "Project:      $ProjectDir/"
Write-Host "Server build: $ProjectDir/build/server/9Data/"
if ($hasClient) {
    Write-Host "Client build: $ProjectDir/build/client/"
}
Write-Host ""
Write-Host "Start Docker (first time):"
Write-Host "  set DOCKER_BUILDKIT=0"
Write-Host "  docker compose -f deploy/docker-compose.yml build"
Write-Host "  docker compose -f deploy/docker-compose.yml up -d"
Write-Host ""
Write-Host "Rebuild after edits:"
Write-Host "  dotnet run --project src/Mimir.Cli -- build $ProjectDir $ProjectDir/build --all"
Write-Host "  docker compose -f deploy/docker-compose.yml restart"
