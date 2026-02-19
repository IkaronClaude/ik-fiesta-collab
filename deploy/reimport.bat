@echo off
:: Re-import all server and client data into test-project, then rebuild.
:: WARNING: Wipes existing test-project data/ and build/ directories first.
:: NOTE: mimir.template.json is preserved — run init-template manually if you need to regenerate it.
cd /d "%~dp0..\test-project"
mimir reimport
