# Fiesta Server - Docker Deployment

Local Fiesta Online server running in Docker for end-to-end testing of Mimir build output.

## Architecture

12 Windows containers on a shared Docker Compose network:

- **sqlserver** — SQL Server Express 2022 with 7 game databases restored from `.bak` files
- **account, accountlog, character, gamelog** — DB bridge processes
- **login** — Login server (client entry point, port 9010)
- **worldmanager** — World manager
- **zone00–zone04** — Zone servers (5 zones)

All game containers use the same Docker image. Each runs a single process specified by environment variables. Inter-process communication uses Docker Compose DNS hostnames.

## Prerequisites

1. **Docker Desktop** with **Windows containers** enabled (requires restart to switch from Linux containers)
2. **Server files** — symlink or copy your server directory into `deploy/server-files/`

## Setup

```powershell
# Option A: Symlink (recommended — no disk space wasted)
mklink /D deploy\server-files Z:\Server

# Option B: Copy
xcopy /E /I Z:\Server deploy\server-files
```

Expected layout:
```
deploy/server-files/
  Account/Account.exe
  AccountLog/AccountLog.exe
  Character/Character.exe
  GameLog/GameLog.exe
  Login/Login.exe
  WorldManager/WorldManager.exe
  Zone00/Zone.exe
  Zone01/Zone.exe
  Zone02/Zone.exe
  Zone03/Zone.exe
  Zone04/Zone.exe
  Databases/*.bak
```

## Usage

```powershell
# Build the Docker images (first time, or after server files change)
docker compose -f deploy/docker-compose.yml build

# Start everything (SQL starts first, then DB bridges, then game processes)
docker compose -f deploy/docker-compose.yml up -d

# Check logs for a specific service
docker logs fiesta-login
docker logs fiesta-sql

# Stop everything
docker compose -f deploy/docker-compose.yml down
```

## Rebuild Cycle

```powershell
# 1. Edit data in Mimir
dotnet run --project src/Mimir.Cli -- edit test-project "UPDATE ItemInfo SET AC = 100 WHERE ..."

# 2. Build server data
dotnet run --project src/Mimir.Cli -- build test-project test-project/build --all

# 3. Restart game containers (pick up new 9Data via volume mount)
docker compose -f deploy/docker-compose.yml restart account accountlog character gamelog login worldmanager zone00 zone01 zone02 zone03 zone04

# 4. Connect Fiesta client to 127.0.0.1:9010
```

## Config

The only config difference from bare-metal is in `deploy/config/ServerInfo.txt`:

- **ODBC driver**: `{SQL Server}` changed to `{ODBC Driver 17 for SQL Server}`
- **ODBC server**: `.\SQLEXPRESS` changed to `sqlserver` (Docker hostname)
- **Process IPs**: `127.0.0.1` changed to Docker service hostnames (`login`, `worldmanager`, `zone00`, etc.)

If the game binaries don't support DNS hostnames, switch to static IPs on a custom Docker network (see git history for the static IP version).

## Troubleshooting

**Container won't start**: Make sure Docker Desktop is in Windows container mode, not Linux.

**SQL restore fails**: Check `docker logs fiesta-sql`. The `.bak` files must be compatible with SQL Server Express 2022.

**Game process crashes immediately**: Check `docker logs fiesta-<process>`. Common causes: missing VC++ Redistributable, missing DLLs in the process directory.

**Processes can't find each other**: If the game binaries reject DNS hostnames, fall back to static IPs. Define a custom network in `docker-compose.yml` with `ipam` config and replace hostnames in `ServerInfo.txt` with IPs.

**Can't connect from client**: Only port 9010 (Login) is exposed to the host. Configure the client to connect to `127.0.0.1:9010`.
