# PathHide

PathHide is a desktop utility for macOS and Windows that hides or shows specific files and directories and remembers the desired visibility of each one, so it can reapply it after files reappear. It's for managing visual clutter — **not** a security tool; hidden files stay fully accessible to anyone who looks. Built on .NET, it uses each platform's native mechanism: the Finder hidden flag on macOS, the HIDDEN attribute on Windows.

## What adding a path does

**Adding a path hides it immediately.** Use **Add Files** or **Add Directories** to open the platform's native multi-selection picker, or drop files and folders into the path list. Accepted paths disappear from Finder or Explorer on the spot — adding is the act of hiding, not a bookkeeping step before it. The result strip reports paths that were duplicates, unavailable, or could not be changed. Select an entry and choose **Show** to bring it back.

Removing an entry is the opposite of adding it to the list, not the opposite of hiding it: the entry goes, and the file **stays hidden**. Show it first if that is what you meant.

## Features

- Remember the desired visibility per entry and reapply in bulk (hide all, show all, reapply all)
- Add paths via pickers or by dropping them into the path list
- Windows: optional stronger hiding (HIDDEN + SYSTEM), with automatic UAC elevation for access-protected paths
- macOS: hiding anything in Desktop, Documents, Downloads, or on a removable or network volume asks for the system's Files and Folders permission the first time

## Requirements

- macOS (Apple Silicon) or Windows
- To build and run from source: the .NET 10 SDK. Note that a source build has no bundle identity on macOS, so the permission-gated folders above are not reachable from it — use a packaged build to exercise those.

## Download

Prebuilt installers and portable builds for macOS (Apple Silicon) and Windows are on the [Releases](https://github.com/nao7sep/pathhide/releases/latest) page. These builds are **unsigned**, so the OS warns the first time you open one:

- **macOS** — right-click the app and choose **Open** (or run `xattr -dr com.apple.quarantine /Applications/PathHide.app`).
- **Windows** — on the SmartScreen prompt, click **More info → Run anyway**.

## Run from source

Launch it with the script for your platform — double-click `scripts/run-dev.command` on macOS, or right-click `scripts/run-dev.ps1` and choose *Run with PowerShell* on Windows. Or run it by hand:

```sh
dotnet run --project src/PathHide
```

Tests, and the coverage report:

```sh
dotnet test -c Release
dotnet test --collect "XPlat Code Coverage" --results-directory coverage
```

Some tests are platform-gated and report as skipped off their platform — the Windows attribute tests on macOS and vice versa — so a green run on one machine does not mean the whole suite ran. CI runs it on both.

## License

[GNU GPL v3 or later](LICENSE) © 2026 Yoshinao Inoguchi

## Contact

Yoshinao Inoguchi — yoshinao@inoguchi.com — <https://inoguchi.com>
