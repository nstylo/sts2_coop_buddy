using Godot;
using HarmonyLib;
using System.Reflection;
using System.Globalization;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rewards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Pooling;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace RewardViewer.Tests;

internal static class RuntimeTests
{
    private static int _passed;
    private static List<CardModel>? _inspected;
    private static int _inspectedIndex;

    internal static async void Run()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        try
        {
            var gameVersion = ReleaseInfoManager.Instance.SemVer ?? throw new InvalidOperationException("Missing game version");
            GD.Print($"TEST_GAME_VERSION: {gameVersion}");
            await ModManager.Initialize(new ModManagerFileIo(), new ModSettings
            {
                PlayerAgreedToModLoading = true,
                ModList = [new SettingsSaveMod { Id = ModEntry.Id, Source = ModSource.ModsDirectory }]
            }, gameVersion);
            Check(ModManager.Mods.Any(mod => mod.manifest?.id == ModEntry.Id &&
                mod.state == ModLoadState.Loaded && mod.errors == null), "native loader applies all Harmony patches");

            TestMode.IsOn = true;
            await OneTimeInitialization.ExecuteVeryEarly();
            OneTimeInitialization.ExecuteEssential();
            SaveManager.Instance.InitPrefsDataForTest();
            SaveManager.Instance.InitProfileId(1);
            SaveManager.Instance.InitProgressData();
            LocalContext.NetId = 1;
            var local = Player.CreateForNewRun<Ironclad>(UnlockState.all, 1);
            var remote = Player.CreateForNewRun<Silent>(UnlockState.all, 1000);
            var thirdPlayer = Player.CreateForNewRun<Ironclad>(UnlockState.all, 2000);
            var fourthPlayer = Player.CreateForNewRun<Ironclad>(UnlockState.all, 3000);
            var state = RunState.CreateForTest([local, remote, thirdPlayer, fourthPlayer], seed: "REWARDVIEWER");
            state.AppendToMapPointHistory(MapPointType.Monster, RoomType.Monster, null);
            var net = DispatchProxy.Create<INetGameService, MultiplayerStub>();
            RunManager.Instance.SetUpTest(state, net);
            var buffer = new RunLocationTargetedMessageBuffer(net);
            using var choices = new PlayerChoiceSynchronizer(net, state);
            using var sync = new RewardsSetSynchronizer(buffer, net, state, 1);
            MixedPeerTests.Run(Check);
            CardReward Make(Player owner, params CardModel[] cards) => new(cards,
                CardCreationSource.Encounter, owner, CardCreationOptions.ForRoom(owner, RoomType.Monster), choices);
            Task<bool> Start(CardReward reward) => (Task<bool>)AccessTools.Method(typeof(CardReward), "OnSelect").Invoke(reward, null)!;
            void Cancel(OpenReward entry) => choices.ReceiveReplayChoice(entry.Player, entry.ChoiceId!.Value,
                new NetPlayerChoiceResult { type = PlayerChoiceType.Index, indexes = [] });

            var bash = state.CreateCard<Bash>(remote);
            var strike = state.CreateCard<StrikeIronclad>(remote);
            var reward = Make(remote, bash, strike);
            _ = sync.BeginRewardsSet(new RewardsSet(remote, sync).WithCustomRewards([reward]));
            Check(RewardTracker.GetOpenRewards().Count == 0, "generated but unopened rewards have no icon");
            var task = Start(reward);
            var active = RewardTracker.GetOpenRewards().Single();
            Check(active.Reward == reward && active.Player == remote && !task.IsCompleted,
                "native OnSelect captures only the opened reward and its pending task");
            Check(active.Reward.Cards.SequenceEqual(new CardModel[] { bash, strike }), "exact native options and order retained");
            uint id = active.ChoiceId!.Value;
            _ = choices.ReserveChoiceId(remote);
            Check(active.ChoiceId == id, "unrelated reservations cannot overwrite a waiting reward choice");
            choices.ReceiveReplayChoice(remote, id + 99, new NetPlayerChoiceResult { type = PlayerChoiceType.Index, indexes = [] });
            Check(RewardTracker.GetOpenRewards().Count == 1, "unrelated native choice results do not close a reward");
            Cancel(active);
            await task;
            Check(RewardTracker.GetOpenRewards().Count == 0, "native cancel removes the open reward without taking a card");
            Check(!reward.SuccessfullySelected, "backing out leaves the reward available to reopen");
            task = Start(reward);
            Check(RewardTracker.GetOpenRewards().Count == 1, "reopening creates a fresh visible reward");
            Cancel(RewardTracker.GetOpenRewards().Single());
            await task;

            // Deep-copy checks use real card mutation and the game's serialization schema.
            var clone = (CardModel)bash.ClonePreservingMutability();
            string before = RewardTracker.Fingerprint([bash]);
            clone.UpgradeInternal();
            Check(RewardTracker.Fingerprint([bash]) == before && clone.IsUpgraded, "preview upgrades do not mutate the reward");
            bash.UpgradeInternal();
            Check(RewardTracker.Fingerprint([bash]) != before, "live upgrade changes are detected");
            before = RewardTracker.Fingerprint([bash]);
            bash.EnchantInternal(ModelDb.Enchantment<Sharp>().ToMutable(), 2);
            var enchanted = (CardModel)bash.ClonePreservingMutability();
            Check(RewardTracker.Fingerprint([bash]) != before && enchanted.Enchantment?.Amount == 2 &&
                enchanted.Enchantment != bash.Enchantment, "enchantments are preserved in independent copies");
            var genetic = state.CreateCard<GeneticAlgorithm>(remote);
            before = RewardTracker.Fingerprint([genetic]);
            genetic.CurrentBlock = 29;
            var geneticCopy = (GeneticAlgorithm)genetic.ClonePreservingMutability();
            Check(RewardTracker.Fingerprint([genetic]) != before && geneticCopy.CurrentBlock == 29 &&
                geneticCopy.DynamicVars.Block.BaseValue == 29, "saved properties and calculated card values survive cloning");

            var parentTask = Start(Make(remote, bash));
            var parent = RewardTracker.GetOpenRewards().Single();
            var nestedTask = Start(Make(remote, strike));
            var nested = RewardTracker.GetOpenRewards().Single();
            Check(nested != parent && nested.Reward.Cards.Single() == strike, "only the innermost opened reward is advertised");
            Cancel(nested);
            await nestedTask;
            Check(RewardTracker.GetOpenRewards().Single() == parent && !parentTask.IsCompleted,
                "closing a nested reward restores the still-open parent");
            Cancel(parent);
            await parentTask;

            // A short-lived result must be hidden even if native post-pick animation is pending.
            var delayed = new TaskCompletionSource<bool>();
            var pending = RewardTracker.Begin(Make(remote, bash), choices);
            RewardTracker.CompleteSetup(pending, delayed.Task, null);
            choices.ReserveChoiceId(remote);
            choices.ReceiveReplayChoice(remote, pending.ChoiceId!.Value, new NetPlayerChoiceResult { type = PlayerChoiceType.Index, indexes = [0] });
            Check(RewardTracker.GetOpenRewards().Count == 0 && !delayed.Task.IsCompleted,
                "a chosen card disappears before completion animations finish");
            choices.ReserveChoiceId(remote);
            Check(RewardTracker.GetOpenRewards().Count == 0,
                "unrelated choices during card-obtained hooks cannot reopen a picked reward");
            pending.Reward.Reroll();
            choices.ReserveChoiceId(remote);
            Check(RewardTracker.GetOpenRewards().Single() == pending, "a further native reward pick can resume the same selection");
            delayed.SetCanceled();
            Check(RewardTracker.GetOpenRewards().Count == 0, "canceled tasks cannot leave stale rewards");

            await CollaborationTests.Run(choices, [local, remote, thirdPlayer], Check);

            // Start only the UI nodes the viewer uses. Test-only patches skip unrelated
            // game startup, Steam, full HUD initialization and run processing.
            var harness = new Harmony("RewardViewer.RuntimeTests.UiHarness");
            foreach (var (type, method) in new (Type, string)[] {
                (typeof(NGame), "_EnterTree"), (typeof(NGame), "_Ready"), (typeof(NGame), "_ExitTree"),
                (typeof(NRun), "_Ready"), (typeof(NRun), "_Process"), (typeof(NRun), "_Notification"),
                (typeof(NGlobalUi), "_Ready"), (typeof(NOverlayStack), "_Ready"),
                (typeof(NMultiplayerPlayerStateContainer), "_EnterTree"), (typeof(NMultiplayerPlayerStateContainer), "_ExitTree"),
                (typeof(NMultiplayerPlayerState), "_Ready"), (typeof(NMultiplayerPlayerState), "_ExitTree") })
                harness.Patch(AccessTools.DeclaredMethod(type, method), prefix: new HarmonyMethod(typeof(RuntimeTests), nameof(Skip)));
            var game = new NGame();
            Set(typeof(NGame), null, "Instance", game);
            var input = new NInputManager();
            var controller = new NControllerManager();
            var hotkeys = new NHotkeyManager();
            Set(typeof(NGame), game, "HotkeyManager", hotkeys);
            Set(typeof(NInputManager), input, "ControllerManager", controller);
            Set(typeof(NGame), game, "InputManager", input);
            var scenes = new NSceneContainer();
            Set(typeof(NGame), game, "RootSceneContainer", scenes);
            var inactiveCursors = new NRemoteMouseCursorContainer();
            Set(typeof(NGame), game, "RemoteCursorContainer", inactiveCursors);
            RewardCursorPatch.Refresh();
            Check(true, "cursor refresh is safe before initialization and after session teardown");
            Set(typeof(NGame), game, "RemoteCursorContainer", null);
            inactiveCursors.Free();
            game.AddChild(scenes);
            var hoverTips = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            Set(typeof(NGame), game, "HoverTipsContainer", hoverTips);
            game.AddChild(hoverTips);
            tree.Root.AddChild(game);
            harness.Patch(AccessTools.Method(typeof(NInspectCardScreen), "Open"),
                prefix: new HarmonyMethod(typeof(RuntimeTests), nameof(CaptureInspection)));
            harness.Patch(AccessTools.Method(typeof(NInspectCardScreen), "Close"),
                prefix: new HarmonyMethod(typeof(RuntimeTests), nameof(Skip)));
            game.InspectCardScreen = new NInspectCardScreen { Visible = false };
            var run = new NRun();
            var ui = new NGlobalUi();
            Set(typeof(NRun), run, "GlobalUi", ui);
            var rooms = new NSceneContainer();
            AccessTools.Field(typeof(NRun), "_roomContainer").SetValue(run, rooms);
            var stack = new NOverlayStack();
            var backstop = new Control { Name = "OverlayBackstop" };
            stack.AddChild(backstop);
            AccessTools.Field(typeof(NOverlayStack), "_backstop").SetValue(stack, backstop);
            Set(typeof(NGlobalUi), ui, "Overlays", stack);
            var map = new NMapScreen();
            Set(typeof(NGlobalUi), ui, "MapScreen", map);
            var capstones = new NCapstoneContainer();
            Set(typeof(NGlobalUi), ui, "CapstoneContainer", capstones);
            var playerRows = new NMultiplayerPlayerStateContainer();
            Set(typeof(NGlobalUi), ui, "MultiplayerPlayerContainer", playerRows);
            ui.AddChild(playerRows);
            run.AddChild(ui);
            ui.AddChild(stack);
            scenes.SetCurrentScene(run);
            AtlasManager.LoadAllAtlases();
            NCard.InitPool();
            NGridCardHolder.InitPool();
            TestMode.IsOn = false;

            CardModel[] ownCards = [state.CreateCard<Bash>(local), state.CreateCard<StrikeIronclad>(local), state.CreateCard<DefendIronclad>(local)];
            var ownScreen = NCardRewardSelectionScreen.ShowScreen(ownCards.Select(card => new CardCreationResult(card)).ToArray(), [])!;
            var ownChoice = ownScreen.OptionSelected();
            var ownPending = new TaskCompletionSource<bool>();
            var own = RewardTracker.Begin(Make(local, ownCards), choices);
            choices.ReserveChoiceId(local);
            RewardTracker.CompleteSetup(own, ownPending.Task, ownScreen);
            var originalLocalHolders = NativeRewardScreen.HoldersInOfferOrder(ownScreen);
            uint? originalChoiceId = own.ChoiceId;
            string originalLocalModels = RewardTracker.Fingerprint(ownCards);
            var originalButtonOffset = ownScreen.GetNode<Control>("UI/RewardAlternatives").OffsetTop;
            var remoteTask = Start(Make(remote, bash, strike));
            active = RewardTracker.GetOpenRewards().Single(entry => entry.Player == remote);
            var localRow = MakeRow(local, new Vector2(12, 100));
            var remoteRow = MakeRow(remote, new Vector2(12, 172));
            playerRows.AddChild(localRow);
            playerRows.AddChild(remoteRow);
            RewardOverlay.Attach(run);
            await Frame(tree);
            var localButton = run.GetNode<TextureButton>("RewardViewerLayer/RewardViewer/RewardViewerPlayer1");
            var remoteButton = run.GetNode<TextureButton>("RewardViewerLayer/RewardViewer/RewardViewerPlayer1000");
            Check(localButton.Visible && remoteButton.Visible, "open local and remote rewards have player-row buttons");
            Check(remoteButton.TextureNormal == MegaCrit.Sts2.Core.Entities.Multiplayer.NetScreenType.CardSelection.GetLocationIcon(),
                "reward button reuses the exact vanilla status icon");
            Check(remoteButton.GetGlobalRect() == remoteRow.GetNode<Control>("%LocationContainer").GetGlobalRect(),
                "clickable icon aligns with the vanilla player's status slot");
            Check(remoteButton.MouseDefaultCursorShape == Control.CursorShape.Arrow,
                "reward icon preserves the game's custom arrow cursor instead of a system hand");
            var iconReticle = remoteButton.GetNode<NSelectionReticle>("RewardViewerHover/Reticle");
            remoteButton.EmitSignal(Control.SignalName.MouseEntered);
            await Delay(tree);
            Check(iconReticle.IsSelected && remoteButton.Scale.X > 1.1f,
                "reward icon hover reuses native selection corners and enlarges the icon");
            remoteButton.EmitSignal(Control.SignalName.MouseExited);
            await Delay(tree);
            Check(!iconReticle.IsSelected && remoteButton.Scale.IsEqualApprox(Vector2.One),
                "leaving the reward icon restores its size and removes hover corners");
            remoteButton.EmitSignal(BaseButton.SignalName.Pressed);
            var preview = stack.Peek() as NCardRewardSelectionScreen;
            Check(preview != null && NativeRewardScreen.IsPreview(preview) && !ownScreen.Visible && !ownChoice.IsCompleted,
                "switching to a teammate hides and preserves the original pending picker");
            await Frame(tree);
            var rendered = Descendants(preview!).OfType<NCard>().ToArray();
            Check(rendered.Length == 2 && rendered[0].Model != bash && rendered[0].Model!.IsUpgraded &&
                rendered[0].Model!.Enchantment?.Amount == 2, "native full-size reward scene renders exact cloned cards");
            var navigation = preview!.GetNode<Control>("UI/RewardAlternatives");
            var backButton = navigation.GetNode<NCardRewardAlternativeButton>("RewardViewerBack");
            Check(navigation.GetChildCount() == 1 && backButton.GetNode<Label>("Label").Text == "Back",
                "spectator replaces gameplay alternatives with a native Back button while the local picker is open");
            var teammateBanner = preview.GetNode<NCommonBanner>("UI/Banner");
            Check(teammateBanner.label.Text == NativeRewardScreen.ObservingTitle(remoteButton.TooltipText) && teammateBanner.SelfModulate != Colors.White,
                "spectator banner identifies the observed player with a distinct tint");
            string cappedTitle = NativeRewardScreen.ObservingTitle(string.Concat(Enumerable.Repeat("e\u0301", 40)));
            Check(cappedTitle.EndsWith("…") && new StringInfo(cappedTitle["Observing ".Length..]).LengthInTextElements == 24 &&
                NativeRewardScreen.ObservingTitle("  Alice\n  Bob  ") == "Observing Alice Bob",
                "observed names respect the length limit without splitting Unicode characters or allowing extra lines");
            Check(((IOverlayScreen)preview).ScreenType == NetScreenType.None && ((IOverlayScreen)ownScreen).ScreenType == NetScreenType.CardSelection,
                "spectating cannot publish the vanilla choosing-card status icon");
            var localReticle = localButton.GetNode<NSelectionReticle>("RewardViewerHover/Reticle");
            Check(iconReticle.IsSelected && !localReticle.IsSelected,
                "native selection corners identify whose reward is being viewed");
            remoteButton.EmitSignal(Control.SignalName.MouseEntered);
            await Delay(tree);
            remoteButton.EmitSignal(Control.SignalName.MouseExited);
            await Delay(tree);
            Check(iconReticle.IsSelected && remoteButton.Scale.IsEqualApprox(Vector2.One) &&
                Descendants(remoteButton).OfType<NSelectionReticle>().Count() == 1 &&
                !Descendants(remoteButton).OfType<Panel>().Any(),
                "selection and hover share one native corner graphic which remains selected after mouse exit");
            // Directly invoke selection handlers to test the guard independently of input timing.
            var forbiddenChoice = preview.OptionSelected();
            AccessTools.Method(typeof(NCardRewardSelectionScreen), "SelectCard").Invoke(preview, new object?[] { null });
            AccessTools.Method(typeof(NCardRewardSelectionScreen), "OnAlternateRewardSelected").Invoke(preview, [0]);
            AccessTools.Method(typeof(NCardRewardSelectionScreen), "InspectCard").Invoke(preview, new object?[] { null });
            Check(!forbiddenChoice.IsCompleted && !remoteTask.IsCompleted && !ownChoice.IsCompleted,
                "mouse, controller and alternative handlers cannot resolve a teammate's reward");
            var offered = NativeRewardScreen.HoldersInOfferOrder(preview);
            var holder = offered[0]!;
            var otherHolder = offered[1]!;
            var cardRow = preview.GetNode<Control>("UI/CardRow");
            cardRow.MoveChild(holder, cardRow.GetChildCount() - 1); // native hover moves the card to the front
            AccessTools.Method(typeof(NCardRewardSelectionScreen), "SelectCard").Invoke(preview, [holder]);
            Check(RewardCollaboration.Current!.Votes(RewardKey.For(active)!.Value).Single().CardIndex == 0 &&
                _inspected == null && !remoteTask.IsCompleted && !forbiddenChoice.IsCompleted,
                "hover reordering cannot change which offered card receives the vote");
            await Delay(tree);
            var ownVoteIcon = preview.GetNode<TextureRect>("RewardViewerVotes/Card0Votes/Vote1");
            Check(ownVoteIcon.Texture == local.Character.IconTexture && ownVoteIcon.CustomMinimumSize == new Vector2(48, 48) &&
                holder.CardNode!.CardHighlight.Modulate == NCardHighlight.gold &&
                ((ShaderMaterial)holder.CardNode.CardHighlight.Material).GetShaderParameter("width").AsSingle() > 0,
                "a local recommendation shows the voter's native portrait alongside the native card glow");
            AccessTools.Method(typeof(RewardCollaboration), "ReceiveVote").Invoke(RewardCollaboration.Current,
                [new ZZZRewardViewerVoteMessage { Reward = RewardKey.For(active)!.Value, CardIndex = 0 }, 2000UL]);
            await Delay(tree);
            var voteIcon = preview.GetNode<TextureRect>("RewardViewerVotes/Card0Votes/Vote2000");
            Check(voteIcon.Texture == thirdPlayer.Character.IconTexture && voteIcon.CustomMinimumSize == new Vector2(48, 48),
                "teammate recommendations use larger native portraits");
            AccessTools.Method(typeof(RewardCollaboration), "ReceiveVote").Invoke(RewardCollaboration.Current,
                [new ZZZRewardViewerVoteMessage { Reward = RewardKey.For(active)!.Value, CardIndex = 0 }, 3000UL]);
            await Delay(tree);
            voteIcon = preview.GetNode<TextureRect>("RewardViewerVotes/Card0Votes/Vote2000");
            var twinIcon = preview.GetNode<TextureRect>("RewardViewerVotes/Card0Votes/Vote3000");
            Check(voteIcon.Texture == twinIcon.Texture &&
                RewardVoteDisplay.PortraitAt([voteIcon, twinIcon], voteIcon.GetGlobalTransform() * (voteIcon.Size * 0.5f)) == voteIcon &&
                RewardVoteDisplay.PortraitAt([voteIcon, twinIcon], twinIcon.GetGlobalTransform() * (twinIcon.Size * 0.5f)) == twinIcon &&
                voteIcon.MouseFilter == Control.MouseFilterEnum.Ignore && twinIcon.MouseFilter == Control.MouseFilterEnum.Ignore,
                "same-character portraits have distinct scaled hover regions without intercepting card input");
            var firstNameTip = RewardVoteDisplay.ShowVoterName(voteIcon, 2000, RunManager.Instance.NetService.Platform)!;
            await Frame(tree);
            var firstTipBody = firstNameTip.GetNode<Control>("textHoverTipContainer").GetChild<Control>(0);
            string firstName = firstTipBody.GetNode<RichTextLabel>("%Description").Text;
            Check(firstNameTip.GetParent() == hoverTips && !firstTipBody.GetNode<Control>("%Title").Visible &&
                firstName.Contains(PlatformUtil.GetPlayerName(RunManager.Instance.NetService.Platform, 2000)),
                "vote name popup uses the real native hover-tip scene and player-name template");
            NHoverTipSet.Remove(voteIcon);
            var secondNameTip = RewardVoteDisplay.ShowVoterName(twinIcon, 3000, RunManager.Instance.NetService.Platform)!;
            await Frame(tree);
            string secondName = secondNameTip.GetNode<Control>("textHoverTipContainer").GetChild<Control>(0)
                .GetNode<RichTextLabel>("%Description").Text;
            Check(firstName != secondName && secondName.Contains(PlatformUtil.GetPlayerName(RunManager.Instance.NetService.Platform, 3000)),
                "identical character portraits show their individual voters' names");
            AccessTools.Method(typeof(RewardCollaboration), "ReceiveVote").Invoke(RewardCollaboration.Current,
                [new ZZZRewardViewerVoteMessage { Reward = RewardKey.For(active)!.Value, CardIndex = -1 }, 3000UL]);
            await Delay(tree);
            Check(hoverTips.GetChildCount() == 0, "removing a vote also removes its native name popup");
            voteIcon = preview.GetNode<TextureRect>("RewardViewerVotes/Card0Votes/Vote2000");
            cardRow.MoveChild(otherHolder, cardRow.GetChildCount() - 1);
            await Delay(tree);
            var voteRow = voteIcon.GetParent<Control>();
            Vector2 expectedCorner = holder.GetGlobalTransform() *
                (NCard.defaultSize * 0.5f - voteRow.Size - new Vector2(10, 10));
            Check(voteIcon.IsVisibleInTree() && voteRow.GlobalPosition.DistanceTo(expectedCorner) < 1 &&
                preview.GetNode<Control>("RewardViewerVotes").ZIndex > 0,
                "teammate portrait stays on its offered card without hover after the native node order changes");
            AccessTools.Method(typeof(NCardRewardSelectionScreen), "InspectCard").Invoke(preview, [holder]);
            Check(_inspected?.Count == 2 && _inspectedIndex == 0 && _inspected[0] != bash && !remoteTask.IsCompleted,
                "inspection keeps the correct original offer index after hover reordering");
            AccessTools.Method(typeof(NCardRewardSelectionScreen), "SelectCard").Invoke(preview, [otherHolder]);
            await Delay(tree);
            Check(((ShaderMaterial)holder.CardNode!.CardHighlight.Material).GetShaderParameter("width").AsSingle() == 0 &&
                otherHolder.CardNode!.CardHighlight.Modulate == NCardHighlight.gold &&
                preview.GetNodeOrNull<TextureRect>("RewardViewerVotes/Card0Votes/Vote1") == null &&
                preview.GetNode<TextureRect>("RewardViewerVotes/Card1Votes/Vote1").Texture == local.Character.IconTexture &&
                preview.GetNode<TextureRect>("RewardViewerVotes/Card0Votes/Vote2000").Texture == thirdPlayer.Character.IconTexture,
                "moving a local vote moves its portrait and glow while preserving teammate recommendations");
            AccessTools.Method(typeof(NCardRewardSelectionScreen), "SelectCard").Invoke(preview, [otherHolder]);
            await Delay(tree);
            Check(((ShaderMaterial)otherHolder.CardNode!.CardHighlight.Material).GetShaderParameter("width").AsSingle() == 0 &&
                preview.GetNodeOrNull<Control>("RewardViewerVotes/Card1Votes") == null &&
                preview.GetNode<TextureRect>("RewardViewerVotes/Card0Votes/Vote2000").IsVisibleInTree(),
                "clearing a local vote removes its portrait and glow while preserving teammate recommendations");
            preview.AfterOverlayHidden();
            bool hiddenBackDisabled = !backButton.IsEnabled;
            preview.AfterOverlayShown();
            Check(hiddenBackDisabled && backButton.IsEnabled && !ownChoice.IsCompleted,
                "spectator navigation releases its native hotkeys while hidden and restores them when shown");
            backButton.EmitSignal(NClickableControl.SignalName.Released, backButton);
            Check(stack.Peek() == ownScreen && ownScreen.Visible && !ownChoice.IsCompleted &&
                localReticle.IsSelected && !iconReticle.IsSelected,
                "native Back button restores the same local picker without resolving either player's choice");
            Check(NativeRewardScreen.HoldersInOfferOrder(ownScreen).SequenceEqual(originalLocalHolders) &&
                originalLocalHolders.All(card => card!.Position.IsEqualApprox(Vector2.Zero) && card.Modulate == Colors.Black) &&
                own.ChoiceId == originalChoiceId && RewardTracker.Fingerprint(ownCards) == originalLocalModels,
                "returning to the local reward replays the entrance on the original holders without replacing cards or choice IDs");
            AccessTools.Method(typeof(NCardRewardSelectionScreen), "SelectCard").Invoke(ownScreen, [originalLocalHolders[0]]);
            Check(!ownChoice.IsCompleted && NativeRewardScreen.IsEntranceBlocked(ownScreen),
                "the entrance click guard prevents an accidental selection on return");
            await Delay(tree);
            Check(originalLocalHolders[0]!.Position.X < 0 && originalLocalHolders[^1]!.Position.X > 0 &&
                originalLocalHolders[0]!.Modulate.R > 0 && originalLocalHolders[0]!.Modulate.R < 1,
                "restored local cards visibly spread and fade with the native entrance curves");
            remoteButton.EmitSignal(BaseButton.SignalName.Pressed);
            localButton.EmitSignal(BaseButton.SignalName.Pressed);
            remoteButton.EmitSignal(BaseButton.SignalName.Pressed);
            localButton.EmitSignal(BaseButton.SignalName.Pressed);
            Check(!ownChoice.IsCompleted && NativeRewardScreen.HoldersInOfferOrder(ownScreen).SequenceEqual(originalLocalHolders),
                "rapid reward switches preserve the same pending choice and card holders");
            await Task.Delay(550);
            Check(Mathf.IsEqualApprox(ownScreen.GetNode<Control>("UI/RewardAlternatives").OffsetTop, originalButtonOffset) &&
                originalLocalHolders[0]!.Position.IsEqualApprox(new Vector2(-350, 0)) &&
                originalLocalHolders[^1]!.Position.IsEqualApprox(new Vector2(350, 0)),
                "repeated entrances finish at the original layout without accumulating position drift");
            AccessTools.Method(typeof(RewardCollaboration), "ReceiveVote").Invoke(RewardCollaboration.Current,
                [new ZZZRewardViewerVoteMessage { Reward = RewardKey.For(own)!.Value, CardIndex = 0 }, 1000UL]);
            await Delay(tree);
            Check(ownScreen.GetNode<TextureRect>("RewardViewerVotes/Card0Votes/Vote1000").Texture == remote.Character.IconTexture && !ownChoice.IsCompleted,
                "reward owner sees the teammate's native recommendation icon while retaining selection control");
            remoteButton.EmitSignal(BaseButton.SignalName.Pressed);
            var bindings = (Dictionary<StringName, List<Action>>)AccessTools.Field(typeof(NHotkeyManager),
                "_hotkeyPressedBindings").GetValue(hotkeys)!;
            bindings[MegaInput.cancel].Last().Invoke();
            Check(stack.Peek() == ownScreen && !ownChoice.IsCompleted,
                "native Back hotkey closes only the spectator and preserves the local reward");
            remoteButton.EmitSignal(BaseButton.SignalName.Pressed);
            active.Reward.Reroll();
            await Delay(tree);
            Check(stack.Peek() is NCardRewardSelectionScreen rerolled && NativeRewardScreen.IsPreview(rerolled) &&
                Descendants(rerolled).OfType<NCard>().All(card => card.Model!.Id != bash.Id), "remote reroll refreshes the native preview");
            Cancel(active);
            await remoteTask;
            await Delay(tree);
            Check(stack.Peek() == ownScreen && ownScreen.Visible && !remoteButton.Visible,
                "remote close clears its icon and automatically restores the local picker");
            remoteTask = Start(Make(remote, bash));
            remoteButton.EmitSignal(BaseButton.SignalName.Pressed);
            var reconnect = MixedPeerTests.Disconnect(remote.NetId);
            await Delay(tree);
            Check(!remoteButton.Visible && stack.Peek() == ownScreen, "disconnect removes both the icon and watched preview");
            Cancel(RewardTracker.GetOpenRewards().Single(entry => entry.Player == remote));
            await remoteTask;
            reconnect();
            await Task.Delay(400);
            AccessTools.Method(typeof(NCardRewardSelectionScreen), "SelectCard").Invoke(ownScreen,
                [NativeRewardScreen.HoldersInOfferOrder(ownScreen)[0]]);
            Check(await ownChoice == 0, "unmarked local picker retains the native selectable behavior");
            remoteTask = Start(Make(remote, bash));
            await Delay(tree);
            remoteButton.EmitSignal(BaseButton.SignalName.Pressed);
            var finalPreview = (NCardRewardSelectionScreen)stack.Peek()!;
            var closeButton = finalPreview.GetNode<NCardRewardAlternativeButton>("UI/RewardAlternatives/RewardViewerBack");
            ownPending.SetResult(true);
            stack.Remove(ownScreen);
            await Delay(tree);
            Check(closeButton.GetNode<Label>("Label").Text == "Close",
                "spectator navigation changes to Close when the background local reward ends");
            closeButton.EmitSignal(NClickableControl.SignalName.Released, closeButton);
            Check(stack.ScreenCount == 0 && !remoteTask.IsCompleted,
                "native Close button returns to the underlying game without selecting or skipping the teammate's reward");
            Cancel(RewardTracker.GetOpenRewards().Single(entry => entry.Player == remote));
            await remoteTask;
            RewardTracker.Clear();
            await Delay(tree);
            Check(!localButton.Visible && RewardTracker.GetOpenRewards().Count == 0, "room/run cleanup removes all reward icons");
            TestMode.IsOn = true;
            game.InspectCardScreen.Free();
            game.Free();
            Set(typeof(NGame), null, "Instance", null);
            input.Free();
            controller.Free();
            hotkeys.Free();
            map.Free();
            rooms.Free();
            capstones.Free();
            await Frame(tree);
            FreePools();
            RunManager.Instance.CleanUp();
            GD.Print($"REWARD_VIEWER_TESTS_PASSED: {_passed}");
            tree.Quit();
        }
        catch (Exception exception)
        {
            GD.PrintErr("REWARD_VIEWER_TESTS_FAILED: " + exception);
            tree.Quit(1);
        }
    }

    private static bool Skip() => false;
    private static void FreePools()
    {
        // Unlike the full game, this short harness exits immediately after using pools.
        var pools = (System.Collections.IDictionary)AccessTools.Field(typeof(NodePool), "_pools").GetValue(null)!;
        var nodes = new HashSet<Node>();
        foreach (var pool in pools.Values)
            foreach (string field in new[] { "_freeObjects", "_usedObjects" })
                foreach (Node node in (System.Collections.IEnumerable)AccessTools.Field(pool!.GetType(), field).GetValue(pool)!)
                    nodes.Add(node);
        foreach (var node in nodes)
            if (GodotObject.IsInstanceValid(node)) node.Free();
        pools.Clear();
    }
    private static bool CaptureInspection(List<CardModel> cards, int index)
    {
        _inspected = cards;
        _inspectedIndex = index;
        return false;
    }
    private static void Set(Type type, object? instance, string property, object? value) =>
        AccessTools.Property(type, property).SetValue(instance, value);
    private static async Task Frame(SceneTree tree) { await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame); await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame); }
    private static async Task Delay(SceneTree tree) => await tree.ToSignal(tree.CreateTimer(0.12), SceneTreeTimer.SignalName.Timeout);
    private static NMultiplayerPlayerState MakeRow(Player player, Vector2 position)
    {
        var row = new NMultiplayerPlayerState { Position = position, Name = $"Player{player.NetId}" };
        Set(typeof(NMultiplayerPlayerState), row, "Player", player);
        var slot = new Control { Name = "LocationContainer", Position = new Vector2(150, 0), Size = new Vector2(28, 28) };
        row.AddChild(slot);
        slot.Owner = row;
        slot.UniqueNameInOwner = true;
        var icon = new TextureRect { Name = "LocationIcon" };
        slot.AddChild(icon);
        icon.Owner = row;
        icon.UniqueNameInOwner = true;
        return row;
    }
    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        GD.Print("PASS: " + name);
        _passed++;
    }
}

// Use native no-op transport to test UI/lifecycle without Steam or network sockets.
public class MultiplayerStub : DispatchProxy
{
    private readonly NetSingleplayerGameService _inner = new();
    protected override object? Invoke(MethodInfo? method, object?[]? args) =>
        method!.Name == "get_Type" ? NetGameType.Client : method.Invoke(_inner, args);
}
