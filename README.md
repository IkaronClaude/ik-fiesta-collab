# Mimir

Server administration toolkit for Fiesta Online private servers. Converts game data files into a git-friendly JSONL project format, provides SQL querying, bulk editing, validation, and builds back to server format.

## How It Works

```
[Server Files] --import--> [JSONL Project] --edit/query--> [JSONL Project] --build--> [Server Files]
   SHN/txt                   git-tracked         SQL/CLI                        SHN/txt
```

1. **Import** your server's data directory into a Mimir project
2. **Query and edit** using SQL or CLI commands against the JSONL files
3. **Commit** your changes to git - diffs are clean, one row per line
4. **Build** to convert back to server format (SHN, txt, etc.)
5. Optionally run **validate** to catch broken references, orphaned items, incomplete sets

## Project Structure

| Project | Purpose |
|---------|---------|
| `Mimir.Core` | Shared interfaces, models, JSONL project format, DI |
| `Mimir.Shn` | SHN binary file provider (decrypt, parse, write) |
| `Mimir.RawTables` | Text-based data file providers (drop tables, configs) |
| `Mimir.Sql` | SQLite query engine (loads JSONL, runs SQL) |
| `Mimir.Cli` | Command line tool |

## Data Formats

| Format | Extension | Description |
|--------|-----------|-------------|
| SHN | `.shn` | XOR-encrypted binary tables (items, mobs, skills, maps, etc.) |
| Raw Tables | `.txt` | Tab/space-delimited config files (drop tables, character defaults) |
| Quest | custom | Quest definitions - TBD |
| Scenario | custom | Instance scripts in a custom scripting language |
| Config | `.json` / `.txt` | Server configuration files |

## See Also

- [TRACKER.md](TRACKER.md) - Detailed task tracker and stretch goals
