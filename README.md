# PathHide

A desktop utility for macOS and Windows that hides or shows specific files and directories and remembers the desired visibility state of each entry.

PathHide is for managing clutter, not for security.

## Features

- Hide and show files and directories using platform-native mechanisms
- Remember desired visibility state per entry — reapply after files reappear
- Add paths via file/folder pickers or drag and drop
- Batch operations: hide all, show all, reapply all
- Async background scanning with cancellation; entries can show a pending state until scanned
- Per-user storage at `~/.pathhide/` with backup files for path and settings recovery

## Platform Behavior

- **macOS**: Toggles the Finder hidden flag (`UF_HIDDEN` via the `chflags(2)`/`lchflags(2)` syscalls). No platform-specific settings.
- **Windows**: Sets `HIDDEN` file attribute by default. A settings dialog (accessible via **Settings** in the toolbar's `☰` menu, or `Ctrl+,`) allows enabling `HIDDEN` + `SYSTEM` mode for stronger hiding. The Settings entry is only present on Windows.

  When an apply fails because a path is protected by Windows access control, PathHide retries those access-denied paths by relaunching itself elevated via a UAC prompt (`runas`). All access-denied paths from one apply are retried together in a single elevated invocation — one prompt per apply operation. Failures from other causes are reported as errors without an elevation retry. After the elevated process exits, PathHide re-inspects each retried path to determine the actual outcome; both the exit code and the re-inspection result are reported independently.

When Windows is in `HiddenOnly` mode, hide and reapply clear `SYSTEM` and keep only `HIDDEN`.

The `Show` action always clears both `HIDDEN` and `SYSTEM` regardless of the current mode.

## Logs

PathHide writes a runtime log so a problem can be reconstructed after the fact.

- **One file per launch**, under `~/.pathhide/logs/`, named with the UTC launch time and nothing else: `yyyymmdd-hhmmss-utc.log`. If the file can't be opened, the app falls back to console logging.
- **JSON Lines** — one event per line, each an object with a `time` (UTC, millisecond ISO-8601), `level` (`debug`/`info`/`warn`/`error`), and `message`, plus event-specific fields. Machine-parseable first, greppable by eye second.
- **Never auto-deleted.** Logs are small; old ones may be exactly what's needed to debug a problem that surfaces later. Delete them by hand if you want to reclaim the space.
- Open the current log from the toolbar's `☰` menu → **Open Log File** (reveals it in Finder/Explorer).
- **Developer detail is off by default.** Per-item `debug` lines (each scanned path, each attribute write) are written only in a development build or when the `PATHHIDE_DEBUG=1` environment variable is set, so a normal install never floods the disk. The Windows elevated apply pass is a separate process and writes its own session log alongside the main one.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Build and Run

`dotnet run` works for day-to-day development on either platform. Helper scripts in `scripts/` add platform-specific ceremony for testing access to OS-protected paths:

- **macOS** (`scripts/run.command`): publishes a self-contained build into a `.app` bundle, ad-hoc signs it (`codesign --sign -`), and launches it via Launch Services. macOS attributes TCC permission prompts to the signed bundle's identity, so the app needs a real bundle (not just `dotnet run`) before TCC will prompt for protected directories like Desktop, Documents, or Downloads. Host architecture (`osx-arm64` or `osx-x64`) is auto-detected from `uname -m`.
- **Windows** (`scripts/run.ps1`): runs `dotnet run` directly. Windows has no TCC equivalent that requires a signed bundle for permission prompts; the elevation flow uses UAC, which is triggered by attribute writes regardless of signing.

## License

[MIT](LICENSE)
