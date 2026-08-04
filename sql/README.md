# SQL

Data the server needs that is not created by migrations.

```
sql/world/schema/<table>.sql    CREATE TABLE — structure only
sql/world/data/<table>.sql      INSERT — rows only
```

`tools/db/import-world.sh` applies every schema file, then its data file. They are separate so
that re-seeding content never touches the structure, and so a schema change is a readable diff
rather than one buried in 124 KB of `INSERT`s.

## Why these are not EF migrations

`auth` and `characters` are schemas we designed, so they are EF-migrated: we own the model, it
evolves, and the ORM queries it. `world` is none of those.

- Nothing queries it through EF. PLAN.md §5.2 specifies a raw `MySqlDataReader` for `world`
  because it is bulk-loaded once at startup and never written — so an EF entity would exist purely
  to generate DDL and then go unused.
- It is upstream's shape, not ours, and there will eventually be 309 tables of it. Auth has three.
- Each dump's `CREATE` and `INSERT` match each other by construction. The moment we own the
  `CREATE`, upstream's rows may not line up — column order, names, types — and every import needs a
  transform.

That transform is worth building, and it is what `WowEmu.Data.Import` is for on the TODO: reading
upstream's schema, cleaning types and names per PLAN §5.2, and writing into a schema we own. Until
then, vendoring upstream's structure verbatim is the honest option — it is the one thing guaranteed
to match the data.

## Why this exists

The three databases are filled three different ways:

| Database | Schema | Data |
|---|---|---|
| `auth` | EF Core migrations, applied by the logon server at startup | the account CLI |
| `characters` | EF Core migrations, applied by the world server at startup | players, at runtime |
| `world` | the dumps in `sql/world/` | the same dumps |

`auth` and `characters` are schemas we designed, so they are migrated. `world` is *content* —
309 tables of community-curated game data — and PLAN.md §5.2 keeps it structurally close to
upstream, because diverging means re-curating it. So it is imported rather than migrated.

## Why the files are here and not read from `database-wotlk/`

`database-wotlk/` is a reference checkout: it is gitignored, has no git root, and is 193 MB. A
fresh clone of this repository would not have it, and the server would not start.

Everything in `sql/world/` is committed, so `tools/db/import-world.sh` works on a clean checkout
with nothing but Docker.

## The rule

**Anything the server needs from `database-wotlk/` gets vendored here first.**

No ad-hoc `docker exec ... < database-wotlk/...`. No runtime path pointing at the reference
checkout. If the server reads it, it lives in `sql/world/` and is committed — so that a fresh
clone starts, and so there is a record of what was taken and why.

The same goes in reverse: if you find something in the database that no file here explains, it
was loaded by hand and needs vendoring before anyone relies on it.

## Adding a table

```bash
tools/db/export-world.sh creature_template "Needed by: creature spawning."
```

That loads upstream's dump, then dumps it back out from the live database with a provenance header
— so what lands here is what the server actually runs against, rather than a file assumed to be
equivalent. It needs `database-wotlk/` checked out; importing does not.

Each file records **what reads it**, which is the thing that is impossible to reconstruct later.

## Provenance and licence

These tables come from [AzerothCore's world database](https://github.com/azerothcore/database-wotlk),
which is licensed **AGPL-3.0**. They are redistributed here unchanged apart from the storage engine
(`MyISAM` → `InnoDB`) and character set (`utf8mb3` → `utf8mb4`), to match the rest of our schema.

Committing them means this repository redistributes AGPL-3.0 content, which carries that licence's
obligations. That is a deliberate choice, not an oversight — the alternative is a server that
cannot start without a 193 MB reference checkout.
