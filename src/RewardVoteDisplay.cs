using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Platform;

namespace CoopBuddy;

// Screen-owned portraits show every recommendation. The viewer's own vote also
// uses the native highlight. Neither presentation depends on hover state.
internal sealed class RewardVoteDisplay : IDisposable
{
    internal const int PortraitSize = 48;
    internal NCardRewardSelectionScreen Screen { get; }
    private readonly OpenReward _reward;
    private readonly RewardCollaboration _collaboration;
    private readonly Control _root = new()
    {
        Name = "CoopBuddyVotes", MouseFilter = Control.MouseFilterEnum.Ignore, ZIndex = 10
    };
    private readonly List<(NCardHolder Holder, HBoxContainer Row)> _rows = [];
    private readonly Dictionary<TextureRect, ulong> _portraits = [];
    private TextureRect? _hoveredPortrait;
    private NHoverTipSet? _nameTip;
    private NCard? _glowingCard;
    private Color _previousGlowColor;
    private string _signature = "";

    internal RewardVoteDisplay(NCardRewardSelectionScreen screen, OpenReward reward, RewardCollaboration collaboration)
    {
        Screen = screen;
        _reward = reward;
        _collaboration = collaboration;
        screen.AddChild(_root);
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
    }

    internal void Refresh()
    {
        if (RewardKey.For(_reward) is not { } key) return;
        var holders = NativeRewardScreen.HoldersInOfferOrder(Screen);
        var votes = _collaboration.Votes(key).OrderBy(vote => vote.PlayerId).ToArray();
        ulong localId = _collaboration.NetService.NetId;
        int localVote = votes.Where(vote => vote.PlayerId == localId).Select(vote => vote.CardIndex).DefaultIfEmpty(-1).Single();
        var selected = NativeRewardScreen.IsPreview(Screen) && localVote >= 0 && localVote < holders.Length ?
            holders[localVote]?.CardNode : null;
        SetGlow(selected);

        string signature = key + ":" + string.Join(",", holders.Select(holder => holder?.GetInstanceId() ?? 0)) + ":" +
            string.Join(",", votes.Select(vote => $"{vote.PlayerId}/{vote.CardIndex}"));
        if (signature == _signature) return;
        _signature = signature;
        ClearNameTip();
        foreach (Node child in _root.GetChildren()) { _root.RemoveChild(child); child.QueueFree(); }
        _rows.Clear();
        _portraits.Clear();
        for (int i = 0; i < holders.Length; i++)
        {
            if (holders[i] is not { } holder) continue;
            var voters = votes.Where(vote => vote.CardIndex == i).ToArray();
            if (voters.Length == 0) continue;
            var row = new HBoxContainer { Name = $"Card{i}Votes", MouseFilter = Control.MouseFilterEnum.Ignore };
            row.AddThemeConstantOverride("separation", 4);
            _root.AddChild(row);
            foreach (var vote in voters)
            {
                var player = _reward.Player.RunState.Players.FirstOrDefault(player => player.NetId == vote.PlayerId);
                if (player == null) continue;
                var icon = SceneHelper.Instantiate<TextureRect>("ui/multiplayer_vote_icon");
                icon.Name = $"Vote{player.NetId}";
                icon.CustomMinimumSize = new Vector2(PortraitSize, PortraitSize);
                icon.Texture = player.Character.IconTexture;
                var outline = icon.GetNode<TextureRect>("Outline");
                outline.Texture = player.Character.IconOutlineTexture;
                outline.MouseFilter = Control.MouseFilterEnum.Ignore;
                row.AddChild(icon);
                _portraits.Add(icon, player.NetId);
            }
            row.Size = new Vector2(voters.Length * PortraitSize + (voters.Length - 1) * 4, PortraitSize);
            _rows.Add((holder, row));
        }
        UpdatePositions();
    }

    private void SetGlow(NCard? card)
    {
        if (card == _glowingCard) return;
        if (GodotObject.IsInstanceValid(_glowingCard) && GodotObject.IsInstanceValid(Screen) && Screen.IsAncestorOf(_glowingCard))
        {
            _glowingCard!.CardHighlight.Modulate = _previousGlowColor;
            _glowingCard.CardHighlight.AnimHideInstantly();
        }
        _glowingCard = card;
        if (card == null) return;
        _previousGlowColor = card.CardHighlight.Modulate;
        card.CardHighlight.Modulate = NCardHighlight.gold;
        card.CardHighlight.AnimShow();
    }

    internal void UpdatePositions()
    {
        if (!GodotObject.IsInstanceValid(_root)) return;
        var rootTransform = _root.GetGlobalTransform();
        foreach (var (holder, row) in _rows)
        {
            if (!GodotObject.IsInstanceValid(holder) || !holder.IsInsideTree()) { row.Hide(); continue; }
            var transform = holder.GetGlobalTransform();
            var corner = NCard.defaultSize * 0.5f - row.Size - new Vector2(10, 10);
            row.Position = rootTransform.AffineInverse() * (transform * corner);
            row.Scale = transform.Scale / rootTransform.Scale;
            row.Rotation = transform.Rotation - rootTransform.Rotation;
            row.Modulate = holder.Modulate;
            row.Visible = holder.IsVisibleInTree();
        }
        // Portraits remain mouse-transparent so the underlying card keeps its native
        // hover and click behavior. Hit testing adds only the game's own hover tip.
        TextureRect? portrait = Screen.IsVisibleInTree() && ActiveScreenContext.Instance.IsCurrent(Screen) &&
            NGame.IsGameFocusedWindow() ? PortraitAt(_portraits.Keys, _root.GetGlobalMousePosition()) : null;
        if (portrait == _hoveredPortrait && GodotObject.IsInstanceValid(_nameTip) && !_nameTip!.IsQueuedForDeletion()) return;
        ClearNameTip();
        if (portrait == null) return;
        _hoveredPortrait = portrait;
        _nameTip = ShowVoterName(portrait, _portraits[portrait], _collaboration.NetService.Platform);
    }

    internal static TextureRect? PortraitAt(IEnumerable<TextureRect> portraits, Vector2 mousePosition) =>
        portraits.FirstOrDefault(icon => GodotObject.IsInstanceValid(icon) && icon.IsVisibleInTree() &&
            new Rect2(Vector2.Zero, icon.Size).HasPoint(icon.GetGlobalTransform().AffineInverse() * mousePosition));

    internal static NHoverTipSet? ShowVoterName(TextureRect portrait, ulong playerId, PlatformType platform)
    {
        // This native template contains only the player name, formatted and escaped
        // by STS2. The regular hover-tip scene supplies the background, font and layout.
        var name = new LocString("run_history", "PLAYER_NAME");
        name.Add("PlayerName", PlatformUtil.GetPlayerName(platform, playerId));
        var tip = NHoverTipSet.CreateAndShow(portrait, new HoverTip(name), HoverTip.GetHoverTipAlignment(portrait));
        tip?.SetFollowOwner();
        return tip;
    }

    private void ClearNameTip()
    {
        if (GodotObject.IsInstanceValid(_hoveredPortrait)) NHoverTipSet.Remove(_hoveredPortrait!);
        _hoveredPortrait = null;
        _nameTip = null;
    }

    public void Dispose()
    {
        SetGlow(null);
        ClearNameTip();
        if (!GodotObject.IsInstanceValid(_root)) return;
        _root.GetParent()?.RemoveChild(_root);
        _root.QueueFree();
        _rows.Clear();
        _portraits.Clear();
    }
}
