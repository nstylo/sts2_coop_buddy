using System.Text.Json;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace RewardViewer;

internal sealed class OpenReward(CardReward reward, PlayerChoiceSynchronizer synchronizer)
{
    internal CardReward Reward { get; } = reward;
    internal Player Player => Reward.Player;
    internal PlayerChoiceSynchronizer Synchronizer { get; } = synchronizer;
    internal Task? Completion { get; set; }
    internal uint? ChoiceId { get; set; }
    internal bool AwaitingChoice { get; set; }
    internal CardModel? PickedCard { get; set; }
    internal NCardRewardSelectionScreen? LocalScreen { get; set; }
}

// Opening a reward is already synchronized by STS2: CardReward.OnSelect runs on
// every peer. Track that task, rather than advertising every generated reward set.
internal static class RewardTracker
{
    private static readonly List<OpenReward> Open = [];
    private static readonly Dictionary<PlayerChoiceSynchronizer, Action<Player, uint, NetPlayerChoiceResult>> Subscriptions = [];
    private static readonly Dictionary<CardReward, string> LoggedCards = [];

    internal static OpenReward Begin(CardReward reward, PlayerChoiceSynchronizer synchronizer)
    {
        Prune();
        var entry = new OpenReward(reward, synchronizer);
        Open.Add(entry);
        if (!Subscriptions.ContainsKey(synchronizer))
        {
            Action<Player, uint, NetPlayerChoiceResult> handler = (player, id, choice) =>
                ModEntry.Guard(() => OnChoice(synchronizer, player, id, choice));
            Subscriptions.Add(synchronizer, handler);
            synchronizer.PlayerChoiceReceived += handler;
        }
        LogOptions(reward);
        return entry;
    }

    internal static void ReserveChoice(PlayerChoiceSynchronizer synchronizer, Player player, uint id)
    {
        var entry = Open.LastOrDefault(entry => entry.Player == player &&
            entry.Synchronizer == synchronizer && entry.Completion?.IsCompleted != true);
        // While a reward is waiting, unrelated choice reservations cannot replace its ID.
        if (entry == null || entry.AwaitingChoice && entry.ChoiceId != null)
            return;
        // Card-obtained hooks may reserve unrelated choices during the pick animation.
        // A genuine further pick starts only after OnSelect removes the obtained card.
        if (entry.PickedCard != null && entry.Reward.Cards.Contains(entry.PickedCard))
            return;
        entry.PickedCard = null;
        entry.ChoiceId = id;
        entry.AwaitingChoice = true;
    }

    private static void OnChoice(PlayerChoiceSynchronizer synchronizer, Player player, uint id, NetPlayerChoiceResult choice)
    {
        var entry = Open.LastOrDefault(entry => entry.Player == player &&
            entry.Synchronizer == synchronizer && entry.ChoiceId == id);
        if (entry == null || choice.type != PlayerChoiceType.Index)
            return;
        // A normal pick or canceled picker disappears before card-obtained animations.
        // Alternatives (including reroll) retain their task; completion closes skip, etc.
        if (choice.indexes == null || choice.indexes.Count == 0)
            entry.AwaitingChoice = false;
        else if (choice.indexes[0] >= 0 && choice.indexes[0] < entry.Reward.Cards.Count())
        {
            entry.PickedCard = entry.Reward.Cards.ElementAt(choice.indexes[0]);
            entry.AwaitingChoice = false;
        }
        else
            entry.ChoiceId = null; // the next iteration reserves a fresh choice ID
    }

    internal static void CompleteSetup(OpenReward entry, Task completion, NCardRewardSelectionScreen? screen)
    {
        entry.Completion = completion;
        entry.LocalScreen = screen;
    }

    internal static List<OpenReward> GetOpenRewards()
    {
        Prune();
        // A nested reward temporarily replaces its parent's visible options.
        return Open.GroupBy(entry => entry.Player.NetId).Select(group => group.Last())
            .Where(entry => entry.AwaitingChoice && entry.Reward.Cards.Any()).ToList();
    }

    private static void Prune()
    {
        Open.RemoveAll(entry => entry.Completion?.IsCompleted == true);
        foreach (var sync in Subscriptions.Keys.Where(sync => Open.All(entry => entry.Synchronizer != sync)).ToArray())
        {
            sync.PlayerChoiceReceived -= Subscriptions[sync];
            Subscriptions.Remove(sync);
        }
        foreach (var reward in LoggedCards.Keys.Where(reward => Open.All(entry => entry.Reward != reward)).ToArray())
            LoggedCards.Remove(reward);
    }

    internal static void LogOptions(CardReward reward)
    {
        var cards = reward.Cards.ToArray();
        string signature = Fingerprint(cards);
        if (LoggedCards.TryGetValue(reward, out string? previous) && previous == signature)
            return;
        LoggedCards[reward] = signature;
        string owner = LocalContext.IsMe(reward.Player) ? "Local" : "Remote";
        ModEntry.Logger.Info($"{owner} player {reward.Player.NetId} opened card reward: " +
            string.Join(", ", cards.Select(card => $"{card.Id.Entry} (upgrade {card.CurrentUpgradeLevel}" +
                $", enchantment {card.Enchantment?.Id.Entry ?? "none"})")));
    }

    // Native serialization covers upgrades, enchantments and SavedProperties.
    internal static string Fingerprint(IEnumerable<CardModel> cards) => string.Join("\n",
        cards.Select(card => JsonSerializer.Serialize(card.ToSerializable(),
            JsonSerializationUtility.GetTypeInfo<SerializableCard>())));

    internal static void Clear()
    {
        foreach (var (sync, handler) in Subscriptions)
            sync.PlayerChoiceReceived -= handler;
        Subscriptions.Clear();
        Open.Clear();
        LoggedCards.Clear();
        RewardCollaboration.Current?.Reset();
    }
}
