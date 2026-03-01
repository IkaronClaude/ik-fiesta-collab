@echo off
setlocal
:: ================================================================
:: mimir deploy secret <subcommand> [KEY] [VALUE]
:: Manages secrets for the current project.
::
::   set KEY VALUE   - store a secret (gitignored .mimir-deploy.secrets)
::                     and register the key name in .mimir-deploy.secret-keys
::                     (committable — lets team members know what secrets exist)
::   get KEY         - print the current value of a secret
::   list            - show all required secrets and whether each is set
::   check           - interactively prompt for any missing required secrets
::                     (run this after cloning a project to fill in secrets)
::
:: Files:
::   .mimir-deploy.secrets      KEY=VALUE pairs  (gitignored, never commit)
::   .mimir-deploy.secret-keys  KEY names only   (commit this)
::
:: NOTE: Passwords containing ! are not supported (cmd batch limitation).
::       Set them manually by editing .mimir-deploy.secrets.
:: ================================================================
if "%~1"=="" (
    echo ERROR: Project name required.
    echo   Run via: mimir deploy secret ^<subcommand^>
    exit /b 1
)

set "PROJECT=%~1"
if not defined MIMIR_PROJ_DIR set "MIMIR_PROJ_DIR=%~dp0..\%PROJECT%"

set "SUBCMD=%~2"
if "%SUBCMD%"=="" (
    echo Usage: mimir deploy secret ^<set^|get^|list^|check^> [KEY] [VALUE]
    echo.
    echo   set KEY VALUE   Store a secret ^(gitignored^)
    echo   get KEY         Print a secret value
    echo   list            Show all required secrets and their status
    echo   check           Prompt for any missing required secrets
    exit /b 1
)

set "SECRETS_FILE=%MIMIR_PROJ_DIR%\.mimir-deploy.secrets"
set "KEYS_FILE=%MIMIR_PROJ_DIR%\.mimir-deploy.secret-keys"

if /i "%SUBCMD%"=="set"   goto :do_set
if /i "%SUBCMD%"=="get"   goto :do_get
if /i "%SUBCMD%"=="list"  goto :do_list
if /i "%SUBCMD%"=="check" goto :do_check

echo ERROR: Unknown subcommand: %SUBCMD%
echo Usage: mimir deploy secret ^<set^|get^|list^|check^>
exit /b 1

:: ----------------------------------------------------------------
:do_set
set "CFG_KEY=%~3"
set "CFG_VAL=%~4"
if "%CFG_KEY%"=="" (
    echo Usage: mimir deploy secret set KEY VALUE
    echo Example: mimir deploy secret set SA_PASSWORD MyStrongPassword1
    exit /b 1
)
if "%CFG_VAL%"=="" (
    echo Usage: mimir deploy secret set KEY VALUE
    echo Example: mimir deploy secret set SA_PASSWORD MyStrongPassword1
    echo Note: use a space between KEY and VALUE, not =
    exit /b 1
)

:: Write value into secrets file (strip old entry first)
set "TMP_FILE=%TEMP%\mimir-secret-set-tmp.txt"
if exist "%SECRETS_FILE%" (
    findstr /v /b /c:"%CFG_KEY%=" "%SECRETS_FILE%" > "%TMP_FILE%" 2>nul
) else (
    type nul > "%TMP_FILE%"
)
(echo %CFG_KEY%=%CFG_VAL%)>>"%TMP_FILE%"
move /y "%TMP_FILE%" "%SECRETS_FILE%" > nul

:: Register key name in keys file (if not already listed)
set "KEY_FOUND=0"
if exist "%KEYS_FILE%" (
    for /f "usebackq" %%K in ("%KEYS_FILE%") do (
        if /i "%%K"=="%CFG_KEY%" set "KEY_FOUND=1"
    )
)
if "%KEY_FOUND%"=="0" (
    (echo %CFG_KEY%)>>"%KEYS_FILE%"
)

echo Set secret %CFG_KEY% for project %PROJECT%.
echo   Values file ^(gitignored^): %SECRETS_FILE%
echo   Keys file  ^(committable^):  %KEYS_FILE%
exit /b 0

:: ----------------------------------------------------------------
:do_get
set "CFG_KEY=%~3"
if "%CFG_KEY%"=="" (
    echo Usage: mimir deploy secret get KEY
    echo Example: mimir deploy secret get SA_PASSWORD
    exit /b 1
)
if not exist "%SECRETS_FILE%" (
    echo ^(not set^)
    exit /b 0
)
for /f "usebackq tokens=1* delims==" %%K in ("%SECRETS_FILE%") do (
    if /i "%%K"=="%CFG_KEY%" (
        echo(%%L
        exit /b 0
    )
)
echo ^(not set^)
exit /b 0

:: ----------------------------------------------------------------
:do_list
echo Secrets for project %PROJECT%:
echo   Keys file: %KEYS_FILE%
echo.
if not exist "%KEYS_FILE%" (
    echo   ^(no required secrets defined^)
    echo.
    echo   Define secrets with: mimir deploy secret set KEY VALUE
    exit /b 0
)
for /f "usebackq" %%K in ("%KEYS_FILE%") do (
    if exist "%SECRETS_FILE%" (
        findstr /b /c:"%%K=" "%SECRETS_FILE%" >nul 2>&1
        if errorlevel 1 (
            echo   %%K = ^(NOT SET^)
        ) else (
            echo   %%K = ^<set^>
        )
    ) else (
        echo   %%K = ^(NOT SET^)
    )
)
exit /b 0

:: ----------------------------------------------------------------
:do_check
:: Use delayed expansion so set /p works inside for loop
setlocal EnableDelayedExpansion
echo Checking required secrets for project %PROJECT%...
if not exist "%KEYS_FILE%" (
    echo   No required secrets defined.
    echo   Define secrets with: mimir deploy secret set KEY VALUE
    exit /b 0
)
set "ALL_SET=1"
for /f "usebackq" %%K in ("%KEYS_FILE%") do (
    set "KEY_MISSING=1"
    if exist "%SECRETS_FILE%" (
        findstr /b /c:"%%K=" "%SECRETS_FILE%" >nul 2>&1
        if not errorlevel 1 set "KEY_MISSING=0"
    )
    if "!KEY_MISSING!"=="1" (
        set "ALL_SET=0"
        set /p "INPUT_VAL=  Enter value for %%K: "
        set "TMP_FILE=%TEMP%\mimir-secret-check.txt"
        if exist "%SECRETS_FILE%" (
            findstr /v /b /c:"%%K=" "%SECRETS_FILE%" > "!TMP_FILE!" 2>nul
        ) else (
            type nul > "!TMP_FILE!"
        )
        (echo %%K=!INPUT_VAL!)>>"!TMP_FILE!"
        move /y "!TMP_FILE!" "%SECRETS_FILE%" > nul
        echo   Saved %%K.
    )
)
if "!ALL_SET!"=="1" echo   All required secrets are set.
exit /b 0
