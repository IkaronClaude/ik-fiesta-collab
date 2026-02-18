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

Write-Host "=== Starting $processName ($processExe) ==="
Write-Host "Process dir: $processDir"
Write-Host "Running as: $(whoami)"

# --- Step 1: Copy per-process ServerInfo config ---

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
        if ($processName -match '^Zone(\d+)$') {
            $zoneNumber = $env:ZONE_NUMBER
            if (-not $zoneNumber) {
                Write-Error "ZONE_NUMBER environment variable must be set for zone processes."
                exit 1
            }

            $template = Get-Content "C:\docker-config\ZoneServerInfo.txt" -Raw
            $zoneConfig = $template -replace '\{\{ZONE_NUMBER\}\}', $zoneNumber

            $zoneConfigDir = "$processDir\ZoneServerInfo"
            if (-not (Test-Path $zoneConfigDir)) {
                New-Item -ItemType Directory -Path $zoneConfigDir -Force | Out-Null
            }
            $configDest = "$zoneConfigDir\ZoneServerInfo.txt"
            Set-Content -Path $configDest -Value $zoneConfig -NoNewline
            Write-Host "Zone $zoneNumber config written to $configDest"
        }
        else {
            Write-Warning "Unknown process: $processName"
        }
    }
}

if ($configSource -and $configDest) {
    Copy-Item -Force $configSource $configDest
    Write-Host "Copied $configSource -> $configDest"
}

# --- Step 2: Resolve Docker hostnames to IPs in ServerInfo.txt ---
# Game exes use inet_addr() which only accepts IP addresses, not hostnames.
# Read from the baked-in docker-config copy (no contention from other containers),
# resolve hostnames, and write to a local per-container path.

# Write resolved ServerInfo to C:\server\ServerInfo\ (writable, outside the :ro 9Data mount).
# Per-process configs #include this path instead of ../9Data/ServerInfo/ServerInfo.txt.
$serverInfoDir = "C:\server\ServerInfo"
if (-not (Test-Path $serverInfoDir)) {
    New-Item -ItemType Directory -Path $serverInfoDir -Force | Out-Null
}

$serverInfoTemplate = "C:\docker-config\ServerInfo\ServerInfo.txt"
$serverInfoPath = "$serverInfoDir\ServerInfo.txt"
$hostnames = @('login', 'worldmanager', 'zone00', 'zone01', 'zone02', 'zone03', 'zone04',
               'account', 'accountlog', 'character', 'gamelog', 'sqlserver')

Write-Host "Reading template: $serverInfoTemplate"
if (-not (Test-Path $serverInfoTemplate)) {
    Write-Error "ServerInfo template not found at $serverInfoTemplate"
    Write-Host "Contents of C:\docker-config:"
    Get-ChildItem C:\docker-config -Recurse | ForEach-Object { Write-Host $_.FullName }
    exit 1
}

$content = Get-Content $serverInfoTemplate -Raw -ErrorAction Stop
Write-Host ('Template loaded: {0} chars' -f $content.Length)

Write-Host "Resolving Docker hostnames to IPs..."
foreach ($hostname in $hostnames) {
    try {
        $ip = [System.Net.Dns]::GetHostAddresses($hostname) |
              Where-Object { $_.AddressFamily -eq 'InterNetwork' } |
              Select-Object -First 1
        if ($ip) {
            $ipStr = $ip.IPAddressToString
            Write-Host ('  {0} -> {1}' -f $hostname, $ipStr)
            $content = $content -replace ('"{0}"' -f [regex]::Escape($hostname)), ('"{0}"' -f $ipStr)
        }
        else {
            Write-Warning "  $hostname - no IPv4 address found"
        }
    }
    catch {
        Write-Warning "  $hostname - DNS resolution failed: $_"
    }
}

Set-Content -Path $serverInfoPath -Value $content -NoNewline -ErrorAction Stop

if (Test-Path $serverInfoPath) {
    $written = (Get-Item $serverInfoPath).Length
    Write-Host ('ServerInfo.txt written to {0} ({1} bytes)' -f $serverInfoPath, $written)
}
else {
    Write-Error "FAILED: ServerInfo.txt was not written to $serverInfoPath"
    exit 1
}

# --- Step 3: Registry keys (from Fantasy.reg and GBO.reg) ---

Write-Host "Setting up registry keys..."
reg add "HKLM\SOFTWARE\Wow6432Node\Fantasy\Fighter" /v Bird /d Eagle /f | Out-Null
reg add "HKLM\SOFTWARE\Wow6432Node\Fantasy\Fighter" /v Insect /d Honet /f | Out-Null
reg add "HKLM\SOFTWARE\Wow6432Node\GBO" /v Desert /d 138127 /f | Out-Null
reg add "HKLM\SOFTWARE\Wow6432Node\GBO" /v Mountain /d 30324 /f | Out-Null
reg add "HKLM\SOFTWARE\Wow6432Node\GBO" /v Natural /d 126810443 /f | Out-Null
reg add "HKLM\SOFTWARE\Wow6432Node\GBO" /v Ocean /d 7241589632 /f | Out-Null
reg add "HKLM\SOFTWARE\Wow6432Node\GBO" /v Sabana /d 2554545953 /f | Out-Null

# --- Step 4: Wait for SQL Server ---

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

# --- Step 5: Register as Windows service ---

Write-Host "Registering $processName as a Windows service..."
Write-Host "Running: $exePath (working dir: $processDir)"

# Capture stdout/stderr from registration
$regProc = Start-Process -FilePath $exePath -WorkingDirectory $processDir `
    -RedirectStandardOutput "$processDir\reg-stdout.txt" `
    -RedirectStandardError "$processDir\reg-stderr.txt" `
    -PassThru
$regProc.WaitForExit()
Write-Host "$processName registration exited with code $($regProc.ExitCode)."

$regStdout = Get-Content "$processDir\reg-stdout.txt" -ErrorAction SilentlyContinue
$regStderr = Get-Content "$processDir\reg-stderr.txt" -ErrorAction SilentlyContinue
if ($regStdout) { Write-Host "Registration stdout: $regStdout" }
if ($regStderr) { Write-Host "Registration stderr: $regStderr" }

# Dump all services starting with underscore (the game server convention)
Write-Host "=== Registered services ==="
sc.exe query type= service state= all | Select-String -Pattern "SERVICE_NAME|DISPLAY_NAME|STATE" | ForEach-Object { Write-Host $_.Line.Trim() }
Write-Host "=== Game services (underscore prefix) ==="
Get-Service | Where-Object { $_.ServiceName -like '_*' } | Format-Table ServiceName, DisplayName, Status -AutoSize | Out-String | Write-Host

# --- Step 6: Start the registered service by known name ---

if ($processName -match '^Zone(\d+)$') {
    $serviceName = ('_Zone{0}' -f $env:ZONE_NUMBER)
}
else {
    $serviceName = ('_{0}' -f $processName)
}

Write-Host "Looking for service: $serviceName"

$maxWait = 15
for ($i = 0; $i -lt $maxWait; $i++) {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($service) { break }
    Write-Host "  Waiting for service to appear... ($i/$maxWait)"
    Start-Sleep -Seconds 1
}

if (-not $service) {
    Write-Error "Service '$serviceName' not found after registration."
    Write-Host "=== All services ==="
    Get-Service | Format-Table ServiceName, DisplayName, Status -AutoSize | Out-String | Write-Host
    Write-Host "Keeping container alive for debugging..."
    while ($true) { Start-Sleep -Seconds 60 }
}

Write-Host "Starting service: $($service.ServiceName) (status: $($service.Status))"
try {
    Start-Service -Name $serviceName -ErrorAction Stop
    Write-Host "$serviceName started successfully."
}
catch {
    Write-Error "Failed to start $serviceName : $_"
    # Check Windows event log for service failure details
    Get-EventLog -LogName System -Newest 20 -ErrorAction SilentlyContinue |
        Where-Object { $_.Source -like '*Service*' } |
        Format-Table TimeGenerated, Source, Message -AutoSize -Wrap | Out-String | Write-Host
    Write-Host "Keeping container alive for debugging..."
    while ($true) { Start-Sleep -Seconds 60 }
}

# --- Step 7: Tail all DebugMessage logs ---

$logDir = "$processDir\DebugMessage"
Write-Host "Watching for logs in $logDir..."

$timeout = 60
$logFiles = @()
for ($i = 0; $i -lt $timeout; $i++) {
    if (Test-Path $logDir) {
        $logFiles = @(Get-ChildItem ($logDir + '\*.txt') -ErrorAction SilentlyContinue)
        if ($logFiles.Count -gt 0) { break }
    }
    Start-Sleep -Seconds 1
}

if ($logFiles.Count -eq 0) {
    Write-Host "No log files found in $logDir after ${timeout}s. Keeping container alive..."
    while ($true) { Start-Sleep -Seconds 60 }
}

Write-Host ('Tailing {0} log file(s): {1}' -f $logFiles.Count, ($logFiles.Name -join ', '))

$jobs = @()
foreach ($lf in $logFiles) {
    $jobs += Start-Job -ScriptBlock {
        param($path, $tag)
        Get-Content -Path $path -Wait | ForEach-Object { '[{0}] {1}' -f $tag, $_ }
    } -ArgumentList $lf.FullName, $lf.BaseName
}

$watcherJob = Start-Job -ScriptBlock {
    param($dir)
    $known = @{}
    while ($true) {
        $files = Get-ChildItem ($dir + '\*.txt') -ErrorAction SilentlyContinue
        foreach ($f in $files) {
            if (-not $known.ContainsKey($f.Name)) {
                $known[$f.Name] = $true
                Write-Output ('NEW_LOG:{0}:{1}' -f $f.FullName, $f.BaseName)
            }
        }
        Start-Sleep -Seconds 5
    }
} -ArgumentList $logDir

while ($true) {
    $watcherOutput = Receive-Job -Job $watcherJob -ErrorAction SilentlyContinue
    foreach ($line in $watcherOutput) {
        if ($line -match '^NEW_LOG:(.+):(.+)$') {
            $newPath = $Matches[1]
            $newTag = $Matches[2]
            Write-Host ('New log file detected: {0}' -f $newTag)
            $jobs += Start-Job -ScriptBlock {
                param($path, $tag)
                Get-Content -Path $path -Wait | ForEach-Object { '[{0}] {1}' -f $tag, $_ }
            } -ArgumentList $newPath, $newTag
        }
    }

    foreach ($job in $jobs) {
        $output = Receive-Job -Job $job -ErrorAction SilentlyContinue
        foreach ($line in $output) {
            Write-Host $line
        }
    }

    Start-Sleep -Milliseconds 500
}
