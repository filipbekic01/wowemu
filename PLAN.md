# WowEmu — a .NET 10 / C# reimplementation of AzerothCore (WoW 3.3.5a)

> Working plan. Every number, path and constant below was read out of the C++ checkout in
> `azerothcore-wotlk/` at commit `e2b5bd2` — not from memory. Line references are `path:line`.

---

## 1. Context

`azerothcore-wotlk/` is a mature C++ emulator for World of Warcraft 3.3.5a (client build **12340**).
We are building a C# equivalent. The point of this document is to make a ~717,000-line C++ codebase
tractable as an ordered sequence of small, verifiable steps.

**Decisions already made** (these shape everything downstream):

| Decision | Choice |
|---|---|
| Scope | **Playable subset first**, grown feature by feature. Not a big-bang 1:1 port. |
| Data | **Reuse extracted client data verbatim** (`.dbc`, `.map`, `.vmtree/.vmtile`, `.mmap/.mmtile`). **New DB schema** with an importer from AzerothCore's. |
| Code style | **Faithful but idiomatic** — keep `Unit`/`Player`/`Spell`/`Map` recognizable so upstream stays diffable, but write real C#. |
| Native deps | **P/Invoke where needed, managed where available.** (Investigation below changed this: almost nothing needs P/Invoke.) |

**Success is defined by milestones, not by percentage ported.** The ordering in §6 is chosen so that a
real, unmodified 3.3.5a client can connect earlier than is comfortable, and stays connected as we build.

⚠️ **This checkout is a fork, not upstream AzerothCore.** It carries a ToCloud9 cluster mode
(`src/server/game/TC9Sidecar/`, 1,099 LOC, custom opcodes `0x51F`/`0x520`) and a **rewritten grid
system** — there is no `NGrid`/`GridLoader`/`GridStates` state machine and **no idle-grid unloading**
(`Map::UnloadGrid` is reached only from `Map::UnloadAll()`, `Map.cpp:1061`). Read this tree, not
upstream docs.

---

## 2. What we are porting — measured

```
src/server/game/     648 files   331,628 lines   core gameplay
src/server/scripts/  693 files   336,548 lines   content (bosses, zones, class spells)
src/common/          173 files    24,591 lines   infrastructure
src/server/shared/    23 files    12,713 lines   auth/world shared
src/server/database/  45 files     8,252 lines   MySQL abstraction
                     ─────────────────────────
                    1,700 files  ~717,000 lines
```

| Subsystem | C++ LOC | Difficulty | Notes |
|---|---:|---|---|
| Entities / object model | 88,544 | extreme | `Unit.cpp` 17,424 · `Player.cpp` 16,725 |
| Spells & auras | 43,780 | extreme | 165-entry effect table, 317-entry aura table |
| Maps / grids / collision / nav | ~32,000 | extreme | 64×64 grids, BIH, Detour |
| Combat / threat / movement | ~17,600 | extreme | attack table, splines, 27 movement opcodes |
| World loop / sessions / opcodes | ~47,000 | high | 1,313-slot opcode table, 497 config values |
| Static data (DBC + ObjectMgr) | ~27,600 | high | 109 DBC stores, 102 `ObjectMgr::Load*` |
| Gameplay features | ~125,000 | high | quests, loot, guilds, BG, LFG, achievements… |
| Auth + network | ~15,300 | high | SRP6, RC4-drop1024, framing |
| Database layer | 8,252 | high | shrinks a lot in .NET |
| Common infrastructure | ~24,600 | medium | **most of this disappears** — BCL covers it |
| AI + scripting engine | ~34,000 | extreme | ScriptMgr: 475 methods, 404 hook IDs |
| Content scripts | 336,548 | high | volume, not difficulty — deferred indefinitely |

Other hard numbers worth internalizing:

- **1,306 opcodes** declared; `Opcodes.cpp` registers **1,312** entries — 725 client handlers
  (`DEFINE_HANDLER`) + 587 server-only stubs. `NUM_OPCODE_HANDLERS = 0x521 = 1313`.
  **292 of the 725 are `Handle_NULL`**, so there are only **433 live client handlers** to write.
- Processing split: `PROCESS_INPLACE` 381 · `PROCESS_THREADUNSAFE` 245 · `PROCESS_THREADSAFE` 97.
- **439 SQL tables**: auth 22, characters 108, world 309 (~297 MB of base dumps).
- **497 typed config values**, 592 lines of `worldserver.conf.dist`.
- **109 DBC stores**, **102** `ObjectMgr::Load*()` methods.
- `PLAYER_END = 1326` uint32 update fields (~5.3 KB of raw field block per player).
- SmartAI: **93 event types, 179 action types, 37 target types**, 52,768 `smart_scripts` rows.

**Estimated C# volume.** Excluding content scripts, the core is ~380,000 C++ lines → roughly
**265,000 C# lines** (≈0.7×; headers vanish, the BCL eats most of `common`, the DB layer shrinks ~70 %,
the scripting registry shrinks ~65 %). Reaching **M5** — a client that kills a mob and levels up — is
roughly **48,000 lines**, about 18 % of the non-content core. That is the number worth planning
around: a demonstrably working server for under a fifth of the total.

---

## 3. Findings that constrain the design

These are the things that, if we get them wrong, cost weeks.

### 3.1 The protocol is fully known and small enough to nail early

- **Auth (port 3724)** is 5 plaintext opcodes: `LOGON_CHALLENGE 0x00`, `LOGON_PROOF 0x01`,
  `RECONNECT_CHALLENGE 0x02`, `RECONNECT_PROOF 0x03`, `REALM_LIST 0x10`
  (`AuthSession.cpp:126-130`).
- **SRP6** uses `g = 7` and the fixed 32-byte modulus
  `894B645E89E1535BBDAD5B8B290650530801B18EBFBF5E8FAB3C82872A3E9BB7`, `k = 3`, 32-byte salt and
  verifier (`SRP6.cpp:26-29`, `SRP6.h:31-35`). All little-endian, fixed-width 32 bytes.
- **World header crypto (port 8085)**: only the **4/5/6 header bytes** are encrypted, never bodies.
  Two ARC4-drop1024 streams keyed by `HMAC-SHA1(constant, sessionKey)` where the 16-byte constant is
  the **key** and the 40-byte session key is the **message** (`AuthCrypt.cpp:7-17`).
- Client header is 6 bytes: `uint16 size` **big-endian** + `uint32 cmd` little-endian, in the same
  struct. Server header mirrors it, with a 5-byte form when `size > 0x7FFF` (high bit `0x80` set).
- 3.3.5a packets are **pure byte streams** — there are zero `WriteBit`/`FlushBits` helpers anywhere in
  this tree. Do not build a bit-writer.

### 3.2 The game layer is single-threaded per map

There is not one mutex, atomic or lock under `src/server/game/Spells/`. Correctness comes from
"everything that touches a map runs on that map's update thread", plus the rule that nothing holds a
raw `Unit*` across a delay (`Spell` stores `ObjectGuid` and re-resolves via `UpdatePointers()`).

**Consequence:** DB async callbacks are *polled* at a fixed point in the tick
(`World.cpp:1283`), not continued on the thread pool. Naively converting to `await` with the default
scheduler destroys the invariant the entire game layer depends on. **A tick-bound scheduler is
mandatory, not optional.** This is the single most important architectural constraint in the port.

### 3.3 Update fields are a flat `uint32[]`, and that's actually fine

`Object` state is `uint32* m_uint32Values` of length `m_valuesCount`, with a parallel `UpdateMask`
that uses **one byte per field, not one bit** (`UpdateMask.h:24`). Setters compare-then-write, mark
dirty, and enqueue into `Map::_updateObjects`; once per tick `Map::SendObjectUpdates()` builds a
per-viewer `UpdateData` and emits `SMSG_UPDATE_OBJECT` (`0x0A9`), zlib-compressed into
`SMSG_COMPRESSED_UPDATE_OBJECT` (`0x1F6`) above 100 bytes.

Keep this design. A `uint[]` plus typed accessor properties is idiomatic enough in C# and is the only
thing that serializes correctly without a translation layer.

### 3.4 Almost nothing needs P/Invoke

| C++ dep | .NET answer |
|---|---|
| recastnavigation (Detour) | **Not drop-in** — see §3.4.1. Forked [DotRecast](https://github.com/ikpil/DotRecast), or P/Invoke the patched native lib |
| OpenSSL SHA1/HMAC/AES-GCM | `System.Security.Cryptography` |
| OpenSSL RC4 | **not in .NET** — hand-write ~30 lines (KSA + PRGA) |
| OpenSSL BIGNUM | `System.Numerics.BigInteger` (`isUnsigned: true` overloads **only**) |
| argon2 | `Konscious.Security.Cryptography.Argon2` |
| zlib | `System.IO.Compression.ZLibStream` (**not** `DeflateStream` — RFC1950 vs RFC1951) |
| G3D vector math | `System.Numerics.Vector3/4/Matrix4x4` (SIMD) |
| boost::asio | `System.IO.Pipelines` + `Socket.AcceptAsync` |
| boost::program_options | `System.CommandLine` |
| libmpq | only needed for the extractor — `Nmpq` / `StormLibSharp`, or skip (see §5) |
| fmt, jemalloc, gperftools, utf8cpp, SFMT, stdfs | BCL / not needed |

#### 3.4.1 The Detour navmesh is **not** stock — verify before committing

`.mmtile` files are a 56-byte AzerothCore header followed by a Detour navmesh blob
(`MapDefines.h:63-79`). But `deps/recastnavigation/recastnavigation.diff` patches Detour in ways that
change binary compatibility:

```diff
-//#define DT_POLYREF64 1          →  +#define DT_POLYREF64 1     // 64-bit polyrefs
-static const unsigned int DT_SALT_BITS  = 16;  →  12
-static const unsigned int DT_TILE_BITS  = 28;  →  21
-static const unsigned int DT_POLY_BITS  = 20;  →  31
```

Upstream's own comment two lines above says: *"tiles built using 32bit refs are not compatible with
64bit refs"*. `dtPolyRef` widens to 8 bytes, which changes `dtLink` and therefore the serialized tile
layout. The patch also changes `RC_SPAN_HEIGHT_BITS 13→16` and `rcSpan::area` from a 6-bit field to a
full byte (Recast side — affects `mmaps_generator` only), and swaps `dtMathSqrtf`→`sqrtf` in
`findDistanceToWall` (a float-precision change).

On top of that, `dtQueryFilterExt::getCost` (`src/common/Navigation/DetourExtended.cpp`) overrides
path cost as `dist * (1 + slopeDegrees/100) * areaCost`, and a custom `DT_SLOPE_TOO_STEEP` status bit
(`1<<8`) is added to `DetourStatus.h`.

**Phase 8, task 1 — spike this before writing anything else.** Load one known `.mmtile`, add the tile,
run a path, compare against the C++ server's `.mmap path` output. Three outcomes:

1. DotRecast already uses 64-bit refs with a compatible split → use it as-is. Best case.
2. Constants differ → **fork DotRecast and change three constants.** It is zlib-licensed; this is a
   trivial fork and still far cheaper than P/Invoke. The custom cost function is *easier* in managed
   code (`IDtQueryFilter` is an interface) than through a native shim.
3. Tile layout genuinely incompatible → P/Invoke the patched native Detour, one `dtNavMeshQuery`
   per `Map` behind a `SafeHandle` (**it is not thread-safe**), `[SuppressGCTransition]` on the hot
   query calls.

#### 3.4.1.1 Spike result: **outcome 2** — fork DotRecast, change three constants

Measured against our own extracted data (`NavMeshFile`, `NavMeshSpikeTests`), not against the C++
headers — the two reference checkouts are at different points in AzerothCore's history, so "the C++
defines `DT_POLYREF64`" and "our tiles were built with it" are different claims.

| Question | Answer | How it was established |
|---|---|---|
| Do our tiles use 64-bit polyrefs? | **Yes** | `DT_POLYREF64` takes `sizeof(dtLink)` 12 → 16. Tile `0002239.mmtile` is 107,380 bytes; the 64-bit layout predicts exactly that, the 32-bit layout predicts 96,600. True for all 40 tiles sampled. |
| What is the bit split? | **12/21/31** | `mmaps_generator` writes `maxPolys = 1 << DT_POLY_BITS`. `000.mmap` holds `0x80000000` — `1 << 31`, which overflows the signed `int` Detour declares, so the field legitimately reads negative. Stock would hold 1,048,576. |
| Is DotRecast's reference width compatible? | **Yes** | C# has no `#ifdef`; `DtNavMesh.GetPolyRefBase` returns `Int64` unconditionally. |
| Is DotRecast's split compatible? | **No** | `DtDetour.DT_SALT_BITS/TILE_BITS/POLY_BITS` are stock 16/28/20, and `DtDetour.EncodePolyId` is **static** — it reads the constants, not the mesh params, so passing our `maxPolys` does not reconfigure it. |

The failure mode is exactly what risk #2 predicts. A reference meaning *(salt 1, tile 5, poly 3)* in
our data decodes under stock DotRecast as *(salt 16, tile 10240, poly 3)* — a valid-looking triple
pointing at the wrong tile, with no error raised anywhere.

**Consequences for Phase 8:**

- Fork DotRecast (zlib) and change the three constants in `DtDetour`. That is the whole fix for the
  reference layout.
- **Separately**, DotRecast reads recast4j's own serialisation format, not AzerothCore's raw
  `dtMeshHeader` + struct blob. A `DtMeshData` reader for the C++ layout is needed whichever Detour
  we use. `NavMeshFile` already parses the header half of it.
- Still unproven: an actual path. The spike settled *which* Detour to get; comparing a real
  `.mmap path` against the C++ server needs the fork and the reader first, and remains Phase 8's
  exit criterion.

Either way `mmaps_generator` stays a native binary — we are not regenerating navmeshes.

### 3.5 Prior art exists and should be read

[CypherCore](https://github.com/CypherCore/CypherCore) is a working C# WoW server (retail-era);
[a WotLK fork](https://github.com/RioMcBoo/CypherCoreClassicWOTLK) and
[ForgedCore](https://github.com/ForgedWoW/ForgedCore) exist. **Use it for C#-shaped problems**
(opcode tables, update-field accessors, packet writers, script registration). **Use AzerothCore as
the behavior source of truth** — it is far more mature for 3.3.5a content.

---

## 4. Target architecture

### 4.1 Solution layout

```
WowEmu.sln
src/
  WowEmu.Core/            primitives: ObjectGuid, Position, time, RNG, TaskScheduler, EventProcessor
  WowEmu.Cryptography/    SRP6, RC4, HMAC/SHA1, AES-GCM, SessionKeyGenerator, argon2
  WowEmu.Network/         Pipelines socket host, framing, header crypt, per-connection scheduler
  WowEmu.Protocol/        Opcodes enum, PacketReader/Writer (ref structs), packed GUID/XYZ/time
  WowEmu.Data.Client/     .dbc, .map, .vmtree/.vmtile, .mmap/.mmtile readers
  WowEmu.Data.Db/         EF Core model + repositories (auth / characters / world)
  WowEmu.Data.Import/     AzerothCore MySQL → WowEmu schema importer
  WowEmu.Game/            entities, maps, grids, movement, combat, spells, AI
  WowEmu.Scripting/       script host: attribute discovery, hook dispatch, AssemblyLoadContext
  WowEmu.Scripts/         content scripts (grows forever)
  WowEmu.AuthServer/      host process
  WowEmu.WorldServer/     host process
tools/
  WowEmu.Extractor/       (optional, late) MPQ → dbc/maps/vmaps/mmaps
tests/
  WowEmu.Tests.Unit/      vectors: SRP6, RC4, packed GUID, attack table, formulas
  WowEmu.Tests.Protocol/  golden-packet comparison against captures
  WowEmu.Tests.Integration/ docker MySQL + headless client harness
```

### 4.2 Threading model — decided up front

```
┌─ Accept loop (1 task)
├─ N network tasks         Pipelines read → decrypt header → parse → enqueue to session
├─ 1 world tick loop       World.Update(diff): sessions (THREADUNSAFE), managers, timers
└─ M map worker tasks      Map.Update(diff): entities, spells, movement, THREADSAFE packets
```

Rules, copied deliberately from the C++:

1. A `WorldObject` is only ever touched on its map's worker task.
2. Cross-map/session work goes through queues drained at a known tick point.
3. DB results resolve on a **tick-bound scheduler**, never on the raw thread pool.
4. `PROCESS_INPLACE` / `THREADUNSAFE` / `THREADSAFE` per-opcode classification is **kept verbatim** —
   it is the contract that makes rule 1 hold. There is not a single mutex in `Entities/`, `Spells/`,
   `Combat/`, `Movement/` or `AI/`; this classification is the *entire* safety story. Do not "just add
   locks" — there is no lock ordering to inherit, and you would have to re-audit all 433 handlers.

Implement rule 3 as a custom `TaskScheduler` (or `SynchronizationContext`) owned by the world loop and
by each map, with a **bounded drain budget** so a callback storm can't blow the tick. Then
`await CharacterDb.QueryAsync(...)` resumes on the right thread and upstream's 4-hop
character-creation callback chain (`CharacterHandler.cpp:393-621`) collapses to linear code. Get this
right in Phase 0; retrofitting it is a rewrite. Enforce it with an analyzer banning bare `Task.Run` in
gameplay code.

**One attempted improvement over upstream — withdrawn.** The C++ pushes all inbound packets into a
single per-session `LockedQueue` that is drained twice per tick from two different threads with
complementary filters — and `LockedQueue::next()` puts a rejected item *back at the front*, so one
`THREADUNSAFE` packet head-of-line-blocks that session on the map thread. The plan was to **classify
at enqueue time and push into two queues** (`_worldThreadQueue`, `_mapThreadQueue`), on the grounds
that this had identical semantics without the blocking.

**It does not have identical semantics: two queues lose arrival order.** The world loop drains before
the map workers, so a `THREADUNSAFE` packet that arrived *after* a `THREADSAFE` one is handled
*before* it. A client sending movement and then a logout has its logout processed first, and the
character is saved at the position it held before it moved. This was built, and the M3 gate caught it
writing the stale position to the database.

What is implemented instead: **one queue, and each loop takes from the front for as long as the
packets belong to it, stopping at the first that does not** (`InboundPackets`). That is upstream's
ordering guarantee with no requeue idiom to emulate, and the head-of-line wait is bounded by a single
tick because the world loop drains before the map workers on every tick. The head-of-line blocking
upstream was criticised for is the mechanism that makes it correct.

### 4.3 Non-obvious mappings

| C++ pattern | C# |
|---|---|
| `MPSCQueueIntrusive` (outbound packets) | `Channel.CreateUnbounded<T>(SingleReader = true)` |
| `LockedQueue<WorldPacket*>` (inbound) | `lock` + `LinkedList<T>` — **head-of-line requeue is load-bearing**, `Channel` can't push to front |
| `pEffect SpellEffects[165]` member-fn table | `Action<Spell, SpellEffIndex>[165]`, built by **source generator** from `[SpellEffect(...)]` |
| `pAuraEffectHandler AuraEffectHandler[317]` | same, from `[AuraEffect(...)]` |
| Opcode table of 1,313 templated handlers | `OpcodeHandler?[1313]`, source-generated from `[Opcode(...)]` |
| `flag96` (3×uint32 spell family mask) | `readonly struct Flag96` with the same operators |
| bitfield structs (`TargetInfo`, `MoveSplineFlag`) | plain fields; nothing serializes the packing |
| `union FacingInfo` | discriminated `readonly struct` — the flags already say which member is live |
| CRTP `MovementGeneratorMedium<T,D>` | `abstract class MovementGenerator<TOwner> where TOwner : Unit` |
| `std::function` iterator generators | `IEnumerable<T>` + `yield return` |
| `ASSERT` / `ABORT` | `Debug.Assert` for invariants; real exceptions where the message matters |

### 4.4 Decouple from `Player` on day one

`Player` is 35,785 LOC across 8 translation units and 796 methods. Feature modules reference it
constantly — but the *coupling is wildly uneven*: 17 of the 25 feature directories have fewer than 30
`Player*` references, while Chat/Guilds/Groups/LFG/Battlegrounds have 80–780.

**Define an `IPlayerContext` interface before writing `Player`**, exposing only what features actually
need (guid, name, level, class, race, team, money, inventory ops, aura ops). Feature modules take
`IPlayerContext`. This lets the 17 leaf features be built and unit-tested before `Player` exists, and
keeps `Player` from becoming an untestable god-object by gravity.

Write `Player` as a `partial class` split across the same file boundaries as the C++ (`Player`,
`PlayerStorage`, `PlayerUpdates`, `PlayerQuest`, …) so upstream stays diffable.

**Convert `LoadFromDB`'s positional 75-column read to named columns immediately.** Upstream documents
that contract only in a comment. It is the single highest-probability silent-corruption site in the
whole port.

### 4.5 Performance targets

| Metric | Target | Note |
|---|---|---|
| World tick | p99 < 50 ms at 1k players | Upstream floor is `MinWorldUpdateTime = 1 ms`; there is **no fixed 50 ms tick** in this tree |
| Map update | p99 per-map < 10 ms | `MapUpdateInterval = 10 ms`, 4-phase round-robin — **out-of-phase maps get `t_diff == 0`, i.e. 3 of every 4 ticks are a session-only pass.** Don't mistake that for a skipped tick |
| GC | Zero Gen2 in steady state | Server GC; pool packet buffers via `ArrayPool<byte>` |
| Allocation | < 64 KB/tick at 1k players | Hot paths: update-object build, movement broadcast, threat sort, aura iteration, grid visit, packet write |
| Player memory | 5,304 B values + 336 B mask | Replace upstream's byte-per-field `UpdateMask` with `ulong[42]` + `BitOperations` — saves ~1.3 KB/player. **Keep `AppendToPacket`'s exact 32-field little-endian block layout.** |
| Startup | ≤ C++ wall time | Only parallelize loaders after building the dependency graph — the load order has real constraints (e.g. `gossip_menu` + all 13 `*_loot_template` + `SpellInfo` must precede `ConditionMgr`, which pushes conditions *into* already-loaded objects) |

Two allocation traps to avoid inheriting: `ByteBuffer::append`'s 400 KB reserve for anything over
6 KB (do **not** replicate), and `creature_template` being stored in an array sized by max entry
**3,460,603** (27.7 MB of mostly-null refs — use a dictionary or two-level page table).

---

## 5. Data strategy

### 5.1 Client data — reuse byte-for-byte

We consume the **same extracted files** AzerothCore produces. Initially, **run the C++ extractors
once** (`src/tools/map_extractor`, `vmap4_extractor`, `vmap4_assembler`, `mmaps_generator`) against a
retail 3.3.5a client and keep the output. Porting the extractors is a Phase-13+ luxury, not a
prerequisite.

Formats we must read (all parseable with `BinaryReader`/spans):

- **`.dbc`** — WDBC magic `0x43424457`, 20-byte header, format-string-driven field offsets
  (`b`/`X` advance 1 byte, others 4); 109 stores (`DBCStores.cpp`).
- **`.map`** — magic `MAPS`, **version 9**, 44-byte `map_fileheader` (11 × uint32: magic, version,
  build, then area/height/liquid/holes offset+size pairs), `GridTerrainData.h:59-72`. Heights are
  packed uint8 when Δ < 2.0, uint16 when Δ < 2048, else float.
- **`.vmtree` / `.vmtile`** — magic `VMAP_4.8` (8 bytes, no NUL), BIH tree + `ModelSpawn` placements.
  A BIH node is one packed uint32 — axis in bits 31-30 (3 = leaf), a single-child flag in bit 29,
  offset in bits 0-28, so the offset mask is `~(7<<29)` and **not** `~(3<<30)`. The node array is
  **not an array of nodes**: a node occupies *three* words, the descriptor plus two split planes
  stored as floats, and its children sit at `offset` and `offset + 3`. Scanning the array linearly
  decodes those floats as descriptors and yields offsets in the hundreds of millions — nodes can
  only be enumerated by traversing from the root.
- **`.mmap` / `.mmtile`** — 28-byte raw `dtNavMeshParams`, then per tile a 56-byte `MmapTileHeader`
  (`MMAP_MAGIC 0x4d4d4150`, `MMAP_VERSION 20`) + Detour tile. See §3.4.1.

**Three traps that produce silent, error-free wrongness:**

0. **A VMAP model name's NUL terminator is significant.** `ModelSpawn::readFromFile` builds the name
   as `std::string(nameBuff, nameLen)`, and `nameLen` counts a terminator about half the time —
   54.5 % with, 45.5 % without, mixed inside a single tile. Upstream then does
   `readFile(basepath + name + ".vmo")` and hands it to `fopen`, which stops at the first NUL — so a
   terminated name opens the **bare** file and never sees the `.vmo`. The extractor writes both
   forms to match: on map 0, every unterminated name has a `.vmo`, and 18,278 spawns whose name is
   terminated have *only* the bare file. Trimming the NUL and appending `.vmo` — the obvious reading
   — silently fails to find 8 % of doodads. See `ModelSpawn.ModelFileName`.

1. **Filename axis.** `.map`, `.vmtile` and `.mmtile` all encode **tileY before tileX**. The extractor
   writes `(mapId, adtY, adtX)`; the server reads `(mapId, gridX, gridY)` — i.e. **`gridX` is the ADT
   row**. Get this backwards and the world is mirrored across the diagonal, with no error anywhere.
2. **`spell_dbc` carries 4,491 custom spells.** A DBC-only reader silently misses every one. The DBC
   files are a *base*; ~110 `*_dbc` MySQL tables overlay them.
3. **DBC locale back-fill only fills slots that are null or empty**, iterating locales 0..8 in order,
   and one missing per-locale file clears that locale bit for **all subsequent stores**.

Liquid offsets are X/Y-swapped at sample time (`cx = (intX & 127) - liquidOffY`). Not a bug — do not
"fix" it.

### 5.2 Database — new schema, imported

Three databases, as before, but ours:

- **`auth`** — small (22 tables upstream → ~8 for us). Design fresh. Store the SRP6 salt+verifier and
  session key exactly as 32/32/40 bytes.
- **`characters`** — ours to own. Design fresh, properly typed and FK'd, added to incrementally as
  each phase needs persistence.
- **`world`** — **stay close to AzerothCore's shape**. This is 309 tables of community-curated content
  data; diverging structurally means re-curating it. Clean the types and names, keep the columns and
  semantics. Import per-table, on demand, as phases need them.

`WowEmu.Data.Import` reads AzerothCore MySQL directly and writes ours. It is incremental by design —
Phase 4 imports ~10 tables, Phase 10 imports ~40 more.

**Where the source SQL already is.** Both reference trees are checked out locally and hold every
schema and every row we will need; nothing has to be downloaded again. They are raw folders with no
git root, so treat them as read-only snapshots.

| Path | Files | Contents |
|---|---|---|
| `azerothcore-wotlk/data/sql/base/db_auth/` | 22 | Auth schema. Ours is designed fresh instead (see above); useful only as a comparison |
| `azerothcore-wotlk/data/sql/base/db_characters/` | 108 | **Characters schema** — the reference for the schema we design in Phase 5 |
| `azerothcore-wotlk/data/sql/base/db_world/` | 309 | World schema + content (296M) |
| `database-wotlk/sql/base/` | 155 | World schema + content (193M): `DROP TABLE`, `CREATE TABLE` and `INSERT`s per table |
| `database-wotlk/sql/updates/` | 5 | Incremental migrations layered on the base dump |

`database-wotlk` is **world data only** — it has no `characters` tables. Phase 5's persistence work
takes its reference from `db_characters/`, not from there.

Because these are plain `.sql` dumps rather than a live server, the importer has a choice: load a
dump into a throwaway MySQL container and read it with the planned `MySqlDataReader` path, or parse
the `INSERT` statements directly. The container route is preferable — it reuses the access path we
need anyway and does not require writing a SQL parser for someone else's dialect quirks.

**Vendoring rule.** Nothing reads `database-wotlk/` at runtime. Any table the server needs is
first moved into `sql/world/` and committed, with a header recording what reads it — see
`sql/README.md`. The reference checkout is gitignored and 193 MB, so a fresh clone does not have
it; a server that depends on it does not start. `tools/db/export-world.sh` does the move by loading
the upstream dump and dumping it back out of the live database, so what is committed is what the
server actually runs against rather than a file assumed to be equivalent.

**Access pattern**: EF Core for `auth` and `characters` (write-heavy, relational, ORM earns its
keep). Dapper or raw `MySqlDataReader` for `world` (read-once bulk load at startup — startup time is
a real metric; upstream loads 309 tables in tens of seconds).

Note upstream's `?` positional placeholders don't work in MySqlConnector — but since we're writing new
SQL, this is a non-issue for us.

---

## 6. Roadmap

Each phase has a **hard exit criterion** that is externally observable. Do not move on without it.

### Milestone gates

| Gate | Meaning | Reached at |
|---|---|---|
| **M1** | Real client shows our realm in the realm list | end of Phase 1 |
| **M2** | Client reaches the character-selection screen | end of Phase 3 |
| **M3** | Character enters the world and sees terrain | end of Phase 5 |
| **M4** | Player moves; two clients see each other; creatures visible | end of Phase 7 |
| **M5** | Kill a mob with autoattack + a spell, gain XP, level up | end of Phase 9 |
| **M6** | Complete a starting-zone quest chain | end of Phase 10 |
| **M7** | SmartAI creatures behave; a scripted boss runs | end of Phase 11 |

---

### Phase 0 — Foundations
*No client involvement. Everything here is tested by unit tests.*

> **Status:** steps 1 and 6 done — solution scaffolding and `WowEmu.Cryptography` (RC4, SRP6,
> AuthCrypt header encryption, SessionKeyGenerator). 55 tests green against an independent Python
> reference in `tools/vectors/`; three deliberate mutations confirmed the tests catch the
> leading-zero, endianness and HMAC-argument-order traps. Remaining: Core primitives, SFMT,
> protocol reader/writer, tick-bound scheduler, config and logging.

1. Solution skeleton, `Directory.Build.props`, nullable + `TreatWarningsAsErrors`, CI.
2. `WowEmu.Core`: `ObjectGuid` (uint64: high bits 48-63, entry bits 24-47, counter bits 0-23),
   `Position`, `WorldLocation`, monotonic clock (`GameTime` seconds + `getMSTime()` uint32
   **with wraparound arithmetic preserved**).
3. **Hand-port SFMT-19937** (~250 LOC; params `POS1=122, SL1=18, SL2=1, SR1=11, SR2=1`) plus
   `urand`/`irand`/`frand`/`rand_norm`/`roll_chance_*` matching libstdc++'s draw counts. **Not for
   replay** — upstream seeds from `std::random_device` and is already non-reproducible. It's for
   **differential testing**: with a fixed seed you can run both servers against the same trace and
   diff loot rolls and melee outcomes. Expose a seed config option the C++ side doesn't have.
   `urand` is **inclusive on both ends** — the attack table depends on 10001 outcomes.
4. **The tick-bound scheduler** (§4.2 rule 3). Write it now.
5. `WowEmu.Protocol`: `PacketReader`/`PacketWriter` ref structs over spans; packed GUID (mask byte +
   non-zero LE bytes); `appendPackXYZ` (3 × 11-bit signed offsets in a uint32); packed time.
6. `WowEmu.Cryptography`: SHA1, HMAC-SHA1, RC4 (+drop1024), AES-128-GCM, `SessionKeyGenerator<SHA1>`,
   SRP6.
7. Config system (ini + env override), `Microsoft.Extensions.Logging` with source-generated messages.

**Exit:** unit tests pass for SRP6 (known vectors **plus 1,000 random `S` including leading-zero
cases**), RC4-drop1024 keystream vs OpenSSL, packed GUID round-trip, `getMSTimeDiff` wraparound, and
SFMT matching `sfmt_genrand_uint32` for a fixed `init_by_array` seed.

**Traps** (each has cost someone a week):
- `SHA1Interleave` skips *leading zero bytes* of `S` with an odd/even correction. Naive even/odd split
  is wrong ~1 login in 256 — an intermittent, near-undebuggable failure.
- Every SRP6 value must be **fixed-width 32 bytes**. `BigInteger.ToByteArray()` is variable-length and
  may append a sign byte.
- `HMAC_SHA1(constant, sessionKey)`: constant is the **key**, session key is the **message**.

---

### Phase 1 — Authserver → **M1**

Implement the 5 auth opcodes and realmlist against an in-memory account store first, then the DB.

- `LOGON_CHALLENGE` in: 35-byte packed struct, `size - 30 == I_len` invariant, `os`/`country` arrive
  byte-reversed.
- `LOGON_CHALLENGE` out (success): 119 bytes — `B[32]`, `g_len=1`, `g=7`, `N_len=32`, `N[32]`,
  `s[32]`, `VersionChallenge[16] = BA A3 1E 99 A0 0B 21 57 FC 37 3F B3 69 CD D2 F1`, securityFlags.
- `LOGON_PROOF` in 75 bytes / out 32 bytes. **Bad password answers `WOW_FAIL_UNKNOWN_ACCOUNT (0x04)`,
  never `INCORRECT_PASSWORD`.** Do not "fix" this.
- Reconnect flow (`0x02`/`0x03`).
- `REALM_LIST`: header `0x10` + `uint16 payloadSize` + `uint32 0` + `uint16 realmCount`, then per
  realm `Type, lock, flags, CString name, CString "ip:port", float pop, uint8 charCount, uint8 tz,
  uint8 realmId`; trailer `0x10, 0x00`.

**Exit (M1):** a real 3.3.5a client with `realmlist.wtf` pointing at us logs in and displays the realm.

**Traps:**
- Account name is uppercased with `Utf8ToUpperOnlyLatin`, **not** `ToUpperInvariant` — the verifier was
  computed over that exact transform.
- The realm string is `boost::lexical_cast<tcp::endpoint>` output; `IPEndPoint.ToString()` matches for
  v4 and v6 but **verify, don't assume**.
- Build gating is data-driven: `build_info` must contain a row for 12340 or every login fails.

---

### Phase 2 — Database layer + schema

- Design and create `auth` and a minimal `characters` schema.
- `WowEmu.Data.Import`: AzerothCore `auth` → ours (accounts, realmlist).
- Wire Phase 1 to the real DB through the tick-bound scheduler.
- Docker Compose for MySQL; migrations via EF Core.

**Exit:** M1 still holds, backed by MySQL. Account creation CLI works. Re-running the importer is
idempotent.

---

### Phase 3 — Worldserver socket + session → **M2**

- Pipelines-based framing: 6-byte client header (**big-endian size**, little-endian cmd), 4/5-byte
  server header. Partial headers across reads must resume correctly.
- `SMSG_AUTH_CHALLENGE (0x1EC)` → `CMSG_AUTH_SESSION (0x1ED)` → digest verify → `_authCrypt.Init` →
  `SMSG_AUTH_RESPONSE (0x1EE)`.
- Digest = `SHA1(accountName || {0,0,0,0} || clientChallenge[4] || authSeed[4] || sessionKey[40])`.
- Addon blob: zlib (RFC1950), cap `0xFFFFF`; reply `SMSG_ADDON_INFO (0x2EF)` — the 256-byte Blizzard
  RSA key is **opaque data, copy it byte for byte**.
- Opcode table (source-generated), session status + processing classification.
- `CMSG_CHAR_ENUM (0x037)` → `SMSG_CHAR_ENUM (0x03B)` returning an empty list.
- `CMSG_PING (0x1DC)` / `SMSG_PONG`, `SMSG_CLIENTCACHE_VERSION`, `SMSG_TUTORIAL_FLAGS`,
  `SMSG_ACCOUNT_DATA_TIMES`.

**Exit (M2):** client passes the loading bar and reaches character selection showing "no characters".

**Traps:**
- Encrypt **only** the header. The RC4 streams are **stateful and continuous across packets** — you
  cannot skip encrypting one packet once initialized.
- `_authCrypt.Init` runs **before** the digest is verified, deliberately — the client cannot read the
  error response without it. Reordering "correctly" breaks error reporting.
- `CMSG_PING` and `CMSG_AUTH_SESSION` are handled at the **socket** layer, not via the opcode table.

---

### Phase 4 — Static game data

- **DBC loader**: header + format-string driven, all 109 stores, locale-aware string block.
- **`.map` terrain loader**: area (16×16 uint16), height (float / uint16 / uint8 variants, V9 129×129 +
  V8 128×128), liquid, holes. Height lookup + liquid queries.
- **World DB import + load**: start with `creature_template`, `creature`, `gameobject_template`,
  `gameobject`, `item_template`, `quest_template`, `npc_text`, `creature_template_addon`,
  `spawn_group*`. Cache into typed dictionaries.
- Character creation static data: `ChrRaces`, `ChrClasses`, `CharStartOutfit`, `playercreateinfo*`.

**Exit:** server starts, loads all DBCs and the world subset, reports counts and timing. Startup
under ~30 s. A `.map` height query at a known coordinate matches the C++ server's `.gps` output.

---

### Phase 5 — Object model + entering the world → **M3**

- `Object` → `WorldObject` → `Unit` → `Player` / `Creature`; `GameObject`; `Item`/`Bag`
  (`Item` is an `Object` but **not** a `WorldObject`).
- Update-field storage: `uint[]` + byte-per-field dirty mask, typed accessors, generated field-index
  constants (`OBJECT_END 6, ITEM_END 64, CONTAINER_END 138, UNIT_END 148, PLAYER_END 1326,
  GAMEOBJECT_END 18, DYNAMICOBJECT_END 12, CORPSE_END 36`).
- `UpdateData`/`UpdateMask` → `SMSG_UPDATE_OBJECT (0x0A9)`, compressed to `0x1F6` above 100 bytes.
- Character create / delete / enum against `characters`.
- `CMSG_PLAYER_LOGIN (0x03D)` → the login packet burst: `SMSG_LOGIN_VERIFY_WORLD`,
  `SMSG_ACCOUNT_DATA_TIMES`, `SMSG_FEATURE_SYSTEM_STATUS`, `SMSG_MOTD`, `SMSG_LEARNED_DANCE_MOVES`,
  initial-packets-before-add-to-map, add to map, initial-packets-after-add-to-map,
  `SMSG_TIME_SYNC_REQ`.
- Player save/load (start narrow: `characters`, `character_homebind`, `character_spell`,
  `character_action`, position).

**Exit (M3):** a created character logs in, the world renders around them, the character stands at the
correct starting position with correct model, name, level and stats. Log out and back in preserves it.

---

### Phase 6 — Maps, grids, visibility

- `Map` owning a 64×64 `MapGrid` array (`SIZE_OF_GRIDS 533.3333`), 8×8 `GridCell` per grid
  (`SIZE_OF_GRID_CELL 66.6667`). Note the inverted axis: `gx = 32 - x/533.3333`.
- `Cell` as a packed struct; visitor traversal over per-cell typed containers.
- Grid creation (terrain) vs grid object loading (DB spawns) as **two separate steps**, matching this
  fork.
- Spawn loading: creatures, gameobjects, corpses per grid.
- Visibility: per-object visible-players map, relocation notifiers, delayed
  `NOTIFY_VISIBILITY_CHANGED` execution, 400 yd "far visible" second pass.
- `MapUpdater` worker pool; `MapMgr` 4-phase round-robin (continents / BG+arena / dungeons / idle).
- Instance/BG map subclasses (structure only, no content).

**Exit:** player sees creatures and gameobjects appear/disappear at correct visibility ranges while
walking. Two clients see each other. Grid load counts are sane.

---

### Phase 7 — Movement → **M4**

- Ingress: 27 movement opcodes → `HandleMovementOpcodes`, all `STATUS_LOGGEDIN` + `PROCESS_THREADSAFE`.
- `MovementInfo` wire format (`WorldSession.cpp:1078`) and the **12 flag-sanitization rules** — most
  importantly, `MOVEMENTFLAG_ROOT` is stripped from *every* client packet unconditionally.
- Rebroadcast to visibility set excluding the sender.
- Speeds: `{2.5 walk, 7.0 run, 4.5 runback, 4.722222 swim, 2.5 swimback, 3.141594 turn, 7.0 fly,
  4.5 flyback, 3.14 pitch}`; the `SMSG_FORCE_*_SPEED_CHANGE` ↔ ack handshake.
- Splines: `MoveSpline`, Catmull-Rom evaluation, `SMSG_MONSTER_MOVE` encoding (including the two
  different cyclic encodings for flying vs ground).
- `MotionMaster` slot stack + Idle/Random/Waypoint/Chase/Follow/Point/Home/Confused/Fleeing generators.
- Time sync (`SMSG_TIME_SYNC_REQ 0x390`, clock-delta filtering).

**Exit (M4):** player runs, jumps, swims; a second client sees it smoothly. Creatures wander with
`RandomMovementGenerator` and return home.

**Traps:**
- `initializers[ModeLinear]` is deliberately `InitCatmullRom` — `InitLinear` is dead code.
- `Spline::lengths[]` holds cumulative **time in milliseconds**, despite the name.
- `MoveSplineInit::Launch()` **overwrites `path[0]`** with the unit's current position.
- Speed-ack mismatch is asymmetric: server-higher silently corrects, server-**lower kicks the player**.
  Float rounding differences produce spurious kicks (comparison is `fabs(diff) > 0.01f`).

---

### Phase 8 — Collision + pathfinding

- **First task: the Detour compatibility spike from §3.4.1.** Do not write anything else in this phase
  until a real `.mmtile` loads and produces a path matching the C++ server's `.mmap path` output.
- VMAP: `.vmtree`/`.vmtile`, BIH traversal, `ModelInstance` transforms, `IsInLineOfSight`.
- `DynamicMapTree` for gameobject-based LoS, rebalanced every 200 ms.
- `PathGenerator`: `MAX_PATH_LENGTH 74`, the **(y, z, x) Detour coordinate swizzle**, `findSmoothPath`
  with `SMOOTH_PATH_STEP_SIZE 4.0` / `SLOP 0.3`, 80 % corridor reuse, the `result[1] += 0.5f` height
  bump, custom cost function (`dist * (1 + slopeDeg/100) * areaCost`).
- `NavTerrain` filter flags; per-map `dtNavMeshQuery` (**not thread-safe** — one per map).

**Exit:** creatures path around obstacles instead of walking through walls; LoS blocks spells through
terrain; a `.mmap` path between two known points matches the C++ server's.

---

### Phase 9 — Combat + spells → **M5**

The largest single phase. Split it:

**9a — Melee combat**
- Attack table: **one** `urand(0, 10000)` compared against a running sum in exact order
  MISS → DODGE → PARRY → BLOCK → GLANCING → CRUSHING → CRIT → NORMAL.
- `skillBonus = 4 * (attackerWeaponSkill - victimMaxSkill)`; behind-target rules (dodge skipped only
  for *player* victims from behind; parry and block skipped for *all* victims from behind).
- Glancing (≤40 %), crushing (≥15 %, ×1.5 damage), parry haste, armor mitigation
  (`tmp = 0.1*armor/(8.5*levelMod + 40)`, clamped to 0.75), diminishing returns per class.
- `SMSG_ATTACKERSTATEUPDATE` — note each sub-damage is written as **both a float and a uint32**; the
  packet is variable-length in three independent ways.

**9b — Threat & combat state**
- `ThreatManager` heap ordering, the 110 %/130 % victim-reselection rules, taunt state incrementing,
  suppression (a suppressed ref does **not** auto-recover), redirection (Misdirection/Tricks/Vigilance).

**9c — Spell core**
- `SpellInfo` from `Spell.dbc` + 16 `spell_*` tables + the 729 `ApplySpellFix` corrections
  (`SpellInfoCorrections.cpp`, 5,427 lines). Build mutable, then freeze.
- `Spell` cast pipeline: `prepare → cast → TakePower/TakeReagents → HandleLaunchPhase → SendSpellGo →
  handle_immediate → DoAllEffectOnTarget → DoSpellHitOnUnit → finish`, over 4 `SpellEffectHandleMode`
  phases.
- Effect dispatch table (165 slots, 122 real handlers) — implement **~25** to start: school damage,
  weapon damage, heal, apply aura, energize, trigger spell, teleport, summon.
- Aura system: `Aura` + `AuraApplication` + up to 3 `AuraEffect`; dispatch table (317 slots, 137 real
  handlers) — implement **~30** to start: stat mods, damage/healing mods, periodic damage/heal,
  stun/root/fear, speed mods.
- Target selection: 111-entry `SpellImplicitTargetInfo` static table.
- `SPELLMOD_*` player modifiers. **Use an ordered container** — upstream iterates an
  `unordered_set<SpellModifier*>` and is order-dependent on pointer hashing. This is a deliberate,
  documented behavior change on our side.
- Proc system (`spell_proc` table driven).

**9d — Progression**
- XP from kills, level-up, stat recalculation, `SMSG_LEVELUP_INFO`, spell learning on level.

**Exit (M5):** attack a mob with autoattack and one class ability, kill it, receive XP, level up, see
correct stats. Melee-outcome distribution over 10⁶ rolls matches the C++ server within 0.1 % for
several level/skill pairs; DoT tick counts and durations identical for ~50 sampled spells.

**Traps:**
- `Acore::AbsorbAuraOrderPred` is **not a strict weak ordering**. `std::sort` tolerates that silently;
  `List<T>.Sort` throws `InvalidOperationException`. Rewrite it as an integer rank function.
- The attack table mutates `tmp` inside the branch conditions (`(tmp -= skillBonus) > 0`). It happens
  to be harmless because each branch reassigns first. **Reproduce the structure, not a cleaned-up
  version.**
- `sittingVictim` is computed *before* the target is stood up, and forces a crit on any non-miss.
  Computing it later silently deletes the sit-crit rule.
- Threat: a *suppressed* reference does **not** auto-recover when the suppressing condition ends —
  only new threat, `TauntUpdate`, or an explicit re-evaluation restores it. This is deliberate.

---

### Phase 10 — Progression systems → **M6**

Inventory & items · loot (`*_loot_template`) · quests (accept/objective/complete/reward) ·
vendors · trainers · gossip · talents/glyphs · reputation · corpse/resurrect/graveyards ·
basic chat (say/yell/whisper) · `.commands` framework.

**Exit (M6):** roll a fresh character and complete the first 3–4 quests of a starting zone entirely —
accept, kill, loot, turn in, get gear, equip it.

---

### Phase 11 — AI + scripting → **M7**

- Script host: attribute-based discovery + source-generated registration, replacing 587 `AddSC_*()`
  functions and the CMake codegen. Hot reload via `AssemblyLoadContext` (design for it, defer it).
- `UnitAI` → `CreatureAI` → `ScriptedAI` → `BossAI`; `EventMap`, `TaskScheduler`, `SummonList`.
- **SmartAI interpreter** — 93 events / 179 actions / 37 targets over the `smart_scripts` table. This
  is the highest-leverage item in the whole plan: it unlocks ~9,773 creature templates of behavior
  from data alone, with no C++ script porting.
- `ScriptMgr` hook dispatch — start with the ~20 hooks we actually use, not all 404.
- `SpellScript`/`AuraScript` binding via `spell_script_names`.
- Port 1–2 hand-written boss scripts as proof.

**Exit (M7):** SmartAI creatures patrol, aggro, cast and evade correctly. One hand-ported boss
completes its full encounter script.

---

### Phase 12+ — Breadth

Groups & raids · guilds · mail · auction house · battlegrounds & arenas · LFG · instances & lockouts ·
achievements · channels · social/friends · pets · vehicles · transports · weather · game events ·
calendar · tickets · Warden.

Order these by what you want to play. Each is largely independent once Phase 9–11 exist.

---

## 7. Risk register

| # | Risk | Impact | Mitigation |
|---|---|---|---|
| 1 | Async/await destroying the single-threaded-per-map invariant | Rare, non-reproducible corruption | Tick-bound scheduler in **Phase 0**; ban raw `Task.Run` in game code via analyzer |
| 2 | Vendored Detour is patched (`DT_POLYREF64`, 12/21/31 split) — stock DotRecast may misread every `.mmtile` | Forces a fork or P/Invoke | **Spike in Phase 8 day 1** (§3.4.1). Fork-three-constants is the likely fix |
| 3 | SRP6 byte-order / `SHA1Interleave` bug | Intermittent login failure, brutal to debug | Known-answer tests in Phase 0; test with a leading-zero `S` |
| 4 | Spell system scope explosion | Phase 9 never ends | Hard-cap at ~25 effect + ~30 aura handlers for M5; add on demand |
| 5 | Content scripts (336k lines) treated as in-scope | Project never ships | **Explicitly out of scope.** SmartAI covers the majority; port scripts individually, by demand |
| 6 | Update-field indices wrong | Client shows garbage or disconnects | Generate constants from `UpdateFields.h`; assert `PLAYER_END == 1326` |
| 7 | Packet-layout drift | Silent client desync | Golden-packet tests against captures from the C++ server (§9) |
| 8 | New DB schema diverging from world content | Re-curating 309 tables | Keep `world` structurally close; only clean types/names |
| 9 | Float determinism differences vs C++ | Spurious speed kicks, damage drift | Match formula *structure*, not "cleaned up" versions; tolerance tests |
| 10 | Fork-specific behavior (TC9Sidecar) copied blindly | **Unauthenticated server** | Cluster mode bypasses the *entire* auth block. Do not port it. |
| 11 | Exceptions as flow control in hot paths | DoS vector | `TryRead`-style span parsing with error flags, never exceptions |
| 12 | Startup time regression | Painful dev loop | Measure from Phase 4; budget < 30 s |
| 13 | Tile filename axis inverted (`gridX` == ADT **row**) | World mirrored across the diagonal, **no error raised** | Assert a known landmark's terrain height in Phase 4 |
| 14 | Reading `.dbc` without the `*_dbc` MySQL overlay | 4,491 custom spells silently missing | Overlay is part of the Phase 4 loader, not an optimization |
| 15 | C++ comparators that aren't strict weak orderings | `List<T>.Sort` throws where `std::sort` didn't | Convert every ported comparator to an integer rank function |

---

## 8. Explicitly NOT ported

| Upstream | Why | Replacement |
|---|---|---|
| `src/common/Threading/*`, `Dynamic/*`, most `Utilities/*` | BCL covers it | `Channel`, `Task`, `Interlocked`, LINQ |
| `MPSCQueueIntrusive` | Formal UB (placement-new into `aligned_storage`) | `Channel<T>` |
| `MySQLHacks.h`, `PreparedResultSet` internals | Reaches into `libmysql` structs | MySqlConnector / EF Core |
| `QueryCallback` union machinery | Poll-based futures | `await` on the tick-bound scheduler |
| DB updater shelling out to `mysql` CLI | Inherits a CLI runtime dependency | EF Core migrations |
| gSOAP / SOAP remote access | Obsolete | Minimal HTTP admin API, later |
| Warden | Anti-cheat, not needed to play | Keep the session key reachable; implement later if ever |
| TC9Sidecar cluster mode | Fork-specific; **bypasses all authentication** | — |
| jemalloc / gperftools | GC | — |
| 336,548 lines of content scripts | Volume | SmartAI (data-driven) + port on demand |
| CMake / PCH / codegen | — | SDK-style projects + source generators |

---

## 9. Verification strategy

**This is what makes the port tractable.** Run the C++ server as an oracle.

1. **Golden-packet tests.** AzerothCore has a built-in packet logger (`PacketLog.cpp`, PKT 3.1 format).
   Capture a full session against the C++ server, then replay our server's output for the same input
   and diff. Byte-exact for the handshake and update blocks; structurally exact elsewhere.
   *(Note: our zlib output will differ byte-wise from upstream's single-shot `deflate(Z_NO_FLUSH)` —
   still valid zlib, but exclude compressed bodies from byte-exact comparison.)*
2. **Side-by-side servers.** Run C++ AzerothCore and WowEmu against the same client data. Use `.gps`,
   `.damage`, `.mmap path` GM commands to compare terrain height, damage rolls and paths at identical
   coordinates.
3. **Seeded differential testing.** Because we hand-port SFMT (Phase 0), a fixed seed on both sides
   lets you diff loot rolls, melee outcomes and `LootGroup::Roll` results directly instead of arguing
   about distributions. Patch the C++ side to accept a seed — it's a two-line change — and this
   becomes the sharpest correctness tool in the project.
4. **Round-trip persistence diff.** Load a character written by the C++ server, save it from ours,
   and diff all ~25 `character_*` tables. Catches the positional-column class of bug (§4.4) instantly.
5. **Known-answer unit tests** for every formula lifted from C++: attack table distribution over 10⁶
   rolls, armor mitigation curve, diminishing returns per class, SRP6 vectors, spline evaluation.
6. **Integration harness**: Docker MySQL + a scripted headless client driving the M1–M7 gates. Each
   gate becomes a permanent regression test.
7. **Startup assertion suite**: DBC row counts, `PLAYER_END == 1326`, opcode table completeness,
   effect/aura handler table completeness (upstream does this with `ASSERT`s in `LoadSpellInfoStore`),
   and loaded-row counts matching the C++ server's startup log **exactly**, table by table.

---

## 10. Where to look in the C++ source

| Topic | Path |
|---|---|
| Auth protocol | `src/server/apps/authserver/Server/AuthSession.cpp` |
| SRP6 | `src/common/Cryptography/Authentication/SRP6.{h,cpp}` |
| Header crypto | `src/common/Cryptography/Authentication/AuthCrypt.cpp` |
| World handshake | `src/server/game/Server/WorldSocket.cpp` |
| Opcode table | `src/server/game/Server/Protocol/Opcodes.{h,cpp}` |
| Session dispatch | `src/server/game/Server/WorldSession.{h,cpp}` |
| World tick order | `src/server/game/World/World.cpp:1119` |
| Login sequence | `src/server/game/Handlers/CharacterHandler.cpp:686-1147` |
| Update fields | `src/server/game/Entities/Object/Updates/UpdateFields.h` |
| Object model | `src/server/game/Entities/Object/Object.{h,cpp}` |
| Grid/terrain | `src/server/game/Grids/`, `src/server/game/Maps/Map.cpp` |
| Binary formats | `src/common/Collision/Maps/MapDefines.h`, `src/server/game/Grids/GridTerrainData.h` |
| Spell pipeline | `src/server/game/Spells/Spell.cpp` |
| Effect table | `src/server/game/Spells/SpellEffects.cpp:70-237` |
| Aura table | `src/server/game/Spells/Auras/SpellAuraEffects.cpp:63-382` |
| Attack table | `Unit::RollMeleeOutcomeAgainst` in `src/server/game/Entities/Unit/Unit.cpp` |
| Movement ingress | `src/server/game/Handlers/MovementHandler.cpp`, `WorldSession.cpp:1078` |
| Pathfinding | `src/server/game/Movement/PathGenerator.cpp` |
| SmartAI | `src/server/game/AI/SmartScripts/SmartScript.cpp` |
| ScriptMgr | `src/server/game/Scripting/ScriptMgr.{h,cpp}` |
| Static data load order | `World::SetInitialWorldSettings` in `src/server/game/World/World.cpp` |

---

## 11. Immediate next steps

1. `dotnet new sln`, create the Phase 0 projects, wire CI.
2. Implement + test SRP6 and RC4 with known vectors.
3. Build the tick-bound scheduler.
4. Stand up Docker MySQL and get a retail 3.3.5a client + extracted data staged locally.
5. Start Phase 1 — target M1 (realm list) as the first visible win.
