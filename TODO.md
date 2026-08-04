# TODO

Working tracker. [PLAN.md](PLAN.md) is the architecture and the *why*; this is the checklist.

**Now:** M3 is done and confirmed in-game. Movement is tracked and position persists across
logout. Next: a real `Map` object and terrain, so the server knows where the ground is — everything
after that (visibility, other players, creatures) needs it.

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

- [ ] `.map` header — magic `MAPS`, version 9, 44-byte `map_fileheader`
- [ ] Area data (16×16 `uint16`)
- [ ] Height data — float / `uint16` / `uint8` variants, V9 129×129 + V8 128×128
- [ ] Liquid data + queries
- [ ] Holes
- [ ] Height lookup at a coordinate
- [ ] **Trap:** filenames encode tileY before tileX — `gridX` is the ADT row (PLAN §5.1)
- [ ] Verify a height query against the C++ server's `.gps` output

**World database**

- [x] `player_levelstats` (4960 rows), `player_classlevelstats` (800 rows) — base stats
- [ ] `creature_template`, `creature`
- [ ] `gameobject_template`, `gameobject`
- [ ] `item_template`
- [ ] `quest_template`, `npc_text`
- [ ] Startup timing report, target under ~30 s

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

- [x] `GameObjectBase` → `WorldObject` → `Player`, with the cumulative type mask
- [x] `Player.Create` from the characters row + `ChrRaces` + `ChrClasses` + base stats
- [ ] `Unit` as its own layer (Player currently sits directly on `WorldObject`)
- [ ] `Creature`, `GameObject`
- [ ] `Item` / `Bag` — an `Object` but **not** a `WorldObject`

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

## Phase 6 — Maps, grids, visibility ⬜

- [ ] `Map` owning a 64×64 grid array, 8×8 cells per grid
- [ ] Inverted axis: `gx = 32 - x/533.3333`
- [ ] Grid creation (terrain) vs grid object loading (spawns) as two steps
- [ ] Spawn loading per grid
- [ ] Visibility — per-object visible-player maps, relocation notifiers
- [ ] `MapUpdater` worker pool, `MapMgr` 4-phase round-robin
- [ ] Move sessions onto the `TickScheduler` (it already exists, unused)

## Phase 7 — Movement 🔵 → **M4**

- [x] 27 movement opcodes → one handler, routed by the generated opcode table
- [x] `MovementInfo` read/write, round-trip tested
- [x] Server tracks the player's position (accepted on trust)
- [ ] Broadcast to nearby players (needs Phase 6 visibility)
- [ ] Speed and anti-cheat plausibility checks — position is currently believed unconditionally
- [ ] Transport movement (both directions refuse rather than guess)

## Phase 8+ ⬜

See [PLAN.md](PLAN.md) §6. Collision and pathfinding (vmaps/mmaps are already extracted), combat,
spells, progression, AI.

---

## Cross-cutting

- [ ] CI — build, test, vector verification on every push
- [ ] `tests/WowEmu.Tests.Protocol` — golden-packet comparison against captures
- [ ] `tests/WowEmu.Tests.Integration` — Docker MySQL + the gate harnesses
- [ ] Analyzer banning bare `Task.Run` in gameplay code (PLAN §4.2)
- [ ] Seeded differential testing against a running C++ server

## Known gaps worth remembering

- Character creation accepts any race/class the client offers — no expansion or faction gating.
- The world server has no tick loop yet; every session runs on its own task.
- `TickScheduler` is built and tested but nothing uses it until Phase 6.
- Both reference trees (`azerothcore-wotlk`, `database-wotlk`) have no git root — updating means
  re-cloning.
