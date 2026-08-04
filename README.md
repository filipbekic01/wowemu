# WowEmu

A .NET 10 / C# reimplementation of [AzerothCore](https://www.azerothcore.org/) — a World of Warcraft
3.3.5a (WotLK, client build 12340) server emulator.

See **[PLAN.md](PLAN.md)** for the full architecture, phased roadmap and risk register.
The C++ reference implementation lives in `azerothcore-wotlk/` and is the behavioral source of truth.

## Status

| Phase | Scope | State |
|---|---|---|
| 0 | Foundations — crypto, packet primitives, tick scheduler | **done** |
| 1 | Authserver → client reaches the realm list | **done — M1 reached** |
| 2 | Database layer + schema | **in progress** — auth schema done |
| 3+ | See [PLAN.md](PLAN.md) §6 | not started |

**M1 is verified**: a retail 3.3.5a client logs in and displays the realm, and
`tools/harness/m1_login.py` drives the same handshake headlessly as a regression gate.

**Phase 0** — every exit criterion in [PLAN.md](PLAN.md) §6 is met and covered by tests.

- [x] Solution skeleton, analyzers, warnings-as-errors
- [x] `WowEmu.Cryptography` — RC4, SRP6, AuthCrypt header encryption, SessionKeyGenerator
- [x] `WowEmu.Protocol` — `PacketReader`/`PacketWriter`, packed GUID / XYZ / time
- [x] `WowEmu.Core` — `ObjectGuid`, `Position`, `WorldLocation`, millisecond clock with wraparound
- [x] Tick-bound scheduler (`TickScheduler`) — PLAN §4.2 rule 3
- [x] SFMT-19937 + `urand` / `irand` / `frand` / `rand_norm` / roll helpers
- [x] Config and logging

One deliberate deviation: PLAN §6 step 7 calls for an ini config system. We use
`appsettings.json` plus environment overrides instead — the same layering, in the idiom the rest of
the .NET stack already speaks.

**Phase 2 progress**

- [x] `auth` schema — accounts (SRP6 salt/verifier/session key as binary), `realmlist`, `build_info`
- [x] EF Core model + repositories + migrations, MySQL in Docker Compose
- [x] Auth server reads accounts, realms and build gating from the database
- [x] Account CLI
- [ ] `characters` schema (Phase 3/5 will need it)
- [ ] `WowEmu.Data.Import` — AzerothCore `auth` → ours

## Layout

```
src/WowEmu.Core/           ObjectGuid, Position, clocks, tick scheduler, SFMT + distributions
src/WowEmu.Cryptography/   SRP6, RC4, header encryption, session key expansion
src/WowEmu.Protocol/       PacketReader / PacketWriter, packed GUID / XYZ / time
src/WowEmu.Data.Db/        EF Core model, repositories and migrations for the auth database
src/WowEmu.AuthServer/     The logon server (port 3724)
tools/WowEmu.AccountCli/   Account and realm maintenance
tools/harness/             Headless protocol clients that drive the milestone gates
tools/vectors/             Reference implementations + golden test vectors
tests/WowEmu.Tests.Unit/   xUnit tests
azerothcore-wotlk/         The C++ server being ported (reference only, not built)
```

## Build and test

```bash
dotnet build
dotnet test
```

Requires the .NET 10 SDK. The unit tests need nothing else — no database, WoW client or extracted
game data.

## Running the logon server

The auth server needs MySQL. Everything below assumes Docker is running.

```bash
docker compose up -d                                             # MySQL on 127.0.0.1:3306
dotnet run --project tools/WowEmu.AccountCli -- account create test
dotnet run --project src/WowEmu.AuthServer
```

The server applies pending migrations itself on startup, so the first run creates the schema. The
`account create` command prompts for the password rather than taking it on the command line, which
keeps it out of your shell history.

Verify it without launching a client:

```bash
python3 tools/harness/m1_login.py
```

Then point a 3.3.5a client at it by setting `SET realmlist "<host>"` in `WTF/Config.wtf` — note that
`Config.wtf` overrides `Data/<locale>/realmlist.wtf`, so editing only the latter does nothing.

The realm advertised after login is seeded as `127.0.0.1:8085`. If the client runs anywhere else —
a VM, another machine — point the realm at an address it can actually reach:

```bash
dotnet run --project tools/WowEmu.AccountCli -- realm set-address 1 192.168.1.10 8085
```

The running server picks that up on its next realm refresh; no restart needed.

**Configuration.** `src/WowEmu.AuthServer/appsettings.json` holds the bind address, port and
connection string. An empty connection string means the Docker Compose default; the
`WOWEMU_AUTH_CONNECTION` environment variable overrides everything and is also what the
`dotnet ef` tooling reads.

### In VS Code

`.vscode/tasks.json` wires up the common commands. Press <kbd>⌘⇧B</kbd> to build, or
<kbd>⌘⇧P</kbd> → *Tasks: Run Task* for the rest:

| Task | What it does |
|---|---|
| `build` | Default build task (<kbd>⌘⇧B</kbd>) |
| `rebuild` / `clean` | Full rebuild / remove output |
| `test` | Run all tests (default test task) |
| `test: filter` | Prompts for a name substring, e.g. `Srp6`, `Interleave` |
| `watch: test` | Re-runs tests on every save — leave it running while you work |
| `vectors: verify` | Checks the golden vectors still match what the tests assert |
| `vectors: regenerate` | Re-runs the Python crypto reference implementation |
| `vectors: regenerate SFMT` | Re-runs AzerothCore's C SFMT to regenerate its vectors |
| `vectors: regenerate distributions` | Re-runs SFMT + libstdc++ in a container (needs Docker) |
| `check` | rebuild → test → verify vectors. Run before committing. |
| `db: up` / `db: down` | Start / stop the MySQL container |
| `db: migrate` | Apply pending EF Core migrations |
| `account: create` / `account: list` | Account maintenance |
| `gate: M1` | Log in over the real protocol, headlessly. Needs the server running. |
| `Run: Auth Server` | Start the logon server on port 3724 |
| `Run: World Server` | **Phase 3** — no server project exists yet |

To debug a single test, use the run/debug icon in the gutter next to the test method, or the
Testing panel. `.vscode/launch.json` has server configurations pre-wired for Phase 1 and Phase 3.

## On the test vectors

Three sets, each generated by something other than our own code — a test that compares our C#
against our C# proves nothing about protocol correctness.

| File | Generated by | Covers |
|---|---|---|
| `vectors.json` | `generate_crypto_vectors.py` — an independent Python transcription | SRP6, RC4, AuthCrypt, SessionKeyGenerator |
| `sfmt_vectors.json` | `generate_sfmt_vectors.sh` — AzerothCore's own `deps/SFMT/SFMT.c`, compiled | The SFMT-19937 bit stream |
| `random_vectors.json` | `generate_random_vectors.sh` — SFMT + libstdc++ in a `gcc` container | `urand`, `irand`, `frand`, `rand_norm` |

`verify_vectors_in_tests.py` checks that every generated value still appears in the test sources,
so regenerating vectors without updating the tests is caught rather than silently passing.

### Why the distributions need their own vectors

AzerothCore does not roll its own distributions — it feeds SFMT into libstdc++'s
`uniform_int_distribution` and `uniform_real_distribution`. Those consume a *specific* number of
32-bit draws: the integer one rejection-samples (so the count varies per call), `frand` takes one
draw and `rand_norm` takes two. An implementation that gets the draw count wrong still produces
perfectly uniform numbers — and desynchronises the two servers' streams after the first roll,
which would quietly destroy the differential testing the SFMT port exists for.

The vectors are generated against **libstdc++ specifically**, inside a container, because libc++
(what clang uses on macOS) implements `uniform_int_distribution` differently. The generator has an
`#error` guard so it cannot be built against the wrong standard library by accident.

### The crypto vectors

The cryptography tests are checked against `tools/vectors/vectors.json`, generated by
`tools/vectors/generate_crypto_vectors.py` — an independent Python transcription of AzerothCore's
`SRP6.cpp` and `AuthCrypt.cpp`. Keeping the reference in a second language matters: a test that only
compares our C# against our C# proves nothing about protocol correctness.

The generator is deterministic (fixed RNG seed), so re-running it must produce a byte-identical
`vectors.json`. It self-checks its RC4 against the published RFC 6229 vectors before emitting
anything, and it implements *both* sides of SRP6 and asserts client and server derive the same
session key.

```bash
python3 tools/vectors/generate_crypto_vectors.py
```

### Why one vector is named `ZEROCASE`

`SHA1Interleave` skips leading zero bytes of the shared secret `S`, rounding the offset up to an even
index before halving. The obvious implementation — split `S` into even/odd bytes and hash both halves
whole — is correct about 255 times out of 256 and fails whenever `S` happens to begin with a zero
byte, producing an intermittent login failure that is very hard to trace.

`ZEROCASE` is a handshake constructed so that `S` starts with `0x00`, and
`Sha1Interleave_DiffersFromNaiveImplementation_WhenSHasLeadingZeroes` asserts that the correct answer
genuinely differs from the naive one. Anyone who "simplifies" that method gets a red test.

## Legal

This project reimplements a server for a game client it does not distribute. It contains no Blizzard
assets. You must own a legitimate copy of the client, and all game data (`.dbc`, `.map`, `.vmap`,
`.mmap`) is extracted locally and never committed.
