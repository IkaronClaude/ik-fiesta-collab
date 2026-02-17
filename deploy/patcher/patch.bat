@echo off
REM Mimir Client Patcher — downloads and applies incremental patches
REM Usage: patch.bat [client-dir]
REM   client-dir: Path to the Fiesta client folder (default: current directory)

set CLIENT_DIR=%~1
if "%CLIENT_DIR%"=="" set CLIENT_DIR=%CD%

powershell -ExecutionPolicy Bypass -File "%~dp0patch.ps1" -ClientDir "%CLIENT_DIR%"
pause
