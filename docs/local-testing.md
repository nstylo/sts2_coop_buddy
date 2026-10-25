# Test with local game windows

The installed STS2 v0.111.0 has a native `fastmp` mode. It uses the actual multiplayer/reward code with ENet transport. The client connects to `127.0.0.1:33771`; it does not need a second Steam account.

These flags and the current mod package have been checked against the **beta** installation. The stable/main v0.107.1 port and its fastmp verification are pending; see [branch compatibility](branches.md). Do not treat the beta test results as a stable release check.

Verified from the installed assembly:

| Argument | Behavior |
| --- | --- |
| `--fastmp=host_standard` | Opens a standard multiplayer host lobby |
| `--fastmp=join` | Joins the local ENet host |
| `--clientId=1` | Matches the fixed native ENet host ID |
| `--clientId=1000` | Gives the second instance a distinct identity |
| `--clientId=2000` / `3000` | Gives additional clients distinct identities |
| `--force-steam=off` | Skips Steam initialization, so identity and saves use the non-Steam backend |

Use `host_standard`, since `host` alone opens the multiplayer menu without selecting a game mode. Pass these arguments directly to the executable: the installed `CommandLineHelper` reads `OS.GetCmdlineArgs()`, so do not put them after a `--` separator.

## Build and install

Close any running game instances and build:

```sh
dotnet build -c Release --disable-build-servers
```

Install the DLL and manifest using the [platform instructions](../README.md#install). On the current Mac:

```sh
mods="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/RewardViewer"
mkdir -p "$mods"
cp dist/RewardViewer/RewardViewer.dll dist/RewardViewer/RewardViewer.json "$mods/"
```

Install the same v0.5.2 package for the host and every client participating in votes/cursor sharing. Both local windows use the same installed files.

The launcher reads the installed mod; it does not copy files into the game or use the isolated engine harness. Rebuilds therefore need another copy before relaunching.

## Launch

From the repository root in Terminal 1:

```sh
python3 tests/launch-local-coop.py host
```

Wait until the host's character-selection lobby appears. Then, from the same directory in Terminal 2:

```sh
python3 tests/launch-local-coop.py join
```

On Windows use `python` if that is how Python is installed. Both commands work on Linux as well. Use `--game "path/to/Steam/game/folder"` for custom installations. The launcher forces windowed mode using the game's native `STS2_DEV_WINDOWED` option.

If either instance shows the game's mod-loading notice, accept it, enable Co-op Reward Viewer, close both instances, and relaunch the two commands. Each non-Steam player ID has separate account/profile settings, so both may need the initial notice. Confirm `[RewardViewer] Loaded Co-op Reward Viewer 0.5.2.` in each log.

Both windows must ready up. For a useful comparison, unlock the test profiles as described below, pick different characters, and start a fresh test run. Complete the initial event and the first combat, controlling both players.

For three or four players, run the same `join` command in another terminal for each additional client **before starting the run**. The launcher automatically reserves IDs `1000`, `2000`, then `3000`. You can still specify `--client-id 2000` or `--client-id 3000` to choose a particular persistent test profile. Each player must ready up, and each fresh profile may need its own mod-loading notice.

The launcher holds a native OS file lock for each running identity and checks the host log for already registered peers. Duplicate launches stop with an explanation before opening another game window. Lock files may remain in `.local/fastmp`; their presence alone does not reserve an ID, and there is no need to delete them.

`--force-steam=off` uses the game's `default/1` and `default/1000` account directories rather than the Steam account directory. These are persistent local test profiles; it is not the in-memory save backend used by the automated harness. Later tests may resume or overwrite an existing local test run. Game settings and logs still live under the normal application data root, with separate engine logs selected by the launcher.

## Only Ironclad, locked ascensions, or identical rewards

Fresh fastmp profiles have fresh progression. The installed game deliberately gives an Ironclad player with zero completed runs and zero unlocked epochs scripted tutorial rewards. The first offer is **Setup Strike, Tremble, Blood Wall**, so two fresh Ironclads see identical options.

Use the game's native console to prepare each test profile:

1. In **each** game window, open the console with **backtick** or **apostrophe**. The installed build enables debug commands when a mod is loaded.
2. Run:

   ```text
   unlock all
   ```

3. Check for `Unlocked all`, then close both game windows. This command is local, so it must be run in both windows.
4. Relaunch the same host/join commands and start a **new run**, rather than continuing the existing one. Choose Ironclad for one player and Silent, Defect, Regent or Necrobinder for the other. The host can now select ascensions 0–10.

The command saves unlocks to each non-Steam fastmp profile. Existing runs retain their original unlock-state snapshot, so unlocking mid-run will not remove their tutorial rewards. Higher ascension changes difficulty; a normal run still begins in Act 1.

Verified against `UnlockConsoleCmd` and `RewardsSet.TryGenerateTutorialRewards` in the installed v0.111.0 assemblies. The launcher does not force reward options or a fixed seed.

## First reward check

1. Finish combat in both windows. With neither card picker open, no clickable reward icons should appear.
2. Open the host's card reward through the normal popup. Its vanilla card icon should become a button in **both** windows, including beside the host's own name. Hover it: the game cursor must remain unchanged, while the icon enlarges slightly and native corners appear. Moving away should restore its size.
3. Open the client's reward. Both rows should now have buttons. Click the client's icon in the host window: compare the full-size cards, upgrades and enchantments with the client's original picker. The selected icon should keep native selection corners after the mouse leaves it. Hovering that same icon must use a single corner graphic without an extra box.
4. The spectator banner must read `Observing <owner name>` with a cool tint. Long names must be shortened without overflowing the banner. Observing alone must not publish the vanilla choosing-card icon. Your own already-open reward may still have its normal button.
5. Move the cursors over the same offer in both windows. Each should see the other player's cursor. Switch one window to a different offer: the cursors must no longer appear together.
6. Left click the **first card**, then move the mouse over the other cards. The voted card must keep a gold native glow on the voter's screen. Its larger voter portrait must appear on every screen viewing that reward, including the voter's own screen. Badges must stay on the voted card without hover. With two teammates on the same character, hover each portrait: the native popup must show the correct individual name. Moving away, removing the vote or closing the view must dismiss it. Card clicks and right-click inspection must still pass through the portrait. Try the middle card too; click again to clear the vote. Right click must inspect the correct card.
7. Click the native **Back** button to return to your original picker. The player icon and Back/Escape should also work. Switch repeatedly: every switch, including returning to your own cards, must replay the card/banner/button entrance. The same host choice must remain pending, and the client must still be able to view it. Rapid switching must not shift the final layout or allow an accidental card pick.
8. While the host watches the client, pick or skip in the client window. Its button, preview and votes should disappear, restoring the host's still-open picker. Rerolling must clear recommendations on the previous cards. Check the reverse direction too.
9. Back out of a picker and reopen it. The icon should disappear while closed, then return. Queued/unopened rewards must never appear as selectable views.
10. With no local reward picker open, view a teammate's reward and click **Close**. This should reveal the underlying game without selecting or skipping the teammate's reward. If your local picker ends while you observe, the button should change from Back to Close.

Native selection messages may arrive after your client's own reward generation/animations catch up; compare once both clients have reached the reward lifecycle.

Then test rerolls, extra/nested rewards, disconnect/rejoin, and returning to the main menu. Only one fastmp host can listen on the fixed port at a time.

## Additional client times out

Check `.local/fastmp/host-1.log`. If it contains `Second client attempted to connect with peer ID 2000, disconnecting them`, the host still has an earlier connection with that ID. The client can report only `Timed out waiting for handshake ack!` / `Connection timed out`, which hides the underlying collision. An abruptly closed or restarted client can leave a stale player in the native fastmp lobby.

Close **all** game windows, restart the host, and launch one `join` command per client in separate terminals. This clears the native host's peer list. If a fresh profile first needed to enable the mod, finish that setup before restarting the session; confirm the mod's loaded message in every client's new log. Do not repeatedly launch the same ID against a lobby that still lists that player.

## Logs and limits

The launcher captures console output in `.local/fastmp/host-1.log` and `.local/fastmp/join-1000.log`, including the C# mod messages. Separate `*-engine.log` files hold Godot's own logs. These files are ignored by Git and replaced when that role/player ID launches again. Keep the terminals open while testing. To preview the executable arguments without launching, add `--dry-run`.

This is the manual integration test to run next. The automated engine harness does not launch these two interactive windows or play a connected run. The three-peer automated wire harness exercises the native message codec and dispatch without opening sockets. A successful fastmp test covers actual native local multiplayer; a subsequent normal Steam co-op session is still needed to verify Steam transport and cross-OS play.
