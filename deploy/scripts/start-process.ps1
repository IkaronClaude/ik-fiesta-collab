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

$content = [System.IO.File]::ReadAllText($serverInfoTemplate)
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

[System.IO.File]::WriteAllText($serverInfoPath, $content)

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

# SQL wait removed — docker-compose depends_on: service_healthy handles this.

# --- Step 4: Register and start GamigoZR (Zone processes only) ---
# GamigoZR is a core service required by all Zone processes. Must be running before Zone.exe starts.

if ($processName -match '^Zone(\d+)$') {
    $gamigoZRExe = 'C:\server\GamigoZR\GamigoZR.exe'
    if (Test-Path $gamigoZRExe) {
        Write-Host 'Registering GamigoZR service...'
        sc.exe create GamigoZR binPath= $gamigoZRExe start= demand | Write-Host

        Write-Host 'Starting GamigoZR...'
        try {
            Start-Service -Name GamigoZR -ErrorAction Stop
            Write-Host 'GamigoZR started.'
        }
        catch {
            Write-Warning ('Failed to start GamigoZR: {0}' -f $_)
        }
    }
    else {
        Write-Warning "GamigoZR.exe not found at $gamigoZRExe — Zone may crash without it."
    }
}

# --- Step 5: Register and start Windows service ---
# Don't run the exe directly — it calls StartServiceCtrlDispatcher() which blocks forever.
# Register the service with sc.exe create, then Start-Service starts it properly via SCM.

if ($processName -match '^Zone(\d+)$') {
    $serviceName = ('_Zone{0}' -f $env:ZONE_NUMBER)
}
else {
    $serviceName = ('_{0}' -f $processName)
}

Write-Host ('Registering service: {0} -> {1}' -f $serviceName, $exePath)
sc.exe create $serviceName binPath= $exePath start= demand | Write-Host

# --- Step 5: Start the registered service ---

Write-Host "Looking for service: $serviceName"

$maxWait = 15
for ($i = 0; $i -lt $maxWait; $i++) {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($service) { break }
    Write-Host "  Waiting for service to appear... ($i/$maxWait)"
    Start-Sleep -Seconds 1
}

if (-not $service) {
    Write-Warning "Service '$serviceName' not found after registration."
    Get-Service | Format-Table ServiceName, DisplayName, Status -AutoSize | Out-String | Write-Host
}
else {
    Write-Host "Starting service: $($service.ServiceName) (status: $($service.Status))"
    try {
        Start-Service -Name $serviceName -ErrorAction Stop
        Write-Host "$serviceName started successfully."
    }
    catch {
        Write-Warning ('Failed to start {0}: {1}' -f $serviceName, $_)
    }
}

# Always fall through to log tailing — even on failure, logs show what went wrong.

# --- Step 6: Tail all logs (DebugMessage/ + root-level Assert/ExitLog/Msg files) ---

$logDir = "$processDir\DebugMessage"
Write-Host "Watching for logs in $logDir and $processDir (root-level)..."

# Collect initial log files from both DebugMessage/ and process root.
# Root-level patterns: Assert*.txt, ExitLog*.txt, Msg_*.txt
$timeout = 60
$logFiles = @()
for ($i = 0; $i -lt $timeout; $i++) {
    $debugFiles = @()
    $rootFiles  = @()
    if (Test-Path $logDir) {
        $debugFiles = @(Get-ChildItem ($logDir + '\*.txt') -ErrorAction SilentlyContinue)
    }
    $rootFiles = @(Get-ChildItem $processDir -Filter 'Assert*.txt'        -ErrorAction SilentlyContinue)
    $rootFiles += @(Get-ChildItem $processDir -Filter 'ExitLog*.txt'       -ErrorAction SilentlyContinue)
    $rootFiles += @(Get-ChildItem $processDir -Filter 'Msg_*.txt'          -ErrorAction SilentlyContinue)
    # Zone-specific logs: Dbg.txt, MapLoad.txt, Message.txt, Size.txt (mostly noise),
    # "Zone.exe <date> CallStack.txt" (crash call stack — matches the .mdmp), 5ZoneServerDumpStack<date>.txt
    $rootFiles += @(Get-ChildItem $processDir -Filter 'Dbg.txt'            -ErrorAction SilentlyContinue)
    $rootFiles += @(Get-ChildItem $processDir -Filter 'MapLoad*.txt'       -ErrorAction SilentlyContinue)
    $rootFiles += @(Get-ChildItem $processDir -Filter 'Message*.txt'       -ErrorAction SilentlyContinue)
    $rootFiles += @(Get-ChildItem $processDir -Filter 'Size*.txt'          -ErrorAction SilentlyContinue)
    $rootFiles += @(Get-ChildItem $processDir -Filter '*CallStack.txt'    -ErrorAction SilentlyContinue)
    $rootFiles += @(Get-ChildItem $processDir -Filter '5ZoneServer*.txt'   -ErrorAction SilentlyContinue)
    $logFiles = $debugFiles + $rootFiles
    if ($logFiles.Count -gt 0) { break }
    Start-Sleep -Seconds 1
}

if ($logFiles.Count -eq 0) {
    Write-Host "No log files found after ${timeout}s. Keeping container alive..."
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

# Watch both DebugMessage/ and process root for newly appearing log files.
$watcherJob = Start-Job -ScriptBlock {
    param($processDir, $logDir)
    $known = @{}
    while ($true) {
        $files = @()
        if (Test-Path $logDir) {
            $files += @(Get-ChildItem ($logDir + '\*.txt') -ErrorAction SilentlyContinue)
        }
        $files += @(Get-ChildItem $processDir -Filter 'Assert*.txt'        -ErrorAction SilentlyContinue)
        $files += @(Get-ChildItem $processDir -Filter 'ExitLog*.txt'       -ErrorAction SilentlyContinue)
        $files += @(Get-ChildItem $processDir -Filter 'Msg_*.txt'          -ErrorAction SilentlyContinue)
        $files += @(Get-ChildItem $processDir -Filter 'Dbg.txt'            -ErrorAction SilentlyContinue)
        $files += @(Get-ChildItem $processDir -Filter 'MapLoad*.txt'       -ErrorAction SilentlyContinue)
        $files += @(Get-ChildItem $processDir -Filter 'Message*.txt'       -ErrorAction SilentlyContinue)
        $files += @(Get-ChildItem $processDir -Filter 'Size*.txt'          -ErrorAction SilentlyContinue)
        $files += @(Get-ChildItem $processDir -Filter '*CallStack.txt'    -ErrorAction SilentlyContinue)
        $files += @(Get-ChildItem $processDir -Filter '5ZoneServer*.txt'   -ErrorAction SilentlyContinue)
        foreach ($f in $files) {
            if (-not $known.ContainsKey($f.FullName)) {
                $known[$f.FullName] = $true
                Write-Output ('NEW_LOG:{0}:{1}' -f $f.FullName, $f.BaseName)
            }
        }
        Start-Sleep -Seconds 5
    }
} -ArgumentList $processDir, $logDir

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
