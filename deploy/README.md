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
| `mimir deploy server` | **Full cycle**: stop all → `mimir build --all` → `mimir pack patches` → snapshot → start all. Use for first-time deploys or after config changes. |
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

To keep containers alive after their game process exits (for `docker exec` investigation):

```bat
mimir deploy set KEEP_ALIVE 1
mimir deploy rebuild-game
```

This applies to all game containers. Containers will stay running after the Windows service stops, letting you `docker exec` in to inspect logs and state. Unset it when done:

```bat
mimir deploy set KEEP_ALIVE 0
mimir deploy rebuild-game
```

Without `KEEP_ALIVE`, containers exit automatically when their Windows service stops, so `docker ps` always reflects actual server health.

## Config

Per-project deploy variables are stored in `<project>/.mimir-deploy.env` and loaded automatically before every deploy command. The file is a plain `KEY=VALUE` list covered by `*.env` in `.gitignore` — do not commit it.

| Command | What it does |
|---------|-------------|
| `mimir deploy set KEY VALUE` | Write a variable to `.mimir-deploy.env` |
| `mimir deploy get KEY` | Read a single variable |
| `mimir deploy list` | Print all variables |
| `mimir deploy get-connection-string` | Print SQL connection strings (for copy-paste) |
| `mimir deploy set-sql-password NEW_PASSWORD` | Change the `sa` password in the running SQL container and update `.mimir-deploy.env`. Run `mimir deploy rebuild-game` afterwards to recreate game containers with the new password. If the API is running, also run `mimir deploy api`. |

| Variable | Required | Description |
|----------|----------|-------------|
| `SA_PASSWORD` | Yes — no default | SQL Server `sa` password used by the `sqlserver` container and all game processes. Set before first start with `mimir deploy set-sql-password YourStrongPassword1`. |
| `KEEP_ALIVE` | No (default `0`) | Set to `1` to keep all game containers running after their process exits. Useful for debugging. Run `mimir deploy rebuild-game` after changing. |

## Secrets

Sensitive values (passwords, API keys) should be stored separately from regular config so they are never accidentally committed.

```bat
:: Store a secret — writes to .mimir-deploy.secrets (gitignored)
:: and registers the key name in .mimir-deploy.secret-keys (committable)
mimir deploy secret set SA_PASSWORD MyStrongPassword1
mimir deploy secret set JWT_SECRET a-long-random-string-here
mimir deploy secret set WEBHOOK_SECRET another-random-string

:: Commit the key registry so teammates know what's needed
git add .mimir-deploy.secret-keys
git commit -m "Track required secret keys"

:: New machine / fresh clone — get prompted for each missing secret
mimir deploy secret check
```

| Command | What it does |
|---------|-------------|
| `mimir deploy secret set KEY VALUE` | Store a secret in `.mimir-deploy.secrets` |
| `mimir deploy secret get KEY` | Print a secret value |
| `mimir deploy secret list` | Show all required secrets and whether each is set |
| `mimir deploy secret check` | Interactively prompt for any missing required secrets |

`.mimir-deploy.secrets` is gitignored automatically. `.mimir-deploy.secret-keys` (just the names) is safe to commit — it tells new team members which secrets they need to fill in before running the server.

> **Note:** Passwords containing `!` are not supported by the interactive `check` prompt (cmd limitation). Edit `.mimir-deploy.secrets` directly for those.

## API Server

A REST API (Mimir.Api) is available as an optional profile. It exposes account management, authentication, and leaderboard endpoints over HTTP/HTTPS.

```bat
:: Set required secrets first (if not already done)
mimir deploy secret set SA_PASSWORD MyStrongPassword1
mimir deploy secret set JWT_SECRET a-long-random-string-at-least-32-chars
mimir deploy secret set ADMIN_KEY your-admin-key

:: Build and start the API container (port 5000)
mimir deploy api
```

| Variable | Default | Description |
|----------|---------|-------------|
| `SA_PASSWORD` | — required | SQL Server password |
| `JWT_SECRET` | dev default | Secret for signing JWT tokens — **change in production** |
| `ADMIN_KEY` | dev default | Key for admin-only endpoints |
| `WORLD_DB_NAME` | `World00_Character` | Character database name |
| `CORS_ORIGINS` | (none) | Comma-separated origins allowed for browser requests — set when using the web frontend (e.g. `http://your-server`) |
| `TURNSTILE_SECRET` / `TURNSTILE_SITE_KEY` | (none) | Cloudflare Turnstile captcha for registration |
| `RECAPTCHA_SECRET` / `RECAPTCHA_SITE_KEY` | (none) | Google reCAPTCHA v2 (used if Turnstile not configured) |
| `HTTPS_CERT_PATH` | (none) | Path to a PFX certificate inside the container (mount via `CERT_DIR`) |
| `HTTPS_CERT_PASSWORD` | (none) | Password for the PFX certificate |

## Web Frontend

A minimal web frontend (Mimir.StaticServer) serves register / login / change-password / leaderboard pages as an optional profile.

```bat
:: Set API URL so the frontend knows where to send requests
mimir deploy set API_URL http://your-server-ip:5000
mimir deploy set CORS_ORIGINS http://your-server-ip

:: Build and start the webapp container (port 80 by default)
mimir deploy webapp
```

| Variable | Default | Description |
|----------|---------|-------------|
| `API_URL` | `http://api:5000` | Public URL of the API — **must be reachable from the browser** |
| `WEBAPP_PORT` | `80` | Host port to expose the frontend on |

> If the frontend and API are on the same machine, set `API_URL` to the server's public IP or hostname, not `api` (which is only resolvable inside Docker).

## CI/CD — Auto-Deploy on Git Push

### Preferred: GitHub Actions with Self-Hosted Runner

Install a GitHub Actions runner directly on your server. On push, GitHub triggers the runner which has direct access to your project and Docker.

**Setup:**

1. In your project repo: **Settings → Actions → Runners → New self-hosted runner → Windows**. Follow GitHub's install steps. Leave `--work` as default — the runner maintains its own clean workspace separate from your local copy of the project.

2. Create a GitHub **Environment** (e.g. `dev`) under **Settings → Environments**. Add the following Variables and Secrets to it:

   **Secrets** (Settings → Environments → dev → Secrets):

   | Name | Example | Description |
   |------|---------|-------------|
   | `SA_PASSWORD` | `MyPassword1` | SQL Server sa password — required |
   | `JWT_SECRET` | `<random 32+ chars>` | JWT signing key — required if using the API |

   **Variables** (Settings → Environments → dev → Variables):

   | Name | Example | Description |
   |------|---------|-------------|
   | `COMPOSE_PROJECT_NAME` | `my-server-ci` | Docker container namespace — **must differ from your local instance** |
   | `PORT_OFFSET` | `1000` | Added to all game + SQL ports (e.g. `1000` → Login on 10010, SQL on 2433). Avoid offsets that land on 6000–6063 (X11, blocked by browsers). |
   | `API_URL` | `http://yourserver:7000` | Public URL of the API — **must be reachable from the browser**. Required if using the webapp. |
   | `CORS_ORIGINS` | `http://yourserver:1080` | Origin(s) allowed to call the API from a browser — must match the webapp's URL. Required if using the webapp. |
   | `API_PORT` | `7000` | Host port for the API container. Use this to override the PORT_OFFSET result (e.g. if the computed port falls in a blocked range). |
   | `WEBAPP_PORT` | `1080` | Host port for the webapp container. |
   | `PATCH_PORT` | `9080` | Host port for the patch server container. |

   > `PORT_OFFSET` is applied to game ports (Login, WorldManager, Zone, SQL). `API_PORT`, `WEBAPP_PORT`, and `PATCH_PORT` are set independently since web ports can land in browser-blocked ranges when an offset is applied.

3. Add `.github/workflows/deploy.yml` to your project repo (update branch name if not `master`):

```yaml
name: Deploy

on:
  push:
    branches: [ "master" ]
  workflow_dispatch:

jobs:
  deploy:
    runs-on: self-hosted
    environment: dev
    defaults:
      run:
        shell: cmd
        working-directory: ${{ github.workspace }}
    steps:
      - name: Update repository
        run: |
          git remote set-url origin git@github.com:your-org/your-repo.git
          git fetch origin
          git reset --hard origin/master

      - name: Remove local mimir.bat
        run: del mimir.bat

      - name: Write deploy config from GitHub variables/secrets
        shell: powershell
        env:
          SA_PASSWORD: ${{ secrets.SA_PASSWORD }}
          JWT_SECRET: ${{ secrets.JWT_SECRET }}
          COMPOSE_PROJECT_NAME: ${{ vars.COMPOSE_PROJECT_NAME }}
          PORT_OFFSET: ${{ vars.PORT_OFFSET }}
          API_URL: ${{ vars.API_URL }}
          CORS_ORIGINS: ${{ vars.CORS_ORIGINS }}
          # Optional: override individual ports (takes precedence over PORT_OFFSET)
          WEBAPP_PORT: ${{ vars.WEBAPP_PORT }}
          PATCH_PORT: ${{ vars.PATCH_PORT }}
          API_PORT: ${{ vars.API_PORT }}
        run: |
          $o = if ($env:PORT_OFFSET) { [int]$env:PORT_OFFSET } else { 0 }
          function Port($base) { $base + $o }
          function Var($envName, $base) {
            $v = [Environment]::GetEnvironmentVariable($envName)
            if ($v) { "$envName=$v" } else { "$envName=$(Port $base)" }
          }
          @(
            "COMPOSE_PROJECT_NAME=$($env:COMPOSE_PROJECT_NAME)"
            "LOGIN_PORT=$(Port 9010)"
            "WM_PORT=$(Port 9013)"
            "ZONE00_PORT=$(Port 9016)"
            "ZONE01_PORT=$(Port 9019)"
            "ZONE02_PORT=$(Port 9022)"
            "ZONE03_PORT=$(Port 9025)"
            "ZONE04_PORT=$(Port 9028)"
            "SQL_PORT=$(Port 1433)"
            (Var 'PATCH_PORT'  8080)
            (Var 'API_PORT'    5000)
            (Var 'WEBAPP_PORT' 80)
            if ($env:API_URL)      { "API_URL=$($env:API_URL)" }
            if ($env:CORS_ORIGINS) { "CORS_ORIGINS=$($env:CORS_ORIGINS)" }
          ) | Set-Content .mimir-deploy.env -Encoding ascii
          @(
            "SA_PASSWORD=$($env:SA_PASSWORD)"
            "JWT_SECRET=$($env:JWT_SECRET)"
          ) | Set-Content .mimir-deploy.secrets -Encoding ascii

      - name: Build & Deploy Game
        run: mimir deploy server

      - name: Deploy API
        run: mimir deploy api

      - name: Deploy WebApp
        run: mimir deploy webapp
```

> `git remote set-url` ensures SSH is used for fetch — HTTPS fetch fails on self-hosted runners without stored credentials. Use your repo's SSH URL (e.g. `git@github.com:org/repo.git`). If you have a multi-account SSH config alias (e.g. `Host claude` → `github.com`), use that form instead: `git@claude:org/repo.git`.
>
> `git fetch + git reset --hard` is used instead of `actions/checkout` to preserve gitignored files (secrets, deployed server snapshot) and avoid a Windows runner bug where Node.js fails to clean up a temp directory.
>
> `del mimir.bat` removes the project's local mimir resolver so the system PATH version is used instead.
>
> `mimir deploy server` handles build + pack + snapshot + restart in one step, including first-time SQL setup. Remove the API/webapp steps if you are not using those profiles.

---

### Alternative: Webhook Container (untested)

> **Note:** The webhook approach below is implemented but untested. Use the GitHub Actions runner above in preference.

The `ci` profile runs a webhook listener that triggers `mimir build + restart` whenever you push to your project's git repository. No GitHub Actions YAML files are needed — GitHub POSTs directly to the container.

### How it works

```
GitHub push
    │
    ▼
POST http://your-server:9000/webhook
    │
    ▼
Mimir.Webhook container (CI)
    ├── Validate HMAC-SHA256 signature
    ├── Check branch matches WEBHOOK_BRANCH
    ├── git pull (using SSH deploy key)
    ├── mimir build --all
    ├── mimir pack patches --env client  (if PACK_ENABLED=true)
    ├── robocopy build/server → deployed/server
    └── docker restart <game containers>
```

### Setup

**Step 1 — Set secrets**

```bat
mimir deploy secret set SA_PASSWORD MyStrongPassword1
mimir deploy secret set WEBHOOK_SECRET $(openssl rand -hex 32)
```

**Step 2 — Create an SSH deploy key** (so the container can `git pull` your project repo)

```bat
:: Generate a key pair (run once, from any machine)
ssh-keygen -t ed25519 -C "mimir-ci" -f deploy_key -N ""
```

- Add `deploy_key.pub` to your GitHub repo: **Settings → Deploy keys → Add deploy key** (read-only is fine)
- Create the `ci-ssh` directory in the `deploy/` folder and put `deploy_key` (the private key, no extension) inside it as `id_deploy`:

```bat
mkdir deploy\ci-ssh
copy deploy_key deploy\ci-ssh\id_deploy
```

> The `ci-ssh/` directory is gitignored. Never commit the private key.

**Step 3 — Configure and start**

```bat
:: Optional: change the branch to watch (default: master)
mimir deploy set WEBHOOK_BRANCH master

:: Optional: enable client pack on each deploy
mimir deploy set PACK_ENABLED true

:: Build and start the CI container (port 9000)
mimir deploy ci
```

**Step 4 — Configure GitHub webhook**

In your **project** repo (not the Mimir repo): **Settings → Webhooks → Add webhook**

| Field | Value |
|-------|-------|
| Payload URL | `http://your-server-ip:9000/webhook` |
| Content type | `application/json` |
| Secret | The value you set for `WEBHOOK_SECRET` |
| Which events? | Just the push event |

Make sure port 9000 is open in your firewall / port-forwarded on your router.

**Step 5 — Test it**

Push a commit to the watched branch. Within a few seconds:

```bat
:: Check the build log
curl http://your-server-ip:9000/log

:: Check status
curl http://your-server-ip:9000/status
```

### CI Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `WEBHOOK_SECRET` | (none — required) | HMAC secret — must match what you enter in GitHub |
| `WEBHOOK_BRANCH` | `master` | Only deploys push to this branch |
| `PACK_ENABLED` | `false` | If `true`, runs `mimir pack patches --env client` after build |
| `CI_PORT` | `9000` | Host port for the webhook listener |
| `CI_SSH_DIR` | `./ci-ssh` | Directory containing `id_deploy` (the SSH private key for git pull) |
| `RESTART_SERVICES` | all game containers | Space-separated list of service names to restart after build |

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

**SQL password not set**: Run `mimir deploy set-sql-password YourStrongPassword1` before first start, then `mimir deploy rebuild-sql`.
Connect: `sqlcmd -S localhost\SQLEXPRESS -U sa -P <your-password> -C`
