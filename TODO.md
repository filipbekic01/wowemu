# TODO

Working tracker. [PLAN.md](PLAN.md) is the architecture and the *why*; this is the checklist.

**Now:** Collision is wired into the game. Line of sight and floor height answer in world
coordinates, and movement validation refuses a player who is under the world — the first height
check that was safe to make. Working towards **M5** (kill a mob, gain XP, level up); melee combat is
next. Still open and still needing a deliberate yes: the DotRecast fork for pathfinding.

| Milestone | Meaning | State |
|---|---|---|
| M1 | Client logs in and sees the realm list | ✅ done, verified with a retail client |
| M2 | Client reaches character selection | ✅ done — create, list and delete all work |
| M3 | Character enters the world and renders | ✅ done — confirmed with a retail client |
| M4 | Movement | 🔵 players move, creatures wander; swimming and home-return still open |

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
- [x] `gameobject_template` (21,512), `gameobject` (85,552) — vendored and loaded
- [ ] `creature_addon`, `creature_equip_template` — auras and visible weapons on spawn
- [ ] `gameobject_template_addon` — per-template faction and flag overrides
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
- [x] `Position`, `LowGuid` and `Rotation` branches of the create block, for gameobjects
- [ ] Transport branch of the movement block (throws for now; Phase 6)
- [ ] Per-observer visibility filtering — the mask intersection exists, nothing computes the
      observer's visible-flag set yet

**Object hierarchy**

- [x] `GameObjectBase` → `WorldObject` → `Unit` → `Player`, with the cumulative type mask
- [x] `Player.Create` from the characters row + `ChrRaces` + `ChrClasses` + base stats
- [x] `Unit` as its own layer — everything below `UNIT_END` lives there, Player and Creature share it
- [x] `Creature` — a port of `InitEntry`, `UpdateEntry`, `SelectLevel` and `LoadFromDB`'s health block
- [x] `GameObject` — a `WorldObject` but **not** a `Unit`; 18 field slots against a unit's 148
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
- [x] Gameobject spawn loading per grid, composed with the creature loader
- [ ] Unload cells and tiles when a grid empties — a grid, once loaded, currently stays for the life
      of the process, and there is no tick to notice that the last player left
- [x] `MapUpdater` worker pool, `MapMgr` 4-phase round-robin with the accumulated-diff rule
- [x] Sessions moved onto the `TickScheduler`; the map lock is gone
- [ ] 400-yard far-visibility second pass

## Phase 7 — Movement 🔵 → **M4**

**Creature movement**

- [x] `MotionMaster` as a slot stack, with `Idle` and `Random`
- [x] `RandomMovementGenerator` — uniform over the disc, anchored to the spawn point so a creature
      cannot random-walk away over an hour
- [x] Straight-line moves advanced on the map tick, re-filed into their cells as they go
- [x] `SMSG_MONSTER_MOVE`, including the point count that is derived from a padded spline and is
      therefore 1 for a two-point move, not 2
- [x] A creature already walking when you arrive is sent the remainder of its move
- [x] Moves are queued behind the create block for the same creature, never ahead of it
- [ ] `HomeMovementGenerator` — nothing can pull a creature away from home yet, so nothing needs it
- [ ] `WaypointMovementGenerator` — 12,000-odd spawns ask for it and currently stand still
- [ ] Real splines: Catmull-Rom, cyclic paths, parabolic arcs, facing targets. A straight line is
      all the generators produce, and a two-point spline *is* a line — this becomes real at flight
      paths and scripted patrols.
- [ ] Terrain height is not consulted when picking a destination, for the same reason movement
      validation does not check it: without vmaps a creature indoors or on a bridge gets dropped
      through the floor
- [ ] Creature movement does not update `UNIT_FIELD_BYTES_1` stand state or emit walk/run flags

**Player movement**

- [x] 27 movement opcodes → one handler, routed by the generated opcode table
- [x] `MovementInfo` read/write, round-trip tested
- [x] Server tracks the player's position (accepted on trust)
- [x] Broadcast to nearby players, under the opcode the client sent
- [x] Coordinate sanity — NaN, infinity and out-of-map refused before they reach the map or the DB
- [x] Teleport cap — no single packet may move more than 150 yards
- [x] Speed check against server-measured elapsed time, not the client's own timestamp
- [x] Contradictory flag combinations refused
- [x] Rejected packets leave player state untouched and snap the client back
- [x] Under-the-world check — refuses a position far below the floor, where the floor is the higher
      of terrain and models
- [ ] The symmetric check — does Z *match* the floor — is still not made, and vmaps did not make it
      safe. It would have to know about transports, lifts, and the gap between leaving a surface and
      the fall being reported; each is an honest player disconnected.
- [ ] Swim/fly distinction — needs the liquid chunk parsed
- [ ] Speed checks against *applied* speeds rather than a fixed ceiling — needs auras (Phase 9)
- [ ] Fall damage
- [ ] Transport movement (both directions refuse rather than guess)

## Phase 8 — Collision + pathfinding 🔵

- [x] **Detour compatibility spike** (PLAN §3.4.1, and the phase's mandated first task).
      Answer: outcome 2 — fork DotRecast, change three constants. Recorded in PLAN.md §3.4.1.1.
- [x] `.mmap` and `.mmtile` headers parsed and verified against 40 real tiles
- [x] **Full tile reader** for AzerothCore's raw layout — vertices, polygons, detail mesh, BV tree,
      off-mesh connections. Needed whichever Detour we use, since DotRecast reads recast4j's own
      serialisation rather than the C++ struct blob.
- [x] Verified by invariant, not by "it parsed": every vertex inside its tile bounds, every polygon
      indexing a real vertex, every detail base inside its array, every BV escape inside the tree,
      and each section consuming exactly its predicted bytes
- [ ] **Decision needed:** fork DotRecast (zlib) and change `DtDetour.DT_SALT_BITS/TILE_BITS/POLY_BITS`
      to 12/21/31. It is three constants, but it means vendoring and maintaining a third-party
      codebase — worth a deliberate yes rather than drifting into it.
- [ ] Load a real tile into a mesh, run a path, compare against the C++ server's `.mmap path` — the
      phase's actual exit criterion, and still unproven
- [x] VMAPs: `.vmtree` and `.vmtile` readers — BIH index, model placements, bounds
- [x] BIH traversal from the root, with a cycle guard; verified to reach every primitive
- [x] The model-name terminator rule, which decides which file on disk holds the geometry
- [x] `.vmo` model files — groups, vertices, triangles, per-group BIH, and model liquids.
      All 7,086 parse; 11,945,292 triangles, every one indexing a vertex its group has.
- [x] `ModelInstance` transforms — the ray is moved into model space, not the model into the world
- [x] Möller–Trumbore ray/triangle intersection, with the nearest-hit distance threaded through
- [x] `BIH::intersectRay` — interval narrowing and near-child-first ordering, which is what makes
      the tree a filter rather than a full enumeration
- [x] `IsInLineOfSight` over one model group
- [x] The world-to-vmap coordinate mirror, and the euler-angle swizzle upstream applies
- [x] `StaticMapTree` — per-map tree, tiles loaded on demand, line of sight and height in world
      coordinates. Verified: 25 of 25 sampled model surfaces block a ray passing through them.
- [x] **The vmap tile naming trap** — a `.vmtile` is `{map}_{gridY}_{gridX}` while a `.map` is
      `{map}{gridX}{gridY}`. Two extractors, opposite conventions, nothing anywhere saying so.
      Measured against 144 real tiles rather than assumed.
- [ ] `DynamicMapTree` for gameobject-based line of sight, rebalanced every 200 ms
- [x] Height queries against vmaps, combined with terrain — the floor is the higher of the two, and
      246 of 437 sampled points in Stormwind stand on a model rather than the ground
- [x] The under-the-world check in movement validation, which is the half of the height test that
      is safe to make
- [ ] `PathGenerator`, the (y, z, x) Detour coordinate swizzle, `findSmoothPath`
- [ ] Custom cost function: `dist * (1 + slopeDegrees/100) * areaCost`, and the
      `DT_SLOPE_TOO_STEEP` status bit
- [ ] Height and liquid checks in movement validation, which have been waiting on vmaps since Phase 7

## Phase 9+ ⬜

See [PLAN.md](PLAN.md) §6. Combat, spells, progression, AI.

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
- [ ] Create-block branches for `Transport`, `HasTarget` and `Vehicle` are unwritten. `Living`,
      `StationaryPosition`, `Self`, `Position`, `LowGuid` and `Rotation` are all produced.
- [ ] Our deflate output is not byte-identical to upstream's, by design (PLAN §9 excludes
      compressed bodies from byte-exact comparison) — worth remembering when diffing captures

**Characters**

- [ ] Only position, zone, map and orientation are saved — level, health and stats are recomputed
      from base tables on every login, so any change to them is lost
- [ ] No name profanity or reserved-name checks
- [ ] No declined names (the Russian client asks for them)
- [ ] Character deletion is immediate — no in-progress state, no undelete window

**Gameobjects**

- [ ] They do nothing. No opening, looting, using, or trapping — a gameobject stands where its row
      puts it and can be looked at.
- [ ] The 32 `data0..data31` columns are not read, so nothing knows what a door opens with or where
      a teleporter goes
- [ ] `gameobject_template_addon` is not read, so per-spawn faction and flag overrides are missing
- [ ] Three templates carry `size = 0` and 11 spawns use them; they are drawn at nothing. Upstream
      does the same, so it is reproduced rather than corrected — but it looks like a bug when found.
- [ ] 1,193 spawns have no display id. These are invisible triggers and upstream creates them too,
      but each still costs a create block to a client that will never see anything.
- [ ] Rotation is computed once at spawn and is not an update field, so anything that ever rotates a
      gameobject will have to resend a whole create block

**Creatures**

- [ ] No AI, no threat, no respawn, no loot. They wander and can be looked at; nothing else.
- [ ] Movement ignores collision entirely — a creature walks through walls, trees and each other,
      because there are no vmaps and no pathfinding (Phase 8).
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

- [ ] Links are not read from `.mmtile` and must not be — the file reserves `MaxLinkCount` links'
      worth of space, but Detour builds them when a tile is added, so what is on disk is
      uninitialised. Anything that starts reading them is reading noise.
- [ ] Line of sight is computed but nothing consumes it: no spell or attack is blocked by a wall,
      because there are no spells or attacks. Movement validation uses the floor height only.
- [ ] The under-the-world threshold (25 yards) is a judgement, not a measurement. It has been
      exercised by the gate walking on open ground, not by a real client in a city or a cave — the
      false-positive rate is unknown.
- [ ] Vmap tiles are loaded on demand and never unloaded, the same gap grid loading has.
- [ ] `CollectCandidates` allocates a `List<uint>` per group per ray. Fine for the tests, not for a
      tick — it wants a reusable buffer once something calls it in anger.
- [ ] The single-child (`BVH2`) branch of the BIH walk is written from the C++ and never observed
      firing in a test; the sampled trees may not contain one.
- [ ] 132 of 436 sampled model groups carry an all-zero bounding box while holding real geometry.
      A zero box means "not recorded", not "empty" — anything that culls by it must not conclude the
      group is nowhere, or its triangles silently stop blocking anything. See
      `WorldModelGroup.HasBounds`.
- [ ] Model liquids parse but are unused; the tileless single-height form was not seen in the
      sampled files, so that branch is written from the C++ and never exercised.
- [ ] Off-mesh connections are exercised by exactly one tile in the entire extraction
      (`5622031.mmtile`, Blade's Edge Arena, 2 connections). That branch of the reader has a single
      real test case and no second opinion.
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

## Phase 4.2 — The tick ✅

The threading model PLAN.md §4.2 calls the single most important constraint in the port.

- [x] Outbound packets go through a `Channel` with one writer task — sending never touches a socket,
      so gameplay code on a map worker cannot block behind a slow client
- [x] Header encryption begins at a *position in the send queue*, not a moment in time, or the
      plaintext challenge would be encrypted if the writer had not caught up
- [x] `Map` is synchronous and lock-free; safety comes from the tick's ordering, not a mutex
- [x] World loop: drain the scheduler → drain sessions → run maps, in that order and never overlapping
- [x] `MapUpdater` — dedicated threads, not the thread pool, so a callback storm cannot starve a tick
- [x] One inbound queue per session, drained by whichever loop may run the packet at its front
- [x] Handlers resume on the loop that started them (`ConfigureAwait(true)` under the tick's
      `SynchronizationContext`), so a database answer never lands on a pool thread holding a `Player`
- [x] Per-viewer update batching — one `SMSG_UPDATE_OBJECT` per tick instead of one per object;
      logging in at Northshire went from 131 packets to 1
- [x] Periodic save — the first thing that could not exist before there was a tick
- [ ] Analyzer banning bare `Task.Run` in gameplay code (PLAN §4.2)
- [ ] `AssertOwnerThread` is not called anywhere yet — the invariant is documented and structural,
      but nothing fails loudly if a future caller breaks it
- [ ] Map workers default to 0 (inline). The pool is written and tested but unproven under load.
- [ ] **Grid loading blocks the tick it happens on.** Measured: 875 ticks/second idle, but the tick
      a player logs in on takes ~71 ms, over the 50 ms budget, because the grid around them builds
      ~960 creatures inside the map update. Upstream splits this per cell rather than per grid.

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

- Session handlers that await resume on the tick, but nothing *enforces* it: a future handler that
  writes `ConfigureAwait(false)` would silently resume on the thread pool and touch a `Player` from
  outside its loop. `TickScheduler.AssertOwnerThread` exists for exactly this and is not yet wired in.
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
