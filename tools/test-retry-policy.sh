#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source <(sed -n '/^function run_bridge_with_retries() {$/,/^}$/p' "$repo_root/ism7mqtt/run.sh")

test_dir="$(mktemp -d)"
trap 'rm -rf -- "$test_dir"' EXIT

run_case() {
    local device="$1"
    local exit_code="$2"
    local trace="$test_dir/$device.trace"
    local output="$test_dir/$device.log"

    (
        export ISM7_HOMEASSISTANT_ID="$device"
        export ISM7_IP=192.0.2.1
        sleep_count=0

        ts() { cat; }
        fake_bridge() {
            [[ "$#" == 1 && "$1" == "argument with spaces" ]] || exit 99
            echo attempt >> "$trace"
            return "$exit_code"
        }
        sleep() {
            echo "sleep $1" >> "$trace"
            sleep_count=$((sleep_count + 1))
            if (( sleep_count == 8 )); then
                exit 0
            fi
        }

        run_bridge_with_retries fake_bridge "argument with spaces"
    ) > "$output" 2>&1

    diff -u "$test_dir/expected.trace" "$trace"
    for retry in 1 2 3; do
        [[ "$(grep -Fc "ISM7 $device (192.0.2.1): retry $retry/3 in 10 seconds." "$output")" == 2 ]]
    done
    [[ "$(grep -Fc 'waiting 5 minutes before trying again.' "$output")" == 2 ]]
    if (( exit_code != 0 )); then
        [[ "$(grep -Fc "unexpectedly quit with return code $exit_code" "$output")" == 8 ]]
    elif grep -Fq 'unexpectedly quit' "$output"; then
        echo "Unexpected error log for a zero exit status." >&2
        return 1
    fi
}

cat > "$test_dir/expected.trace" <<'EOF'
attempt
sleep 10
attempt
sleep 10
attempt
sleep 10
attempt
sleep 300
attempt
sleep 10
attempt
sleep 10
attempt
sleep 10
attempt
sleep 300
EOF

# A protocol failure can also end the bridge with exit status zero.
run_case normal-exit 0
run_case failed-exit 1
run_case aborted-exit 134

run_case primary 1 &
primary_pid=$!
run_case additional 0 &
additional_pid=$!
wait "$primary_pid"
wait "$additional_pid"

echo "Retry policy tests passed."
