#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 path/to/SignalForge.Desktop" >&2
  exit 2
fi

executable="$1"
if [[ ! -x "$executable" ]]; then
  echo "Published app is missing or not executable: $executable" >&2
  exit 2
fi

log_file="$(mktemp)"
"$executable" >"$log_file" 2>&1 &
app_pid=$!

cleanup() {
  if kill -0 "$app_pid" 2>/dev/null; then
    kill -TERM "$app_pid" 2>/dev/null || true
    for _ in {1..5}; do
      if ! kill -0 "$app_pid" 2>/dev/null; then
        break
      fi
      sleep 1
    done
    kill -KILL "$app_pid" 2>/dev/null || true
  fi
  wait "$app_pid" 2>/dev/null || true
  rm -f "$log_file"
}
trap cleanup EXIT

for _ in {1..12}; do
  if ! kill -0 "$app_pid" 2>/dev/null; then
    status=0
    wait "$app_pid" || status=$?
    cat "$log_file" >&2
    echo "Desktop process exited during startup with code $status." >&2
    exit 1
  fi
  sleep 1
done

echo "Desktop process remained running for 12 seconds."
