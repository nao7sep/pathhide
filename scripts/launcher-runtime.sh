#!/usr/bin/env bash
# Shared by the macOS launchers (run-dev, run-built, rebuild): one PathHide runtime per repository.
# A launcher claims ownership, stops any PathHide it or another launcher started, waits for the new one
# to appear, and releases ownership only if no later launcher has claimed it since.

launcher_owner_file() {
  printf '%s/.git/launcher-runtime-owner' "$1"
}

claim_launcher_runtime() {
  local token="$1" repo_dir="$2"
  local owner_file
  owner_file="$(launcher_owner_file "$repo_dir")"
  local temporary_file="${owner_file}.$$.$RANDOM"
  printf '%s\n' "$token" > "$temporary_file"
  mv -f "$temporary_file" "$owner_file"
}

is_launcher_runtime_owner() {
  local token="$1" repo_dir="$2"
  local owner_file
  owner_file="$(launcher_owner_file "$repo_dir")"
  [[ -f "$owner_file" && "$(tr -d '\r\n' < "$owner_file")" == "$token" ]]
}

release_launcher_runtime() {
  local token="$1" repo_dir="$2"
  local owner_file
  owner_file="$(launcher_owner_file "$repo_dir")"
  if is_launcher_runtime_owner "$token" "$repo_dir"; then
    rm -f "$owner_file"
  fi
}

runtime_field() {
  /usr/bin/plutil -extract "$2" raw -o - "$1/.git/launcher-runtime-child" 2>/dev/null
}

owned_runtime_pids() {
  local repo_dir="$1" pid recorded actual
  [[ "$(runtime_field "$repo_dir" state)" == running ]] || return 0
  pid="$(runtime_field "$repo_dir" pid)" || return 0
  [[ "$pid" =~ ^[0-9]+$ ]] || return 0
  recorded="$(runtime_field "$repo_dir" startLabel)" || return 0
  actual="$(LC_ALL=C TZ=UTC ps -p "$pid" -o lstart= | awk '{$1=$1; print}')"
  [[ -n "$actual" && "$actual" == "$recorded" ]] && printf '%s\n' "$pid"
  return 0
}

request_runtime_close() {
  /usr/bin/osascript -l JavaScript - "$1" <<'JXA'
ObjC.import('AppKit');
function run(args) {
  var application = $.NSRunningApplication.runningApplicationWithProcessIdentifier(Number(args[0]));
  if (application) application.terminate;
}
JXA
}

stop_owned_runtime() {
  local label="$1" repo_dir="$2" pid deadline
  if [[ -n "${6:-}" && "$(runtime_field "$repo_dir" token)" != "$6" ]]; then return 0; fi
  pid="$(owned_runtime_pids "$repo_dir")"
  [[ -z "$pid" ]] && return 0
  request_runtime_close "$pid"
  deadline=$((SECONDS + 5))
  while (( SECONDS < deadline )); do
    [[ -z "$(owned_runtime_pids "$repo_dir")" ]] && return 0
    sleep 0.1
  done
  echo "$label is still open. Finish or cancel its pending changes before replacing it." >&2
  return 1
}

wait_for_owned_runtime() {
  local label="$1" repo_dir="$2" timeout_seconds="$6" deadline token
  token="$(cat "$(launcher_owner_file "$repo_dir")")"
  deadline=$((SECONDS + timeout_seconds))
  while (( SECONDS < deadline )); do
    if [[ "$(runtime_field "$repo_dir" token)" == "$token" ]]; then
      if [[ "$(runtime_field "$repo_dir" state)" == activated ]]; then
        echo "$label activated an independently started instance; the selected launch mode did not replace it."
        return 0
      fi
      [[ -n "$(owned_runtime_pids "$repo_dir")" ]] && return 0
    fi
    sleep 0.1
  done
  echo "$label did not start within ${timeout_seconds}s." >&2
  return 1
}
