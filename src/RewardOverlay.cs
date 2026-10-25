using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Platform;

namespace RewardViewer;

// Native player rows reserve the icon's position; a layer above the overlay backstop
// makes that same icon clickable while a reward picker covers the rest of the game.
internal sealed class RewardOverlay
{
    private sealed record PlayerButton(NMultiplayerPlayerState Row, Control Slot, TextureRect Icon,
        TextureButton Button, NSelectionReticle Reticle)
    {
        internal bool Selected { get; set; }
        internal bool MouseOver { get; set; }
        internal bool Focused { get; set; }
        internal bool Emphasized { get; set; }
        internal Tween? HoverTween { get; set; }
    }
    private const float ReticleScale = 0.4f;
    private static readonly AccessTools.FieldRef<NMultiplayerPlayerState, Tween?> LocationTween =
        AccessTools.FieldRefAccess<NMultiplayerPlayerState, Tween?>("_locationIconTween");
    private readonly Dictionary<ulong, PlayerButton> _buttons = [];
    private readonly NRun _run;
    private readonly Control _root = new() { Name = "RewardViewer", MouseFilter = Control.MouseFilterEnum.Ignore };
    private OpenReward? _watching;
    private NCardRewardSelectionScreen? _preview;
    private string _signature = "";
    private SceneTree _tree = null!;
    private RewardCollaboration _collaboration = null!;
    private RewardVoteDisplay? _votes;
    private ulong _nextRefresh;

    private RewardOverlay(NRun run) => _run = run;

    internal static void Attach(NRun run)
    {
        if (!GodotObject.IsInstanceValid(run) || run.HasNode("RewardViewerLayer")) return;
        new RewardOverlay(run).Mount();
    }

    private void Mount()
    {
        var layer = new CanvasLayer { Name = "RewardViewerLayer", Layer = 40 };
        _run.AddChild(layer);
        layer.AddChild(_root);
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _collaboration = new RewardCollaboration(RunManager.Instance.NetService);
        RewardCollaboration.Current = _collaboration;
        _tree = _run.GetTree();
        _tree.ProcessFrame += Process;
        layer.TreeExiting += () =>
        {
            _tree.ProcessFrame -= Process;
            _votes?.Dispose();
            ClosePreview(replayLocal: false);
            _collaboration.Dispose();
        };
        Refresh();
    }

    private void Process() => ModEntry.Guard(() =>
    {
        if (Time.GetTicksMsec() >= _nextRefresh)
        {
            _nextRefresh = Time.GetTicksMsec() + 50;
            Refresh();
        }
        foreach (var item in _buttons.Values)
        {
            if (!GodotObject.IsInstanceValid(item.Row)) { item.Button.Hide(); continue; }
            if (!item.Button.Visible) continue;
            // Keep alignment through HUD movement, viewport resizing and name changes.
            var rect = item.Slot.GetGlobalRect();
            item.Button.GlobalPosition = rect.Position;
            item.Button.Size = rect.Size;
            item.Button.PivotOffset = rect.Size * 0.5f;
            item.Reticle.Size = rect.Size / ReticleScale;
            item.Reticle.PivotOffset = item.Reticle.Size * 0.5f;
            float alpha = 1;
            for (Node? node = item.Row; node != null; node = node.GetParent())
                if (node is CanvasItem canvas) alpha *= canvas.Modulate.A;
            item.Button.Modulate = new Color(1, 1, 1, alpha);
            item.Button.Visible = item.Row.IsVisibleInTree() && alpha > 0.01f;
            UpdateAppearance(item);
        }
        _votes?.UpdatePositions();
    });

    private void Refresh()
    {
        var manager = RunManager.Instance;
        var open = RewardTracker.GetOpenRewards();
        bool multiplayer = manager.IsInProgress && !manager.IsSingleplayerOrFakeMultiplayer;
        var connected = NativeCompatibility.ConnectedPlayers.ToHashSet();
        open.RemoveAll(entry => !multiplayer || !connected.Contains(entry.Player.NetId) || entry.Player.Creature.IsDead);

        foreach (var row in _run.GlobalUi.MultiplayerPlayerContainer.GetChildren().OfType<NMultiplayerPlayerState>())
            if (row.Player != null && !_buttons.ContainsKey(row.Player.NetId)) AddButton(row);
        foreach (var id in _buttons.Where(pair => !GodotObject.IsInstanceValid(pair.Value.Row)).Select(pair => pair.Key).ToArray())
        {
            _buttons[id].Button.QueueFree();
            _buttons.Remove(id);
        }

        if (_watching != null)
        {
            if (!open.Contains(_watching) || !GodotObject.IsInstanceValid(_preview) || _preview!.IsQueuedForDeletion())
                ClosePreview();
            else if (open.Any(entry => LocalContext.IsMe(entry.Player) && entry.LocalScreen != null &&
                NOverlayStack.Instance?.Peek() == entry.LocalScreen))
                ClosePreview(replayLocal: false); // a new local picker already has its native entrance
            else
            {
                string signature = RewardTracker.Fingerprint(_watching.Reward.Cards);
                if (signature != _signature)
                {
                    var entry = _watching;
                    ClosePreview(replayLocal: false);
                    ShowPreview(entry);
                }
            }
        }

        if (_preview != null) NativeRewardScreen.RefreshNavigation(_preview);

        foreach (var item in _buttons.Values)
        {
            var entry = open.FirstOrDefault(entry => entry.Player == item.Row.Player);
            bool active = entry != null;
            if (active)
            {
                LocationTween(item.Row)?.Kill();
                LocationTween(item.Row) = null;
                item.Slot.Show();
                item.Icon.Hide();
            }
            else if (item.Button.Visible || !item.Icon.Visible)
            {
                // Return the slot to vanilla screen-status behavior when no reward is open.
                item.Icon.Show();
                var texture = LocalContext.IsMe(item.Row.Player) ? null :
                    manager.InputSynchronizer.GetScreenType(item.Row.Player.NetId).GetLocationIcon();
                item.Icon.Texture = texture;
                item.Icon.Scale = Vector2.One;
                item.Slot.Visible = texture != null;
            }
            item.Selected = active && (entry == _watching && _preview!.Visible ||
                entry!.LocalScreen is { Visible: true } screen && NOverlayStack.Instance?.Peek() == screen);
            item.Button.Visible = active && item.Row.IsVisibleInTree();
            UpdateAppearance(item);
        }

        var visibleScreen = NOverlayStack.Instance?.Peek() as NCardRewardSelectionScreen;
        var visibleReward = visibleScreen is { Visible: true } &&
            ActiveScreenContext.Instance.IsCurrent(visibleScreen) ? NativeRewardScreen.GetReward(visibleScreen) : null;
        if (visibleReward != null && !open.Contains(visibleReward)) visibleReward = null;
        _collaboration.Tick(visibleReward);
        if (_votes?.Screen != visibleScreen || visibleReward == null)
        {
            _votes?.Dispose();
            _votes = visibleReward == null ? null : new RewardVoteDisplay(visibleScreen!, visibleReward, _collaboration);
        }
        _votes?.Refresh();
    }

    private void AddButton(NMultiplayerPlayerState row)
    {
        var button = new TextureButton
        {
            Name = $"RewardViewerPlayer{row.Player.NetId}",
            TextureNormal = NetScreenType.CardSelection.GetLocationIcon(),
            IgnoreTextureSize = true,
            StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.Arrow,
            FocusMode = Control.FocusModeEnum.All,
            TooltipText = PlatformUtil.GetPlayerNameRaw(RunManager.Instance.NetService.Platform, row.Player.NetId)
        };
        _root.AddChild(button);
        var frame = new Control
        {
            Name = "RewardViewerHover", MouseFilter = Control.MouseFilterEnum.Ignore,
            Scale = Vector2.One * ReticleScale
        };
        button.AddChild(frame);
        var reticle = SceneHelper.Instantiate<NSelectionReticle>("ui/selection_reticle");
        reticle.Name = "Reticle";
        reticle.Position = Vector2.Zero;
        frame.AddChild(reticle);
        var item = new PlayerButton(row, row.GetNode<Control>("%LocationContainer"),
            row.GetNode<TextureRect>("%LocationIcon"), button, reticle);
        _buttons.Add(row.Player.NetId, item);
        button.MouseEntered += () => { item.MouseOver = true; UpdateAppearance(item); };
        button.MouseExited += () => { item.MouseOver = false; UpdateAppearance(item); };
        button.FocusEntered += () => { item.Focused = true; UpdateAppearance(item); };
        button.FocusExited += () => { item.Focused = false; UpdateAppearance(item); };
        button.VisibilityChanged += () =>
        {
            if (!button.Visible) { item.MouseOver = false; item.Focused = false; UpdateAppearance(item); }
        };
        button.Pressed += () => ModEntry.Guard(() => SwitchTo(row.Player.NetId));
    }

    private static void UpdateAppearance(PlayerButton item)
    {
        if (!GodotObject.IsInstanceValid(item.Button) || !item.Button.IsInsideTree()) return;
        bool emphasized = item.Button.Visible && (item.MouseOver || item.Focused &&
            NativeCompatibility.IsUsingDirectionalNavigation);
        if (emphasized != item.Emphasized)
        {
            item.Emphasized = emphasized;
            item.HoverTween?.Kill();
            item.HoverTween = item.Button.CreateTween();
            item.HoverTween.TweenProperty(item.Button, "scale", Vector2.One * (emphasized ? 1.16f : 1f), 0.12)
                .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        }
        // Selection, hover and controller focus share one native corner graphic.
        bool showReticle = item.Button.Visible && (item.Selected || emphasized);
        if (showReticle == item.Reticle.IsSelected) return;
        if (showReticle) item.Reticle.OnSelect();
        else item.Reticle.OnDeselect();
    }

    private void SwitchTo(ulong playerId)
    {
        var entry = RewardTracker.GetOpenRewards().FirstOrDefault(entry => entry.Player.NetId == playerId);
        if (entry == null) return;
        if (LocalContext.IsMe(entry.Player))
        {
            // Removing just our spectator overlay reveals the still-awaiting original picker.
            ClosePreview();
        }
        else if (entry == _watching)
            ClosePreview();
        else
        {
            ClosePreview(replayLocal: false);
            ShowPreview(entry);
        }
        Refresh();
    }

    private void ShowPreview(OpenReward entry)
    {
        var stack = NOverlayStack.Instance;
        if (stack == null) return;
        _signature = RewardTracker.Fingerprint(entry.Reward.Cards);
        var screen = NativeRewardScreen.Create(entry,
            () => ModEntry.Guard(() => { ClosePreview(); Refresh(); }));
        _watching = entry;
        _preview = screen;
        stack.Push(screen);
    }

    private void ClosePreview(bool replayLocal = true)
    {
        var screen = _preview;
        _preview = null;
        _watching = null;
        _signature = "";
        if (!GodotObject.IsInstanceValid(screen) || screen!.IsQueuedForDeletion()) return;
        NativeRewardScreen.CloseInspection(screen);
        if (screen.IsInsideTree())
        {
            var stack = NOverlayStack.Instance;
            bool wasTop = stack?.Peek() == screen;
            stack?.Remove(screen);
            if (replayLocal && wasTop && stack?.Peek() is NCardRewardSelectionScreen local &&
                !NativeRewardScreen.IsPreview(local) && RewardTracker.GetOpenRewards().Any(entry => entry.LocalScreen == local))
                NativeRewardScreen.ReplayEntrance(local);
        }
        else screen.QueueFree();
    }

}
