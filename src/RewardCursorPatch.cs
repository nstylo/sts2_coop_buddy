using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;

namespace CoopBuddy;

// Preserve native cursor movement, interpolation, artwork, size and input packets.
// Only visibility changes: reward viewers must be looking at the very same offer.
[HarmonyPatch(typeof(NRemoteMouseCursorContainer), "UpdateCursorVisibility")]
internal static class RewardCursorPatch
{
    private static readonly System.Reflection.MethodInfo UpdateVisibility =
        AccessTools.Method(typeof(NRemoteMouseCursorContainer), "UpdateCursorVisibility");
    private static readonly AccessTools.FieldRef<NRemoteMouseCursorContainer, PeerInputSynchronizer?> Input =
        AccessTools.FieldRefAccess<NRemoteMouseCursorContainer, PeerInputSynchronizer?>("_synchronizer");

    private static void Postfix(List<NRemoteMouseCursor> ____cursors, PeerInputSynchronizer? ____synchronizer)
    {
        if (RewardCollaboration.Current is not { } collaboration || ____synchronizer == null ||
            collaboration.NetService != ____synchronizer.NetService) return;
        ModEntry.Guard(() =>
        {
            foreach (var cursor in ____cursors)
                if (collaboration.CursorVisibility(cursor.PlayerId, ____synchronizer) is { } visible)
                    cursor.Visible = visible;
        });
    }

    internal static void Refresh()
    {
        if (NGame.Instance?.RemoteCursorContainer is { } container && GodotObject.IsInstanceValid(container) && Input(container) != null)
            UpdateVisibility.Invoke(container, null);
    }
}
