#!/usr/bin/env bash
# Shared by the macOS launchers (run-dev, run-built, rebuild): one PathHide runtime per repository.
# The launcher that starts PathHide records its process ID and executable under .git; the next launcher
# asks that process to quit only while it still runs that executable. PathHide started any other way is
# left to its single-instance behavior, which brings its window forward instead of opening a second one.

runtime_record() {
  printf '%s/.git/launcher-runtime-process' "$1"
}

record_runtime() {
  local repo_dir="$1" pid="$2"
  printf '%s\n%s\n' "$pid" "$(ps -p "$pid" -o comm=)" > "$(runtime_record "$repo_dir")"
}

# The recorded process ID, printed only while that process still runs the recorded executable.
recorded_runtime_pid() {
  local record pid executable
  record="$(runtime_record "$1")"
  [[ -f "$record" ]] || return 0
  { IFS= read -r pid && IFS= read -r executable; } < "$record" || return 0
  [[ "$pid" =~ ^[1-9][0-9]*$ && -n "$executable" ]] || return 0
  [[ "$(ps -p "$pid" -o comm= 2>/dev/null)" == "$executable" ]] && printf '%s\n' "$pid"
  return 0
}

# Asks the recorded PathHide to quit as a user would, so it can still ask about unsaved work, and stops
# with a message rather than force-closing it when it stays open.
stop_recorded_runtime() {
  local label="$1" repo_dir="$2" pid deadline
  pid="$(recorded_runtime_pid "$repo_dir")"
  [[ -z "$pid" ]] && return 0
  echo "Asking the running $label (pid $pid) to quit."
  /usr/bin/osascript -l JavaScript - "$pid" >/dev/null <<'JXA'
ObjC.import('AppKit');
function run(args) {
  var application = $.NSRunningApplication.runningApplicationWithProcessIdentifier(Number(args[0]));
  if (application) application.terminate;
}
JXA
  deadline=$((SECONDS + 5))
  while (( SECONDS < deadline )); do
    [[ -z "$(recorded_runtime_pid "$repo_dir")" ]] && return 0
    sleep 0.1
  done
  echo "$label is still open. Finish or cancel its pending changes, then run this launcher again." >&2
  return 1
}

# Opens a new instance of the bundle through Launch Services, which gives it the bundle identity TCC
# needs, and records the process it started.
launch_bundle() {
  local label="$1" repo_dir="$2" bundle="$3" pid
  pid="$(/usr/bin/osascript -l JavaScript - "$bundle" <<'JXA'
ObjC.import('AppKit');
function run(args) {
  var application = $.NSWorkspace.sharedWorkspace.launchApplicationAtURLOptionsConfigurationError(
    $.NSURL.fileURLWithPath(args[0]), $.NSWorkspaceLaunchNewInstance, $({}), null);
  return application.isNil() ? '' : String(application.processIdentifier);
}
JXA
)"
  if [[ ! "$pid" =~ ^[1-9][0-9]*$ ]]; then
    # A new instance exits at once when another one already holds the app's single-instance lease.
    if pgrep -f "$bundle/Contents/MacOS/" >/dev/null; then
      echo "$label is already running outside these launchers; its window is brought forward instead."
      return 0
    fi
    echo "$label could not be opened." >&2
    return 1
  fi
  record_runtime "$repo_dir" "$pid"
}

# Records the app process that the wrapper (dotnet run) starts, once it appears. Fails when the wrapper
# ends first, as it does when the build fails.
record_wrapped_runtime() {
  local repo_dir="$1" wrapper_pid="$2" executable_name="$3" pid executable
  while kill -0 "$wrapper_pid" 2>/dev/null; do
    for pid in $(pgrep -P "$wrapper_pid" || true); do
      executable="$(ps -p "$pid" -o comm= 2>/dev/null || true)"
      if [[ "${executable##*/}" == "$executable_name" ]]; then
        record_runtime "$repo_dir" "$pid"
        printf '%s\n' "$pid"
        return 0
      fi
    done
    sleep 0.2
  done
  return 1
}
