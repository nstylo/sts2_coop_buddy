# Platform compatibility

The production artifact is a .NET 9 AnyCPU class library. It runs inside STS2's CLR and uses the game's Godot, Harmony, and networking implementations. It has no P/Invoke methods, bundled native libraries, desktop-framework dependencies, or runtime OS-specific file paths. This removes common portability barriers; it does not prove that untested game builds have identical behavior.

## Current evidence

The runtime results cover the **same public-built mod v0.6.0 DLL on public v0.107.1 and public-beta v0.111.0**. Both current branches are supported; see [branch compatibility](branches.md).

| Platform/game runtime | Build and layout checks | Native engine checks | Live Steam co-op / visual review |
| --- | --- | --- | --- |
| macOS arm64 | Pass against public and beta | Public: 86 passed; beta: 87 passed | Pending |
| macOS x86_64 | Pass against public and beta | Public: 86 passed; beta: 87 passed under Rosetta on Apple Silicon | Pending on physical Intel hardware |
| Windows x86_64 | Windows layout, MSBuild properties, and staging fixtures pass | Pending on Windows with its installed game assemblies | Pending |
| Linux x86_64 | Linux layout and MSBuild properties pass | Pending on Linux with its installed game assemblies | Pending |

All four engine runs exercised the **same DLL compiled against public**, SHA-256 `9cc7a2b00f764e09d1e00179c671d20f645f3c286fd6fdae4f2343dc60f5dba9`. `--mod-dir` pins that artifact in both the native mod-loader directory and the test assembly's load path while tests compile against each selected game. The harness uses real reward tasks, native card picker scenes and the native overlay stack inside a minimal UI shell. Its three-peer relay harness exercises native message discovery, the packet codec, dispatch and cursor synchronization; no ENet or Steam sockets are opened. Player rows are stand-ins, and inspection calls are captured to verify models/indices; full HUD appearance and interactive inspection still need the fastmp playtest. The same package is intended for Windows and Linux; their native game/runtime combinations have not been executed here. An IL-only PE `.dll` is the normal .NET format on all these OSes.

## Build configuration

`GamePaths.props` is shared by the mod and runtime tests. It supports:

- Windows Steam install/Steam root registry entries, then the default Program Files (x86) Steam library.
- Linux `~/.local/share/Steam`, `~/.steam/steam`, `~/.steam/root`, and the common Flatpak Steam data directory.
- macOS Steam's Application Support directory, including Apple Silicon and Intel assembly exports.
- Explicit `Sts2Dir`, `SteamLibraryPath` (the `steamapps` directory), or `Sts2DataDir` overrides for custom libraries.

Assembly directories are `data_sts2_windows_x86_64`, `data_sts2_linuxbsd_x86_64`, and the app bundle's `Contents/Resources/data_sts2_macos_arm64` or `data_sts2_macos_x86_64`. Windows/Linux names follow the [mod template's path discovery](https://github.com/Alchyr/ModTemplate-StS2/blob/master/content/ModTemplate/Sts2PathDiscovery.props); native game runs must verify the installed export and method signatures on each OS.

Do not cross-publish a self-contained app or ship separate CLR/native dependencies. Build the managed DLL normally. The production output remains just `CoopBuddy.dll` and `CoopBuddy.json` on all platforms.

## Run the checks

Requires Python 3.10+, .NET 9 SDK, and the matching native game installed locally. Python and the SDK are development/test requirements, not mod-player requirements. Run commands from the repository root.

All platforms can check discovery/staging fixtures without running the game:

```sh
python -m unittest discover -s tests -p "test_*.py" -v
```

On macOS/Linux, use `python3` if `python` is unavailable. Fixture tests evaluate Windows/Linux path rules on the current host; they are not native Windows/Linux runtime evidence.

Build against the current installation and audit the resulting DLL:

```sh
dotnet build -c Release --disable-build-servers
dotnet build tests/PortabilityChecks.csproj -c Release --disable-build-servers
dotnet tests/bin/Release/net9.0/PortabilityChecks.dll dist/CoopBuddy/CoopBuddy.dll
```

The audit verifies IL-only AnyCPU flags, absence of 32-bit/native entrypoints, no P/Invoke, and the expected game/.NET assembly references.

Native Windows engine run (PowerShell):

```powershell
python tests/run-runtime-tests.py --game "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
```

Native Linux engine run:

```sh
python3 tests/run-runtime-tests.py --game "$HOME/.local/share/Steam/steamapps/common/Slay the Spire 2"
```

macOS engine run (selects the host architecture):

```sh
python3 tests/run-runtime-tests.py
```

Intel macOS runtime on Apple Silicon, when Rosetta is available:

```sh
python3 tests/run-runtime-tests.py --data-dir "$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_x86_64"
```

The runner accepts a Steam game directory or macOS `.app`, and optional `--executable`, `--data-dir`, and `--pack` overrides when discovery is ambiguous. It reads the export's own CLR configuration/dependency graph and rebuilds the tests against that installation. It does not download or redistribute game files. Windows/Linux copying can require several GB of temporary space when filesystem cloning is unavailable. Logs remain in the printed staging directory; remove that directory after collecting results.

A successful engine run must exit with code 0 and print `COOP_BUDDY_TESTS_PASSED: 86` on public or `87` on beta. Public skips the transport-level handshake check because that API does not exist there; both branches test vanilla message IDs and native host/client handling of unknown viewer packets. Native game loading plus Harmony patching are part of those checks. A build alone or a passing fixture test is insufficient to mark native runtime verification complete.

## Release validation still needed

After the native Windows/Linux checks pass, test a Steam co-op session containing players on different OSes. Compare actual reward screens with the viewer for picks, skips, rerolls, enchantments, nested rewards, disconnect/rejoin, and save/load. Verify both host and client roles, window sizing, card inspection, and controller input. Keep all players on the same STS2 version.

View metadata and advisory votes use two small native mod-message types; cursor positions continue to use the existing game protocol. Install the same mod version on the host and all participants for collaboration. Actual cross-OS co-op behavior remains dependent on STS2 and requires the session above to verify.

Mixed-player checks use the actual native receivers with a vanilla message table and verify that built-in traffic still decodes after unknown viewer packets. They do not claim a complete connected vanilla/modded Steam playthrough. Other mods that introduce network messages with different type tables still need matching installations.
