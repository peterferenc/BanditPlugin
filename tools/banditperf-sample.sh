#!/usr/bin/env bash
#
# Tier 3 of the bandit performance measurement: what the two Unturned processes and the GPU are
# really doing, sampled from outside them.
#
# This exists because /banditperf cannot answer the question that matters most. Two reasons:
#
#   1. The dedicated server sleeps to hit a target frame rate, so its frame time stops moving long
#      before it runs out of headroom. CPU% keeps moving all the way to the wall.
#   2. A frame rate drop noticed while standing in front of fifty bandits may be entirely the
#      client rendering fifty more player models. On this machine the client is a separate process
#      under Proton, and nothing running inside the server can see it.
#
# Sampling both processes plus the GPU separates those three costs, which is the whole point: if
# client CPU and GPU busy% move and server CPU does not, no amount of AI tuning will help.
#
# Usage:
#   tools/banditperf-sample.sh [-d SECONDS] [-i INTERVAL] LABEL
#
# e.g.  tools/banditperf-sample.sh -d 60 baseline-0-bandits
#       tools/banditperf-sample.sh -d 60 50-bandits-idle
#       tools/banditperf-sample.sh -d 60 50-bandits-shooting
#
# CPU% is per single core: 100 means one core saturated, and this box has several.

set -uo pipefail

# Forced, not inherited. Under a locale that writes decimals with a comma, awk emits "0,2" - which
# in a comma-separated file is not a number, it is two columns, and every field after it on the row
# shifts left. The summary at the bottom then averages the wrong columns and reports it confidently.
export LC_ALL=C

DURATION=60
INTERVAL=1
LABEL=""

usage() {
    echo "usage: $0 [-d SECONDS] [-i INTERVAL] LABEL" >&2
    exit 2
}

while getopts ":d:i:h" opt; do
    case $opt in
        d) DURATION=$OPTARG ;;
        i) INTERVAL=$OPTARG ;;
        h) usage ;;
        *) usage ;;
    esac
done
shift $((OPTIND - 1))

[[ $# -ge 1 ]] || usage
LABEL=$1

CLK_TCK=$(getconf CLK_TCK)
CORES=$(nproc)

# Resolved once. The card is not going to change mid-run, and globbing per sample would put a
# directory scan inside the measurement loop.
GPU_BUSY_FILE=""
GPU_VRAM_FILE=""
for candidate in /sys/class/drm/card*/device/gpu_busy_percent; do
    if [[ -r $candidate ]]; then
        GPU_BUSY_FILE=$candidate
        GPU_VRAM_FILE=${candidate%/gpu_busy_percent}/mem_info_vram_used
        break
    fi
done

# The process with the largest resident set among everything matching, because the patterns below
# also match the launcher shell and (for the client) Proton's various shims - all of which are tiny
# next to the game itself.
pick_pid() {
    local pattern=$1 best_pid="" best_rss=0 pid rss
    for pid in $(pgrep -f "$pattern" 2>/dev/null); do
        rss=$(awk '/^VmRSS:/{print $2}' "/proc/$pid/status" 2>/dev/null)
        [[ -z $rss ]] && continue
        if (( rss > best_rss )); then
            best_rss=$rss
            best_pid=$pid
        fi
    done
    printf '%s' "$best_pid"
}

# utime + stime, summed across every thread of the process. Parsed after the last ')' so a process
# name containing a space or a bracket cannot shift the field numbering.
cpu_ticks() {
    local pid=$1 stat rest
    [[ -z $pid ]] && return
    stat=$(cat "/proc/$pid/stat" 2>/dev/null) || return
    [[ -z $stat ]] && return
    rest=${stat##*) }
    # shellcheck disable=SC2086
    set -- $rest
    # Braced deliberately: $12 parses as $1 followed by a literal 2, which yields the process state
    # letter with a 2 stuck to it rather than utime.
    printf '%s' $(( ${12} + ${13} ))
}

rss_mb() {
    local pid=$1 kb
    [[ -z $pid ]] && return
    kb=$(awk '/^VmRSS:/{print $2}' "/proc/$pid/status" 2>/dev/null)
    [[ -z $kb ]] && return
    awk -v k="$kb" 'BEGIN { printf "%.1f", k / 1024 }'
}

pct() {
    local before=$1 after=$2 elapsed=$3
    [[ -z $before || -z $after ]] && return
    awk -v a="$before" -v b="$after" -v el="$elapsed" -v hz="$CLK_TCK" \
        'BEGIN { if (el > 0) printf "%.1f", (b - a) / hz / el * 100 }'
}

read_file_or_blank() {
    [[ -n $1 && -r $1 ]] && cat "$1" 2>/dev/null || true
}

SERVER_PID=$(pick_pid 'Unturned_Headless\.x86_64')
CLIENT_PID=$(pick_pid 'Unturned\.exe')

# Never silently overwrite a previous run - these are measurements, and a lost baseline costs
# another sixty seconds of standing still in game to retake.
OUT="perf-${LABEL}.csv"
suffix=2
while [[ -e $OUT ]]; do
    OUT="perf-${LABEL}-${suffix}.csv"
    suffix=$((suffix + 1))
done

echo "label:    $LABEL"
echo "server:   ${SERVER_PID:-NOT RUNNING} (Unturned_Headless.x86_64)"
echo "client:   ${CLIENT_PID:-NOT RUNNING} (Unturned.exe under Proton)"
echo "gpu:      ${GPU_BUSY_FILE:-unavailable}"
echo "sampling: ${DURATION}s at ${INTERVAL}s, ${CORES} cores, CPU% is of one core"
echo "output:   $OUT"
echo

if [[ -z $SERVER_PID && -z $CLIENT_PID ]]; then
    echo "Neither process is running - nothing to sample." >&2
    exit 1
fi

echo "t_s,server_cpu_pct,server_rss_mb,client_cpu_pct,client_rss_mb,gpu_busy_pct,vram_mb" > "$OUT"

start_s=$(date +%s.%N)
prev_s=$start_s
prev_server=$(cpu_ticks "$SERVER_PID")
prev_client=$(cpu_ticks "$CLIENT_PID")

while :; do
    sleep "$INTERVAL"

    now_s=$(date +%s.%N)
    cur_server=$(cpu_ticks "$SERVER_PID")
    cur_client=$(cpu_ticks "$CLIENT_PID")

    elapsed=$(awk -v a="$prev_s" -v b="$now_s" 'BEGIN { printf "%.4f", b - a }')
    total=$(awk -v a="$start_s" -v b="$now_s" 'BEGIN { printf "%.1f", b - a }')

    server_pct=$(pct "$prev_server" "$cur_server" "$elapsed")
    client_pct=$(pct "$prev_client" "$cur_client" "$elapsed")
    server_rss=$(rss_mb "$SERVER_PID")
    client_rss=$(rss_mb "$CLIENT_PID")
    gpu_busy=$(read_file_or_blank "$GPU_BUSY_FILE")
    vram=$(read_file_or_blank "$GPU_VRAM_FILE")
    [[ -n $vram ]] && vram=$(awk -v b="$vram" 'BEGIN { printf "%.1f", b / 1048576 }')

    echo "$total,$server_pct,$server_rss,$client_pct,$client_rss,$gpu_busy,$vram" >> "$OUT"
    printf '\r  %6ss  server %6s%%  client %6s%%  gpu %3s%%   ' \
        "$total" "${server_pct:--}" "${client_pct:--}" "${gpu_busy:--}"

    prev_s=$now_s
    prev_server=$cur_server
    prev_client=$cur_client

    done_yet=$(awk -v t="$total" -v d="$DURATION" 'BEGIN { print (t >= d) ? 1 : 0 }')
    [[ $done_yet -eq 1 ]] && break
done

printf '\r%*s\r' 60 ''
echo "--- $LABEL ---"

# Mean and max per column. The max columns are not decoration: a client that averages 60% but peaks
# at 100% is a client that stutters, and the mean alone would call that healthy.
awk -F, 'NR > 1 {
    for (i = 2; i <= 7; i++) {
        if ($i != "") { sum[i] += $i; n[i]++; if ($i > max[i]) max[i] = $i }
    }
}
END {
    split("server_cpu% server_rss_mb client_cpu% client_rss_mb gpu_busy% vram_mb", name, " ")
    for (i = 2; i <= 7; i++) {
        if (n[i] > 0) printf "  %-14s mean %8.1f   max %8.1f\n", name[i-1], sum[i]/n[i], max[i]
        else          printf "  %-14s (no samples)\n", name[i-1]
    }
}' "$OUT"

echo
echo "Saved to $OUT"
