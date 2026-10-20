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

owned_runtime_pids() {
  local repo_dir="$1" project_file="$2" executable_name="$3" built_executable="$4"
  local pid command
  ps -axo pid=,command= | while read -r pid command; do
    [[ "$pid" == "$$" || "$pid" == "$PPID" ]] && continue
    if [[ "$command" == *"$repo_dir/scripts/run-dev.command"* ||
          "$command" == *"$repo_dir/scripts/run-built.command"* ||
          "$command" == *"$repo_dir/scripts/rebuild.command"* ]]; then
      printf '%s\n' "$pid"
      continue
    fi
    if [[ "$command" == "$built_executable" || "$command" == "$built_executable "* ]] ||
       [[ "$command" == "$repo_dir/"*"/bin/"*"/$executable_name" || "$command" == "$repo_dir/"*"/bin/"*"/$executable_name "* ]] ||
       [[ -n "$project_file" && "$command" == *"dotnet"* && "$command" == *"$project_file"* ]]; then
      printf '%s\n' "$pid"
    fi
  done
}

stop_owned_runtime() {
  local label="$1" repo_dir="$2" project_file="$3" executable_name="$4" built_executable="$5"
  local pids pid deadline remaining current_owned
  pids="$(owned_runtime_pids "$repo_dir" "$project_file" "$executable_name" "$built_executable")"
  [[ -z "$pids" ]] && return 0

  echo "Stopping the existing $label runtime (pid $(echo "$pids" | tr '\n' ' '))."
  while read -r pid; do
    [[ -n "$pid" ]] && kill -TERM "$pid" 2>/dev/null || true
  done <<< "$pids"

  deadline=$((SECONDS + 5))
  while (( SECONDS < deadline )); do
    remaining=""
    while read -r pid; do
      if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
        remaining+="$pid"$'\n'
      fi
    done <<< "$pids"
    [[ -z "$remaining" ]] && return 0
    sleep 0.1
  done

  current_owned="$(owned_runtime_pids "$repo_dir" "$project_file" "$executable_name" "$built_executable")"
  while read -r pid; do
    if [[ -n "$pid" ]] && grep -qx "$pid" <<< "$current_owned"; then
      kill -KILL "$pid" 2>/dev/null || true
    fi
  done <<< "$pids"
}

ready_runtime_pids() {
  local repo_dir="$1" executable_name="$2" built_executable="$3"
  local pid command
  ps -axo pid=,command= | while read -r pid command; do
    [[ "$pid" == "$$" || "$pid" == "$PPID" ]] && continue
    if [[ "$command" == "$built_executable" || "$command" == "$built_executable "* ]] ||
       [[ "$command" == "$repo_dir/"*"/bin/"*"/$executable_name" || "$command" == "$repo_dir/"*"/bin/"*"/$executable_name "* ]]; then
      printf '%s\n' "$pid"
    fi
  done
}

wait_for_owned_runtime() {
  local label="$1" repo_dir="$2" project_file="$3" executable_name="$4" built_executable="$5" timeout_seconds="$6"
  local deadline pids
  deadline=$((SECONDS + timeout_seconds))
  while (( SECONDS < deadline )); do
    pids="$(ready_runtime_pids "$repo_dir" "$executable_name" "$built_executable")"
    [[ -n "$pids" ]] && return 0
    sleep 0.1
  done
  echo "$label did not start within ${timeout_seconds}s." >&2
  return 1
}
