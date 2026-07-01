# PathHide

PathHide is a desktop utility for macOS and Windows that hides or shows specific files and directories and remembers the desired visibility of each one, so it can reapply it after files reappear. It's for managing visual clutter — **not** a security tool; hidden files stay fully accessible to anyone who looks. Built on .NET, it uses each platform's native mechanism: the Finder hidden flag on macOS, the HIDDEN attribute on Windows.

## Features

- Hide and show files and directories using platform-native mechanisms
- Remember the desired visibility per entry and reapply in bulk (hide all, show all, reapply all)
- Add paths via pickers or drag and drop
- Windows: optional stronger hiding (HIDDEN + SYSTEM), with automatic UAC elevation for access-protected paths
- Per-user storage with backups for path and settings recovery

## Requirements

- macOS or Windows
- To build and run from source: the .NET 10 SDK

## Download

Prebuilt installers and portable builds for macOS (Apple Silicon) and Windows are on the [Releases](https://github.com/nao7sep/pathhide/releases) page. These builds are **unsigned**, so the OS warns the first time you open one:

- **macOS** — right-click the app and choose **Open** (or run `xattr -dr com.apple.quarantine /Applications/PathHide.app`).
- **Windows** — on the SmartScreen prompt, click **More info → Run anyway**.

## Getting started

Double-click the launcher for your platform (`scripts/run-dev.command` on macOS, `scripts/run-dev.ps1` on Windows), or run from source:

```sh
dotnet run --project src/PathHide
```

## License

MIT © 2026 Yoshinao Inoguchi

## Contact

Yoshinao Inoguchi — nao7sep@gmail.com
