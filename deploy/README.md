# Fiesta Server — Docker Deployment

Local Fiesta Online server running in Docker for end-to-end testing of Mimir build output.

## Architecture

12 Windows containers on a shared Docker Compose network:

- **sqlserver** — SQL Server Express 2025 with 7 game databases restored from `.bak` files
- **account, accountlog, character, gamelog** — DB bridge processes
- **login** — Login server (client entry point, port 9010)
- **worldmanager** — World manager
- **zone00–zone04** — Zone servers (5 zones)
- **patch-server** — nginx serving `patches/` on port 8080 (started via `--profile patch`)

All game containers use the same Docker image. Each runs a single process specified by environment variables. Container names and volume names are namespaced by project name (derived from the directory containing `mimir.json`) so two projects can run side by side without conflicts.

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

Run from inside your project directory (where `mimir.json` lives):

```bat
cd my-server

:: 1. Build SQL image, start SQL container, restore .bak databases
mimir deploy rebuild-sql

:: 2. Build game server image, start all game + patch containers
mimir deploy rebuild-game
```

SQL must be healthy before game containers start. `rebuild-sql` blocks until the healthcheck passes.

## Deploy Commands

All commands are run from inside the project directory. Mimir finds the project automatically.

| Command | What it does |
|---------|-------------|
| `mimir deploy start` | Start all containers (no rebuild) |
| `mimir deploy stop` | Stop all containers |
| `mimir deploy update` | **Iterative dev cycle**: `mimir build --all` → `mimir pack patches` → snapshot → restart game servers. No SQL touch, no Docker rebuild. Use this for day-to-day data changes. |
| `mimir deploy deploy` | **Full cycle**: stop all → `mimir build --all` → `mimir pack patches` → snapshot → start all. Use for first-time deploys or after config changes. |
| `mimir deploy restart-game` | Snapshot only → restart game containers. Use after a manual `mimir build` if you skipped pack. |
| `mimir deploy reimport` | Full reimport from source (slow — wipes data/, rebuilds, reseeds pack baseline) |
| `mimir deploy rebuild-game` | Rebuild game server Docker image + start (needed after server binary/script changes) |
| `mimir deploy rebuild-sql` | Wipe and restore SQL databases from `.bak` files (destructive) |
| `mimir deploy logs` | Stream logs from all game containers (`docker compose logs -f`) |

## Day-to-Day Workflow

After editing data in Mimir:

```bat
cd my-server
mimir deploy update
```

Then patch your client:

```bat
:: Copy deploy\player\patch.bat to your client folder (edit MIMIR_PATCH_URL first)
:: Then double-click it — or run repair.bat to force a full re-download
```

`mimir deploy update` runs `mimir build --all`, generates an incremental client patch (and refreshes the master patch), snapshots `build/server/` → `deployed/server/`, and restarts the game processes. SQL is not touched. Typical turnaround under a minute.

## Volume Mount Architecture

Game containers mount `<project>/deployed/server/9Data` (read-write). This allows `mimir build` to write to `build/` while containers are running — no file locking conflicts.

`mimir deploy update` and `mimir deploy restart-game` both robocopy `build/server → deployed/server` before restarting containers, so the game always sees the latest built data.

Zone.exe opens `9Data/SubAbStateClass.txt` with write access at startup — the mount must be read-write.

## Debugging a Crashed Container

To keep a container alive after its game process exits (for `docker exec` investigation), add `KEEP_ALIVE=1` to the service's environment in `docker-compose.yml`:

```yaml
zone00:
  <<: *gameserver
  environment:
    - PROCESS_NAME=Zone00
    - PROCESS_EXE=Zone.exe
    - ZONE_NUMBER=0
    - KEEP_ALIVE=1
```

Without `KEEP_ALIVE`, containers exit automatically when their Windows service stops, so `docker ps` always reflects actual server health.

## Config

Per-project deploy variables (SQL password, etc.) are stored in `<project>/.mimir-deploy.env` and loaded automatically before every deploy command. Set them with:

```bat
mimir deploy set SA_PASSWORD=MyStrongPassword1
```

The file is a plain `KEY=VALUE` list (no comments). It is covered by `*.env` in `.gitignore` — do not commit it.

| Variable | Default | Description |
|----------|---------|-------------|
| `SA_PASSWORD` | `V63WsdafLJT9NDAn` | SQL Server `sa` password used by the `sqlserver` container and all game processes |

The `deploy/docker-config/ServerInfo/ServerInfo.txt` override (baked into the image) changes:
- **ODBC driver**: `{SQL Server}` → `{ODBC Driver 17 for SQL Server}`
- **ODBC server**: `.\SQLEXPRESS` → `sqlserver` (Docker Compose hostname)
- **Process IPs**: `127.0.0.1` → Docker Compose service hostnames (`login`, `worldmanager`, `zone00`, etc.)

## Troubleshooting

**Container won't start**: Confirm Docker Desktop is in Windows container mode (whale icon → "Switch to Windows containers").

**`docker compose build` hangs or fails**: Run `set DOCKER_BUILDKIT=0` first. BuildKit is incompatible with Windows containers.

**SQL restore fails**: Check `docker logs <project>-sqlserver-1`. `.bak` files must be SQL Server 2025-compatible.

**Zone.exe crashes at startup**: GamigoZR must be present in `server-files/GamigoZR/` and started before Zone.exe. The startup script handles this automatically — check that GamigoZR was copied correctly.

**Can't connect from client**: Only port 9010 (Login) is exposed to the host. Configure the client to connect to `127.0.0.1:9010`.

**SQL password**: Default is `V63WsdafLJT9NDAn`. Override with `mimir deploy set SA_PASSWORD=...`.
Connect: `sqlcmd -S localhost\SQLEXPRESS -U sa -P <your-password> -C`
