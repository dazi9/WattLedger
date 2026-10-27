#!/usr/bin/env bash
set -Eeuo pipefail

if [[ $# -lt 1 || $# -gt 2 ]]; then
  printf 'Usage: %s <published-desktop-executable> [published-cli-executable]\n' "${0##*/}" >&2
  exit 2
fi

desktop_binary="$(cd -- "$(dirname -- "$1")" && pwd -P)/$(basename -- "$1")"
if [[ ! -x "$desktop_binary" ]]; then
  printf 'Published desktop executable not found: %s\n' "$desktop_binary" >&2
  exit 1
fi
cli_binary=
if [[ $# -eq 2 ]]; then
  if [[ ! -x "$2" ]]; then
    printf 'Published CLI executable not found: %s\n' "$2" >&2
    exit 1
  fi
  cli_binary="$(cd -- "$(dirname -- "$2")" && pwd -P)/$(basename -- "$2")"
fi

for command_name in dbus-run-session python3 timeout xvfb-run xwininfo; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    printf 'Required smoke-test command not found: %s\n' "$command_name" >&2
    exit 1
  fi
done

smoke_dir="$(mktemp -d "${TMPDIR:-/tmp}/wattledger-ui-smoke.XXXXXXXX")"
trap 'rm -rf -- "$smoke_dir"' EXIT
install -d "$smoke_dir/config" "$smoke_dir/data" "$smoke_dir/cache" "$smoke_dir/state"
install -d -m 0700 "$smoke_dir/runtime"

set +e
timeout --signal=TERM --kill-after=2s 15s \
  xvfb-run -a dbus-run-session -- env \
    XDG_CONFIG_HOME="$smoke_dir/config" \
    XDG_DATA_HOME="$smoke_dir/data" \
    XDG_CACHE_HOME="$smoke_dir/cache" \
    XDG_STATE_HOME="$smoke_dir/state" \
    XDG_RUNTIME_DIR="$smoke_dir/runtime" \
    DOTNET_BUNDLE_EXTRACT_BASE_DIR="$smoke_dir/bundles" \
    WATTLEDGER_BACKEND=x11 \
    bash -c '
      set -Eeuo pipefail
      app_pid=
      cleanup() {
        if [[ -n "$app_pid" ]] && kill -0 "$app_pid" 2>/dev/null; then
          kill -TERM "$app_pid" 2>/dev/null || true
          for _ in {1..20}; do
            kill -0 "$app_pid" 2>/dev/null || break
            sleep 0.1
          done
          kill -KILL "$app_pid" 2>/dev/null || true
          wait "$app_pid" 2>/dev/null || true
        fi
      }
      trap cleanup EXIT

      "$1" --tray --db "$2" &
      app_pid=$!
      for _ in {1..100}; do
        if ! kill -0 "$app_pid" 2>/dev/null; then
          wait "$app_pid"
          printf "Desktop process exited before showing its window.\n" >&2
          exit 1
        fi
        if xwininfo -name WattLedger 2>/dev/null | grep -F "Map State: IsViewable" >/dev/null; then
          for _ in {1..30}; do
            sample_count="$(python3 -c "import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); print(c.execute(\"SELECT count(*) FROM samples\").fetchone()[0])" "$2" 2>/dev/null || true)"
            if [[ "$sample_count" =~ ^[0-9]+$ ]] && (( sample_count > 0 )); then
              printf "Desktop window appeared and wrote %s sample(s) to its isolated database.\n" "$sample_count"
              if [[ -n "$3" ]]; then
                if timeout --signal=TERM --kill-after=1s 5s "$3" status --db "$2"; then
                  printf "CLI status exited successfully against the active desktop database.\n"
                else
                  cli_status=$?
                  if [[ $cli_status -eq 124 || $cli_status -eq 137 ]]; then
                    printf "CLI status timed out after 5 seconds (status %s).\n" "$cli_status" >&2
                  else
                    printf "CLI status failed against the active desktop database (status %s).\n" "$cli_status" >&2
                  fi
                  exit 1
                fi
              fi
              if timeout --signal=TERM --kill-after=1s 5s "$1" --db "$2"; then
                :
              else
                second_status=$?
                printf "Second desktop launch failed or did not exit promptly (status %s).\n" "$second_status" >&2
                exit 1
              fi
              if ! kill -0 "$app_pid" 2>/dev/null; then
                printf "First desktop process exited after the second launch.\n" >&2
                exit 1
              fi
              if ! xwininfo -name WattLedger 2>/dev/null | grep -F "Map State: IsViewable" >/dev/null; then
                printf "First desktop window disappeared after the second launch.\n" >&2
                exit 1
              fi
              printf "Second launch exited cleanly; first instance and window remain active.\n"
              exit 0
            fi
            sleep 0.1
          done
          printf "Desktop window appeared but no sample was written to the isolated database.\n" >&2
          exit 1
        fi
        sleep 0.1
      done

      printf "Desktop process stayed alive but did not create a WattLedger window.\n" >&2
      exit 1
    ' _ "$desktop_binary" "$smoke_dir/data/wattledger.db" "$cli_binary"
status=$?
set -e

if [[ $status -ne 0 ]]; then
  printf 'Linux desktop startup smoke failed (status %s).\n' "$status" >&2
  exit "$status"
fi
