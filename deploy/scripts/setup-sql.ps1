# setup-sql.ps1 - SQL Server Express entrypoint
# Starts SQL Server, restores game databases from .bak files on first run,
# then keeps the process running.

$sqlInstance = ".\SQLEXPRESS"
$saPassword = $env:SA_PASSWORD
if (-not $saPassword) {
    Write-Host "ERROR: SA_PASSWORD is not set." -ForegroundColor Red
    Write-Host "  Run: mimir deploy set SA_PASSWORD YourStrongPassword1" -ForegroundColor Yellow
    exit 1
}
# Password baked into the Docker image at install time (Dockerfile.sql /SAPWD=...).
# If the user changes SA_PASSWORD via deploy config, we detect the mismatch and
# ALTER LOGIN on startup so rebuild-sql is never needed just for a password change.
$installPassword = 'V63WsdafLJT9NDAn'

$backupDir = "C:\backups"
$dataDir = "C:\sql-data"

# Ensure data directory exists
if (-not (Test-Path $dataDir)) {
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
}

# Start SQL Server service
Write-Host "Starting SQL Server Express..."
Start-Service MSSQL`$SQLEXPRESS
Start-Sleep -Seconds 5

# Wait for SQL to be ready -- try the configured password first, fall back to install password
Write-Host "Waiting for SQL Server to accept connections..."
$maxRetries = 30
$connectPassword = $saPassword
$ready = $false
for ($i = 0; $i -lt $maxRetries; $i++) {
    $result = sqlcmd -S $sqlInstance -U sa -P $connectPassword -C -Q "SELECT 1" 2>&1
    if ($LASTEXITCODE -eq 0) {
        $ready = $true
        break
    }
    # On first failure, also try the install password in case it hasn't been changed yet
    if ($i -eq 2 -and $connectPassword -ne $installPassword) {
        $result2 = sqlcmd -S $sqlInstance -U sa -P $installPassword -C -Q "SELECT 1" 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Connected with install password -- will update to configured SA_PASSWORD."
            $connectPassword = $installPassword
            $ready = $true
            break
        }
    }
    Start-Sleep -Seconds 2
}

if (-not $ready) {
    Write-Host "ERROR: SQL Server did not become ready after $maxRetries retries." -ForegroundColor Red
    exit 1
}

Write-Host "SQL Server is ready."

# If we connected with the install password, change it to the configured one
if ($connectPassword -ne $saPassword) {
    Write-Host "Changing sa password to match SA_PASSWORD from deploy config..."
    sqlcmd -S $sqlInstance -U sa -P $connectPassword -C -Q "ALTER LOGIN sa WITH PASSWORD = '$saPassword'"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Failed to change sa password." -ForegroundColor Red
        exit 1
    }
    Write-Host "sa password updated."
}

# Enable remote access
Write-Host "Enabling remote access..."
sqlcmd -S $sqlInstance -U sa -P $saPassword -C -Q @"
EXEC sp_configure 'remote access', 1;
RECONFIGURE;
"@

# Restore databases from .bak files (only if not already restored)
$databases = @("Account", "AccountLog", "OperatorTool", "Options", "StatisticsData", "World00_Character", "World00_GameLog")

foreach ($db in $databases) {
    $bakFile = Join-Path $backupDir "$db.bak"
    if (-not (Test-Path $bakFile)) {
        Write-Host "WARNING: Backup file not found: $bakFile"
        continue
    }

    # Check if database already exists (persisted via volume).
    # Use a COUNT query and match the numeric result to avoid false matches on error text.
    $dbCount = sqlcmd -S $sqlInstance -U sa -P $saPassword -C -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name = N'$db'" -h -1 -W 2>&1 |
               Where-Object { $_ -match '^\s*\d+\s*$' } | Select-Object -First 1
    if ($dbCount -and $dbCount.Trim() -eq '1') {
        Write-Host "Database '$db' already exists, skipping restore."
        continue
    }

    Write-Host "Restoring database '$db'..."

    # Get logical file names from backup
    $fileList = sqlcmd -S $sqlInstance -U sa -P $saPassword -C -Q "RESTORE FILELISTONLY FROM DISK = '$bakFile'" -s "|" -W -h -1 2>&1

    $moveClause = ""
    $dataFileIdx = 0
    $logFileIdx = 0
    foreach ($line in $fileList) {
        $parts = $line -split '\|'
        if ($parts.Count -ge 3) {
            $logicalName = $parts[0].Trim()
            $type = $parts[2].Trim()
            if ($type -eq 'D') {
                $suffix = if ($dataFileIdx -eq 0) { '' } else { "_$dataFileIdx" }
                $moveClause += "MOVE '$logicalName' TO '$dataDir\${db}${suffix}.mdf', "
                $dataFileIdx++
            }
            elseif ($type -eq 'L') {
                $suffix = if ($logFileIdx -eq 0) { '' } else { "_$logFileIdx" }
                $moveClause += "MOVE '$logicalName' TO '$dataDir\${db}${suffix}_log.ldf', "
                $logFileIdx++
            }
        }
    }

    # Use REPLACE if .mdf files exist on disk but the database is not registered
    # (orphaned files from a previous container that was stopped uncleanly).
    # Safe here because we already confirmed the database does not exist in sys.databases.
    $replaceClause = ''
    if (Get-ChildItem "$dataDir\$db*.mdf" -ErrorAction SilentlyContinue) {
        Write-Host "  Orphaned data files found for $db -- using WITH REPLACE."
        $replaceClause = ', REPLACE'
    }

    if ($moveClause -eq "") {
        Write-Host "WARNING: Could not parse file list for $db, attempting restore without MOVE..."
        $restoreResult = sqlcmd -S $sqlInstance -U sa -P $saPassword -C -Q "RESTORE DATABASE [$db] FROM DISK = '$bakFile'" 2>&1
    }
    else {
        $moveClause = $moveClause.TrimEnd(", ")
        $sql = "RESTORE DATABASE [$db] FROM DISK = '$bakFile' WITH $moveClause$replaceClause"
        Write-Host "SQL: $sql"
        $restoreResult = sqlcmd -S $sqlInstance -U sa -P $saPassword -C -Q $sql 2>&1
    }

    $restoreStr = $restoreResult | Out-String
    if ($restoreStr -match 'Msg \d+, Level 1[6-9]') {
        Write-Host "ERROR: Failed to restore '$db':"
        Write-Host $restoreStr
    }
    else {
        Write-Host "Database '$db' restored successfully."
    }
}

Write-Host "SQL Server setup complete. All databases ready."

# Keep container alive by tailing the SQL error log
# SQL Server 2025 = MSSQL17, 2022 = MSSQL16 - find whichever exists
$logFile = Get-ChildItem "C:\Program Files\Microsoft SQL Server\MSSQL*.SQLEXPRESS\MSSQL\Log\ERRORLOG" -ErrorAction SilentlyContinue | Select-Object -First 1
$errorLog = if ($logFile) { $logFile.FullName } else { $null }
if ($errorLog -and (Test-Path $errorLog)) {
    Get-Content -Path $errorLog -Wait
}
else {
    # Fallback: just keep alive
    while ($true) { Start-Sleep -Seconds 60 }
}
