# Mimir

Server administration toolkit for Fiesta Online private servers. Converts game data files into a git-friendly JSON project format, provides SQL querying, bulk editing, validation, and builds back to server format. Supports multi-environment workflows (server + client data) and client patch distribution.

## How It Works

```
[Server/Client Files] --import--> [JSON Project] --edit/query--> [JSON Project] --build--> [Server/Client Files]
      SHN/txt                      git-tracked        SQL/CLI                         SHN/txt
                                                                                           |
                                                                                        --pack--> [Patch Server]
                                                                                                  HTTP patches
```

1. **Import** your server and client data directories into a Mimir project
2. **Query and edit** using SQL or the interactive shell
3. **Commit** your changes to git — each table is a single JSON file, clean diffs
4. **Build** to convert back to server/client format per environment
5. **Pack** to generate incremental client patches served over HTTP
6. **Validate** to catch broken references, orphaned items, and constraint violations

---

## Setup (First Time)

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Git

### 1. Clone the repo

```bat
git clone https://github.com/IkaronClaude/ProjectMimir.git
cd ProjectMimir
```

### 2. Add mimir to your PATH (optional but recommended)

`mimir.bat` at the repo root wraps `dotnet run` so you always run from source. Add the repo directory to your `PATH` so you can run `mimir` from anywhere:

```bat
:: Add to PATH permanently (run once as admin, replace with your actual path)
setx PATH "%PATH%;C:\Projects\Mimir"
```

### 3. Create a project

A "project" is a directory that holds your imported game data as JSON. Create one with `mimir init`:

```bat
mimir init my-server --env server=Z:/Server --env client=Z:/Client/ressystem
```

This creates inside `my-server/`:
- `mimir.json` — project manifest
- `mimir.bat` — local mimir resolver (all generated scripts call this; edit if mimir isn't in PATH)
- `.gitignore` — excludes `build/` and `patches/`
- `deploy/reimport.bat` — wipe + reimport + rebuild
- `deploy/server.bat` — build + pack + start all containers
- `deploy/player/` — client patch scripts

> Pass `--mimir <cmd>` to bake a specific path into `mimir.bat` (e.g. `--mimir C:\Tools\mimir.bat`). Default assumes `mimir` is in PATH.

> The `server` and `client` env names are yours to choose — they become the env names used in build output.

### 4. Generate merge rules

Mimir needs to know how to handle tables that exist in both server and client. Auto-generate rules:

```bat
cd my-server
mimir init-template --passthrough server
```

This scans both environments and writes `mimir.template.json`. Tables in both envs get a `merge` rule; tables in only one env get a `copy` rule. `--passthrough server` also adds `copyFile` actions for any non-table files found in the server env (e.g. plain `.txt` config files like `_ServerGroup.txt`).

> **Important:** After generating, set `split` strategy on tables with known value conflicts (e.g. ColorInfo, ItemViewInfo). This tells Mimir to preserve both env's values in separate columns rather than erroring on conflict:
>
> ```bat
> mimir edit-template --conflict-strategy split
> ```
>
> Once you've customized the template, **don't regenerate it** — `init-template` overwrites your edits.

### 5. Import

```bat
mimir import
```

This reads all SHN and text table files from each configured environment, merges them according to your template, and writes `data/**/*.json` files.

After import completes, Mimir automatically seeds the **pack baseline** by hashing all source files. This means the first `mimir pack` only distributes files that actually differ from the stock client — not every file. The baseline is stored in `.mimir-pack-manifest.json` (version 0) in the project directory.

To skip the baseline reseed (e.g. if you already have patches in the wild and don't want to reset the diff baseline):

```bat
mimir import --retain-pack-baseline
```

To reset the baseline without doing a full reimport (just re-hashes source files, takes seconds):

```bat
mimir import --reseed-baseline-only
```

### 6. Build

```bat
mimir build --all
```

Converts the JSON project back to native server/client formats. Output goes to `build/server/` and `build/client/` (as configured in `mimir.json`). Also copies any `copyFile` passthrough files and applies `overridesPath` files on top.

### 7. Pack client patches

```bat
mimir pack patches --env client
```

Compares `build/client/` against the previous pack state, creates a versioned zip of changed files in `patches/`, and updates `patches/patch-index.json`. Client patchers check this index to download only what changed.

On first run the baseline was seeded by `mimir import`, so patch v1 only contains files that differ from the stock client. On subsequent runs each pack is incremental from the previous one.

---

## Day-to-Day Workflow

Run all commands from within your project directory (like git — Mimir finds `mimir.json` by walking up from CWD):

```bat
cd my-server

:: Edit data with SQL
mimir edit "UPDATE ItemInfo SET AC = 100 WHERE InxName = 'NoviceSword'"

:: Interactive SQL shell
mimir shell

:: Query
mimir query "SELECT InxName, AC, ReqLevel FROM ItemInfo WHERE ReqLevel > 100 LIMIT 20"

:: Rebuild after edits
mimir build --all

:: Pack fresh client patches
mimir pack patches --env client

:: Commit your changes
git add data/
git commit -m "Increase NoviceSword AC to 100"
```

---

## Clean Re-import

Wipes `data/` and `build/`, re-imports everything from source, and rebuilds. Your template is preserved.

```bat
:: From within the project dir:
mimir reimport

:: Or via the generated script:
deploy\reimport.bat
```

---

## Client Patching

Mimir includes a self-contained patching system so game clients can update themselves from a patch server.

### How it works

1. `mimir pack patches --env client` — diffs `build/client/` against the previous pack state, writes a versioned incremental zip and a `patches/patch-master.zip` (full current snapshot), then updates `patches/patch-index.json`
2. A patch server (any HTTP server) serves the `patches/` directory — the Docker setup includes nginx on port 8080
3. Players run `patch.bat` from their game folder to download only what changed

### Player patch scripts

`deploy/player/` contains two scripts players copy to their game client folder:

| Script | What it does |
|--------|-------------|
| `patch.bat` | Downloads and applies any new patches. If the client is too far out of date it automatically downloads the full master patch instead of chaining incrementals. |
| `repair.bat` | Forces a full re-download of all client files — call this if the client is broken or corrupted. |

Before distributing, edit the `MIMIR_PATCH_URL=` line near the top of `patch.bat` to point at your patch server.

The patcher stores `.mimir-version` in the client folder to track the current version and only download what's new.

---

## Docker Game Server

See `deploy/` for Docker Compose configuration that runs a full Fiesta server stack locally (SQL Server + all 11 game processes + optional patch server).

All deploy commands are run from inside your project directory using `mimir deploy <script>`. Mimir finds your project automatically (same upward walk as `mimir build`) and namespaces all Docker containers and volumes under your project name so two projects can run side by side.

### Quick start (first time)

```bat
cd my-server

:: Build the SQL image and restore databases from .bak files
mimir deploy rebuild-sql

:: Build the game server image and start everything
mimir deploy rebuild-game
```

### Deploy commands

| Command | What it does |
|---------|-------------|
| `mimir deploy start` | Start all containers (no rebuild) |
| `mimir deploy stop` | Stop all containers |
| `mimir deploy update` | **Iterative dev cycle**: `mimir build --all` → `mimir pack patches` → snapshot → restart game servers. No SQL touch, no Docker rebuild. |
| `mimir deploy deploy` | **Full cycle**: stop all → build → pack → snapshot → start all |
| `mimir deploy restart-game` | Snapshot + restart game containers (use after a manual `mimir build`) |
| `mimir deploy reimport` | Full reimport from source (slow — wipes data/, rebuilds, reseeds pack baseline) |
| `mimir deploy rebuild-game` | Rebuild game server Docker image + start (needed after server binary changes) |
| `mimir deploy rebuild-sql` | Wipe and restore SQL databases from `.bak` files (destructive) |
| `mimir deploy logs` | Stream logs from all game containers |

### Iterative data change workflow

```bat
cd my-server
mimir deploy update
```

Builds data, generates an incremental client patch, snapshots to `deployed/server/`, and restarts only the game processes. SQL and Docker images are untouched. Typical turnaround under a minute.

### Full deploy (first time or after binary/config changes)

```bat
cd my-server
mimir deploy deploy
```

Stops all containers, builds everything, packs patches, and starts fresh.

### Patch server

nginx container serving `patches/` on port 8080, started automatically with `mimir deploy start`. `mimir deploy deploy` and `mimir deploy update` both run `mimir pack` before restarting so the patch server is always current.

### Debugging a crashed container

Add `KEEP_ALIVE=1` to a service's environment in `docker-compose.yml` to keep the container alive after the game process exits. This lets you `docker exec` in to inspect logs, files, and service state.

> **SQL Server SA password**: `V63WsdafLJT9NDAn`
> Connect: `sqlcmd -S localhost\SQLEXPRESS -U sa -P V63WsdafLJT9NDAn -C`

---

## Project Structure

```
my-server/
  mimir.json              # project manifest (environments, table index)
  mimir.template.json     # merge rules, constraints, column annotations
  mimir.bat               # mimir resolver — edit this if mimir isn't in PATH
  .gitignore              # excludes build/ and patches/ from git
  data/
    shn/                  # SHN tables (server, client, or merged)
    shinetable/           # text tables (#table format)
    configtable/          # #DEFINE config tables
  build/
    server/               # built server output (SHN + txt)
    client/               # built client output
  patches/                # incremental + master patch zips, patch-index.json
  deployed/
    server/               # robocopy snapshot of build/server (Docker mounts this)
  deploy/                 # Docker Compose + deploy scripts (shared across projects)
    docker-compose.yml
    player/
      patch.bat           # distribute to players — edit MIMIR_PATCH_URL at top
      repair.bat          # forces full client re-download
```

---

## CLI Reference

Mimir automatically finds the project by walking up from the current directory (like git). Pass `--project <path>` or `-p <path>` to override.

| Command | Description |
|---------|-------------|
| `init <dir> --env name=path [--mimir cmd]` | Create a new project with `mimir.json`, `mimir.bat`, and `deploy/` scripts |
| `init-template [--passthrough env]` | Scan environments and auto-generate `mimir.template.json`; `--passthrough` adds `copyFile` actions for non-table files |
| `import [--reimport] [--retain-pack-baseline] [--reseed-baseline-only]` | Import data from configured environments; seeds pack baseline from source afterward. `--reimport` wipes `data/` and `build/` first. `--retain-pack-baseline` skips baseline reseed. `--reseed-baseline-only` only re-hashes source files for the baseline, no import. |
| `reimport [--retain-pack-baseline]` | Shortcut: `import --reimport` then `build --all` |
| `build [--all] [--env name] [--output dir]` | Build to native formats; defaults to `--all` when no flags given |
| `pack <output-dir> [--env client]` | Generate incremental patch zip + update `patch-index.json` |
| `snapshot <output-dir> [--env client] [--patches dir]` | Copy a full client snapshot (source files + all patches applied) to `output-dir` |
| `query "<sql>"` | Run a SQL SELECT against loaded tables |
| `edit "<sql>"` | Execute SQL modifications (UPDATE/INSERT/DELETE) and save back to JSON |
| `shell` | Interactive SQL shell with `.tables`, `.schema`, `.save` dot-commands |
| `validate` | Check foreign key constraints and data integrity |
| `edit-template [--table name] [--conflict-strategy split\|report]` | Modify merge actions in `mimir.template.json` |
| `shn <file> [options]` | Inspect a raw SHN file without importing — see [SHN Inspection](#shn-inspection) |

---

## SHN Inspection

Inspect raw SHN files directly — no project needed. Useful for debugging build output, comparing files, and verifying roundtrip fidelity.

```bat
:: Print column schema (default when no display option given)
mimir shn ItemInfo.shn --schema

:: Row count (reads only the header — very fast)
mimir shn ItemInfo.shn --row-count

:: First / last N rows
mimir shn ItemInfo.shn --head 10
mimir shn ItemInfo.shn --tail 5

:: Arbitrary row slice
mimir shn ItemInfo.shn --skip 100 --take 20

:: Diff two SHN files side by side (positional row comparison)
mimir shn source\ItemInfo.shn build\server\9Data\Shine\ItemInfo.shn --diff
mimir shn source\ItemInfo.shn build\server\9Data\Shine\ItemInfo.shn --diff --max-diffs 50

:: Write decrypted bytes for hex analysis
mimir shn ItemInfo.shn --decrypt-to ItemInfo.bin
```

**`--diff` output** compares files row by row. For each differing row it shows which columns changed. The reorder heuristic: if ≥50% of shared columns differ in a positional row, it's flagged as a likely row reorder rather than a data change.

Wide tables (>8 columns) are printed in vertical card format; narrow tables print as a horizontal grid.

---

## mimir.json Environment Config

```json
{
  "environments": {
    "server": {
      "importPath": "Z:/Server",
      "buildPath": "build/server"
    },
    "client": {
      "importPath": "Z:/Client/ressystem",
      "buildPath": "build/client",
      "overridesPath": "Z:/ClientOverrides"
    }
  }
}
```

| Field | Description |
|-------|-------------|
| `importPath` | Source directory to import from |
| `buildPath` | Output directory for `mimir build` |
| `overridesPath` | Optional directory — every file here is copied verbatim into build output last, overriding tables and `copyFile` actions |

---

## Code Projects

| Project | Purpose |
|---------|---------|
| `Mimir.Core` | Shared models, interfaces, project/template system, definitions |
| `Mimir.Shn` | SHN binary provider + QuestData provider |
| `Mimir.ShineTable` | Text table providers (`#table` and `#DEFINE` formats) |
| `Mimir.Sql` | SQLite in-memory engine (load, query, extract) |
| `Mimir.Cli` | CLI: init, import, reimport, build, pack, query, edit, shell, validate, edit-template |

## Data Formats

| Format | Extension | Provider | Description |
|--------|-----------|----------|-------------|
| SHN | `.shn` | `ShnDataProvider` | XOR-encrypted binary tables (items, mobs, skills, maps, etc.) |
| QuestData | `.shn` | `QuestDataProvider` | Custom binary format with inlined PineScripts (no XOR encryption) |
| Shine Table | `.txt` | `ShineTableFormatParser` | `#table/#columntype/#columnname/#record` format (spawns, NPCs, drops) |
| Config Table | `.txt` | `ConfigTableFormatParser` | `#DEFINE/#ENDDEFINE` format (server configs, character defaults) |

## Multi-Environment Support

Mimir handles server and client data as separate environments. Tables that exist in both are merged, with per-environment column tracking and metadata. At build time, each environment gets its own output with the correct directory structure.

### Value Conflict Resolution

When merged tables have different values for the same row and column (e.g. different RGB values in ColorInfo), you can choose how to handle it:

- **`"report"`** (default): Keep the target (server) value, log conflicts
- **`"split"`**: Create split columns (`ColorR__client`) preserving both values — each environment builds with its own correct data

```bat
:: Set split strategy on a specific table
mimir edit-template --table ColorInfo --conflict-strategy split

:: Or apply to ALL merge actions at once (recommended after init-template)
mimir edit-template --conflict-strategy split
```

## Import Coverage

- **219/219 SHN tables** (including QuestData.shn)
- **~1100 text tables** (shine tables + config tables)
- Byte-identical round-trip for all formats

---

## Common Problems

### `Fail to read ServerGroup.txt[0]` — Zone/WorldManager startup crash

**Cause:** `_ServerGroup.txt` (and other plain non-table files under the server import path) are not copied to the build output unless `--passthrough server` was passed when generating the template.

**Fix:** Regenerate with the passthrough flag:

```bat
mimir init-template --passthrough server
```

This adds `copyFile` actions for every non-table file in the server env (plain `.txt` configs, scripts, etc.) so they are copied verbatim to `build/server/` during `mimir build`.

If you've already customised your template and don't want to regenerate it, add the entry manually to `mimir.template.json`:

```json
{
  "action": "copyFile",
  "env": "server",
  "path": "9Data/ServerInfo/_ServerGroup.txt"
}
```

---

## See Also

- [TRACKER.md](TRACKER.md) — Detailed task tracker, architecture notes, and stretch goals
