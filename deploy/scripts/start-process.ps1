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

# --- Step 1: Copy per-process ServerInfo config ---

# Determine which config file to copy and where
$configSource = $null
$configDest = $null

switch ($processName) {
    "Account"      { $configSource = "C:\docker-config\DataServerInfo_Account.txt";    $configDest = "$processDir\DataServerInfo_Account.txt" }
    "AccountLog"   { $configSource = "C:\docker-config\DataServerInfo_AccountLog.txt";  $configDest = "$processDir\DataServerInfo_AccountLog.txt" }
    "Character"    { $configSource = "C:\docker-config\DataServerInfo_Character.txt";   $configDest = "$processDir\DataServerInfo_Character.txt" }
    "GameLog"      { $configSource = "C:\docker-config\DataServerInfo_GameLog.txt";     $configDest = "$processDir\DataServerInfo_GameLog.txt" }
    "Login"        { $configSource = "C:\docker-config\LoginServerInfo.txt";            $configDest = "$processDir\LoginServerInfo.txt" }
    "WorldManager" { $configSource = "C:\docker-config\WMServerInfo.txt";               $configDest = "$processDir\WMServerInfo.txt" }
    default {
        # Zone processes: Zone00, Zone01, etc.
        if ($processName -match '^Zone(\d+)$') {
            $zoneNumber = $env:ZONE_NUMBER
            if (-not $zoneNumber) {
                Write-Error "ZONE_NUMBER environment variable must be set for zone processes."
                exit 1
            }

            # Read template and substitute zone number
            $template = Get-Content "C:\docker-config\ZoneServerInfo.txt" -Raw
            $zoneConfig = $template -replace '\{\{ZONE_NUMBER\}\}', $zoneNumber

            # Zones expect config in a ZoneServerInfo subdirectory
            $zoneConfigDir = "$processDir\ZoneServerInfo"
            if (-not (Test-Path $zoneConfigDir)) {
                New-Item -ItemType Directory -Path $zoneConfigDir -Force | Out-Null
            }
            $configDest = "$zoneConfigDir\ZoneServerInfo.txt"
            Set-Content -Path $configDest -Value $zoneConfig -NoNewline
            Write-Host "Zone $zoneNumber config written to $configDest"
        }
        else {
            Write-Warning "Unknown process: $processName — no per-process ServerInfo to copy."
        }
    }
}

if ($configSource -and $configDest) {
    Copy-Item -Force $configSource $configDest
    Write-Host "Copied $configSource -> $configDest"
}

# --- Step 2: Registry keys (from Fantasy.reg and GBO.reg) ---

Write-Host "Setting up registry keys..."
reg add "HKLM\SOFTWARE\Wow6432Node\Fantasy\Fighter" /v Bird /d Eagle /f | Out-Null
reg add "HKLM\SOFTWARE\Wow6432Node\Fantasy\Fighter" /v Insect /d Honet /f | Out-Null
reg add "HKLM\SOFTWARE\Wow6432Node\GBO" /v Desert /d 138127 /f | Out-Null
reg add "HKLM\SOFTWARE\Wow6432Node\GBO" /v Mountain /d 30324 /f | Out-Null
reg add "HKLM\SOFTWARE\Wow6432Node\GBO" /v Natural /d 126810443 /f | Out-Null
reg add "HKLM\SOFTWARE\Wow6432Node\GBO" /v Ocean /d 7241589632 /f | Out-Null
reg add "HKLM\SOFTWARE\Wow6432Node\GBO" /v Sabana /d 2554545953 /f | Out-Null

# --- Step 3: Wait for SQL Server ---

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

# --- Step 4: Register as Windows service (first run installs the service, then exits) ---

Write-Host "Registering $processName as a Windows service..."
$regProc = Start-Process -FilePath $exePath -WorkingDirectory $processDir -PassThru
$regProc.WaitForExit()
Write-Host "$processName registration exited with code $($regProc.ExitCode)."

# --- Step 5: Start the registered service by known name ---

# Derive service name from process name
if ($processName -match '^Zone(\d+)$') {
    $serviceName = "_Zone$($env:ZONE_NUMBER)"
}
else {
    $serviceName = "_$processName"
}

Write-Host "Starting service: $serviceName"

# Wait for the service to appear (registration may take a moment)
$maxWait = 15
for ($i = 0; $i -lt $maxWait; $i++) {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($service) { break }
    Start-Sleep -Seconds 1
}

if (-not $service) {
    Write-Error "Service '$serviceName' not found after registration. Available services:"
    Get-Service | Where-Object { $_.ServiceName -like "_*" } | Format-Table -Property ServiceName, DisplayName, Status
    exit 1
}

Start-Service -Name $serviceName
Write-Host "$serviceName service started successfully."

# --- Step 6: Tail all DebugMessage logs for container output ---
# Each process can produce multiple log files (Msg_*.txt) for different event types.
# We tail ALL of them concurrently, prefixed with the filename for identification.

$logDir = "$processDir\DebugMessage"
Write-Host "Watching for logs in $logDir..."

# Wait for the log directory and at least one log file to appear
$timeout = 60
$logFiles = @()
for ($i = 0; $i -lt $timeout; $i++) {
    if (Test-Path $logDir) {
        $logFiles = @(Get-ChildItem "$logDir\*.txt" -ErrorAction SilentlyContinue)
        if ($logFiles.Count -gt 0) { break }
    }
    Start-Sleep -Seconds 1
}

if ($logFiles.Count -eq 0) {
    Write-Host "No log files found in $logDir after ${timeout}s. Keeping container alive..."
    while ($true) { Start-Sleep -Seconds 60 }
}

Write-Host "Tailing $($logFiles.Count) log file(s): $($logFiles.Name -join ', ')"

# Start a background job per log file, each tailing with a filename prefix
$jobs = @()
foreach ($lf in $logFiles) {
    $jobs += Start-Job -ScriptBlock {
        param($path, $tag)
        Get-Content -Path $path -Wait | ForEach-Object { "[$tag] $_" }
    } -ArgumentList $lf.FullName, $lf.BaseName
}

# Also watch for NEW log files that appear after startup
$watcherJob = Start-Job -ScriptBlock {
    param($dir)
    $known = @{}
    while ($true) {
        $files = Get-ChildItem "$dir\*.txt" -ErrorAction SilentlyContinue
        foreach ($f in $files) {
            if (-not $known.ContainsKey($f.Name)) {
                $known[$f.Name] = $true
                Write-Output "NEW_LOG:$($f.FullName):$($f.BaseName)"
            }
        }
        Start-Sleep -Seconds 5
    }
} -ArgumentList $logDir

# Main loop: receive output from all tail jobs and the watcher
while ($true) {
    # Check for new log files from the watcher
    $watcherOutput = Receive-Job -Job $watcherJob -ErrorAction SilentlyContinue
    foreach ($line in $watcherOutput) {
        if ($line -match '^NEW_LOG:(.+):(.+)$') {
            $newPath = $Matches[1]
            $newTag = $Matches[2]
            Write-Host "New log file detected: $newTag"
            $jobs += Start-Job -ScriptBlock {
                param($path, $tag)
                Get-Content -Path $path -Wait | ForEach-Object { "[$tag] $_" }
            } -ArgumentList $newPath, $newTag
        }
    }

    # Receive and print output from all tail jobs
    foreach ($job in $jobs) {
        $output = Receive-Job -Job $job -ErrorAction SilentlyContinue
        foreach ($line in $output) {
            Write-Host $line
        }
    }

    Start-Sleep -Milliseconds 500
}
