using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace CoopBuddy;

// Choice IDs identify an open picker. The digest also invalidates votes on rerolls
// and modified offers, without sending card models or changing any game state.
internal readonly record struct RewardKey(ulong OwnerId, uint ChoiceId, ulong OptionsHash)
{
    internal static RewardKey? For(OpenReward reward)
    {
        if (reward.ChoiceId is not { } id) return null;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(RewardTracker.Fingerprint(reward.Reward.Cards)));
        return new RewardKey(reward.Player.NetId, id, BinaryPrimitives.ReadUInt64LittleEndian(hash));
    }

    internal void Write(PacketWriter writer)
    {
        writer.WriteULong(OwnerId);
        writer.WriteUInt(ChoiceId);
        writer.WriteULong(OptionsHash);
    }

    internal static RewardKey Read(PacketReader reader) => new(reader.ReadULong(), reader.ReadUInt(), reader.ReadULong());
}

// STS2 discovers INetMessage implementations in loaded mods. These use its native
// serialization, reliable delivery, host relay and sender identity; no extra transport.
// Public v0.107.1 sorts by class name. Keep these after all vanilla messages so
// installing this optional UI mod does not change vanilla players' message IDs.
public sealed class ZZZCoopBuddyViewMessage : INetMessage
{
    internal RewardKey? Reward { get; set; }
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => false;
    public void Serialize(PacketWriter writer)
    {
        writer.WriteBool(Reward.HasValue);
        if (Reward is { } key) key.Write(writer);
    }
    public void Deserialize(PacketReader reader) => Reward = reader.ReadBool() ? RewardKey.Read(reader) : null;
}

public sealed class ZZZCoopBuddyVoteMessage : INetMessage
{
    internal RewardKey Reward { get; set; }
    internal int CardIndex { get; set; }
    public bool ShouldBroadcast => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.VeryDebug;
    public bool ShouldBuffer => false;
    public void Serialize(PacketWriter writer) { Reward.Write(writer); writer.WriteInt(CardIndex); }
    public void Deserialize(PacketReader reader) { Reward = RewardKey.Read(reader); CardIndex = reader.ReadInt(); }
}

internal sealed class RewardCollaboration : IDisposable
{
    internal static RewardCollaboration? Current { get; set; }
    private readonly INetGameService _net;
    private readonly Dictionary<ulong, RewardKey?> _views = [];
    private readonly Dictionary<(RewardKey Reward, ulong Voter), int> _votes = [];
    private Dictionary<RewardKey, OpenReward> _active = [];
    private ulong _nextHeartbeat;
    private bool _published;
    internal INetGameService NetService => _net;

    internal RewardCollaboration(INetGameService net)
    {
        _net = net;
        net.RegisterMessageHandler<ZZZCoopBuddyViewMessage>(ReceiveView);
        net.RegisterMessageHandler<ZZZCoopBuddyVoteMessage>(ReceiveVote);
        net.Disconnected += OnDisconnected;
    }

    internal void Tick(OpenReward? visibleReward)
    {
        RefreshActive();
        RewardKey? key = visibleReward == null ? null : RewardKey.For(visibleReward);
        if (key.HasValue && !_active.ContainsKey(key.Value)) key = null;
        bool changed = !_views.TryGetValue(_net.NetId, out var old) || old != key;
        _views[_net.NetId] = key;
        ulong now = Time.GetTicksMsec();
        if (_net.IsConnected && (!_published || changed || now >= _nextHeartbeat))
        {
            if (!_published || changed || key != null) _net.SendMessage(new ZZZCoopBuddyViewMessage { Reward = key });
            // Small periodic snapshots let a newly connected teammate recover our view/votes.
            foreach (var vote in _votes.Where(pair => pair.Key.Voter == _net.NetId))
                _net.SendMessage(new ZZZCoopBuddyVoteMessage { Reward = vote.Key.Reward, CardIndex = vote.Value });
            _published = true;
            _nextHeartbeat = now + 1000;
        }
        if (changed && Current == this) RewardCursorPatch.Refresh();
    }

    internal void Vote(OpenReward reward, int cardIndex)
    {
        RefreshActive();
        if (RewardKey.For(reward) is not { } key || !CanVote(key, cardIndex, _net.NetId)) return;
        int value = _votes.TryGetValue((key, _net.NetId), out int previous) && previous == cardIndex ? -1 : cardIndex;
        ApplyVote(key, value, _net.NetId);
        if (_net.IsConnected) _net.SendMessage(new ZZZCoopBuddyVoteMessage { Reward = key, CardIndex = value });
    }

    internal IEnumerable<(ulong PlayerId, int CardIndex)> Votes(RewardKey reward) =>
        _votes.Where(pair => pair.Key.Reward == reward).Select(pair => (pair.Key.Voter, pair.Value));

    private void RefreshActive()
    {
        var connected = NativeCompatibility.ConnectedPlayers.ToHashSet();
        _active = RewardTracker.GetOpenRewards().Where(entry => connected.Contains(entry.Player.NetId) &&
                !entry.Player.Creature.IsDead && entry.ChoiceId.HasValue)
            .ToDictionary(entry => RewardKey.For(entry)!.Value);
        foreach (var key in _votes.Keys.Where(key => !_active.ContainsKey(key.Reward) || !connected.Contains(key.Voter)).ToArray())
            _votes.Remove(key);
        foreach (ulong id in _views.Keys.Where(id => !connected.Contains(id)).ToArray()) _views.Remove(id);
    }

    private bool CanVote(RewardKey key, int index, ulong sender) => sender != key.OwnerId &&
        NativeCompatibility.ConnectedPlayers.Contains(sender) &&
        _active.TryGetValue(key, out var reward) && index >= -1 && index < reward.Reward.Cards.Count();

    private void ApplyVote(RewardKey key, int index, ulong sender)
    {
        if (index == -1) _votes.Remove((key, sender));
        else _votes[(key, sender)] = index;
    }

    private void ReceiveView(ZZZCoopBuddyViewMessage message, ulong sender) => ModEntry.Guard(() =>
    {
        if (sender == _net.NetId || !NativeCompatibility.ConnectedPlayers.Contains(sender)) return;
        _views[sender] = message.Reward;
        if (Current == this) RewardCursorPatch.Refresh();
    });

    private void ReceiveVote(ZZZCoopBuddyVoteMessage message, ulong sender) => ModEntry.Guard(() =>
    {
        RefreshActive();
        if (CanVote(message.Reward, message.CardIndex, sender)) ApplyVote(message.Reward, message.CardIndex, sender);
    });

    // null means to retain vanilla visibility outside reward views. An explicit empty
    // view suppresses fallback while a modded peer is inspecting or has another screen open.
    internal bool? CursorVisibility(ulong playerId, PeerInputSynchronizer input)
    {
        RewardKey? View(ulong id)
        {
            if (_views.TryGetValue(id, out var key))
                return key.HasValue && _active.ContainsKey(key.Value) ? key : null;
            if (input.GetScreenType(id) != NetScreenType.CardSelection) return null;
            return _active.Keys.Where(key => key.OwnerId == id).Select(key => (RewardKey?)key).FirstOrDefault();
        }
        var local = View(_net.NetId);
        var remote = View(playerId);
        if (local != null) return local == remote;
        return remote != null ? false : null;
    }

    internal void Reset()
    {
        _active.Clear();
        _votes.Clear();
        _views.Clear();
        _published = false;
        _nextHeartbeat = 0;
    }

    private void OnDisconnected(NetErrorInfo _) => Reset();

    public void Dispose()
    {
        _net.UnregisterMessageHandler<ZZZCoopBuddyViewMessage>(ReceiveView);
        _net.UnregisterMessageHandler<ZZZCoopBuddyVoteMessage>(ReceiveVote);
        _net.Disconnected -= OnDisconnected;
        Reset();
        if (Current == this) Current = null;
    }
}
