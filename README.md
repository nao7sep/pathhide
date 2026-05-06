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
- **Windows**: Sets `HIDDEN` file attribute by default. A settings dialog (accessible via **Settings** in the toolbar's `☰` menu) allows enabling `HIDDEN` + `SYSTEM` mode for stronger hiding. The Settings entry is only present on Windows.

  When a path is protected by Windows access control, PathHide retries the whole failed batch by relaunching itself elevated via a UAC prompt (`runas`). All failing paths are retried in a single elevated invocation — one prompt per apply operation. After the elevated process exits, PathHide re-inspects each path to determine the actual outcome; both the exit code and the re-inspection result are reported independently.

When Windows is in `HiddenOnly` mode, hide and reapply clear `SYSTEM` and keep only `HIDDEN`.

The `Show` action always clears both `HIDDEN` and `SYSTEM` regardless of the current mode.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Build and Run

```
dotnet run
```

## License

[MIT](LICENSE)
