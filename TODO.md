# TODO

Working tracker. [PLAN.md](PLAN.md) is the architecture and the *why*; this is the checklist.

**Now:** Creatures spawn. 145,946 of them load from `creature`, are built through their template
and base stats, and are filed into grids that load the first time a player can see into one. Next:
gameobject spawns, which are the same shape with a different create block — and then creatures need
to *do* something, which is Phase 8's pathfinding and Phase 11's AI.

| Milestone | Meaning | State |
|---|---|---|
| M1 | Client logs in and sees the realm list | ✅ done, verified with a retail client |
| M2 | Client reaches character selection | ✅ done — create, list and delete all work |
| M3 | Character enters the world and renders | ✅ done — confirmed with a retail client |
| M4 | Movement | ⬜ Phase 7 |

---

## Phase 0 — Foundations ✅

- [x] Solution skeleton, analyzers, warnings-as-errors
- [x] `WowEmu.Cryptography` — SRP6, RC4 + drop1024, AuthCrypt, SessionKeyGenerator
- [x] Golden crypto vectors from an independent Python reference
- [x] `PacketReader` / `PacketWriter` over spans
- [x] Packed GUID, packed XYZ, packed time — round-trip tested
- [x] `ObjectGuid` — 64-bit layout, entry/counter split per type
- [x] `Position` / `WorldLocation` — distances, angles, arcs, orientation normalization
- [x] `MsTime` / `GameTime` — 32-bit millisecond clock with wraparound preserved
- [x] `TickScheduler` — tick-bound continuations, bounded drain (PLAN §4.2 rule 3)
- [x] SFMT-19937, verified against upstream's own C
- [x] `urand` / `irand` / `frand` / `rand_norm`, verified against libstdc++ draw counts
- [x] Config and logging (JSON + env, not ini — deliberate deviation)

## Phase 1 — Auth server ✅ → **M1**

- [x] Logon challenge / proof, SRP6 server side
- [x] Reconnect handshake
- [x] Realm list
- [x] Build gating from `build_info`
- [x] M1 gate script (`tools/harness/m1_login.py`)

## Phase 2 — Database layer ✅

- [x] MySQL in Docker Compose, schema init script
- [x] `auth` schema — accounts, `realmlist`, `build_info`
- [x] EF Core model, repositories, migrations
- [x] Session key persisted so the world server can read it
- [x] Account CLI — create, set-password, delete, list, realm management
- [x] `characters` schema — designed fresh, migrated by the world server
- [x] First `world` table imported: `playercreateinfo`
- [ ] `WowEmu.Data.Import` — a real importer instead of piping `.sql` files by hand
- [ ] Idempotent re-import
- [ ] Import AzerothCore `auth` accounts (only matters if migrating an existing server)

## Phase 3 — World server socket + session ✅ → **M2**

- [x] Pipelines framing — mixed-endian headers, split reads, large-packet form
- [x] RC4 header encryption, continuous streams, serialized sends
- [x] `SMSG_AUTH_CHALLENGE` → `CMSG_AUTH_SESSION` → digest → `SMSG_AUTH_RESPONSE`
- [x] Addon manifest (zlib) → `SMSG_ADDON_INFO`
- [x] Opcode enum — all 1313, generated
- [x] Opcode table — status/processing classification for all 1312, generated and enforced
- [x] `CMSG_PING` / `SMSG_PONG`, cache version, tutorial flags, account data times
- [x] `CMSG_CHAR_ENUM` from the database
- [x] `CMSG_CHAR_CREATE` — name rules, race/class validity, start position
- [x] `CMSG_CHAR_DELETE` with ownership check
- [x] M2 gate script (`tools/harness/m2_world.py`)
- [ ] Character creation: expansion gating (death knight level requirement)
- [ ] Character creation: faction rules (no mixed-faction accounts on PvP realms)
- [ ] `playercreateinfo_item` — start the character with gear rather than naked
- [ ] `playercreateinfo_spell` / `_action` — starting abilities and action bar

## Phase 4 — Static game data 🔵 in progress

**DBC**

- [x] `DbcFile` — header, format-string column offsets, string block, validation
- [x] `DbcStore<T>` — id-indexed, sparse
- [x] Localized string groups with locale fallback
- [x] `ChrRaces` — display ids per gender, faction, name
- [x] `ChrClasses` — power type, name
- [x] `Map` — type, flags, directory, expansion
- [x] Tests against real extracted files, skipped when data is absent
- [x] `player_levelstats` / `player_classlevelstats` loaded into `PlayerStatsStore`
- [ ] `CharStartOutfit` — starting gear per race/class
- [ ] `AreaTable` — zone lookup for the character list and login
- [ ] `SpellItemEnchantment` — needed for equipment in the character list
- [ ] Remaining stores, as phases need them (109 total upstream)

**Terrain**

- [x] `.map` header — magic `MAPS`, version 9, 44-byte `map_fileheader`
- [x] Area data (16×16 `uint16`), including the single-area shortcut
- [x] Height data — all three widths (float / `uint16` / `uint8`), V9 129×129 + V8 128×128
- [x] Holes, with upstream's lookup tables
- [x] Height lookup — four-triangle interpolation per cell
- [x] Tiles loaded on demand and cached, absence cached too
- [x] Verified against three race start positions on two maps (within 2 yards)
- [x] Zone id derived from terrain on login and refreshed as the player moves
- [ ] Liquid data + queries (swimming, drowning, fall damage)
- [ ] Verify a height query against the C++ server's `.gps` output
- [ ] Unload tiles when a grid empties (Phase 6)

**World database**

- [x] `player_levelstats` (4960 rows), `player_classlevelstats` (800 rows) — base stats
- [x] `creature_template` (29,928), `creature` (145,946), `creature_model_info` (24,143),
      `creature_classlevelstats` (400) — vendored and loaded in 0.9 s
- [ ] `creature_addon`, `creature_equip_template` — auras and visible weapons on spawn
- [ ] `gameobject_template`, `gameobject`
- [ ] `item_template`
- [ ] `quest_template`, `npc_text`
- [ ] Startup timing report, target under ~30 s (creature load is timed; nothing else is)

## Phase 5 — Object model + entering the world ⬜ → **M3**

**Update fields — the critical path**

- [x] Generated field-index constants — 381 of them, all eight block boundaries match PLAN §5
- [x] Per-field size, type and visibility flags carried through from upstream's comments
- [x] `uint[]` storage with a per-field dirty mask, no-op on unchanged writes
- [x] Typed accessors — uint32, int32, float, packed byte, packed uint16, guid, flags
- [x] `UpdateMask` — block-based bitmask, 42 blocks for a player
- [x] `UpdateData` — block accumulation, out-of-range block, payload assembly
- [x] Compression above 100 bytes, uncompressed size prefixed
- [x] Movement block (`MovementInfo` + nine speeds) and the create-block builder
- [ ] Transport branch of the movement block (throws for now; Phase 6)
- [ ] Per-observer visibility filtering — the mask intersection exists, nothing computes the
      observer's visible-flag set yet

**Object hierarchy**

- [x] `GameObjectBase` → `WorldObject` → `Unit` → `Player`, with the cumulative type mask
- [x] `Player.Create` from the characters row + `ChrRaces` + `ChrClasses` + base stats
- [x] `Unit` as its own layer — everything below `UNIT_END` lives there, Player and Creature share it
- [x] `Creature` — a port of `InitEntry`, `UpdateEntry`, `SelectLevel` and `LoadFromDB`'s health block
- [ ] `GameObject`
- [ ] `Item` / `Bag` — an `Object` but **not** a `WorldObject`
- [ ] Creature equipment, addon auras, and the `creature_template_model` split (see below)

**Login**

- [x] `CMSG_PLAYER_LOGIN` → build the player from the `characters` row + DBC + level stats
- [x] `SMSG_LOGIN_VERIFY_WORLD`
- [x] `SMSG_FEATURE_SYSTEM_STATUS`, `SMSG_MOTD`, `SMSG_LEARNED_DANCE_MOVES`
- [x] Before add-to-map: `SMSG_BINDPOINTUPDATE`, `SMSG_INSTANCE_DIFFICULTY`, `SMSG_LOGIN_SETTIMESPEED`
- [x] The player's own create block, compressed
- [x] `SMSG_TIME_SYNC_REQ`
- [x] Session moves to `SessionStatus.LoggedIn`, admitting gameplay opcodes
- [x] M3 gate in `tools/harness/m2_world.py`
- [x] Confirmed with a retail client — logs in and roams
- [ ] A real `Map` object (the player currently has a map id but nothing owns it)

**Persistence**

- [x] Player save — position, zone, map, orientation
- [x] Saved on clean logout *and* on disconnect (alt-F4 must not lose progress)
- [x] `CMSG_LOGOUT_REQUEST` / `CMSG_LOGOUT_CANCEL`, session returns to `Authed`
- [x] Log out and back in preserves position — covered by the M3 gate
- [x] M3 gate in `tools/harness/m2_world.py`
- [ ] Periodic save (needs a tick loop)
- [ ] `character_homebind`, `character_spell`, `character_action`
- [ ] Level, stats and health saved (only position is written today)

## Phase 6 — Maps, grids, visibility 🔵

- [x] `Map` per map id, objects filed into 8×8 cells per grid (512×512 across a map)
- [x] Inverted, origin-centred axis for grids and cells
- [x] Visibility — per-player visible set, create on enter, destroy on leave, no duplicate creates
- [x] Movement broadcast to everyone who can see the mover, never back to the mover
- [x] Players added on login, removed on logout *and* on disconnect
- [x] Cells hold `WorldObject`, not just `Player` — creatures are filed and made visible the same way
- [x] Creature spawn loading per grid, on first sight, at most once per grid
- [x] Grid creation (terrain) vs grid object loading as two distinct steps — the second is
      `IGridObjectLoader`, which is also what lets a map be tested without a database
- [x] `spawnMask` honoured, so the 78 spawns that exclude difficulty 0 stay out of the normal world
- [ ] Gameobject spawn loading per grid
- [ ] Unload cells and tiles when a grid empties — a grid, once loaded, currently stays for the life
      of the process, and there is no tick to notice that the last player left
- [ ] `MapUpdater` worker pool, `MapMgr` 4-phase round-robin
- [ ] Move sessions onto the `TickScheduler` — the map lock is a stand-in for it
- [ ] 400-yard far-visibility second pass

## Phase 7 — Movement 🔵 → **M4**

- [x] 27 movement opcodes → one handler, routed by the generated opcode table
- [x] `MovementInfo` read/write, round-trip tested
- [x] Server tracks the player's position (accepted on trust)
- [x] Broadcast to nearby players, under the opcode the client sent
- [x] Coordinate sanity — NaN, infinity and out-of-map refused before they reach the map or the DB
- [x] Teleport cap — no single packet may move more than 150 yards
- [x] Speed check against server-measured elapsed time, not the client's own timestamp
- [x] Contradictory flag combinations refused
- [x] Rejected packets leave player state untouched and snap the client back
- [ ] Height check against terrain — needs vmaps first, or every bridge and building is a false
      positive (Phase 8)
- [ ] Swim/fly distinction — needs the liquid chunk parsed
- [ ] Speed checks against *applied* speeds rather than a fixed ceiling — needs auras (Phase 9)
- [ ] Fall damage
- [ ] Transport movement (both directions refuse rather than guess)

## Phase 8+ ⬜

See [PLAN.md](PLAN.md) §6. Collision and pathfinding (vmaps/mmaps are already extracted), combat,
spells, progression, AI.

---

## Stubs and silent gaps

Things that currently work by returning nothing, zero, or a constant. Each is invisible in normal
play, which is exactly why they are written down.

**Auth server**

- [ ] Bans and suspensions — `FailBanned` / `FailSuspended` exist as codes but nothing ever sends
      them; there is no `account_banned` table and no IP ban list
- [ ] Failed-login lockout — passwords can be guessed at socket speed
- [ ] Realm flags are static — a realm shows as online whether or not the world server is running
- [ ] Realm population is always 0, and the character count per realm in the realm list is always 0

**World server**

- [ ] `CMSG_TIME_SYNC_RESP` is never handled — the request is sent once and the reply ignored, so
      the server has no clock offset for the client
- [ ] Latency is never measured, despite answering every ping
- [ ] Account data (`CMSG_UPDATE_ACCOUNT_DATA`) is never stored — `SMSG_ACCOUNT_DATA_TIMES` always
      reports zeros, so client-side settings do not follow the account
- [ ] Tutorial flags are always zero and never saved
- [ ] MOTD comes from config, not from a database table
- [ ] Banned-addon list is always empty
- [ ] Client cache version is a static config value
- [ ] Logout is instant — upstream makes a player sit for 20 seconds unless resting or a GM, which
      needs a tick to expire on
- [ ] `SessionStatus.Transfer` is never entered; nothing moves between maps yet

**Update objects**

- [ ] Per-observer field filtering — `UpdateMask.IntersectWith` exists but nothing computes which
      fields a given observer may see, so every observer currently gets every non-zero field
      (a real information leak once there is anything private to leak)
- [ ] Create-block branches for `Transport`, `HasTarget`, `Vehicle`, `Rotation` and `LowGuid` are
      unwritten — only `Living | StationaryPosition | Self` is produced
- [ ] Our deflate output is not byte-identical to upstream's, by design (PLAN §9 excludes
      compressed bodies from byte-exact comparison) — worth remembering when diffing captures

**Characters**

- [ ] Only position, zone, map and orientation are saved — level, health and stats are recomputed
      from base tables on every login, so any change to them is lost
- [ ] No name profanity or reserved-name checks
- [ ] No declined names (the Russian client asks for them)
- [ ] Character deletion is immediate — no in-progress state, no undelete window

**Creatures**

- [ ] They do nothing. No AI, no threat, no movement generator, no respawn, no loot — a creature
      stands where its row puts it and can be looked at. That is what M4 asks for and no more.
- [ ] Health uses no per-rank rate. Upstream multiplies by `Rate.Creature.*.HP` from the config;
      all five default to 1.0 and there is no config system for them, so they are omitted rather
      than hard-coded to a number that would look deliberate.
- [ ] `creature_addon` and `creature_equip_template` are not read, so no creature has visible
      weapons or its spawn auras
- [ ] Damage, attack power and attack times are read from `creature_classlevelstats` into nothing —
      `SelectLevel` sets them upstream and there is no combat to consume them (Phase 9)
- [ ] Difficulty entries (`difficulty_entry_1..3`) are ignored; every creature is built from its
      normal-mode template
- [ ] `phaseMask` is loaded and stored but never checked — everything is visible to everyone

**Data**

- [ ] Only 3 of 109 DBC stores are loaded (`ChrRaces`, `ChrClasses`, `Map`)
- [ ] `.map` flight bounds are parsed past but discarded
- [ ] Terrain holes are implemented but never exercised by a test against a known hole
- [ ] World tables are imported by piping vendored `.sql` dumps from `sql/world/` — there is no
      real importer, no schema mapping and no type cleanup. Only 3 of upstream's 309 tables are
      vendored so far; `tools/db/export-world.sh` adds more one at a time.
- [ ] `world` structure is upstream's verbatim, not a schema we own. PLAN §5.2 wants cleaned types
      and names with columns and semantics preserved — that belongs in `WowEmu.Data.Import`, and
      until it exists the vendored `CREATE` is what is guaranteed to match the vendored rows.
      (Considered EF migrations for `world` and rejected: nothing queries it through EF, it is
      309 tables of someone else's shape, and owning the `CREATE` without a transform breaks the
      match with upstream's `INSERT`s. See `sql/README.md`.)

## Cross-cutting

- [ ] CI — build, test, vector verification on every push
- [ ] `tests/WowEmu.Tests.Protocol` — golden-packet comparison against captures
- [ ] `tests/WowEmu.Tests.Integration` — Docker MySQL + the gate harnesses
- [ ] Analyzer banning bare `Task.Run` in gameplay code (PLAN §4.2)
- [ ] Seeded differential testing against a running C++ server
- [ ] A harness client that deliberately misbehaves — nothing has yet proven a real client is
      snapped back correctly when its movement is rejected
- [ ] Startup timing report (PLAN §6 Phase 4 wants under ~30 s)

## Conventions

- **Anything used from `database-wotlk/` gets vendored into `sql/world/` first**, via
  `tools/db/export-world.sh`, with a note saying what reads it. The server never reads the
  reference checkout at runtime — it is gitignored and a fresh clone will not have it. See
  `sql/README.md`.
- Extracted client data (`data/`) is the same idea in reverse: too large to commit, so it stays
  out and `data/README.md` records how to regenerate it.
- Golden vectors and generated code are committed so that regenerating them is a visible diff.

## Known gaps worth remembering

- The world server has no tick loop; every session runs on its own task, and the map's lock stands
  in for the per-map worker PLAN §4.2 describes.
- `TickScheduler` is built and tested but still unused.
- Movement is validated for coordinates, teleports, speed and flag sanity — but not against
  terrain height or liquid, because both need vmaps to avoid false positives.
- Character creation accepts any race/class the client offers — no expansion or faction gating.
- Both reference trees (`azerothcore-wotlk`, `database-wotlk`) have no git root — updating means
  re-cloning.
- **The two reference trees are at different points in AzerothCore's history**, and creature
  spawning is the first place it bites. The C++ reads creature models from a `creature_template_model`
  table via `GetFirstValidModel()`; the vendored database still has `modelid1..4` on
  `creature_template` and no such table. Ours follows the *data*, so `GetRandomValidModelId` is the
  older four-slot form. Expect more of these as later phases touch data-shaped code: read the C++
  for behaviour, but check the dump before trusting a column name.
- `sql/world/` is now 19 MB, almost all of it `creature` (12 MB) and `creature_template` (7 MB).
  The vendoring rule says a fresh clone must be able to start, and that is the price. `item_template`
  and `gameobject` will roughly double it.
- `data/` is 3 GB of extracted client data and is not committed; a fresh clone needs the extractors
  run again. See `data/README.md`.
- `sql/world/` redistributes AGPL-3.0 content from AzerothCore's database. Deliberate — see
  `sql/README.md` — but it is a licence obligation worth being aware of.
