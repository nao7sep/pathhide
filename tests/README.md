# PathHide's areas, and the tests that stand for them

`dotnet test` runs this whole test project: at a few seconds it is already a fixed, balanced run, so
nothing selects a subset of it. PathHide has nothing paid, external, or heavy in the product, so there
is no separate full gate: `dotnet test` is it. Some tests are platform-gated and report as skipped off
their platform — the macOS syscall path on Windows and the Windows attribute path on macOS — so a green
run on one machine covers one of the two native mechanisms for real and the other only where its
behaviour is pure.

This file is the balance judgement the `tests-folder-conventions` require — which areas PathHide has,
and which tests stand for each — so a reader can tell what a green run covered, and an area with no test
standing for it is visible rather than merely absent. `PathHide.Tests/AreaMapTests.cs` holds every path
below to what is on disk, so a rename or a deletion breaks the run instead of quietly hollowing out the
map.

Paths are relative to this folder. Test doubles and harness files — `PathHide.Tests/TestApp.cs`,
`PathHide.Tests/PlatformFacts.cs`, `PathHide.Tests/Storage/StorageRootEnvironment.cs` and
`PathHide.Tests/Fakes/` — are infrastructure and stand for no area; every other test file in the project
appears below exactly once.

| Area | What it covers | Tests standing for it |
|---|---|---|
| Per-platform visibility | The mechanism the product is: the Finder hidden flag through `getattrlist`/`chflags` on macOS, and the HIDDEN (optionally plus SYSTEM) attribute on Windows, read back as well as written | `PathHide.Tests/Services/MacVisibilityServiceTests.cs`, `PathHide.Tests/Services/WindowsVisibilityServiceTests.cs`, `PathHide.Tests/Services/WindowsFileVisibilityTests.cs` |
| Elevated apply on Windows | The out-of-process elevated pass for access-protected paths: the command line the unelevated launcher builds, and the JSON-Lines results the child reports back per path | `PathHide.Tests/Services/ElevatedApplyCommandTests.cs`, `PathHide.Tests/Services/ElevatedApplyResultsTests.cs` |
| Paths and scanning | Turning what a user drops or picks into a stored entry — normalization, path family, rejection — and the scan that reports each entry's actual state back in order and under cancellation | `PathHide.Tests/Services/PathNormalizerTests.cs`, `PathHide.Tests/Services/PathScannerTests.cs` |
| Persisted state | Everything the app writes under its home: where that root resolves (including the `PATHHIDE_HOME` override), the atomic JSON store that holds the path list and settings, the write-through backup database, and the id and timestamp the stored filenames are made of | `PathHide.Tests/Storage/StorageRootTests.cs`, `PathHide.Tests/Storage/JsonStoreTests.cs`, `PathHide.Tests/Backup/BackupStoreTests.cs`, `PathHide.Tests/Storage/FileTimestampTests.cs`, `PathHide.Tests/NanoIdTests.cs` |
| Settings, fonts, and themes | The settings a user changes and what the app looks like once they have: the UI font setting and its resolution to the bundled Inter, the theme preference's mapping to a variant, the themed brushes and their contrast, and the shared control styles | `PathHide.Tests/UiFontTests.cs`, `PathHide.Tests/FontResolutionTests.cs`, `PathHide.Tests/ThemeResourcesTests.cs`, `PathHide.Tests/AppStylesTests.cs` |
| The main window | The path list and the window around it: add, dedup, the commit-after-save contract, the apply and status summaries, where selection lands after rows are removed, the derived minimum size, and the viewport that scrolls below it | `PathHide.Tests/ViewModels/MainWindowViewModelTests.cs`, `PathHide.Tests/Views/SelectionRecoveryTests.cs`, `PathHide.Tests/Views/WindowMetricsTests.cs`, `PathHide.Tests/Views/WindowOverflowTests.cs` |
| Dialogs | The shared dialog shell and the dialogs built on it: the bounded scrolling body with a footer that stays reachable, the close-mode policy that decides when a dirty draft is worth a question, and the Settings and About dialogs' own behaviour | `PathHide.Tests/Views/DialogBaseLayoutTests.cs`, `PathHide.Tests/Views/DialogCloseGuardTests.cs`, `PathHide.Tests/Views/SettingsDialogTests.cs`, `PathHide.Tests/Views/AboutDialogTests.cs` |
| Shortcuts and the macOS menu bar | The keyboard surface and its catalogue: which actions are command-backed and which the view owns, the AppKit menu bar's layout and the keys that reach its items, and arrow/Home/End navigation within a button group | `PathHide.Tests/Views/ShortcutRouterTests.cs`, `PathHide.Tests/Views/MacMenuBarTests.cs`, `PathHide.Tests/Views/ActionButtonNavigationTests.cs` |
| Text entry and IME | Composing text in the app's fields: the IME-corrected text box and its macOS input-method client, and text the OS hands back to a background window, such as the emoji picker's choice | `PathHide.Tests/Controls/ImeTextBoxTests.cs`, `PathHide.Tests/Views/BackgroundTextInputTests.cs` |
| The text a failure shows | What the user reads when something goes wrong, and that raw diagnostics never reach it: the failure-message catalogue, the view-action boundary that owns an unhandled failure, and opening an external link without a crash | `PathHide.Tests/ViewModels/FailurePresentationTests.cs`, `PathHide.Tests/Views/ViewActionBoundaryTests.cs`, `PathHide.Tests/Services/ExternalLauncherTests.cs` |
| Session logging | The per-session JSON-Lines log: its UTC filename, the envelope each level writes, the key redaction that keeps secrets out of it, and revealing the current log or its directory to the user | `PathHide.Tests/Services/SessionLogTests.cs`, `PathHide.Tests/Services/SessionLoggerTests.cs`, `PathHide.Tests/Services/LogRedactorTests.cs`, `PathHide.Tests/Services/LogRevealTests.cs` |
| Startup and single instance | Claiming the storage root for one running copy: the lease a second launch cannot take, which instead activates the owner already running | `PathHide.Tests/Services/SingleInstanceLeaseTests.cs` |
| Release shape | What ships, checked against the manifests that describe it: the Inno Setup installer's install modes and privileges, the macOS bundle assembly, and the one version kept in lock-step across `Directory.Build.props`, `Info.plist` and `app.manifest` | `PathHide.Tests/InstallerConfigurationTests.cs`, `PathHide.Tests/VersionConsistencyTests.cs` |
| This map | The map itself: that it still names more than one area, that no area is left with nothing standing for it, and that every path above exists on disk | `PathHide.Tests/AreaMapTests.cs` |
