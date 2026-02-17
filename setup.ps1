# setup.ps1 — One-stop Mimir project setup
# Prompts for paths, links server files for Docker, runs the full pipeline.
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

# --- Link server files for Docker ---

$serverFilesLink = Join-Path "deploy" "server-files"
if (-not (Test-Path $serverFilesLink)) {
    Write-Host "Creating symlink: deploy/server-files -> $ServerPath"
    Write-Host "  (Docker needs this for server executables and database backups)"
    try {
        New-Item -ItemType SymbolicLink -Path $serverFilesLink -Target $ServerPath | Out-Null
        Write-Host "  Symlink created." -ForegroundColor Green
    } catch {
        Write-Host "  Symlink failed (may need admin rights). Creating junction instead..." -ForegroundColor Yellow
        cmd /c "mklink /J `"$serverFilesLink`" `"$ServerPath`""
        if ($LASTEXITCODE -ne 0) {
            Write-Host "  ERROR: Could not create symlink or junction." -ForegroundColor Red
            Write-Host "  Please run as Administrator, or manually create:"
            Write-Host "    mklink /D deploy\server-files $ServerPath"
            exit 1
        }
        Write-Host "  Junction created." -ForegroundColor Green
    }
} else {
    Write-Host "deploy/server-files already exists, skipping link creation."
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
