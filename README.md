# Co-op Reward Viewer

A small Slay the Spire 2 mod for switching between players' currently open card rewards. Uses the vanilla player-status card icon and full native reward screen. Teammates can share cursors and recommend cards with native player portraits; only the owner can take a reward.

The current development build targets **public-beta v0.111.0** and **.NET 9**. Our release target is **stable/main v0.107.1**; that port needs validation against stable's installed assemblies. The current package still declares `min_game_version: 0.111.0` and is not yet a stable release. See [branch compatibility](docs/branches.md).

No BaseLib, NuGet packages, separate networking transport, or PCK required. Harmony and GodotSharp are referenced from your installed game.

One **AnyCPU DLL and manifest** are intended for macOS, Linux, and Windows. Mac Apple Silicon and Intel runtime tests pass; Windows and Linux still require native game validation. See the [compatibility matrix](docs/compatibility.md) for the exact evidence and remaining checks.

## Build

Install the .NET 9 SDK, then run:

```sh
dotnet build -c Release
```

`GamePaths.props` discovers the standard Steam installation on each OS: macOS's Application Support folder, Windows's Steam registry/default Program Files location, and Linux's common native Steam and Flatpak directories. SDK selection stays on .NET 9, even when .NET 10 is installed. No OS-specific `-r` build or separate mod binaries are needed.

For a different Steam library, copy `local.props.example` to `local.props` and set `Sts2Dir` to the game's Steam folder, or pass the assembly directory directly:

```sh
dotnet build -c Release -p:Sts2DataDir="/path/to/directory/containing/sts2.dll"
```

The two distributable files are `dist/RewardViewer/RewardViewer.dll` and `dist/RewardViewer/RewardViewer.json`. A ZIP is also generated at `dist/RewardViewer-0.5.2.zip`. Do not distribute the game's assemblies or the runtime test output.

## Steam Workshop

Workshop packaging uses [Mega Crit's official uploader](https://github.com/megacrit/sts2-mod-uploader) and the game's existing subscription loader. Prepare and review the listing with:

```sh
python3 tools/workshop.py prepare
python3 tools/workshop.py setup
python3 tools/workshop.py upload --dry-run
```

To submit it using your logged-in Steam account, run `python3 tools/workshop.py upload`. The initial listing is **private** and marked for **public-beta v0.111.0**. Its ID is retained in `workshop/mod_id.txt` for future updates. See [Workshop publishing](docs/workshop.md) for review, existing-item updates and subscription testing.

## Install

Use the same `RewardViewer.dll` and `RewardViewer.json` on every platform, with the exact spelling and capitalization shown below. The game supplies the runtime; players do not need to install the SDK to use the mod.

| Platform | Folder for both files, relative to Steam's game directory |
| --- | --- |
| Windows | `mods/RewardViewer/` beside the game executable |
| Linux | `mods/RewardViewer/` beside the game executable |
| macOS (Apple Silicon and Intel) | `SlayTheSpire2.app/Contents/MacOS/mods/RewardViewer/` |

Steam's **Manage → Browse local files** opens the game directory, including for custom Steam libraries. Quit the game before copying files.

### Windows (PowerShell)

```powershell
$game = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
New-Item -ItemType Directory -Force -Path "$game\mods\RewardViewer" | Out-Null
Copy-Item .\dist\RewardViewer\RewardViewer.dll,.\dist\RewardViewer\RewardViewer.json -Destination "$game\mods\RewardViewer\"
```

Change `$game` to the folder opened by Steam if it is installed elsewhere.

### Linux

```sh
game="$HOME/.local/share/Steam/steamapps/common/Slay the Spire 2"
mkdir -p "$game/mods/RewardViewer"
cp dist/RewardViewer/RewardViewer.dll dist/RewardViewer/RewardViewer.json "$game/mods/RewardViewer/"
```

For Flatpak or a custom library, replace `game` with the folder opened by Steam. Preserve filename case on Linux.

### macOS

Quit STS2. The installed game's loader searches for `mods` **beside its executable**, which is inside the app bundle on macOS. For the default Steam installation:

```sh
mods="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods"
mkdir -p "$mods/RewardViewer"
cp dist/RewardViewer/RewardViewer.dll dist/RewardViewer/RewardViewer.json "$mods/RewardViewer/"
```

Launch through Steam, accept the game's mod-loading notice if prompted, enable Co-op Reward Viewer, and restart if requested. Confirm `[RewardViewer] Loaded Co-op Reward Viewer 0.5.2.` in the game log. Steam updates may remove files inside the app bundle; reinstall the two files if needed. To uninstall, remove only the `RewardViewer` folder while the game is closed.

## Use

Open your card reward using the normal reward popup. A clickable vanilla card icon appears beside your name. Teammates get the same button when they open a reward.

- Click another player's card icon to view their open reward in the native full-size picker. Native selection corners mark the owner of the displayed reward. Hover enlarges the icon slightly and uses the same corner graphic while keeping the game's normal cursor.
- Click your icon to return to the **same** pending local picker and choose normally. Every reward switch replays the entrance animation, including the return to your own cards. Your reward stays open for teammates while you look at theirs.
- Click **Back** to return to your still-open reward, or **Close** when you have no local picker open. Clicking the selected teammate icon again or pressing Back/Escape does the same.
- The spectator banner says `Observing <player name>` with a cool tint. Names are capped at 24 text characters, including an ellipsis for long names. Observing does not publish the vanilla choosing-card status or add another player-row icon.
- Players looking at the **same offer** see each other's native multiplayer cursors. Different reward views keep their cursors separate.
- **Left click** a teammate's card to recommend it. Your vote gets the native gold card glow on your screen. Everyone sees the voters' character portraits at that card's bottom right, including your own portrait when you vote. Badges stay visible without hover. Hover a portrait to see the voter's full name in the game's native hover-tip popup, including when several players use the same character. Clicking another card moves your vote; clicking the same card again removes it.
- **Right click** keeps native card inspection. The spectator preview has no skip/reroll actions; recommendations cannot take a card for its owner.

Only the currently open reward is shared. Unopened rewards and queued reward sets have no button. Picking, skipping or backing out removes that reward; reopening adds it again. Rerolls refresh the preview. If the watched reward closes or its owner disconnects, your own pending picker reappears automatically. Room/run transitions clear all state.

Basic reward viewing uses STS2's native reward lifecycle. **For shared cursors and recommendations, install v0.5.2 on the host and every participating player.** These features use small UI messages through the game's existing networking. Use the same mod version on all participating peers. Custom rewards implemented outside the native lifecycle are outside this scope.

## How it works

1. Harmony observes `CardReward.OnSelect()` and its original task on every client. Native `PlayerChoiceSynchronizer` handles actual picks, cancels and rerolls.
2. Buttons reuse the vanilla status-icon positions and texture, plus the native selection-reticle scene for hover. A spectator instance of the real picker hides the local picker without completing it. Returning replays native movement/fade curves, banner and buttons on the same local controls and models. The native click guard prevents accidental picks during the entrance.
3. The existing `PeerInputSynchronizer` carries cursor positions. A small native `INetMessage` identifies the viewed offer; the native cursor renderer shows only peers on that same offer.
4. A second `INetMessage` carries advisory votes. STS2 discovers both message classes in the mod, assigns packet IDs, and handles reliable delivery and host relay. No separate sockets or services are used.
5. An offer key combines its owner, native choice ID and a digest of serialized card options. Old votes cannot attach to a reopened or rerolled offer. Votes are cleared on close/disconnect, and small periodic snapshots let newly listening peers recover current recommendations.
6. Voter portraits instantiate the game's `multiplayer_vote_icon` scene at 48 pixels in the game viewport and follow the card's bottom-right corner. Your own vote also uses `NCardHighlight`. UI nodes belong to the screen rather than pooled cards. Votes and inspection map to the original offer order, since native hover effects reorder card nodes.
7. Display models use `ClonePreservingMutability()`. Only marked spectator screens have selection guards. Native inspection remains available, and Back/Escape closes the spectator without resolving the local reward.

Room/run cleanup uses native events. The mod sends only view/recommendation metadata, does not change reward results or RNG, and never submits a gameplay choice for a teammate.

See [API notes](docs/api-notes.md) for the exact installed build and inspected APIs.

## Verification

For an interactive local co-op test with two real game windows, follow [the fastmp testing guide](docs/local-testing.md). It uses STS2's built-in host/join mode and distinct player IDs, without a second Steam account.

Build and native engine tests:

```sh
dotnet build -c Release
python3 tests/run-runtime-tests.py
```

The test runner supports native macOS, Linux, and Windows installations. It creates a private temporary copy of the engine/PCK, supplies a small test entrypoint, and uses in-memory game saves. Filesystem cloning is optional; Windows uses file copies without requiring symlink privileges. It disables the normal main scene, Steam, and crash reporting. It exercises the real mod loader and Harmony patches, reward tracking, clone isolation, native UI creation, and a three-peer wire harness using real packet serialization, deserialization and message dispatch. It does not test packets between separate machines or visually inspect a rendered window.

The current UI is checked with the native loader and engine harness, including actual reward tasks, full-size card scenes, spectator input guards, vote portraits and restoration of the original pending picker. See the [compatibility guide](docs/compatibility.md) for the recorded results and remaining platform checks. The harness uses a minimal test UI shell, not a complete interactive game session.

Manual co-op checks before release:

- On host and client, open rewards at different times. Only opened rewards should have icons, including your own. Compare every displayed card.
- Switch repeatedly between icons. Your original picker must remain pending; the native selection corners must identify the displayed owner. Pick your card after switching back.
- Pick, skip, back out, reopen and reroll in the watched window. Check that the other viewer closes or updates promptly. Test nested and multiple reward sets.
- Vote on the first and middle cards, move the mouse to other cards, then move/clear your vote and inspect cards. Confirm the glow identifies your vote and persistent portraits identify every vote, including your own. Confirm native cursors only appear for players looking at the same reward. Resize the window and try a controller. Recommendations must never change either deck.
- Disconnect/rejoin, save/reload, change rooms and return to the menu. Check for stale icons and log errors.

## Current scope

Tracks open card rewards offered through the native `CardReward.OnSelect` lifecycle, including normal combat rewards and relic/event rewards that use it. Generic card selection screens, shops, and other mods' private local-only rewards are outside this implementation. Non-card alternative buttons are not shown.

Remote options become available when **your client** starts the corresponding native reward selection. If a teammate finishes their combat animations earlier, the viewer cannot show their rewards until that point. A future STS2 build that generates rewards only on their owner's client would require an explicit native-network message extension.

Saved card properties trigger refreshes. A third-party mod that silently mutates only unsaved custom fields may need an additional refresh hook. Reconnection recovery relies on STS2's own lifecycle and should be checked in live multiplayer before distribution.
