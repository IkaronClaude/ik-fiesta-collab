# Fiesta Server — Docker Deployment

Local Fiesta Online server running in Docker for end-to-end testing of Mimir build output.

## Architecture

12 Windows containers on a shared Docker Compose network:

- **sqlserver** — SQL Server Express 2025 with 7 game databases restored from `.bak` files
- **account, accountlog, character, gamelog** — DB bridge processes
- **login** — Login server (client entry point, port 9010)
- **worldmanager** — World manager
- **zone00–zone04** — Zone servers (5 zones)
- **patchserver** — nginx serving `patches/` on port 8080 (started via `--profile patch`)

All game containers use the same Docker image. Each runs a single process specified by environment variables.

## Prerequisites

1. **Docker Desktop** with **Windows containers** enabled (requires restart to switch from Linux containers)
2. **Server binaries** in `deploy/server-files/` (symlink or copy)
3. **GamigoZR** in `deploy/server-files/GamigoZR/` (required by Zone.exe)
4. **Database .bak files** in `deploy/server-files/Databases/`
5. **`set DOCKER_BUILDKIT=0`** before any `docker compose build` — BuildKit is not supported for Windows containers

```bat
:: Symlink server files (recommended — no disk space wasted)
mklink /D deploy\server-files Z:\Server

:: Or copy GamigoZR specifically if it lives elsewhere
xcopy /E /I Z:\Server\GamigoZR deploy\server-files\GamigoZR
```

## First-Time Setup

```bat
cd deploy

:: 1. Build images (SQL + game server)
set DOCKER_BUILDKIT=0
rebuild-sql.bat        :: builds SQL image, starts SQL, restores .bak databases
rebuild-game.bat       :: builds game server image, starts all game containers
```

Wait for SQL to become healthy before starting game containers. The `rebuild-sql.bat` script blocks until the healthcheck passes.

## Scripts

| Script | What it does |
|--------|-------------|
| `start.bat` | Start all containers (no rebuild) |
| `stop.bat` | Stop all containers |
| `update.bat` | **Iterative dev cycle**: `mimir build --all` → `mimir pack patches` → snapshot → restart game servers. No SQL touch, no Docker rebuild. Use this for day-to-day data changes. |
| `deploy.bat` | **Full cycle**: stop all → `mimir build --all` → `mimir pack patches` → snapshot → start all. Use for first-time deploys or after config changes. |
| `restart-game.bat` | Snapshot only → restart game containers. Use after a manual `mimir build` if you skipped pack. |
| `reimport.bat` | Full reimport from source (slow — wipes data/, rebuilds, reseeds pack baseline) |
| `rebuild-game.bat` | Rebuild game server Docker image + start (needed after server binary/script changes) |
| `rebuild-sql.bat` | Wipe and restore SQL databases from `.bak` files (destructive) |
| `logs.bat` | Stream logs from all containers (`docker compose logs -f`) |

## Day-to-Day Workflow

After editing data in Mimir:

```bat
cd deploy
update.bat
```

Then patch your client and launch:

```bat
deploy\patcher\patch.bat C:\Fiesta\Client
```

`update.bat` runs `mimir build --all`, generates an incremental client patch, snapshots `build/server/` → `deployed/server/`, and restarts the game processes. SQL is not touched. Typical turnaround under a minute.

## Volume Mount Architecture

Game containers mount `test-project/deployed/server/9Data` (read-write, not `build/server/` directly). This allows `mimir build` to write to `build/` while containers are running — no file locking conflicts.

`deploy.bat` and `restart-game.bat` both run a `robocopy build/server → deployed/server` snapshot before restarting containers, so the game always sees the latest built data.

Zone.exe opens `9Data/SubAbStateClass.txt` with write access at startup — the mount must be read-write.

## Config

The `deploy/config/ServerInfo/ServerInfo.txt` override (volume-mounted over the server's default) changes:
- **ODBC driver**: `{SQL Server}` → `{ODBC Driver 17 for SQL Server}`
- **ODBC server**: `.\SQLEXPRESS` → `sqlserver` (Docker Compose hostname)
- **Process IPs**: `127.0.0.1` → Docker Compose service hostnames (`login`, `worldmanager`, `zone00`, etc.)

## Troubleshooting

**Container won't start**: Confirm Docker Desktop is in Windows container mode (whale icon → "Switch to Windows containers").

**`docker compose build` hangs or fails**: Run `set DOCKER_BUILDKIT=0` first. BuildKit is incompatible with Windows containers.

**SQL restore fails**: Check `docker logs fiesta-sql`. `.bak` files must be SQL Server 2025-compatible.

**Zone.exe crashes at startup**: GamigoZR must be present in `server-files/GamigoZR/` and started before Zone.exe. The startup script handles this automatically — check that GamigoZR was copied correctly.

**"ItemInfo iteminfoserver Order not match[N]"**: Column order mismatch between ItemInfo.shn and ItemInfoServer.shn after merge+split. Investigate TableSplitter column ordering for split-strategy columns.

**Can't connect from client**: Only port 9010 (Login) is exposed to the host. Configure the client to connect to `127.0.0.1:9010`.

**SQL password**: `V63WsdafLJT9NDAn`
Connect: `sqlcmd -S localhost\SQLEXPRESS -U sa -P V63WsdafLJT9NDAn -C`
