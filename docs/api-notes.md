# Installed STS2 API findings

Inspected locally on 2026-10-25 using ILSpy against the installed Steam macOS arm64 assemblies. Decompiled game source is kept outside the repository and is not distributed.

| Item | Verified value |
| --- | --- |
| Release | v0.111.0 |
| Commit | 41cef1ea |
| Build timestamp | 2026-08-13T17:39:18-07:00 |
| Target framework | net9.0 |
| Bundled CLR | Microsoft.NETCore.App 9.0.7 |
| Engine | MegaDot 4.5.1.m.14.mono.custom_build |
| sts2.dll SHA-256 | 9cb4f1ad8c9f284aa8fec3122ffd6d780bbf543d875c817abdd12ff63fbf12b4 |

## Native lifecycle

- `RewardsSet.Offer(): Task` calls `GenerateWithoutOffering(): Task`, then `RewardsSetSynchronizer.BeginRewardsSet(this)`. It creates a rewards screen only when `LocalContext.IsMe(Player)` is true, but generates and tracks rewards for remote players too.
- `GenerateWithoutOffering` populates each reward, runs modification hooks (including newly added rewards), awaits their completion, and sorts the results. Tracking at `BeginRewardsSet` therefore observes the final initial options, rather than an intermediate `CardFactory` result.
- `RewardsSetSynchronizer.BeginRewardsSet(RewardsSet set): Task` assigns a per-player set ID, pushes a state on that player's stack, consumes buffered native messages, and returns a task completed when that set is finished or skipped.
- `RewardsSetSynchronizer.HandleRewardSelectedMessage(RewardSelectedMessage, ulong)` routes the native message to the appropriate player's reward. `CardReward.OnSelect(): Task<bool>` uses `PlayerChoiceSynchronizer` for the selected option, including alternatives and rerolls.
- `RewardsSetSynchronizer.HandleRewardSetSkippedMessage(RewardSetSkippedMessage, ulong)` calls `OnSkipped` on remaining rewards and completes the set's task. Room exit sends these same native messages.
- `Reward.SuccessfullySelected` becomes true after a selection succeeds and its hooks complete. A canceled card screen returns false and leaves the reward available.
- `CardReward.Cards: IEnumerable<CardModel>` is a projection of its current card creation results. `Reroll()` clears and repopulates that list. `AfterGenerated` exists as a native event but can fire before later set-level modifications; observing the current models also covers those modifications.
- `LinkedRewardSet.Rewards: IReadOnlyList<Reward>` contains mutually linked options. Once its parent is selected, remaining card choices should no longer be displayed.

## Open picker and UI hooks (0.5.2)

`CardReward.OnSelect(): Task<bool>` executes locally for both the owning player and remote players after native `RewardSelectedMessage` processing. Its task covers the open card picker and ends on pick/skip/cancel. Tracking the enclosing rewards-set task would incorrectly include unopened rewards and already-closed pickers.

The viewer observes `PlayerChoiceSynchronizer.ReserveChoiceId(Player)` and its native `PlayerChoiceReceived` event. IDs distinguish reward choices from unrelated player choices. Normal picks/cancels hide immediately; a subsequent choice reservation supports continued selection. Alternatives retain the task so native rerolls continue without closing the reward.

`NMultiplayerPlayerState` owns `%LocationContainer` and `%LocationIcon`. `NetScreenType.CardSelection.GetLocationIcon()` returns `res://images/ui/reward_screen/reward_icon_card.png`, the existing vanilla status texture. A clickable texture button follows that slot on a layer above the reward backstop. Selection and hover share one instance of the native `res://scenes/ui/selection_reticle.tscn` (`NSelectionReticle.OnSelect` / `OnDeselect`) in a wrapper scaled for the 28-pixel slot, with the corners remaining visible for the displayed reward's owner after mouse exit. Hover adds a 16% icon enlargement. The button requests `CursorShape.Arrow`, preserving STS2's cursor installed by `NCursorManager`, rather than using the system pointing hand. Controller focus gets the same corners. When inactive, the original location indicator resumes native behavior.

The spectator scene is `res://scenes/screens/card_selection/card_reward_selection_screen.tscn`. The native scene requires its private `_options` and `_extraOptions` before `_Ready`; those two fields are populated with independent card copies and an empty alternatives list. `ConditionalWeakTable` marks only spectator instances. Harmony guards their `SelectCard`, `OnAlternateRewardSelected`, `InspectCard` and `PowerCardFtueCheck` methods. A spectator `_Ready` postfix changes its banner to `Observing <name>` with a cool tint and adds a native `NCardRewardAlternativeButton` for navigation. Names are capped at 24 grapheme clusters including an ellipsis, with whitespace normalized and pixel overflow trimmed. The button says Back while a local picker remains open and Close otherwise, and never calls `OptionSelected` or any reward alternative. Its `ScreenType` getter returns `None`, so `ScreenStateTracker` does not advertise a spectator as choosing their own reward. Original local pickers retain their native choice handlers. Spectator shown/hidden/closed hooks use `NHotkeyManager.AddBlockingScreen` / `RemoveBlockingScreen` to isolate background hotkeys. Back/Escape closes only the spectator. Its navigation button is disabled while hidden so native button hotkeys cannot affect screens above it.

`NOverlayStack.Push` hides the prior screen without exiting it or resolving its pending choice. `Remove(spectator)` restores it. The viewer never removes the owner's original picker. It closes the spectator when its tracked reward ends, changes, disconnects or leaves the room.

Returning from a spectator to a pending local picker replays the native entrance on the existing card holders: 350-pixel spacing, 0.5-second Expo movement, 1-second Cubic color fade, native banner animation and 0.5-second Back transition for the alternatives container. The alternatives' original anchored offset is captured in `_Ready` to avoid drift during rapid switches and resizing. Existing `_cardTween`/`_buttonTween` instances are killed before replay.

Replay does not call `RefreshOptions`, `AfterOverlayOpened`, or `OptionSelected`: models, control instances, native choice IDs and the pending completion task remain unchanged; tutorials and relic flashes are not repeated. The native `DisableCardsForShortTimeAfterOpening` helper is reused, with an additional 350-ms selection deadline so an older animation's continuation cannot re-enable picking during a newer entrance. Intermediate local screens are not animated when switching directly between two teammates.

## Native collaboration messages and cursors

- `MessageTypes.Initialize()` includes `ReflectionHelper.GetSubtypesInMods<INetMessage>()`; no BaseLib registration or protocol patch is necessary. Both public message classes have parameterless constructors and native `Serialize`/`Deserialize` methods.
- `INetGameService.RegisterMessageHandler<T>` supplies the original sender ID. `ShouldBroadcast = true` requests native host relay. The view/vote messages use `NetTransferMode.Reliable` and `ShouldBuffer = false`.
- `RewardKey` contains the owner ID, native choice ID and the first 64 bits of SHA-256 over `SerializableCard` JSON. This identifies the exact open offer without duplicating card synchronization. Vote packets carry an index or `-1` to clear. Unknown/disconnected voters, owner self-votes, invalid indices and stale offer keys are rejected.
- Each player can recommend one card per open reward. Local recommendations are periodically resent with view metadata for late subscribers. Native reward completion, rerolls, disconnects and room/run cleanup discard stale entries.
- `PeerInputSynchronizer` already sends normalized mouse/controller positions at a minimum interval of 50 ms. No new cursor-position packets are sent.
- `NRemoteMouseCursorContainer.UpdateCursorVisibility` normally hides card-selection cursors. A postfix overrides only reward-view visibility: both participants must identify the same active offer. Movement, interpolation, textures, DPI handling and input remain native. Unrelated screens and sessions retain native behavior.
- Card portraits reuse `res://scenes/ui/multiplayer_vote_icon.tscn`, including the character outline, at a 48-pixel minimum size. Each viewer sees all voters' portraits, including their own when observing a teammate. Their own recommendation also uses the native `NCardHighlight` gold glow. Moving/clearing the vote restores the previous highlight color and clears its shader width. No custom glow assets or card-model mutations are needed.
- `NGridCardHolder.OnFocus` calls `MoveToFrontSafely`, which changes child order. Vote placement, vote indices and inspection therefore map card-holder model references back to the screen's `_options`, never to the current child index. Regression checks reorder the native card nodes before voting and inspection.
- Vote-name popups use `NHoverTipSet.CreateAndShow` with the native `run_history.PLAYER_NAME` localization template and `HoverTip`, so styling, fonts, name escaping, layout and edge correction remain native. Full voter names come from `PlatformUtil.GetPlayerName`; plain banner/button text uses `GetPlayerNameRaw`. Portraits keep `MouseFilter.Ignore`; transformed pointer hit-testing adds the popup without intercepting card input. Native owner-exit cleanup and display cleanup remove stale popups.
- Portrait UI belongs to the screen above card artwork, follows the native card-holder transform each frame, and remains visible independently of hover. No mod nodes are attached to pooled cards.

Shared collaboration requires this mod on the host and participating peers. The native packet codec and three-peer relay are covered by the runtime harness; actual ENet/Steam transport still requires the interactive test.

## UI and models

- Native card scene: `res://scenes/cards/card.tscn`, implementing `NCard`.
- `NCard.UpdateVisuals(PileType, CardPreviewMode)` updates card text and calculated previews. It mutates preview values on the supplied model, so the viewer gives it an independent copy.
- `AbstractModel.ClonePreservingMutability(): AbstractModel` deep-copies mutable card fields and keeps ownership/context. The viewer calls it only for display copies.
- Avoid `CardModel.CreateClone()` and `RunState.CloneCard()` for this UI: they register clones in game state. Adding display cards to that scope could affect IDs and checksums.
- `CardModel.ToSerializable(): SerializableCard` includes model ID, upgrade level, enchantment, saved properties, and deck-added floor. JSON uses `JsonSerializationUtility.GetTypeInfo<SerializableCard>()` so the game's converters and property rules are respected.
- `RunLobby.PlayerIds` contains connected players; `RunState.Players` also contains disconnected players.

## Loader and dependencies

`[ModInitializer(nameof(Initialize))]` works on a static class. No `Node` subclass, source generator, PCK, or publicizer is needed for this implementation. The native loader searches `Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath()), "mods")`; on macOS this resolves inside `SlayTheSpire2.app/Contents/MacOS`.

The manifest marks `affects_gameplay: false`: the production mod observes native state and sends only advisory UI metadata. It changes no RNG and issues no gameplay choices. STS2 explicitly supports non-gameplay mod messages in its native type sorting and message bus. The test assembly is separate and must not be installed as a production mod.

Platform discovery is shared in `GamePaths.props`. The AnyCPU artifact is intended for both Mac architectures; current validation is recorded in the compatibility guide. Windows/Linux layout names follow the template, but their native game assemblies/runtimes have not been tested here. See the [compatibility matrix](compatibility.md).

## Open-source references consulted

- [Alchyr's template](https://github.com/Alchyr/ModTemplate-StS2): entrypoint, manifest shape, target framework, and references. The full template was unnecessary because this mod has no custom assets or scripts.
- [BaseLib](https://github.com/Alchyr/BaseLib-StS2): considered as an optional dependency; not needed for the inspected native reward lifecycle.
- [ViewMultiplayerCardRewards](https://github.com/gboyd068/Sts2-ViewMultiplayerCardRewards): useful comparison for reward collection and card inspection. Its implementation uses BaseLib custom messages; this project uses the newer installed game's existing remote reward objects instead. No source from that implementation is included.
