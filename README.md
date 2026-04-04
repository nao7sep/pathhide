# PathHide

A desktop utility for macOS and Windows that hides or shows specific files and directories and remembers the desired visibility state of each entry.

PathHide is for managing clutter, not for security.

## Features

- Hide and show files and directories using platform-native mechanisms
- Remember desired visibility state per entry — reapply after files reappear
- Add paths via file/folder pickers or drag and drop
- Batch operations: hide all, show all, reapply all
- Async background scanning with cancellation
- Per-user storage at `~/.pathhide/`

## Platform Behavior

- **macOS**: Uses the Finder hidden flag (`chflags hidden/nohidden`)
- **Windows**: Sets `HIDDEN` (or `HIDDEN` + `SYSTEM`) file attributes

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Build and Run

```
dotnet run
```

## License

[MIT](LICENSE)
