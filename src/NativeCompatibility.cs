using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBuddy;

// These two native properties were renamed between public v0.107.1 and beta
// v0.111.0. Resolve them once so the same DLL can use either installed API.
internal static class NativeCompatibility
{
    private static readonly System.Reflection.PropertyInfo PlayerIds =
        AccessTools.Property(typeof(RunLobby), "PlayerIds") ?? AccessTools.Property(typeof(RunLobby), "ConnectedPlayerIds");
    private static readonly System.Reflection.PropertyInfo DirectionalNavigation =
        AccessTools.Property(typeof(NControllerManager), "IsUsingDirectionalNavigation") ??
        AccessTools.Property(typeof(NControllerManager), "IsUsingController");

    internal static IEnumerable<ulong> ConnectedPlayers => RunManager.Instance.RunLobby is { } lobby ?
        (IEnumerable<ulong>)PlayerIds.GetValue(lobby)! : [];
    internal static bool IsUsingDirectionalNavigation => NControllerManager.Instance is { } controller &&
        (bool)DirectionalNavigation.GetValue(controller)!;
}
