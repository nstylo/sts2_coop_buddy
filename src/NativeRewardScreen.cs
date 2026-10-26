using System.Runtime.CompilerServices;
using System.Globalization;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.addons.mega_text;

namespace CoopBuddy;

// Use the real picker scene and its native hover, tooltips, spacing and inspection.
// Only these marked instances are read-only; the original local picker is untouched.
internal static class NativeRewardScreen
{
    private sealed class Preview(OpenReward reward, CardModel[] cards, Action back)
    {
        internal OpenReward Reward { get; } = reward;
        internal CardModel[] Cards { get; } = cards;
        internal bool Inspecting { get; set; }
        internal Action Back { get; } = back;
        internal bool Blocking { get; set; }
        internal NCardRewardAlternativeButton? BackButton { get; set; }
    }

    internal const int MaxObserverNameLength = 24;

    private sealed class Entrance(Vector2 alternativesOffset)
    {
        internal Vector2 AlternativesOffset { get; } = alternativesOffset;
        internal ulong InputBlockedUntil { get; set; }
    }

    private static readonly ConditionalWeakTable<NCardRewardSelectionScreen, Preview> Previews = new();
    private static readonly ConditionalWeakTable<NCardRewardSelectionScreen, Entrance> Entrances = new();
    private static readonly AccessTools.FieldRef<NCardRewardSelectionScreen, IReadOnlyList<CardCreationResult>> Options =
        AccessTools.FieldRefAccess<NCardRewardSelectionScreen, IReadOnlyList<CardCreationResult>>("_options");
    private static readonly AccessTools.FieldRef<NCardRewardSelectionScreen, IReadOnlyList<CardRewardAlternative>> Alternatives =
        AccessTools.FieldRefAccess<NCardRewardSelectionScreen, IReadOnlyList<CardRewardAlternative>>("_extraOptions");
    private static readonly AccessTools.FieldRef<NCardRewardSelectionScreen, Tween?> CardTween =
        AccessTools.FieldRefAccess<NCardRewardSelectionScreen, Tween?>("_cardTween");
    private static readonly AccessTools.FieldRef<NCardRewardSelectionScreen, Tween?> ButtonTween =
        AccessTools.FieldRefAccess<NCardRewardSelectionScreen, Tween?>("_buttonTween");
    private static readonly System.Reflection.MethodInfo DisableCardsBriefly =
        AccessTools.Method(typeof(NCardRewardSelectionScreen), "DisableCardsForShortTimeAfterOpening");

    internal static void CaptureEntrance(NCardRewardSelectionScreen screen)
    {
        var buttons = screen.GetNode<Control>("UI/RewardAlternatives");
        Entrances.Add(screen, new Entrance(new Vector2(buttons.OffsetLeft, buttons.OffsetTop)));
    }

    internal static bool IsEntranceBlocked(NCardRewardSelectionScreen screen) =>
        Entrances.TryGetValue(screen, out var entrance) && Time.GetTicksMsec() < entrance.InputBlockedUntil;

    internal static void ReplayEntrance(NCardRewardSelectionScreen screen)
    {
        if (!Entrances.TryGetValue(screen, out var entrance)) return;
        // Replay the native RefreshOptions curves on the SAME holders/models. Calling
        // RefreshOptions or AfterOverlayOpened would rebuild controls or repeat tutorials/relic flashes.
        var holders = HoldersInOfferOrder(screen);
        CardTween(screen)?.Kill();
        var cards = screen.CreateTween().SetParallel();
        CardTween(screen) = cards;
        for (int i = 0; i < holders.Length; i++)
        {
            if (holders[i] is not { } holder) continue;
            holder.Position = Vector2.Zero;
            holder.Scale = holder.SmallScale;
            holder.Modulate = Colors.Black;
            var position = Vector2.Left * (holders.Length - 1) * 350f * 0.5f + Vector2.Right * 350f * i;
            cards.TweenProperty(holder, "position", position, 0.5).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
            cards.TweenProperty(holder, "modulate", Colors.White, 1.0).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        }
        screen.GetNode<NCommonBanner>("UI/Banner").AnimateIn();
        var buttons = screen.GetNode<Control>("UI/RewardAlternatives");
        var parentSize = buttons.GetParent<Control>().Size;
        var target = new Vector2(parentSize.X * buttons.AnchorLeft, parentSize.Y * buttons.AnchorTop) + entrance.AlternativesOffset;
        ButtonTween(screen)?.Kill();
        ButtonTween(screen) = screen.CreateTween();
        ButtonTween(screen)!.TweenProperty(buttons, "position", target, 0.5).SetEase(Tween.EaseType.Out)
            .SetTrans(Tween.TransitionType.Back).From(target - new Vector2(0, 50));
        // Keep the native click guard and cover rapid switches: an older guard's
        // continuation must not allow a click during the latest entrance animation.
        entrance.InputBlockedUntil = Time.GetTicksMsec() + 350;
        TaskHelper.RunSafely((Task)DisableCardsBriefly.Invoke(screen, null)!);
    }

    internal static NCardRewardSelectionScreen Create(OpenReward reward, Action back)
    {
        // CreateClone registers cards in the run. ClonePreservingMutability does not.
        var clones = reward.Reward.Cards.Select(card => (CardModel)card.ClonePreservingMutability()).ToArray();
        var screen = PreloadManager.Cache.GetScene("res://scenes/screens/card_selection/card_reward_selection_screen.tscn")
            .Instantiate<NCardRewardSelectionScreen>();
        screen.Name = "CoopBuddyPreview";
        Options(screen) = clones.Select(card => new CardCreationResult(card)).ToArray();
        Alternatives(screen) = Array.Empty<CardRewardAlternative>();
        Previews.Add(screen, new Preview(reward, clones, back));
        screen.TreeExiting += () => UpdateHotkeys(screen, false);
        return screen;
    }

    internal static bool IsPreview(NCardRewardSelectionScreen screen) => Previews.TryGetValue(screen, out _);

    internal static OpenReward? GetReward(NCardRewardSelectionScreen screen) =>
        Previews.TryGetValue(screen, out var preview) ? preview.Reward :
        RewardTracker.GetOpenRewards().FirstOrDefault(entry => entry.LocalScreen == screen);

    internal static string ObservingTitle(string name)
    {
        name = string.Join(" ", name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0) name = "Teammate";
        int[] characters = StringInfo.ParseCombiningCharacters(name);
        if (characters.Length > MaxObserverNameLength)
            name = name[..characters[MaxObserverNameLength - 1]] + "…";
        return "Observing " + name;
    }

    internal static void ConfigureBanner(NCardRewardSelectionScreen screen)
    {
        if (!Previews.TryGetValue(screen, out var preview)) return;
        var banner = screen.GetNode<NCommonBanner>("UI/Banner");
        string name = PlatformUtil.GetPlayerNameRaw(RunManager.Instance.NetService.Platform, preview.Reward.Player.NetId);
        banner.label.SetTextAutoSize(ObservingTitle(name));
        banner.label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        banner.SelfModulate = new Color(0.72f, 0.88f, 1f);
        banner.label.AddThemeColorOverride("font_color", new Color(0.88f, 0.96f, 1f));
        var button = NCardRewardAlternativeButton.Create(BackLabel(), MegaInput.cancel);
        button.Name = "CoopBuddyBack";
        button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => ModEntry.Guard(preview.Back)));
        preview.BackButton = button;
        screen.GetNode<Control>("UI/RewardAlternatives").AddChild(button);
    }

    private static string BackLabel() => RewardTracker.GetOpenRewards().Any(entry => LocalContext.IsMe(entry.Player) &&
        entry.LocalScreen is { } screen && GodotObject.IsInstanceValid(screen) && !screen.IsQueuedForDeletion()) ? "Back" : "Close";

    internal static void RefreshNavigation(NCardRewardSelectionScreen screen)
    {
        if (!Previews.TryGetValue(screen, out var preview) || preview.BackButton is not { } button ||
            !GodotObject.IsInstanceValid(button)) return;
        var label = button.GetNode<MegaLabel>("Label");
        string text = BackLabel();
        if (label.Text != text) label.SetTextAutoSize(text);
    }

    // Hovering changes the child-node order. Always map through the game's actual
    // options, the same model references its selection code uses.
    internal static NCardHolder?[] HoldersInOfferOrder(NCardRewardSelectionScreen screen)
    {
        var holders = screen.GetNode<Control>("UI/CardRow").GetChildren().OfType<NCardHolder>()
            .Where(holder => !holder.IsQueuedForDeletion()).ToArray();
        return Options(screen).Select(option => holders.FirstOrDefault(holder => holder.CardModel == option.Card)).ToArray();
    }

    private static int CardIndex(NCardRewardSelectionScreen screen, NCardHolder holder) =>
        Array.IndexOf(HoldersInOfferOrder(screen), holder);

    internal static void Vote(NCardRewardSelectionScreen screen, NCardHolder? holder)
    {
        if (holder == null || !Previews.TryGetValue(screen, out var preview)) return;
        int index = CardIndex(screen, holder);
        if (index >= 0) RewardCollaboration.Current?.Vote(preview.Reward, index);
    }

    internal static void Inspect(NCardRewardSelectionScreen screen, NCardHolder? holder)
    {
        if (holder == null || !Previews.TryGetValue(screen, out var preview)) return;
        int index = CardIndex(screen, holder);
        if (index < 0) return;
        NGame.Instance?.GetInspectCardScreen().Open(preview.Cards.ToList(), index);
        preview.Inspecting = true;
    }

    internal static void UpdateHotkeys(NCardRewardSelectionScreen screen, bool visible)
    {
        if (!Previews.TryGetValue(screen, out var preview) || NHotkeyManager.Instance is not { } hotkeys ||
            preview.Blocking == visible) return;
        preview.Blocking = visible;
        if (visible)
        {
            preview.BackButton?.Enable();
            hotkeys.AddBlockingScreen(screen);
            hotkeys.PushHotkeyPressedBinding(MegaInput.cancel, preview.Back);
            hotkeys.PushHotkeyPressedBinding(MegaInput.pauseAndBack, preview.Back);
        }
        else
        {
            hotkeys.RemoveHotkeyPressedBinding(MegaInput.cancel, preview.Back);
            hotkeys.RemoveHotkeyPressedBinding(MegaInput.pauseAndBack, preview.Back);
            hotkeys.RemoveBlockingScreen(screen);
            preview.BackButton?.Disable();
        }
    }

    internal static void CloseInspection(NCardRewardSelectionScreen screen)
    {
        if (!Previews.TryGetValue(screen, out var preview) || !preview.Inspecting) return;
        NGame.Instance?.InspectCardScreen?.Close();
        preview.Inspecting = false;
    }
}

[HarmonyPatch(typeof(NCardRewardSelectionScreen), nameof(NCardRewardSelectionScreen._Ready))]
internal static class PreviewBannerPatch
{
    private static void Postfix(NCardRewardSelectionScreen __instance) => ModEntry.Guard(() =>
    {
        NativeRewardScreen.CaptureEntrance(__instance);
        NativeRewardScreen.ConfigureBanner(__instance);
    });
}

[HarmonyPatch(typeof(NCardRewardSelectionScreen), "get_ScreenType")]
internal static class PreviewScreenTypePatch
{
    private static void Postfix(NCardRewardSelectionScreen __instance, ref NetScreenType __result)
    {
        if (NativeRewardScreen.IsPreview(__instance)) __result = NetScreenType.None;
    }
}

[HarmonyPatch]
internal static class PreviewHotkeysPatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
        new[] { "AfterOverlayShown", "AfterOverlayHidden", "AfterOverlayClosed" }
            .Select(name => AccessTools.Method(typeof(NCardRewardSelectionScreen), name));

    private static void Postfix(NCardRewardSelectionScreen __instance) =>
        ModEntry.Guard(() => NativeRewardScreen.UpdateHotkeys(__instance,
            __instance.Visible && !__instance.IsQueuedForDeletion()));
}

[HarmonyPatch(typeof(NCardRewardSelectionScreen), "SelectCard")]
internal static class ReadOnlyCardPatch
{
    private static bool Prefix(NCardRewardSelectionScreen __instance, NCardHolder cardHolder)
    {
        if (NativeRewardScreen.IsEntranceBlocked(__instance)) return false;
        if (!NativeRewardScreen.IsPreview(__instance)) return true;
        ModEntry.Guard(() => NativeRewardScreen.Vote(__instance, cardHolder));
        return false;
    }
}

[HarmonyPatch(typeof(NCardRewardSelectionScreen), "InspectCard")]
internal static class PreviewInspectPatch
{
    private static bool Prefix(NCardRewardSelectionScreen __instance, NCardHolder cardHolder)
    {
        if (!NativeRewardScreen.IsPreview(__instance)) return true;
        ModEntry.Guard(() => NativeRewardScreen.Inspect(__instance, cardHolder));
        return false;
    }
}

[HarmonyPatch(typeof(NCardRewardSelectionScreen), "OnAlternateRewardSelected")]
internal static class ReadOnlyAlternativePatch
{
    private static bool Prefix(NCardRewardSelectionScreen __instance) => !NativeRewardScreen.IsPreview(__instance);
}

[HarmonyPatch(typeof(NCardRewardSelectionScreen), "PowerCardFtueCheck")]
internal static class PreviewTutorialPatch
{
    private static bool Prefix(NCardRewardSelectionScreen __instance) => !NativeRewardScreen.IsPreview(__instance);
}
