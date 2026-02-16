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

```bash
# Import server + client data into a project
dotnet run --project src/Mimir.Cli -- import my-project

# Interactive SQL shell
dotnet run --project src/Mimir.Cli -- shell my-project

# Run a query
dotnet run --project src/Mimir.Cli -- query my-project "SELECT * FROM ItemInfo WHERE Level > 100"

# Build back to server files
dotnet run --project src/Mimir.Cli -- build my-project ./build/server --env server

# Build all environments
dotnet run --project src/Mimir.Cli -- build my-project ./build --all
```

## Project Structure

| Project | Purpose |
|---------|---------|
| `Mimir.Core` | Shared models, interfaces, project/template system, definitions |
| `Mimir.Shn` | SHN binary provider + QuestData provider |
| `Mimir.ShineTable` | Text table providers (`#table` and `#DEFINE` formats) |
| `Mimir.Sql` | SQLite in-memory engine (load, query, extract) |
| `Mimir.Cli` | CLI: import, build, query, edit, shell, init-template, validate |

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

## CLI Commands

| Command | Description |
|---------|-------------|
| `import <project>` | Import data from configured environments into a project |
| `build <project> <output>` | Build project back to native file formats |
| `query <project> "<sql>"` | Run a SQL query against loaded tables |
| `edit <project> "<sql>"` | Execute SQL modifications and save back to JSON |
| `shell <project>` | Interactive SQL shell with `.tables`, `.schema`, `.save` |
| `init-template <project>` | Auto-generate merge/copy template from environment scan |
| `validate <project>` | Check foreign key constraints and data integrity |

## Import Coverage

- **219/219 SHN tables** (including QuestData.shn)
- **~1100 text tables** (shine tables + config tables)
- Byte-identical round-trip for all formats

## See Also

- [TRACKER.md](TRACKER.md) — Detailed task tracker, architecture notes, and stretch goals
