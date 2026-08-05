# WowEmu

A .NET 10 / C# reimplementation of [AzerothCore](https://www.azerothcore.org/) — a World of Warcraft
3.3.5a (WotLK, client build 12340) server emulator.

The C++ reference implementation lives in `azerothcore-wotlk/` and is the behavioural source of truth.

## Where everything is

**Progress, status, setup and known gaps have moved to [`web/`](web/).**

[`web/todo.json`](web/todo.json) is the single source of truth for what is done, what is next, and
every stub and silent gap worth remembering. [`web/index.html`](web/index.html) renders it as a
progress board — phases, milestones, filters, search.

```bash
cd web && python3 -m http.server 8000
# then open http://localhost:8000
```

It has to be served rather than opened off disk: a browser will not `fetch` a sibling file over
`file://`. The page tells you so if you try.

**Edit `web/todo.json`, not this file.**

[PLAN.md](PLAN.md) remains the architecture, the phased roadmap and the risk register — the *why*.
The board is the *what*.

## Quick start

```bash
dotnet build && dotnet test          # needs only the .NET 10 SDK
docker compose up -d                 # MySQL on 127.0.0.1:3306
tools/db/import-world.sh             # vendored world content
dotnet run --project tools/WowEmu.AccountCli -- account create test
dotnet run --project src/WowEmu.AuthServer     # port 3724
dotnet run --project src/WowEmu.WorldServer    # port 8085
```

Verify without a client — three headless gates that speak the real protocol:

```bash
python3 tools/harness/m1_login.py     # logon, realm list, reconnect
python3 tools/harness/m2_world.py     # world handshake, character create/list/delete
python3 tools/harness/m5_combat.py    # walk to a mob, kill it, gain experience
```

Then point a 3.3.5a client at it with `SET realmlist "<host>"` in `WTF/Config.wtf` — note that
`Config.wtf` overrides `Data/<locale>/realmlist.wtf`, so editing only the latter does nothing.

The full setup, the layout, where each database comes from, and the reasoning behind the test
vectors are all on the board.

## Legal

This project reimplements a server for a game client it does not distribute. It contains no Blizzard
assets. You must own a legitimate copy of the client, and all game data (`.dbc`, `.map`, `.vmap`,
`.mmap`) is extracted locally and never committed.
