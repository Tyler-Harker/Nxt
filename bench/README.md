# bench — Nxt vs Next.js

Side-by-side throughput, latency, cold-start, and memory comparison across three scenarios both frameworks support natively.

## Scenarios

| Path | What | Both frameworks |
|---|---|---|
| `/static` | A prerendered HTML page with no per-request work | **Nxt**: `@nxt:static` directive; cached in memory after `nxt build`. **Next.js**: no dynamic fetches; baked to disk at `next build` and served by its static handler. |
| `/ssr` | Render a 100-item list per request | **Nxt**: default render mode (SSR). **Next.js**: `export const dynamic = "force-dynamic"` to opt out of static rendering. |
| `/api/items` | Return the same JSON payload (100 items) | **Nxt**: a `Get` method on a class in an `api/` folder. **Next.js**: `app/api/items/route.js` with `GET` handler, also `force-dynamic`. |

Both apps share the same data shape (`{ id, name, price }` × 100) and the same wrapper HTML. No styling, no JS, no DB — the goal is to isolate framework overhead, not measure how fast a SELECT runs.

## Layout

```
bench/
├── nxt/                          # Nxt app — ProjectReference to ../../src/Nxt.Runtime
│   ├── Program.cs                # one line + ItemStore registration
│   ├── Pages/{Index,Static,Ssr}.razor
│   ├── api/Items.cs
│   └── Data/Item.cs
├── nextjs/                       # Next.js 15 App Router (JSX, no TS)
│   ├── app/{layout,page}.jsx
│   ├── app/static/page.jsx
│   ├── app/ssr/page.jsx
│   ├── app/api/items/route.js
│   └── lib/items.js
├── run.sh                        # orchestrator
└── README.md
```

## Running

You need:

- **.NET 10 SDK** (already required for the repo)
- **Node.js 18+**
- **One** of `oha` / `wrk` / `hey` / `bombardier` on PATH (the script picks whichever it finds)

```bash
# from the repo root
./bench/run.sh
```

First run takes longer because it installs Next.js npm deps and prerenders the Nxt static page. Subsequent runs reuse `node_modules/` and just rebuild.

### Knobs

```bash
DURATION=30 CONNECTIONS=100 ./bench/run.sh   # longer + heavier
ONLY=nxt    ./bench/run.sh                   # only one app
NXT_PORT=5099 NEXT_PORT=3099 ./bench/run.sh  # override ports
```

## What the script measures

For each `(app, scenario)`:

- **Cold start** — milliseconds from process spawn to first HTTP 200 on `/`. Captured once per app (not per scenario). Includes JIT/bootstrap, NOT the prerender cost (that happened at build time).
- **Throughput** — requests/sec sustained over `$DURATION` seconds at `$CONNECTIONS` concurrent connections.
- **p99 latency** — read from the load tool's output (exact format varies by tool).
- **Peak RSS** — resident set size of the server process (and its children, for Next.js's worker) sampled every 200ms during the load test.

Results land in `bench/.results/results.md` plus raw per-test logs.

## Honest caveats

- **One machine, one run, one moment in time.** Numbers will vary across hardware, kernel versions, GC settings, Node/.NET minor versions, and ambient CPU load. Treat these as order-of-magnitude.
- **Cold-start is noisy.** First-run JIT compilation, filesystem cache warmth, and Next.js's `.next/cache` priming all affect it. Run a few times.
- **`peak_rss_kb` is a sampler**, not a tracer. It can miss spikes shorter than 200ms.
- **Hardware acceleration / async IO** differences matter at high RPS. .NET 10's Kestrel and Next.js's standalone-Node server both have their own optimizations; this bench measures end-to-end behavior, not which framework "wins" on theoretical max.
- **Identical work is hard.** Next.js's React renderer produces different HTML than Blazor's Razor renderer for the same data — payload sizes won't exactly match. We control for inputs, not outputs.

The bench is intentionally simple. If you want microbenchmarks, look at TechEmpower; if you want production realism, instrument your own app. This is for "given the same trivially-fair work, what does each framework's runtime cost look like."
