#!/usr/bin/env bash
#
# Runs BanditPlugin's end-to-end tests on a real dedicated server, unattended, and exits with
# whether they passed.
#
#   1. builds the plugin (Release - what the server has always run)
#   2. refuses to go on if the server is already running, because replacing a DLL Mono has mapped
#      corrupts the running server rather than updating it
#   3. deploys BanditPlugin.dll and 0Harmony.dll, each by rename rather than overwrite
#   4. starts the server with BANDIT_TEST set, so the plugin runs the tests once the level has
#      loaded, writes Tests/results.json, and shuts the server down
#   5. prints every result and exits 0 only if every test passed
#
# Usage:
#   tools/bandit-test.sh [-n] [-t SECONDS] [TESTS]
#
#   TESTS       all (default), one name, or several separated by commas: goto,cover
#   -n          skip the build and deploy, and test whatever is already installed
#   -t SECONDS  give up and kill the server after this long (default 600)
#
# The tests run at this map's arena points. Record one in game first, standing somewhere open with
# some cover nearby: /bandit test arena set

set -uo pipefail
export LC_ALL=C

REPO=$(cd "$(dirname "$0")/.." && pwd)
SERVER="$REPO/DedicatedServer"
INSTANCE=Bandit
PLUGINS="$SERVER/Servers/$INSTANCE/Rocket/Plugins"
LIBRARIES="$SERVER/Servers/$INSTANCE/Rocket/Libraries"
SERVER_LOG="$SERVER/Logs/Server_$INSTANCE.log"
RESULTS="$PLUGINS/BanditPlugin/Tests/results.json"
BUILD_OUT="$REPO/BanditPlugin/bin/Release"
STDOUT_LOG=$(mktemp -t bandit-test-server.XXXXXX.log)

PYTHON="$HOME/Desktop/Dev/global_venv/bin/python"
[[ -x $PYTHON ]] || PYTHON=python3

BUILD=1
TIMEOUT=600
TESTS=all

usage() {
    sed -n '/^# Usage:/,/^$/p' "$0" | sed 's/^# \{0,1\}//' >&2
    exit 2
}

while getopts ":nt:h" opt; do
    case $opt in
        n) BUILD=0 ;;
        t) TIMEOUT=$OPTARG ;;
        *) usage ;;
    esac
done
shift $((OPTIND - 1))
[[ $# -ge 1 ]] && TESTS=$1

server_pid() {
    pgrep -f "Unturned_Headless\.x86_64.*$INSTANCE" | head -1
}

if [[ -n $(server_pid) ]]; then
    echo "The $INSTANCE server is running (pid $(server_pid)). Stop it first - the tests start their own." >&2
    exit 2
fi

# Rename, never overwrite: a new inode leaves anything still holding the old file intact.
deploy() {
    local source=$1 directory=$2 name
    name=$(basename "$source")
    if cmp -s "$source" "$directory/$name"; then
        return
    fi
    cp "$source" "$directory/.$name.tmp" && mv -f "$directory/.$name.tmp" "$directory/$name"
    echo "deployed: $directory/$name"
}

if (( BUILD )); then
    echo "building..."
    if ! dotnet build "$REPO/BanditPlugin/BanditPlugin.csproj" -c Release -nologo -v quiet; then
        echo "Build failed." >&2
        exit 2
    fi
    deploy "$BUILD_OUT/BanditPlugin.dll" "$PLUGINS"
    deploy "$BUILD_OUT/0Harmony.dll" "$LIBRARIES"
fi

rm -f "$RESULTS"

# The server rotates its log as it starts, so last session's file is still in place for a moment
# after launch. Its inode is how the new one is told apart.
old_log_inode=$(stat -c %i "$SERVER_LOG" 2>/dev/null || echo none)

echo "starting the server with BANDIT_TEST=$TESTS (server stdout: $STDOUT_LOG)"
(
    cd "$SERVER" || exit 1
    BANDIT_TEST=$TESTS BANDIT_TEST_EXIT=1 exec ./ServerHelper.sh +LanServer/$INSTANCE
) >"$STDOUT_LOG" 2>&1 &
LAUNCHER=$!

started=$SECONDS
printed=0
finished=0

while :; do
    if [[ -f $SERVER_LOG && $(stat -c %i "$SERVER_LOG" 2>/dev/null) != "$old_log_inode" ]]; then
        mapfile -t lines < <(grep -a '\[BanditTest\]' "$SERVER_LOG")
        for (( i = printed; i < ${#lines[@]}; i++ )); do
            line=${lines[$i]#*\[BanditTest\] }
            echo "  $line"
            [[ $line == Finished:* ]] && finished=1
        done
        printed=${#lines[@]}
    fi

    if ! kill -0 "$LAUNCHER" 2>/dev/null && [[ -z $(server_pid) ]]; then
        break
    fi

    if (( SECONDS - started > TIMEOUT )); then
        echo "Timed out after ${TIMEOUT}s - killing the server." >&2
        pid=$(server_pid)
        [[ -n $pid ]] && kill "$pid"
        sleep 10
        pid=$(server_pid)
        [[ -n $pid ]] && kill -9 "$pid"
        break
    fi

    sleep 2
done

wait "$LAUNCHER" 2>/dev/null

if [[ ! -f $RESULTS ]]; then
    echo "No results were written." >&2
    (( finished )) || echo "The run never finished - the last lines of the server's output:" >&2
    tail -20 "$STDOUT_LOG" >&2
    exit 2
fi

"$PYTHON" -I - "$RESULTS" <<'EOF'
import json, sys

with open(sys.argv[1]) as f:
    data = json.load(f)

results = data["results"]
print()
print(f"map {data['map']}, {len(results)} test(s)")
for r in results:
    print(f"  {r['outcome']:<7} {r['name']:<10} {r['seconds']:>6.1f}s  {r['message']}")

if data.get("error"):
    print(f"error: {data['error']}")

failed = [r for r in results if r["outcome"] != "PASS"]
sys.exit(1 if failed or data.get("error") or not results else 0)
EOF
