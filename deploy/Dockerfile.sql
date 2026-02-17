# SQL Server Express 2022 on Windows Server Core
# Restores Fiesta game databases from .bak files on first run
#
# Database backups are copied from deploy/server-files/Databases/ at build time.

FROM mcr.microsoft.com/windows/servercore:ltsc2022

SHELL ["powershell", "-Command", "$ErrorActionPreference = 'Stop';"]

# Download and install SQL Server Express 2022 silently
RUN Invoke-WebRequest -Uri 'https://download.microsoft.com/download/5/1/4/5145fe04-4d30-4b85-b0d1-39571571ef55/SQL2022-SSEI-Expr.exe' \
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
