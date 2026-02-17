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

Write-Host "Starting $processName ($processExe)..."
$proc = Start-Process -FilePath $exePath -WorkingDirectory $processDir -PassThru

# Keep container alive — exit when the process exits
Write-Host "$processName started (PID $($proc.Id)). Waiting for exit..."
$proc.WaitForExit()
$exitCode = $proc.ExitCode
Write-Host "$processName exited with code $exitCode."
exit $exitCode
