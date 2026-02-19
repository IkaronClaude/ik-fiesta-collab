# Mimir

Server administration toolkit for Fiesta Online private servers. Converts game data files into a git-friendly JSON project format, provides SQL querying, bulk editing, validation, and builds back to server format. Supports multi-environment workflows (server + client data).

## How It Works

```
[Server/Client Files] --import--> [JSON Project] --edit/query--> [JSON Project] --build--> [Server/Client Files]
      SHN/txt                      git-tracked        SQL/CLI                         SHN/txt
```

1. **Import** your server and client data directories into a Mimir project
2. **Query and edit** using SQL or the interactive shell
3. **Commit** your changes to git — each table is a single JSON file, clean diffs
4. **Build** to convert back to server/client format per environment
5. **Validate** to catch broken references, orphaned items, and constraint violations

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

Or just call it with the full path, or `cd` to the repo root before running.

### 3. Create a project

A "project" is a directory that holds your imported game data as JSON. Create one with `mimir init`:

```bat
mimir init my-server --env server=Z:/Server --env client=Z:/Client
```

This creates `my-server/mimir.json` with your import paths, plus `.gitignore` and `reimport.bat`.

> The `server` and `client` names are yours to choose — they become the env names used in build output.

### 4. Generate merge rules

Mimir needs to know how to handle tables that exist in both server and client. Auto-generate rules:

```bat
cd my-server
mimir init-template
```

This scans both environments and writes `mimir.template.json`. Tables in both envs get a `merge` rule; tables in only one env get a `copy` rule.

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

### 6. Build

```bat
mimir build --all
```

Converts the JSON project back to native server/client formats. Output goes to `build/server/` and `build/client/` (as configured in `mimir.json`).

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

:: Or run the generated script:
reimport.bat
```

Or manually:

```bat
mimir import --reimport
mimir build --all
```

---

## Docker Game Server

See `deploy/` for Docker Compose configuration that runs a full Fiesta server stack locally (SQL Server + all 11 game processes).

```bat
:: Build containers (disable BuildKit for Windows containers)
set DOCKER_BUILDKIT=0
cd deploy
docker compose build

:: Start SQL Server first
docker compose up sqlserver -d

:: Start all game processes
docker compose up -d

:: View logs
docker compose logs -f

:: Stop everything
docker compose down
```

The game server containers volume-mount `build/server/` from your Mimir project, so `mimir build --all` followed by `docker compose restart` picks up data changes without rebuilding the image.

> **SQL Server SA password**: `V63WsdafLJT9NDAn`
> Connect: `sqlcmd -S localhost\SQLEXPRESS -U sa -P V63WsdafLJT9NDAn -C`

---

## Project Structure

```
my-server/
  mimir.json              # project manifest (environments, table index)
  mimir.template.json     # merge rules, constraints, column annotations
  .gitignore              # excludes build/ from git
  reimport.bat            # convenience script: reimport + rebuild
  data/
    shn/                  # SHN tables (server, client, or merged)
    shinetable/           # text tables (#table format) preserving 9Data layout
    configtable/          # #DEFINE config tables (ServerInfo, DefaultCharacterData)
  build/
    server/               # built server output (SHN + txt)
    client/               # built client output
```

---

## CLI Reference

Mimir automatically finds the project by walking up from the current directory (like git). Pass `--project <path>` or `-p <path>` to override.

| Command | Description |
|---------|-------------|
| `init <dir> --env name=path` | Create a new project with `.gitignore`, `reimport.bat`, and `mimir.json` |
| `init-template` | Scan environments and auto-generate `mimir.template.json` |
| `import [--reimport]` | Import data from configured environments; `--reimport` wipes `data/` and `build/` first |
| `reimport` | Shortcut: `import --reimport` then `build --all` |
| `build [--all] [--env name] [--output dir]` | Build to native formats; defaults to `--all` when no flags given |
| `query "<sql>"` | Run a SQL SELECT against loaded tables |
| `edit "<sql>"` | Execute SQL modifications (UPDATE/INSERT/DELETE) and save back to JSON |
| `shell` | Interactive SQL shell with `.tables`, `.schema`, `.save` dot-commands |
| `validate` | Check foreign key constraints and data integrity |
| `edit-template [--table name] [--conflict-strategy split\|report]` | Modify merge actions in `mimir.template.json` |
| `pack <output-dir> [--env client]` | Package client build into incremental patch zips with `patch-index.json` |

---

## Code Projects

| Project | Purpose |
|---------|---------|
| `Mimir.Core` | Shared models, interfaces, project/template system, definitions |
| `Mimir.Shn` | SHN binary provider + QuestData provider |
| `Mimir.ShineTable` | Text table providers (`#table` and `#DEFINE` formats) |
| `Mimir.Sql` | SQLite in-memory engine (load, query, extract) |
| `Mimir.Cli` | CLI: init, import, reimport, build, query, edit, shell, validate, edit-template, pack |

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

## See Also

- [TRACKER.md](TRACKER.md) — Detailed task tracker, architecture notes, and stretch goals
