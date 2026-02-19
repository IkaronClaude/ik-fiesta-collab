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

## Quick Start

`mimir.bat` in the repo root is a convenience wrapper — all commands below use it. Alternatively substitute `dotnet run --project src/Mimir.Cli --` for `mimir`.

### First-time project setup

```bat
:: 1. Create project directory and mimir.json with environment configs
mimir init my-project --env server=Z:/Server --env client=Z:/Client

:: 2. Scan environments and auto-generate merge/copy rules
mimir init-template my-project

:: 3. Import all data files into JSON
mimir import my-project

:: 4. Build back to native server/client formats
mimir build my-project ./my-project/build --all
```

### Day-to-day workflow

```bat
:: Edit data with SQL
mimir edit my-project "UPDATE ItemInfo SET AC = 100 WHERE InxName = 'NoviceSword'"

:: Interactive SQL shell
mimir shell my-project

:: Run a query
mimir query my-project "SELECT * FROM ItemInfo WHERE Level > 100"

:: Rebuild after edits
mimir build my-project ./my-project/build --all
```

### Clean re-import (wipes data/ and build/, regenerates template)

```bat
deploy\reimport.bat
```

Or manually:

```bat
mimir init-template my-project
mimir import my-project --reimport
mimir build my-project ./my-project/build --all
```

## Project Structure

| Project | Purpose |
|---------|---------|
| `Mimir.Core` | Shared models, interfaces, project/template system, definitions |
| `Mimir.Shn` | SHN binary provider + QuestData provider |
| `Mimir.ShineTable` | Text table providers (`#table` and `#DEFINE` formats) |
| `Mimir.Sql` | SQLite in-memory engine (load, query, extract) |
| `Mimir.Cli` | CLI: init, init-template, import, build, query, edit, shell, validate, pack |

## Data Formats

| Format | Extension | Provider | Description |
|--------|-----------|----------|-------------|
| SHN | `.shn` | `ShnDataProvider` | XOR-encrypted binary tables (items, mobs, skills, maps, etc.) |
| QuestData | `.shn` | `QuestDataProvider` | Custom binary format with inlined PineScripts (no XOR encryption) |
| Shine Table | `.txt` | `ShineTableFormatParser` | `#table/#columntype/#columnname/#record` format (spawns, NPCs, drops) |
| Config Table | `.txt` | `ConfigTableFormatParser` | `#DEFINE/#ENDDEFINE` format (server configs, character defaults) |

## Multi-Environment Support

Mimir handles server and client data as separate environments. Tables that exist in both are merged, with per-environment column tracking and metadata. At build time, each environment gets its own output with the correct directory structure.

```
my-project/
  mimir.json              # project manifest with environment configs
  mimir.template.json     # merge rules, constraints, column annotations
  data/
    shn/                  # SHN tables (server, client, or merged)
    shinetable/           # text tables preserving 9Data directory layout
    configtable/          # #DEFINE config tables
```

### Value Conflict Resolution

When merged tables have different values for the same row and column (e.g. different RGB values in ColorInfo), you can choose how to handle it:

- **`"report"`** (default): Keep the target (server) value, log conflicts
- **`"split"`**: Create split columns (`ColorR__client`) preserving both values — each environment builds with its own correct data

```bash
# After generating the template, set split strategy on tables with known conflicts
dotnet run --project src/Mimir.Cli -- edit-template my-project --table ColorInfo --conflict-strategy split

# Or apply to all merge actions at once
dotnet run --project src/Mimir.Cli -- edit-template my-project --conflict-strategy split
```

## CLI Commands

| Command | Description |
|---------|-------------|
| `init <project> --env name=path` | Create a new project directory with a skeleton `mimir.json` |
| `init-template <project>` | Scan environments and auto-generate `mimir.template.json` |
| `import <project> [--reimport]` | Import data from configured environments; `--reimport` wipes `data/` and `build/` first |
| `build <project> <output> [--env name\|--all]` | Build project back to native file formats per environment |
| `query <project> "<sql>"` | Run a SQL SELECT against loaded tables |
| `edit <project> "<sql>"` | Execute SQL modifications (UPDATE/INSERT/DELETE) and save back to JSON |
| `shell <project>` | Interactive SQL shell with `.tables`, `.schema`, `.save` dot-commands |
| `validate <project>` | Check foreign key constraints and data integrity |
| `edit-template <project>` | Modify merge actions in `mimir.template.json` (e.g. set conflict strategy) |
| `pack <project> <output-dir>` | Package client build into incremental patch zips with a `patch-index.json` |

## Import Coverage

- **219/219 SHN tables** (including QuestData.shn)
- **~1100 text tables** (shine tables + config tables)
- Byte-identical round-trip for all formats

## See Also

- [TRACKER.md](TRACKER.md) — Detailed task tracker, architecture notes, and stretch goals
