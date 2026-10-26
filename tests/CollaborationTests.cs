using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rewards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBuddy.Tests;

internal static class CollaborationTests
{
    internal static async Task Run(PlayerChoiceSynchronizer choices, Player[] players, Action<bool, string> check)
    {
        var hub = new NativeMessageHub();
        var hostNet = hub.Add(1);
        var clientNet = hub.Add(1000);
        var thirdNet = hub.Add(2000);
        using var host = new RewardCollaboration(hostNet);
        using var client = new RewardCollaboration(clientNet);
        using var third = new RewardCollaboration(thirdNet);
        using var hostInput = new PeerInputSynchronizer(hostNet);
        using var clientInput = new PeerInputSynchronizer(clientNet);
        using var thirdInput = new PeerInputSynchronizer(thirdNet);

        check(MessageTypes.TypeToId<ZZZCoopBuddyViewMessage>() != MessageTypes.TypeToId<ZZZCoopBuddyVoteMessage>(),
            "native mod message discovery assigns distinct packet IDs");
        check(new ZZZCoopBuddyVoteMessage().Mode == NetTransferMode.Reliable && new ZZZCoopBuddyViewMessage().ShouldBroadcast &&
            !new ZZZCoopBuddyVoteMessage().ShouldBuffer, "collaboration packets use reliable native host relay without stale buffering");

        (OpenReward Entry, TaskCompletionSource<bool> Pending) Open(Player player)
        {
            var reward = new CardReward([player.RunState.CreateCard<Bash>(player), player.RunState.CreateCard<StrikeIronclad>(player)],
                CardCreationSource.Encounter, player, CardCreationOptions.ForRoom(player, RoomType.Monster), choices);
            var entry = RewardTracker.Begin(reward, choices);
            choices.ReserveChoiceId(player);
            var completion = new TaskCompletionSource<bool>();
            RewardTracker.CompleteSetup(entry, completion.Task, null);
            return (entry, completion);
        }
        var (own, ownTask) = Open(players[0]);
        var (remote, remoteTask) = Open(players[1]);
        var key = RewardKey.For(own)!.Value;
        host.Tick(own);
        client.Tick(own);
        third.Tick(remote);
        check(host.CursorVisibility(1000, hostInput) == true && host.CursorVisibility(2000, hostInput) == false,
            "native serialized view metadata separates viewers of different rewards");

        hostNet.SendMessage(new PeerInputMessage { screenType = NetScreenType.CardSelection, netMousePos = new Vector2(0.125f, -0.25f) });
        check(clientInput.GetControlSpaceFocusPosition(1, null).DistanceTo(new Vector2(0.125f, -0.25f)) < 0.001f,
            "cursor positions round-trip through the actual native PeerInput packet and synchronizer");
        var container = new NRemoteMouseCursorContainer();
        var firstCursor = new NRemoteMouseCursor();
        var secondCursor = new NRemoteMouseCursor();
        AccessTools.Property(typeof(NRemoteMouseCursor), "PlayerId").SetValue(firstCursor, 1000UL);
        AccessTools.Property(typeof(NRemoteMouseCursor), "PlayerId").SetValue(secondCursor, 2000UL);
        AccessTools.Field(typeof(NRemoteMouseCursorContainer), "_cursors").SetValue(container, new List<NRemoteMouseCursor> { firstCursor, secondCursor });
        AccessTools.Field(typeof(NRemoteMouseCursorContainer), "_synchronizer").SetValue(container, hostInput);
        RewardCollaboration.Current = host;
        AccessTools.Method(typeof(NRemoteMouseCursorContainer), "UpdateCursorVisibility").Invoke(container, null);
        check(firstCursor.Visible && !secondCursor.Visible, "native cursor visibility patch reveals only peers sharing the current reward");
        RewardCollaboration.Current = client;
        AccessTools.Method(typeof(NRemoteMouseCursorContainer), "UpdateCursorVisibility").Invoke(container, null);
        check(firstCursor.Visible && secondCursor.Visible, "unrelated native cursor sessions retain vanilla visibility");
        RewardCollaboration.Current = null;
        AccessTools.Field(typeof(NRemoteMouseCursorContainer), "_synchronizer").SetValue(container, null);
        firstCursor.Free();
        secondCursor.Free();
        container.Free();

        int[] deckCounts = players.Select(player => player.Deck.Cards.Count).ToArray();
        client.Vote(own, 1);
        check(host.Votes(key).Single() == (1000UL, 1) && third.Votes(key).Single() == (1000UL, 1),
            "client vote reaches host and third peer through native serialization and host relay");
        third.Tick(own);
        third.Vote(own, 1);
        check(host.Votes(key).Count() == 2 && client.Votes(key).Count() == 2,
            "two teammates can recommend the same card independently");
        client.Vote(own, 0);
        check(host.Votes(key).Single(vote => vote.PlayerId == 1000).CardIndex == 0,
            "voting another card moves only that teammate's recommendation");
        client.Vote(own, 0);
        check(host.Votes(key).Single() == (2000UL, 1), "clicking the same recommendation again clears it on every peer");
        host.Vote(own, 0);
        check(host.Votes(key).Count() == 1, "reward owner cannot cast a spectator vote on their own picker");
        hub.Inject(999, new ZZZCoopBuddyVoteMessage { Reward = key, CardIndex = 0 });
        clientNet.SendMessage(new ZZZCoopBuddyVoteMessage { Reward = key, CardIndex = 20 });
        clientNet.SendMessage(new ZZZCoopBuddyVoteMessage { Reward = key, CardIndex = -2 });
        clientNet.SendMessage(new ZZZCoopBuddyVoteMessage { Reward = key with { ChoiceId = uint.MaxValue }, CardIndex = 0 });
        check(host.Votes(key).Single() == (2000UL, 1), "unknown senders, invalid indices and unrelated choices cannot create votes");
        check(players.Select(player => player.Deck.Cards.Count).SequenceEqual(deckCounts) && !ownTask.Task.IsCompleted,
            "recommendations neither change decks nor resolve the owner's pending choice");

        // A peer starts listening late. The periodic UI snapshot must recover existing votes.
        third.Dispose();
        using var rejoined = new RewardCollaboration(thirdNet);
        client.Vote(own, 0);
        check(rejoined.Votes(key).Single() == (1000UL, 0), "a newly listening peer receives fresh votes");
        // It missed the previous third-player vote; host snapshots only its own recommendations.
        // Add a host recommendation for the other player's reward and recover that instead.
        host.Vote(remote, 0);
        check(host.Votes(RewardKey.For(remote)!.Value).Single() == (1UL, 0),
            "host recommendations also use the native broadcast path");
        rejoined.Reset();
        await Task.Delay(1100);
        host.Tick(own);
        check(rejoined.Votes(RewardKey.For(remote)!.Value).Single() == (1UL, 0),
            "periodic native snapshots recover an existing recommendation for a late peer");

        own.Reward.Reroll();
        host.Tick(own);
        client.Tick(own);
        rejoined.Tick(own);
        var rerolledKey = RewardKey.For(own)!.Value;
        check(rerolledKey != key && !host.Votes(key).Any() && !host.Votes(rerolledKey).Any(),
            "rerolls invalidate old card votes even when the same reward object remains open");
        clientNet.SendMessage(new ZZZCoopBuddyVoteMessage { Reward = key, CardIndex = 0 });
        check(!host.Votes(rerolledKey).Any(), "delayed votes for previous options cannot attach to rerolled cards");
        client.Vote(own, 1);
        ownTask.SetResult(true);
        host.Tick(null);
        client.Tick(null);
        rejoined.Tick(null);
        check(!host.Votes(rerolledKey).Any() && host.CursorVisibility(1000, hostInput) != true,
            "closing a reward clears recommendations and its shared cursor context");

        rejoined.Vote(remote, 1);
        var reconnect = MixedPeerTests.Disconnect(2000);
        host.Tick(remote);
        check(host.Votes(RewardKey.For(remote)!.Value).All(vote => vote.PlayerId != 2000),
            "disconnect removes that player's recommendations");
        reconnect();
        remoteTask.SetCanceled();
        host.Tick(null);
        client.Tick(null);
        rejoined.Tick(null);
        RewardTracker.Clear();
        check(!host.Votes(key).Any() && !client.Votes(key).Any(), "run cleanup leaves no collaborative reward state");
    }
}

// Deterministic host/client wire harness. Every packet passes through STS2's real
// serializer, message IDs, deserializer and handler dispatch. No sockets or saves.
internal sealed class NativeMessageHub
{
    private readonly List<NativeMessagePeer> _peers = [];
    internal INetGameService Add(ulong id)
    {
        var net = DispatchProxy.Create<INetGameService, NativeMessagePeer>();
        var peer = (NativeMessagePeer)(object)net;
        peer.Id = id;
        peer.Hub = this;
        _peers.Add(peer);
        return net;
    }
    internal void Relay(NativeMessagePeer sender, INetMessage message)
    {
        if (sender.Id == 1)
        {
            foreach (var peer in _peers.Where(peer => peer != sender)) Deliver(peer, sender.Id, message);
            return;
        }
        Deliver(_peers.Single(peer => peer.Id == 1), sender.Id, message);
        if (message.ShouldBroadcast)
            foreach (var peer in _peers.Where(peer => peer.Id != 1 && peer != sender)) Deliver(peer, sender.Id, message);
    }
    internal void Inject(ulong sender, INetMessage message)
    {
        foreach (var peer in _peers) Deliver(peer, sender, message);
    }
    private static void Deliver(NativeMessagePeer peer, ulong sender, INetMessage message)
    {
        var bytes = peer.Bus.SerializeMessage(sender, message, out int length).AsSpan(0, length).ToArray();
        if (!peer.Bus.TryDeserializeMessage(bytes, out var decoded, out var originalSender))
            throw new InvalidOperationException("Native message decoding failed");
        peer.Bus.SendMessageToAllHandlers(decoded!, originalSender!.Value);
    }
}

public class NativeMessagePeer : DispatchProxy
{
    internal ulong Id;
    internal NativeMessageHub Hub = null!;
    internal readonly NetMessageBus Bus = MixedPeerTests.CreateBus();
    private readonly NetSingleplayerGameService _fallback = new();
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "get_NetId": return Id;
            case "get_IsConnected": return true;
            case "get_Type": return Id == 1 ? NetGameType.Host : NetGameType.Client;
            case "RegisterMessageHandler":
            case "UnregisterMessageHandler":
                typeof(NetMessageBus).GetMethod(method.Name)!.MakeGenericMethod(method.GetGenericArguments()).Invoke(Bus, args);
                return null;
            case "SendMessage": Hub.Relay(this, (INetMessage)args![0]!); return null;
            default: return method.Invoke(_fallback, args);
        }
    }
}
