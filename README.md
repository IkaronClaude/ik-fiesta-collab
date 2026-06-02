# ik-fiesta-collab

[![CI](https://github.com/IkaronClaude/ik-fiesta-collab/actions/workflows/ci.yml/badge.svg)](https://github.com/IkaronClaude/ik-fiesta-collab/actions/workflows/ci.yml)

A **git-friendly content toolkit** for Fiesta Online private servers. It converts
the game's binary/text data (`.shn`, `.txt` tables) into a **JSON project** you
can diff, edit with **SQL**, validate, and build back to native server/client
format — so server content lives in version control with clean diffs instead of
opaque blobs. The CLI command is **`fiesta`**.

> Formerly **Mimir**. Two concerns were split into their own repos:
> client-patch packing/serving → [ik-fiesta-patch-server](https://github.com/IkaronClaude/ik-fiesta-patch-server),
> and the build→deploy pipeline → [ik-fiesta-collab-pipelines](https://github.com/IkaronClaude/ik-fiesta-collab-pipelines).
> This repo is now just the content toolkit.

## How it works

```
[SHN/txt files] --import--> [JSON project] --edit/query (SQL)--> [JSON project] --build--> [SHN/txt files]
                            git-tracked, clean diffs
```

1. **Import** your server (and client) data directories into a JSON project.
2. **Edit / query** with SQL or the interactive shell; **commit** clean per-table diffs.
3. **Build** back to native server/client format per environment.
4. **Validate** to catch broken references, orphans, and constraint violations.

(To turn `build` output into client patches, or to deploy the result, see the two
companion repos above.)

## Run it

The CLI assembly is `fiesta`. Pick one:

```bash
# From source
dotnet run --project src/Fiesta.Collab.Cli -- <command> [options]

# Or the published image (no .NET needed) — mount your project at /project
docker run --rm -v "$PWD/my-server:/project" \
  ghcr.io/ikaronclaude/ik-fiesta-collab:latest build --env server
```

`fiesta --help` lists every command: `env`, `init`, `import`, `reimport`,
`build`, `query`, `edit`, `shell`, `validate`, `init-template`, `edit-template`,
`shn`, `dump`, `analyze-types`.

## Typical workflow

```bash
# 1. Create a project + register data sources
fiesta init my-server
cd my-server
fiesta env server init Z:/Server --type server
fiesta env client init Z:/ClientSource/ressystem --type client

# 2. Generate merge rules, then import -> JSON
fiesta init-template
fiesta import

# 3. Edit with SQL, rebuild, commit
fiesta edit "UPDATE ItemInfo SET AC = 100 WHERE InxName = 'NoviceSword'"
fiesta query "SELECT InxName, AC, ReqLevel FROM ItemInfo WHERE ReqLevel > 100 LIMIT 20"
fiesta build --all
git add data/ && git commit -m "Bump NoviceSword AC"
```

Like git, `fiesta` finds the project by walking up from the CWD to the nearest
`fiesta.json`.

## Project layout

```
my-server/
├── fiesta.json            project manifest (the marker the CLI looks for)
├── fiesta.template.json   merge/copy rules across environments
├── data/                  the JSON tables — commit these
├── environments/<env>.json
└── build/                 generated SHN/txt output (gitignored)
```

`fiesta.definitions.json` (repo root) holds the table/constraint definitions used
by `validate`.

## Build & test

```bash
dotnet build ik-fiesta-collab.sln -c Release
dotnet test  ik-fiesta-collab.sln -c Release
```

CI builds + tests on every push and publishes the `fiesta` image to
`ghcr.io/ikaronclaude/ik-fiesta-collab` on `main`.

## Related

- [ik-fiesta-patch-server](https://github.com/IkaronClaude/ik-fiesta-patch-server) — pack/serve client patches from `build` output.
- [ik-fiesta-collab-pipelines](https://github.com/IkaronClaude/ik-fiesta-collab-pipelines) — example JSON→SHN→deploy CI pipeline.
- [ik-fiesta-docker](https://github.com/IkaronClaude/ik-fiesta-docker) — BYO Docker/k8s images for the server runtime + SQL + proxy.

## License

[Apache License 2.0](LICENSE).
