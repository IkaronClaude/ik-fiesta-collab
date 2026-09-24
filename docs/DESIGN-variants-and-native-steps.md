# Design: build variants and native "generated steps"

Status: proposal (2026-09-24). Driven by Fiesta2026on2016, which today works around the gaps below with Python.

## Why

Fiesta2026on2016 ships three products from one project:

| product | layers |
|---|---|
| **2026** (the truthful port) | `data/` + `migrations/` |
| **2026 QoL** (cash shop free) | + `migrations-qol/` |
| **2026 Rebalanced** | + `migrations-qol/` + `migrations-rebalance/` |

collab has no notion of a variant, so `tools/build_variant.py` copies the whole project (fiesta.json, environments/,
overrides/, data/) to `build/<variant>-project`, replays the layers with `fiesta edit --file`, builds there and diffs
against the parity build. Most layer steps are Python (`NNNN-name.py`) that read the copy's JSON, compute, and emit
SQL plus side files. They exist because of six gaps, not because the logic needs a general-purpose language:

| gap | what the Python works around |
|---|---|
| no variants | the project copy + diff (`build_variant.py`) |
| no functions / asserts / reports in SQL | curve math, "lowest free id", lockstep checks, markdown reports |
| row environments are positional and dropped on any INSERT/DELETE | cannot add an env-specific row; 2016-only rows leak into the 2026-client build (corrupt MobInfo/ItemInfo there) |
| no per-env meaning of a column | 2026 `MobInfo.Name` is a `Loca/MobLoca` id, 2016's is text -> a separate `client26-*.json` patcher rebuilds client files from the official ones |
| QuestData is a hex `FixedData` blob | the quest curve edits bytes in Python |
| no table creation / lockstep cloning | shop files written into `overrides/`, NPC/item clones across 4 (mob) / 2 (item) lockstep tables by hand |

## Proposal

### 1. Variants (layers) as a collab concept

```jsonc
// fiesta.json
"variants": {
  "qol":        ["migrations-qol"],
  "rebalanced": ["migrations-qol", "migrations-rebalance"]
}
```

`fiesta build --variant qol [--env server|client|overlay|all] [--diff-against build/<env>]`
- loads `data/` into ONE session, replays `migrations/`, then each layer's `*.sql` in order (the `Migrations.RunAsync`
  loop, pointed at more directories), and builds to `build/qol/<env>` - never writes `data/`;
- `--diff-against` emits only the files that differ (a deployable patch set);
- `fiesta migrate --variant X` / `fiesta query --variant X` for inspection.

### 2. SQL that can express the steps

- **Functions** registered on the connection (`SqliteConnection.CreateFunction` / `CreateAggregate`):
  `ln`, `exp`, `pow`, `median(x)`, `interp_log(level, '60:0.40,100:0.20,...')`,
  `blob_u8/u16/u32(hex, off)` and `blob_set_u8/u16/u32(hex, off, v)` (until 5. lands).
- **Directives** in migration comments, run by the migration runner:
  - `-- @assert <SELECT>`: fail the run if it returns rows (lockstep ids, u16 ranges, the 256-slot shop tab, "template exists").
  - `-- @report <name> <SELECT>`: write the result as a markdown table to `build/<variant>/reports/<name>.md`.
  - `-- @param NAME = value`: named constants substituted as `:NAME` (the knobs that sit at the top of the Python steps).
- Lowest free id is already plain SQL:
  `WITH RECURSIVE n(i) AS (SELECT 60000 UNION ALL SELECT i + 1 FROM n WHERE i < 65535) SELECT MIN(i) FROM n WHERE i NOT IN (SELECT ID FROM ItemInfo)`.

### 3. Row environments as a real column

Load a hidden `_envs` column (e.g. `'server,client'`, NULL = every env) into SQLite and write it back, instead of the
positional side list that `ApplySqlAsync` drops whenever a row count changes. Then:
- `INSERT ... (_envs) VALUES ('server')` adds a server-only row;
- the 2016-only rows get `_envs = 'server,client'` once, and the overlay (2026 client) build stops emitting them.

### 4. Per-environment typing

Import `Loca/*` (at least `MobLoca`) into the overlay env. A migration inserting an NPC also inserts its MobLoca row
and sets `Name__overlay` to that id - or collab resolves "text -> Loca id" for columns declared as Loca references in
`fiesta.definitions.json`. With 3 + 4 the overlay build is trustworthy and the `client26-*.json` patcher goes away:
the 2026 client patch is `build/<variant>/overlay --diff-against` the official files.

### 5. QuestData decoded into child tables

`QuestData_Mob`, `QuestData_Item`, `QuestData_Action`, `QuestData_Reward` keyed by (quest id, slot), encoded into the
680-byte record at build. Quest edits become UPDATEs. Longer term the 2016 monolith is DERIVED at build from the 2026
normalized tables (QuestReward / QuestEndNpc / QuestEndItem / QuestAction), moving Fiesta2026on2016's
`merge_quests.py` rules into collab, instead of keeping two copies in sync.

### 6. Table creation and entity cloning

- `-- @table QoL_Enchant_Tab00 LIKE RouItemMctPey_Tab00 FILE Shine/NPCItemList/QoL_Enchant.txt` registers a new table
  in the (variant's) manifest.
- `fiesta.definitions.json` declares lockstep entities:
  `"mob": ["MobInfo", "MobInfoServer", "MobSpecies", "QuestSpecies"]`, `"item": ["ItemInfo", "ItemInfoServer"]`.
  `-- @clone mob RouItemMctPey AS QoL_Perks SET Name = '[QoL] Perks'` appends one row per member table, in step,
  with the next free id.

## What stays outside collab

Generators that need non-table inputs - walkable NPC spots from `.shbd`, reverse-engineered constants - run ONCE and
write a committed SQL migration with concrete values (the `fiesta edit --record` model). Reviewable, and the build
never re-derives positions.

## Order

1. Variants + `@assert` / `@report` / `@param` + SQL functions (1, 2). Converts most Fiesta2026on2016 layer steps
   (enchant/buff/Pey merchants' price and shop rows, creation gifts, tradeable, perk gates, charm supersede, enchant
   rates, remote quests) to SQL; `build_variant.py` goes.
2. `_envs` column + Loca (3, 4). Deletes the client26 patcher; also fixes the parity overlay build.
3. `@table` / `@clone` (6). Costume, perk and stone merchants become SQL.
4. Decoded QuestData (5). The Rebalanced quest curve becomes SQL.

After 4 the only Python left is exploratory analysis and one-off generators that emit committed SQL.
