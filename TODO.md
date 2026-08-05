# TODO

**Moved to [`web/`](web/).**

The checklist now lives in [`web/todo.json`](web/todo.json) — one file, structured, with every
phase, item, note and known gap. [`web/index.html`](web/index.html) renders it as a progress board.

```bash
cd web && python3 -m http.server 8000
# then open http://localhost:8000
```

A browser will not `fetch` a sibling file over `file://`, so it has to be served rather than opened
straight off disk. The page says so if you try.

**Edit `web/todo.json`, not this file.** The board follows it.

[PLAN.md](PLAN.md) is still the architecture and the *why*; the board is the *what*.
