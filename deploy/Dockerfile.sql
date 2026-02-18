# SQL Server Express 2025 on Windows Server Core
# Restores Fiesta game databases from .bak files on first run
#
# Database backups are copied from deploy/server-files/Databases/ at build time.
# Run setup.ps1 first to copy server files into deploy/server-files/.

FROM mcr.microsoft.com/windows/servercore:ltsc2022

SHELL ["powershell", "-Command", "$ErrorActionPreference = 'Stop';"]

# Download and install SQL Server Express 2025 silently
RUN Invoke-WebRequest -Uri 'https://download.microsoft.com/download/7ab8f535-7eb8-4b16-82eb-eca0fa2d38f3/SQL2025-SSEI-Expr.exe' \
      -OutFile C:/sql-express-setup.exe ; \
    Start-Process -FilePath C:/sql-express-setup.exe \
      -ArgumentList '/ACTION=Download', '/MEDIAPATH=C:/sql-media', '/MEDIATYPE=Core', '/QUIET' \
      -Wait ; \
    Start-Process -FilePath C:/sql-media/SQLEXPR_x64_ENU.exe \
      -ArgumentList '/Q', '/ACTION=Install', '/FEATURES=SQLEngine', \
        '/INSTANCENAME=SQLEXPRESS', '/SQLSYSADMINACCOUNTS="BUILTIN\Administrators"', \
        '/SECURITYMODE=SQL', '/SAPWD=V63WsdafLJT9NDAn', \
        '/TCPENABLED=1', '/IACCEPTSQLSERVERLICENSETERMS' \
      -Wait ; \
    Remove-Item -Recurse -Force C:/sql-express-setup.exe, C:/sql-media

# Pin SQL Express to port 1433 (default instance port)
# Express uses a dynamic port by default, which requires SQL Browser for discovery.
# Find the instance registry path dynamically (version number varies: MSSQL16, MSSQL17, etc.)
RUN $base = 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server'; \
    $instance = (Get-ChildItem $base | Where-Object { $_.PSChildName -match 'MSSQL\d+\.SQLEXPRESS' }).PSChildName; \
    if (-not $instance) { throw 'Could not find SQLEXPRESS registry key' }; \
    $regPath = ('{0}\{1}\MSSQLServer\SuperSocketNetLib\Tcp\IPAll' -f $base, $instance); \
    Write-Host "Setting TCP port 1433 at: $regPath"; \
    Set-ItemProperty -Path $regPath -Name TcpPort -Value '1433'; \
    Set-ItemProperty -Path $regPath -Name TcpDynamicPorts -Value ''

# Install sqlcmd for healthcheck
RUN Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/?linkid=2230791' \
      -OutFile C:/sqlcmd.msi ; \
    Start-Process msiexec.exe -ArgumentList '/i', 'C:/sqlcmd.msi', '/quiet', '/norestart' -Wait ; \
    Remove-Item C:/sqlcmd.msi

# Add sqlcmd to PATH
RUN $sqlcmdPath = 'C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn'; \
    [Environment]::SetEnvironmentVariable('PATH', $env:PATH + ';' + $sqlcmdPath, 'Machine')

# Copy database backup files into image
COPY server-files/Databases/ C:/backups/

COPY scripts/setup-sql.ps1 C:/setup-sql.ps1

EXPOSE 1433

CMD ["powershell", "-File", "C:/setup-sql.ps1"]
