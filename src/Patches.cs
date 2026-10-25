using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rewards;

namespace RewardViewer;

[HarmonyPatch(typeof(CardReward), "OnSelect")]
internal static class OpenRewardPatch
{
    private static void Prefix(CardReward __instance, PlayerChoiceSynchronizer ____synchronizer,
        out OpenReward? __state)
    {
        OpenReward? entry = null;
        ModEntry.Guard(() => entry = RewardTracker.Begin(__instance, ____synchronizer));
        __state = entry;
    }

    private static void Postfix(CardReward __instance, Task<bool> __result, OpenReward? __state)
    {
        if (__state == null) return;
        ModEntry.Guard(() => RewardTracker.CompleteSetup(__state, __result,
            LocalContext.IsMe(__instance.Player) ? NOverlayStack.Instance?.Peek() as NCardRewardSelectionScreen : null));
    }
}

[HarmonyPatch(typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.ReserveChoiceId))]
internal static class RewardChoicePatch
{
    private static void Postfix(PlayerChoiceSynchronizer __instance, Player player, uint __result) =>
        ModEntry.Guard(() => RewardTracker.ReserveChoice(__instance, player, __result));
}

[HarmonyPatch(typeof(RewardsSetSynchronizer), nameof(RewardsSetSynchronizer.Dispose))]
internal static class EndRunPatch
{
    private static void Postfix() => RewardTracker.Clear();
}

[HarmonyPatch(typeof(NRun), nameof(NRun._Ready))]
internal static class RunUiPatch
{
    private static void Postfix(NRun __instance) => Callable.From(() =>
        ModEntry.Guard(() => RewardOverlay.Attach(__instance))).CallDeferred();
}
