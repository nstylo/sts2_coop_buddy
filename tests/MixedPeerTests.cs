using System.Collections;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Platform;

namespace RewardViewer.Tests;

// Exercise native handshakes and receivers with a vanilla message table. Swap the
// process-wide table only inside this synchronous block, then restore it before UI tests.
internal static class MixedPeerTests
{
    internal static void Run(Action<bool, string> check)
    {
        var field = AccessTools.Field(typeof(MessageTypes), "_cache");
        var modded = (NetTypeCache<INetMessage>)field.GetValue(null)!;
        var vanilla = new NetTypeCache<INetMessage>(INetMessageSubtypes.All.ToList());
        check(INetMessageSubtypes.All.All(type => modded.TypeToId(type) == vanilla.TypeToId(type)) &&
            modded.TypeToId<ZZZRewardViewerViewMessage>() >= INetMessageSubtypes.All.Count() &&
            modded.TypeToId<ZZZRewardViewerVoteMessage>() >= INetMessageSubtypes.All.Count(),
            "installing the viewer preserves every vanilla network message ID");

        // Public v0.107.1 predates transport-level PeerVersionInfo handshakes.
        // Reflection keeps this test binary source usable with both installed APIs.
        var assembly = typeof(NetHostGameService).Assembly;
        var versionType = assembly.GetType("MegaCrit.Sts2.Core.Multiplayer.PeerVersionInfo");
        object? version = null;
        if (versionType != null)
        {
            version = Activator.CreateInstance(versionType)!;
            void Set(object value, string name, object data) => versionType.GetField(name)!.SetValue(value, data);
            Set(version, "version", ReleaseInfoManager.Instance.SemVer!.ToString());
            Set(version, "branch", PlatformBranch.PublicBeta);
            Set(version, "idDatabaseHash", ModelIdSerializationCache.Hash);
            Set(version, "gameplayAffectingMods", new List<string>());
            Set(version, "otherMods", new List<string>());
            var modVersion = System.Runtime.CompilerServices.RuntimeHelpers.GetObjectValue(version);
            Set(modVersion, "otherMods", new List<string> { "RewardViewer-0.6.0" });
            var managerType = assembly.GetType("MegaCrit.Sts2.Core.Multiplayer.Connection.HandshakeManager")!;
            var handlerType = assembly.GetType("MegaCrit.Sts2.Core.Multiplayer.Connection.IHandshakeHandler")!;
            bool Handshake(NetGameType role, object local, object remote)
            {
                var proxy = DispatchProxy.Create(handlerType, typeof(HandshakeProbe));
                var handler = (HandshakeProbe)proxy;
                handler.Role = role;
                var writer = new PacketWriter();
                var manager = Activator.CreateInstance(managerType, proxy, local, writer)!;
                managerType.GetMethod("BeginHandshakeFor")!.Invoke(manager, [1000UL]);
                managerType.GetMethods().Single(method => method.Name == "WriteHandshakeMessage" && method.IsStatic)
                    .Invoke(null, [1000UL, remote, writer]);
                var reader = new PacketReader();
                reader.Reset(writer.Buffer.AsSpan(0, writer.BytePosition).ToArray());
                managerType.GetMethod("HandshakeMessageReceived")!.Invoke(manager, [1000UL, reader]);
                return handler.Succeeded && !handler.Failed;
            }
            check(Handshake(NetGameType.Host, modVersion, version) && Handshake(NetGameType.Client, version, modVersion),
                "native handshakes allow a viewer host with a vanilla client and a vanilla host with a viewer client");
        }
        else GD.Print("SKIP: transport PeerVersionInfo handshake does not exist in this public game build");

        var bus = CreateBus();
        byte[] Encode(INetMessage message) => bus.SerializeMessage(1000, message, out int length).AsSpan(0, length).ToArray();
        byte[] view = Encode(new ZZZRewardViewerViewMessage());
        byte[] vote = Encode(new ZZZRewardViewerVoteMessage());
        byte[] input = Encode(new PeerInputMessage { screenType = NetScreenType.CardSelection, netMousePos = Vector2.One });
        try
        {
            field.SetValue(null, vanilla);
            var client = (NetClientGameService)CreateService(typeof(NetClientGameService), version);
            int clientInputs = 0;
            client.RegisterMessageHandler<PeerInputMessage>((_, _) => clientInputs++);
            client.OnPacketReceived(1, view, NetTransferMode.Reliable, 0);
            client.OnPacketReceived(1, vote, NetTransferMode.Reliable, 0);
            client.OnPacketReceived(1, input, NetTransferMode.Reliable, 0);
            check(clientInputs == 1, "a vanilla client ignores viewer packets and continues receiving native gameplay traffic");

            var host = (NetHostGameService)CreateService(typeof(NetHostGameService), version);
            ((IList)AccessTools.Field(typeof(NetHostGameService), "_connectedPeers").GetValue(host)!)
                .Add(new NetClientData { peerId = 1000 });
            int hostInputs = 0;
            host.RegisterMessageHandler<PeerInputMessage>((_, _) => hostInputs++);
            host.OnPacketReceived(1000, view, NetTransferMode.Reliable, 0);
            host.OnPacketReceived(1000, vote, NetTransferMode.Reliable, 0);
            host.OnPacketReceived(1000, input, NetTransferMode.Reliable, 0);
            check(hostInputs == 1, "a vanilla host drops collaboration packets and continues receiving native gameplay traffic");
        }
        finally { field.SetValue(null, modded); }
    }

    internal static NetMessageBus CreateBus() => typeof(NetMessageBus).GetConstructor(Type.EmptyTypes) != null ?
        (NetMessageBus)Activator.CreateInstance(typeof(NetMessageBus))! :
        (NetMessageBus)Activator.CreateInstance(typeof(NetMessageBus), new PacketReader(), new PacketWriter())!;

    internal static Action Disconnect(ulong playerId)
    {
        var lobby = MegaCrit.Sts2.Core.Runs.RunManager.Instance.RunLobby!;
        if (AccessTools.Property(lobby.GetType(), "Players")?.GetValue(lobby) is IList players)
        {
            var player = players.Cast<object>().Single(player => (ulong)AccessTools.Field(player.GetType(), "id").GetValue(player)! == playerId);
            players.Remove(player);
            return () => players.Add(player);
        }
        var ids = (HashSet<ulong>)AccessTools.Field(lobby.GetType(), "_connectedPlayerIds").GetValue(lobby)!;
        ids.Remove(playerId);
        return () => ids.Add(playerId);
    }

    private static object CreateService(Type type, object? version) => version == null ?
        Activator.CreateInstance(type)! : Activator.CreateInstance(type, version)!;
}

public class HandshakeProbe : DispatchProxy
{
    internal NetGameType Role;
    internal bool Succeeded;
    internal bool Failed;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "get_Type": return Role;
            case "HandshakeSucceeded": Succeeded = true; break;
            case "HandshakeFailed": Failed = true; break;
        }
        return null;
    }
}
