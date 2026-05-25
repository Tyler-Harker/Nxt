#!/usr/bin/env bash
#
# Benchmark Nxt vs Next.js across three scenarios:
#   - /static    prerendered HTML
#   - /ssr       per-request render of 100 items
#   - /api/items JSON endpoint
#
# Both apps in bench/{nxt,nextjs}/ expose the same routes and same data shape.
# This script: builds both → boots each in production → for each scenario
# measures cold-start (ms-to-first-200), throughput, latency, and peak RSS.
#
# Env knobs:
#   DURATION=20    seconds per scenario (default 20)
#   CONNECTIONS=50 concurrent connections (default 50)
#   NXT_PORT=5050  ports for the two apps
#   NEXT_PORT=3050
#   ONLY=nxt|nextjs  run only one app (default: both)
#
set -euo pipefail

DURATION="${DURATION:-20}"
CONNECTIONS="${CONNECTIONS:-50}"
NXT_PORT="${NXT_PORT:-5050}"
NEXT_PORT="${NEXT_PORT:-3050}"
ONLY="${ONLY:-}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BENCH="$REPO_ROOT/bench"
RESULTS_DIR="$BENCH/.results"
rm -rf "$RESULTS_DIR" && mkdir -p "$RESULTS_DIR"

# Kill any leftover bench processes from previous runs (so we don't accidentally
# load-test a stale process on the same port and report 12ms cold-starts).
pkill -f "Nxt.Bench.dll"        2>/dev/null || true
pkill -f "next-server.*--port"  2>/dev/null || true
pkill -f "node.*next/dist/bin"  2>/dev/null || true
sleep 1

# ── pick a load tool ─────────────────────────────────────────────────────────
TOOL=""
for t in oha wrk hey bombardier; do
    command -v "$t" >/dev/null 2>&1 && TOOL="$t" && break
done
if [[ -z "$TOOL" ]]; then
    cat >&2 <<'EOF'
✗ No HTTP load generator on PATH. Install ONE of:
    oha (recommended): cargo install oha   OR   brew install oha
                       OR   curl -L https://github.com/hatoo/oha/releases/latest/download/oha-linux-amd64 -o ~/.local/bin/oha && chmod +x ~/.local/bin/oha
    wrk              : apt-get install wrk   OR   brew install wrk
    hey              : go install github.com/rakyll/hey@latest
    bombardier       : go install github.com/codesenberg/bombardier@latest
EOF
    exit 1
fi
echo "→ load tool: $TOOL"

# ── build both apps ──────────────────────────────────────────────────────────
build_nxt() {
    echo "→ building bench/nxt …"
    rm -rf "$BENCH/nxt/publish"
    # `nxt build` operates on cwd (it just runs dotnet publish), so cd in.
    ( cd "$BENCH/nxt" && "$REPO_ROOT/nxt" build --output ./publish ) \
        > "$RESULTS_DIR/nxt-build.log" 2>&1
}
build_nextjs() {
    echo "→ building bench/nextjs …"
    cd "$BENCH/nextjs"
    if [[ ! -d node_modules ]]; then
        echo "  installing npm deps (one-time) …"
        npm install --silent > "$RESULTS_DIR/nextjs-install.log" 2>&1
    fi
    NEXT_TELEMETRY_DISABLED=1 npx --yes next build > "$RESULTS_DIR/nextjs-build.log" 2>&1
    cd "$REPO_ROOT"
}

[[ "$ONLY" != "nextjs" ]] && build_nxt
[[ "$ONLY" != "nxt" ]] && build_nextjs

# ── helpers ──────────────────────────────────────────────────────────────────
# Spawn a command in the background, poll until the URL returns 200, print
# "PID MS" where MS is ms-to-first-200. Caller is responsible for killing PID.
start_and_measure() {
    local ready_url="$1" logfile="$2"; shift 2
    local t0
    t0=$(date +%s%N)
    "$@" > "$logfile" 2>&1 &
    local pid=$!
    local i
    for i in $(seq 1 600); do
        if curl -fsS -o /dev/null --max-time 1 "$ready_url" 2>/dev/null; then
            local t1
            t1=$(date +%s%N)
            echo "$pid $(( (t1 - t0) / 1000000 ))"
            return 0
        fi
        sleep 0.05
        kill -0 "$pid" 2>/dev/null || { echo "$pid CRASHED"; return 1; }
    done
    kill "$pid" 2>/dev/null || true
    echo "$pid TIMEOUT"
    return 1
}

stop_app() {
    local pid=$1
    # Walk the full process tree (npx → next-start → node, or just dotnet) and
    # SIGTERM every descendant before the root, so children don't get re-parented
    # to init when we kill the wrapper.
    local tids
    tids=$(pstree -p "$pid" 2>/dev/null | grep -oE '\([0-9]+\)' | tr -d '()' | tac)
    local t
    for t in $tids; do kill "$t" 2>/dev/null || true; done
    kill "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true
}

# Sample RSS of PID + all descendant PROCESSES (not threads) every 200ms for $2
# seconds; print peak KB. pstree -p returns both processes AND threads — threads
# have no VmRSS of their own, so we filter via /proc/$tid/status::Tgid (a tid
# whose Tgid != tid is a thread; skip it).
peak_rss_kb() {
    local pid=$1 secs=$2
    local peak=0 end
    end=$(( $(date +%s) + secs ))
    while [[ $(date +%s) -lt $end ]]; do
        kill -0 "$pid" 2>/dev/null || break
        local total=0 tids tid tgid rss
        tids=$(pstree -p "$pid" 2>/dev/null | grep -oE '\([0-9]+\)' | tr -d '()' || echo "$pid")
        for tid in $tids; do
            [[ -r "/proc/$tid/status" ]] || continue
            tgid=$(awk '/^Tgid:/{print $2; exit}' "/proc/$tid/status" 2>/dev/null)
            [[ "$tgid" == "$tid" ]] || continue   # skip threads
            rss=$(awk '/^VmRSS:/{print $2; exit}' "/proc/$tid/status" 2>/dev/null)
            [[ -n "$rss" ]] && total=$(( total + rss ))
        done
        (( total > peak )) && peak=$total
        sleep 0.2
    done
    echo "$peak"
}

# Run the load tool against URL for $DURATION seconds, parse req/s + p99.
# Echoes "REQ_PER_SEC P99_MS" (best-effort across tools; "?" if unparseable).
run_load() {
    local url=$1 out="$RESULTS_DIR/load-$(basename "$0").$$"
    case "$TOOL" in
    oha)
        oha -z "${DURATION}s" -c "$CONNECTIONS" --no-tui "$url" \
            > "$out" 2>&1 || true
        local rps p99
        rps=$(grep -E "Requests/sec:" "$out" | awk '{print $2}')
        # oha calls its percentile block "Response time distribution"
        p99=$(grep -E '^\s*99\.00%' "$out" | head -1 | awk '{print $3 " " $4}')
        echo "${rps:-?} ${p99:-?}"
        ;;
    wrk)
        wrk -t 4 -c "$CONNECTIONS" -d "${DURATION}s" --latency "$url" > "$out" 2>&1 || true
        local rps p99
        rps=$(grep "Requests/sec:" "$out" | awk '{print $2}')
        p99=$(grep -E '^\s*99%' "$out" | head -1 | awk '{print $2}')
        echo "${rps:-?} ${p99:-?}"
        ;;
    hey)
        hey -z "${DURATION}s" -c "$CONNECTIONS" "$url" > "$out" 2>&1 || true
        local rps p99
        rps=$(grep "Requests/sec:" "$out" | awk '{print $2}')
        p99=$(awk '/Latency distribution:/,/Status code distribution:/' "$out" \
              | grep -E '\s99%' | head -1 | awk '{print $4 " " $5}')
        echo "${rps:-?} ${p99:-?}"
        ;;
    bombardier)
        bombardier -c "$CONNECTIONS" -d "${DURATION}s" -l --no-print "$url" \
            > "$out" 2>&1 || true
        local rps p99
        rps=$(grep -E 'Reqs/sec' "$out" | awk '{print $2}')
        p99=$(grep -E '\s+99%' "$out" | head -1 | awk '{print $2}')
        echo "${rps:-?} ${p99:-?}"
        ;;
    esac
}

# ── scenarios ────────────────────────────────────────────────────────────────
# (path, friendly-name) pairs
SCENARIOS=(
    "/static:static"
    "/ssr:ssr"
    "/api/items:api"
)

declare -A R     # R[app-scenario-rps], R[app-scenario-p99], R[app-scenario-rss], R[app-cold]

run_app() {
    local app=$1 port=$2 cmd=("${@:3}")
    local base="http://localhost:$port"

    echo
    echo "──────── $app (port $port) ────────"
    local out pid cold
    out=$(start_and_measure "$base/" "$RESULTS_DIR/$app.log" "${cmd[@]}")
    pid=$(echo "$out" | awk '{print $1}')
    cold=$(echo "$out" | awk '{print $2}')
    R[$app-cold]=$cold
    echo "  cold start: ${cold}ms"

    # brief warmup
    local i
    for i in $(seq 1 100); do curl -fsS -o /dev/null "$base/" 2>/dev/null || true; done

    local pair path name
    for pair in "${SCENARIOS[@]}"; do
        path=${pair%%:*}
        name=${pair##*:}
        echo "  $name: $base$path"
        # sample peak RSS during load test in background
        ( peak_rss_kb "$pid" "$((DURATION + 5))" > "$RESULTS_DIR/rss-$app-$name" ) &
        local rss_pid=$!
        local res
        res=$(run_load "$base$path")
        wait "$rss_pid" 2>/dev/null || true
        R[$app-$name-rps]=$(echo "$res" | awk '{print $1}')
        R[$app-$name-p99]=$(echo "$res" | cut -d' ' -f2-)
        R[$app-$name-rss]=$(cat "$RESULTS_DIR/rss-$app-$name" 2>/dev/null || echo "?")
        echo "    req/s: ${R[$app-$name-rps]}   p99: ${R[$app-$name-p99]}   peak RSS: $(( ${R[$app-$name-rss]:-0} / 1024 ))MB"
    done

    stop_app "$pid"
}

# Suppress per-request access logs — at 90k rps these balloon the captured
# stdout to multiple GB per run (which then refuses to push to GitHub).
export Logging__LogLevel__Default=Warning
export Logging__LogLevel__Microsoft__AspNetCore=Warning
export NEXT_TELEMETRY_DISABLED=1

[[ "$ONLY" != "nextjs" ]] && run_app nxt    "$NXT_PORT"  dotnet "$BENCH/nxt/publish/Nxt.Bench.dll" --urls "http://localhost:$NXT_PORT"
# Invoke the binary directly — `npx --yes next` ignores the local node_modules
# when invoked from outside the project dir and downloads the network-latest
# (which broke our SSR page on next@16).
[[ "$ONLY" != "nxt"    ]] && run_app nextjs "$NEXT_PORT" "$BENCH/nextjs/node_modules/.bin/next" start "$BENCH/nextjs" -p "$NEXT_PORT"

# ── pretty-printing helpers ─────────────────────────────────────────────────
# Insert thousands separators into an integer ("37556" → "37,556"). POSIX sed.
commas() {
    [[ "$1" =~ ^[0-9]+$ ]] || { echo "$1"; return; }
    echo "$1" | sed -e :a -e 's/\(.*[0-9]\)\([0-9]\{3\}\)/\1,\2/;ta'
}
# Round a float to N decimals; passes through "?" unchanged.
roundn() {
    local v=$1 n=$2
    [[ -z "$v" || "$v" == "?" ]] && { echo "?"; return; }
    awk -v v="$v" -v n="$n" 'BEGIN{printf "%.*f\n", n, v}'
}
# Format req/s: round to int, add commas.
fmt_rps() {
    local v=$1
    [[ -z "$v" || "$v" == "?" ]] && { echo "?"; return; }
    commas "$(awk -v v="$v" 'BEGIN{printf "%.0f\n", v}')"
}
# Format p99: input may be "6.6991 ms" or "?"; round value, keep unit.
fmt_p99() {
    local v=$1
    [[ -z "$v" || "$v" == "?" ]] && { echo "?"; return; }
    local num unit
    num=${v%% *}; unit=${v#* }
    echo "$(roundn "$num" 1) $unit"
}
# Compute "Nxt / Next.js" speedup ratio for req/s. Uses ASCII "x" instead of
# "×" so printf's byte-counting right-alignment doesn't drift across rows.
speedup() {
    local a=$1 b=$2
    [[ "$a" == "?" || "$b" == "?" || -z "$a" || -z "$b" ]] && { echo "-"; return; }
    awk -v a="$a" -v b="$b" 'BEGIN{ if(b>0) printf "%.1fx\n", a/b; else print "-" }'
}

# ── markdown comparison tables ──────────────────────────────────────────────
# Two tables (one for cold start, one per-scenario side-by-side) — much easier
# to read than the original "long-form" table where apps were interleaved with
# blank cold-start cells. Both tables use fixed-width padding so the markdown
# source is also legible in a terminal (not just in a rendered viewer).
echo
echo "## Results"
echo "_load tool: $TOOL — ${DURATION}s @ ${CONNECTIONS} connections_"
echo

{
    # ── cold start ───
    printf "### Cold start\n\n"
    printf "| App    | ms   |\n"
    printf "|--------|-----:|\n"
    for app in nxt nextjs; do
        [[ "$ONLY" == "nxt"    && "$app" == "nextjs" ]] && continue
        [[ "$ONLY" == "nextjs" && "$app" == "nxt"    ]] && continue
        printf "| %-6s | %4s |\n" "$app" "${R[$app-cold]:-?}"
    done
    echo

    # ── per-scenario comparison ───
    printf "### Throughput / latency / memory\n\n"
    if [[ -z "$ONLY" ]]; then
        # Both apps → side-by-side comparison with speedup column.
        printf "| Scenario | Nxt req/s | Next.js req/s | Speedup | Nxt p99   | Next.js p99 | Nxt RSS | Next.js RSS |\n"
        printf "|----------|----------:|--------------:|--------:|----------:|------------:|--------:|------------:|\n"
        for pair in "${SCENARIOS[@]}"; do
            name=${pair##*:}
            nxt_rps_raw=${R[nxt-$name-rps]:-?}
            nextjs_rps_raw=${R[nextjs-$name-rps]:-?}
            nxt_rps=$(fmt_rps "$nxt_rps_raw")
            nextjs_rps=$(fmt_rps "$nextjs_rps_raw")
            sp=$(speedup "$nxt_rps_raw" "$nextjs_rps_raw")
            nxt_p99=$(fmt_p99 "${R[nxt-$name-p99]:-?}")
            nextjs_p99=$(fmt_p99 "${R[nextjs-$name-p99]:-?}")
            nxt_rss_kb=${R[nxt-$name-rss]:-0}
            nextjs_rss_kb=${R[nextjs-$name-rss]:-0}
            nxt_rss_mb="?"; nextjs_rss_mb="?"
            [[ "$nxt_rss_kb"    =~ ^[0-9]+$ ]] && nxt_rss_mb="$(( nxt_rss_kb    / 1024 )) MB"
            [[ "$nextjs_rss_kb" =~ ^[0-9]+$ ]] && nextjs_rss_mb="$(( nextjs_rss_kb / 1024 )) MB"
            printf "| %-8s | %9s | %13s | %7s | %9s | %11s | %7s | %11s |\n" \
                "$name" "$nxt_rps" "$nextjs_rps" "$sp" \
                "$nxt_p99" "$nextjs_p99" "$nxt_rss_mb" "$nextjs_rss_mb"
        done
    else
        # Single app mode → simpler 4-column table.
        printf "| Scenario | Req/s | p99 | Peak RSS |\n"
        printf "|----------|------:|----:|---------:|\n"
        for pair in "${SCENARIOS[@]}"; do
            name=${pair##*:}
            rps=$(fmt_rps "${R[$ONLY-$name-rps]:-?}")
            p99=$(fmt_p99 "${R[$ONLY-$name-p99]:-?}")
            rss_kb=${R[$ONLY-$name-rss]:-0}
            rss_mb="?"
            [[ "$rss_kb" =~ ^[0-9]+$ ]] && rss_mb="$(( rss_kb / 1024 )) MB"
            printf "| %-8s | %5s | %5s | %8s |\n" "$name" "$rps" "$p99" "$rss_mb"
        done
    fi
} | tee "$RESULTS_DIR/results.md"

echo
echo "Raw outputs in: $RESULTS_DIR"
