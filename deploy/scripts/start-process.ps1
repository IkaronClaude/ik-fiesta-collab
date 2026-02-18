# start-process.ps1 - Single game process entrypoint
# Runs one server process specified by PROCESS_NAME and PROCESS_EXE env vars.
# Used by all 11 game containers (same image, different config).

$processName = $env:PROCESS_NAME
$processExe = $env:PROCESS_EXE

if (-not $processName -or -not $processExe) {
    Write-Error "PROCESS_NAME and PROCESS_EXE environment variables must be set."
    exit 1
}

$processDir = "C:\server\$processName"
$exePath = "$processDir\$processExe"

if (-not (Test-Path $exePath)) {
    Write-Error "Executable not found: $exePath"
    exit 1
}

# Override ServerInfo.txt (9Data is volume-mounted, so copy Docker version over it)
Write-Host "Applying Docker ServerInfo.txt override..."
$serverInfoDir = "C:\server\9Data\ServerInfo"
if (-not (Test-Path $serverInfoDir)) {
    New-Item -ItemType Directory -Path $serverInfoDir -Force | Out-Null
}
Copy-Item -Force "C:\docker-config\ServerInfo.txt" "$serverInfoDir\ServerInfo.txt"

# Registry keys (from Fantasy.reg and GBO.reg)
Write-Host "Setting up registry keys..."
reg add "HKLM\SOFTWARE\Wow6432Node\Fantasy\Fighter" /v Bird /d Eagle /f | Out-Null
reg add "HKLM\SOFTWARE\Wow6432Node\Fantasy\Fighter" /v Insect /d Honet /f | Out-Null
reg add "HKLM\SOFTWARE\Wow6432Node\GBO" /v Desert /d 138127 /f | Out-Null
reg add "HKLM\SOFTWARE\Wow6432Node\GBO" /v Mountain /d 30324 /f | Out-Null
reg add "HKLM\SOFTWARE\Wow6432Node\GBO" /v Natural /d 126810443 /f | Out-Null
reg add "HKLM\SOFTWARE\Wow6432Node\GBO" /v Ocean /d 7241589632 /f | Out-Null
reg add "HKLM\SOFTWARE\Wow6432Node\GBO" /v Sabana /d 2554545953 /f | Out-Null

# Wait for SQL Server to be ready (DB bridge processes need this)
Write-Host "Waiting for SQL Server..."
$maxRetries = 30
for ($i = 0; $i -lt $maxRetries; $i++) {
    try {
        $conn = New-Object System.Data.Odbc.OdbcConnection(
            "DRIVER={ODBC Driver 17 for SQL Server};SERVER=sqlserver;UID=sa;PWD=V63WsdafLJT9NDAn")
        $conn.Open()
        $conn.Close()
        Write-Host "SQL Server connection verified."
        break
    }
    catch {
        if ($i -eq $maxRetries - 1) {
            Write-Host "WARNING: Could not verify SQL Server connection after $maxRetries attempts."
        }
        Start-Sleep -Seconds 2
    }
}

# Step 1: Register as Windows service (first run installs the service, then exits)
Write-Host "Registering $processName as a Windows service..."
$regProc = Start-Process -FilePath $exePath -WorkingDirectory $processDir -PassThru
$regProc.WaitForExit()
Write-Host "$processName registration exited with code $($regProc.ExitCode)."

# Step 2: Find and start the registered service
# The service name typically matches the exe name without extension
$serviceName = [System.IO.Path]::GetFileNameWithoutExtension($processExe)

# Try common service name patterns: exact exe name, or process directory name
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (-not $service) {
    $service = Get-Service -Name $processName -ErrorAction SilentlyContinue
}
if (-not $service) {
    # Search for any newly registered service containing the process name
    $service = Get-Service | Where-Object { $_.DisplayName -like "*$processName*" -or $_.ServiceName -like "*$processName*" } | Select-Object -First 1
}

if (-not $service) {
    Write-Error "Could not find registered service for $processName. Available services:"
    Get-Service | Where-Object { $_.ServiceName -notlike ".*" } | Format-Table -Property ServiceName, DisplayName, Status
    exit 1
}

Write-Host "Found service: $($service.ServiceName) ($($service.DisplayName)). Starting..."
Start-Service -Name $service.ServiceName
Write-Host "$processName service started."

# Step 3: Tail the log file to keep container alive and provide live output
$logFile = Get-ChildItem "$processDir\*.log", "$processDir\Log\*" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($logFile) {
    Write-Host "Tailing log: $($logFile.FullName)"
    Get-Content -Path $logFile.FullName -Wait
}
else {
    # Log file may not exist yet — wait for it to appear
    Write-Host "Waiting for log file in $processDir..."
    $timeout = 30
    for ($i = 0; $i -lt $timeout; $i++) {
        $logFile = Get-ChildItem "$processDir\*.log", "$processDir\Log\*" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($logFile) {
            Write-Host "Tailing log: $($logFile.FullName)"
            Get-Content -Path $logFile.FullName -Wait
            break
        }
        Start-Sleep -Seconds 1
    }
    if (-not $logFile) {
        Write-Host "No log file found. Keeping container alive..."
        while ($true) { Start-Sleep -Seconds 60 }
    }
}
