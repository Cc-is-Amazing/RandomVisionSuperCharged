using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;

namespace MathMod;

internal sealed partial class MathMerchantOverlay : Control
{
    public const string OverlayNodeName = "MathMerchantOverlay";

    private const string LocTable = "merchant_room";
    private const float PanelMargin = 24f;
    private const float PanelWidth = 400f;
    private const float PanelHeight = 300f;
    private const float TitleBarHeight = 54f;
    private const float ContentPadding = 16f;

    private static readonly Color SummaryBackgroundColor = new(0f, 0f, 0f, 0.72f);
    private static readonly Color TitleBarColor = new(0.08f, 0.08f, 0.08f, 0.9f);
    private static readonly Color BadgeColor = new(1f, 0.92f, 0.35f, 1f);
    private static readonly Color DragHintColor = new(0.8f, 0.8f, 0.8f, 0.92f);

    private static Vector2? _lastPanelPosition;

    private readonly List<NMerchantSlot> _selectedSlots = new();
    private readonly Dictionary<NMerchantSlot, Label> _selectionBadges = new();
    private readonly List<MerchantEntry> _subscribedEntries = new();

    private Control? _panelRoot;
    private ColorRect? _summaryBackground;
    private ColorRect? _titleBar;
    private Label? _titleLabel;
    private Label? _dragHintLabel;
    private Label? _summaryLabel;
    private NMerchantRoom? _room;
    private Player? _player;
    private bool _summaryDirty = true;
    private bool _isDraggingPanel;
    private Vector2 _panelDragOffset;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 500;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        SetProcessInput(true);

        _room = GetParent() as NMerchantRoom;
        CreateSummaryUi();
        RefreshSubscriptions();
        UpdateOverlayVisibility();
        RefreshSummary();
    }

    public override void _ExitTree()
    {
        UnsubscribeFromEntries();
        if (_player != null)
        {
            _player.GoldChanged -= OnGoldChanged;
        }
    }

    public override void _Process(double delta)
    {
        RefreshSubscriptions();
        UpdateOverlayVisibility();
        CleanupInvalidSelections();

        if (!Visible)
        {
            return;
        }

        EnsurePanelInBounds();
        UpdateSelectionBadges();
        if (_summaryDirty)
        {
            RefreshSummary();
        }
    }

    public override void _Input(InputEvent inputEvent)
    {
        if (!_isDraggingPanel || _panelRoot == null || !Visible)
        {
            return;
        }

        switch (inputEvent)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }:
                _isDraggingPanel = false;
                GetViewport().SetInputAsHandled();
                break;
            case InputEventMouseMotion mouseMotion:
                _panelRoot.Position = ClampPanelPosition(mouseMotion.GlobalPosition - _panelDragOffset);
                _lastPanelPosition = _panelRoot.Position;
                GetViewport().SetInputAsHandled();
                break;
        }
    }

    public void ToggleSlotSelection(NMerchantSlot slot)
    {
        if (!IsSelectable(slot))
        {
            return;
        }

        int existingIndex = _selectedSlots.IndexOf(slot);
        if (existingIndex >= 0)
        {
            RemoveSelectionAt(existingIndex);
        }
        else
        {
            _selectedSlots.Add(slot);
        }

        _summaryDirty = true;
        UpdateSelectionBadges();
        RefreshSummary();
    }

    private void CreateSummaryUi()
    {
        _panelRoot = new Control
        {
            Name = "SummaryPanelRoot",
            MouseFilter = MouseFilterEnum.Ignore,
            Position = GetDefaultPanelPosition(),
            Size = new Vector2(PanelWidth, PanelHeight)
        };
        AddChild(_panelRoot);

        _summaryBackground = new ColorRect
        {
            Name = "SummaryBackground",
            MouseFilter = MouseFilterEnum.Ignore,
            Color = SummaryBackgroundColor,
            Position = Vector2.Zero,
            Size = _panelRoot.Size
        };
        _panelRoot.AddChild(_summaryBackground);

        _titleBar = new ColorRect
        {
            Name = "SummaryTitleBar",
            MouseFilter = MouseFilterEnum.Stop,
            Color = TitleBarColor,
            Position = Vector2.Zero,
            Size = new Vector2(_panelRoot.Size.X, TitleBarHeight)
        };
        _titleBar.Connect(Control.SignalName.GuiInput, Callable.From<InputEvent>(OnTitleBarGuiInput));
        _panelRoot.AddChild(_titleBar);

        _titleLabel = new Label
        {
            Name = "SummaryTitleLabel",
            MouseFilter = MouseFilterEnum.Ignore,
            Position = new Vector2(ContentPadding, 6f),
            Size = new Vector2(_panelRoot.Size.X - ContentPadding * 2f, 22f),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Text = GetText("MATH_MERCHANT.SUMMARY.TITLE")
        };
        _titleLabel.AddThemeFontSizeOverride("font_size", 18);
        _titleLabel.AddThemeColorOverride("font_color", Colors.White);
        _titleLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        _titleLabel.AddThemeConstantOverride("outline_size", 3);
        _panelRoot.AddChild(_titleLabel);

        _dragHintLabel = new Label
        {
            Name = "SummaryDragHintLabel",
            MouseFilter = MouseFilterEnum.Ignore,
            Position = new Vector2(ContentPadding, 26f),
            Size = new Vector2(_panelRoot.Size.X - ContentPadding * 2f, 18f),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Text = GetText("MATH_MERCHANT.SUMMARY.DRAG_HINT")
        };
        _dragHintLabel.AddThemeFontSizeOverride("font_size", 12);
        _dragHintLabel.AddThemeColorOverride("font_color", DragHintColor);
        _dragHintLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        _dragHintLabel.AddThemeConstantOverride("outline_size", 2);
        _panelRoot.AddChild(_dragHintLabel);

        _summaryLabel = new Label
        {
            Name = "SummaryLabel",
            MouseFilter = MouseFilterEnum.Ignore,
            Position = new Vector2(ContentPadding, TitleBarHeight + 10f),
            Size = new Vector2(_panelRoot.Size.X - ContentPadding * 2f, _panelRoot.Size.Y - TitleBarHeight - ContentPadding),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        _summaryLabel.AddThemeFontSizeOverride("font_size", 18);
        _summaryLabel.AddThemeColorOverride("font_color", Colors.White);
        _summaryLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        _summaryLabel.AddThemeConstantOverride("outline_size", 3);
        _panelRoot.AddChild(_summaryLabel);
    }

    private void OnTitleBarGuiInput(InputEvent inputEvent)
    {
        if (_panelRoot == null)
        {
            return;
        }

        switch (inputEvent)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } mouseButton:
                _isDraggingPanel = mouseButton.Pressed;
                if (mouseButton.Pressed)
                {
                    // 拖拽时记录鼠标与面板左上角的偏移，避免标题栏一按下就发生“跳点”。
                    _panelDragOffset = mouseButton.GlobalPosition - _panelRoot.Position;
                }

                GetViewport().SetInputAsHandled();
                break;
            case InputEventMouseMotion mouseMotion when _isDraggingPanel:
                _panelRoot.Position = ClampPanelPosition(mouseMotion.GlobalPosition - _panelDragOffset);
                _lastPanelPosition = _panelRoot.Position;
                GetViewport().SetInputAsHandled();
                break;
        }
    }

    private void RefreshSubscriptions()
    {
        Player? player = GetMerchantPlayer();
        if (!ReferenceEquals(_player, player))
        {
            if (_player != null)
            {
                _player.GoldChanged -= OnGoldChanged;
            }

            _player = player;
            if (_player != null)
            {
                _player.GoldChanged += OnGoldChanged;
            }

            _summaryDirty = true;
        }

        MerchantInventory? inventory = GetMerchantInventory();
        if (inventory == null)
        {
            UnsubscribeFromEntries();
            return;
        }

        if (_subscribedEntries.Count == inventory.AllEntries.Count())
        {
            return;
        }

        UnsubscribeFromEntries();
        foreach (MerchantEntry entry in inventory.AllEntries)
        {
            entry.PurchaseCompleted += OnPurchaseCompleted;
            _subscribedEntries.Add(entry);
        }
    }

    private void UnsubscribeFromEntries()
    {
        foreach (MerchantEntry entry in _subscribedEntries)
        {
            entry.PurchaseCompleted -= OnPurchaseCompleted;
        }

        _subscribedEntries.Clear();
    }

    private void OnGoldChanged()
    {
        _summaryDirty = true;
    }

    private void OnPurchaseCompleted(PurchaseStatus _, MerchantEntry entry)
    {
        for (int index = _selectedSlots.Count - 1; index >= 0; index--)
        {
            if (ReferenceEquals(_selectedSlots[index].Entry, entry))
            {
                RemoveSelectionAt(index);
            }
        }

        _summaryDirty = true;
    }

    private void UpdateOverlayVisibility()
    {
        bool shouldDisplay = _room?.Inventory != null
            && _room.Inventory.IsOpen
            && ActiveScreenContext.Instance.IsCurrent(_room.Inventory);
        Visible = shouldDisplay;
    }

    private void CleanupInvalidSelections()
    {
        bool changed = false;
        for (int index = _selectedSlots.Count - 1; index >= 0; index--)
        {
            if (IsSelectable(_selectedSlots[index]))
            {
                continue;
            }

            RemoveSelectionAt(index);
            changed = true;
        }

        if (changed)
        {
            _summaryDirty = true;
        }
    }

    private void UpdateSelectionBadges()
    {
        List<NMerchantSlot> purchaseOrder = GetEffectivePurchaseOrder(out _);
        for (int index = 0; index < purchaseOrder.Count; index++)
        {
            NMerchantSlot slot = purchaseOrder[index];
            Label badge = GetOrCreateBadge(slot);
            badge.Text = $"#{index + 1}";

            Rect2 rect = slot.GetGlobalRect();
            badge.GlobalPosition = rect.Position + new Vector2(8f, -20f);
            badge.Visible = true;
        }
    }

    private Label GetOrCreateBadge(NMerchantSlot slot)
    {
        if (_selectionBadges.TryGetValue(slot, out Label? existingBadge))
        {
            return existingBadge;
        }

        // 角标展示的是“实际计价顺序”，这样会员卡被自动提前时，界面提示和总价计算能保持一致。
        Label badge = new()
        {
            Name = $"SelectionBadge_{_selectionBadges.Count}",
            MouseFilter = MouseFilterEnum.Ignore,
            ZIndex = 20
        };
        badge.AddThemeFontSizeOverride("font_size", 24);
        badge.AddThemeColorOverride("font_color", BadgeColor);
        badge.AddThemeColorOverride("font_outline_color", Colors.Black);
        badge.AddThemeConstantOverride("outline_size", 8);
        AddChild(badge);

        _selectionBadges[slot] = badge;
        return badge;
    }

    private void RemoveSelectionAt(int index)
    {
        NMerchantSlot slot = _selectedSlots[index];
        _selectedSlots.RemoveAt(index);

        if (_selectionBadges.Remove(slot, out Label? badge))
        {
            badge.QueueFree();
        }
    }

    private void RefreshSummary()
    {
        _summaryDirty = false;
        if (_summaryLabel == null)
        {
            return;
        }

        if (_selectedSlots.Count == 0)
        {
            _summaryLabel.Text = GetText("MATH_MERCHANT.SUMMARY.EMPTY_HINT");
            return;
        }

        Player? player = GetMerchantPlayer();
        int currentGold = player?.Gold ?? 0;
        bool ownsMembershipCard = player?.GetRelic<MembershipCard>() != null;
        List<NMerchantSlot> purchaseOrder = GetEffectivePurchaseOrder(out bool membershipPrioritized);
        bool membershipActivated = ownsMembershipCard;
        decimal membershipMultiplier = ownsMembershipCard ? GetOwnedMembershipMultiplier(player) : 1m;
        int totalCost = 0;

        List<string> lines = [];
        if (membershipPrioritized)
        {
            lines.Add(GetText("MATH_MERCHANT.SUMMARY.MEMBERSHIP_PRIORITIZED"));
        }

        // 这里显式使用 "\n" 拼接文本，避免 Windows 下 "\r\n" 被 Godot 解析成双倍空行。
        // 同时继续按“最终计价顺序”逐个模拟花费；如果选中了会员卡，就先把它提到最前，保证后续商品都按最优折扣重算。
        for (int index = 0; index < purchaseOrder.Count; index++)
        {
            NMerchantSlot slot = purchaseOrder[index];
            MerchantEntry entry = slot.Entry;
            int price = entry.Cost;
            bool discountedByMembership = false;
            bool isMembershipCard = TryGetMembershipMultiplier(entry, out decimal nextMultiplier);

            if (membershipActivated && !isMembershipCard)
            {
                price = ApplyMembershipDiscount(price, membershipMultiplier);
                discountedByMembership = true;
            }

            totalCost += price;
            string suffix = string.Empty;
            if (membershipPrioritized && isMembershipCard)
            {
                suffix = GetText("MATH_MERCHANT.SUMMARY.ITEM_SUFFIX.PRIORITIZED");
            }
            else if (discountedByMembership)
            {
                suffix = GetText("MATH_MERCHANT.SUMMARY.ITEM_SUFFIX.MEMBERSHIP_PRICE");
            }

            lines.Add(FormatText(
                "MATH_MERCHANT.SUMMARY.ITEM_LINE",
                ("Order", index + 1),
                ("Name", GetDisplayName(entry)),
                ("Price", price),
                ("Suffix", suffix)));

            if (!membershipActivated && isMembershipCard)
            {
                membershipActivated = true;
                membershipMultiplier = nextMultiplier;
            }
        }

        int remainingGold = currentGold - totalCost;
        lines.Add(string.Empty);
        lines.Add(FormatText("MATH_MERCHANT.SUMMARY.CURRENT_GOLD", ("Gold", currentGold)));
        lines.Add(FormatText("MATH_MERCHANT.SUMMARY.TOTAL_COST", ("Cost", totalCost)));
        lines.Add(FormatText(
            "MATH_MERCHANT.SUMMARY.CAN_AFFORD",
            ("Value", GetText(remainingGold >= 0 ? "MATH_MERCHANT.SUMMARY.AFFORD.YES" : "MATH_MERCHANT.SUMMARY.AFFORD.NO"))));
        lines.Add(FormatText("MATH_MERCHANT.SUMMARY.REMAINING_GOLD", ("Gold", remainingGold)));

        _summaryLabel.Text = string.Join("\n", lines);
    }

    private Player? GetMerchantPlayer()
    {
        return GetMerchantInventory()?.Player;
    }

    private MerchantInventory? GetMerchantInventory()
    {
        return _room?.Inventory?.Inventory;
    }

    private List<NMerchantSlot> GetEffectivePurchaseOrder(out bool membershipPrioritized)
    {
        List<NMerchantSlot> purchaseOrder = _selectedSlots.Where(IsSelectable).ToList();
        membershipPrioritized = false;

        Player? player = GetMerchantPlayer();
        if (player?.GetRelic<MembershipCard>() != null)
        {
            return purchaseOrder;
        }

        int membershipIndex = purchaseOrder.FindIndex(slot => slot.Entry is MerchantRelicEntry { Model: MembershipCard });
        if (membershipIndex <= 0)
        {
            return purchaseOrder;
        }

        // 会员卡一旦在这轮购物里买下，就应该优先结算，这样才符合玩家真正想看的“最省钱”总价。
        NMerchantSlot membershipSlot = purchaseOrder[membershipIndex];
        purchaseOrder.RemoveAt(membershipIndex);
        purchaseOrder.Insert(0, membershipSlot);
        membershipPrioritized = true;
        return purchaseOrder;
    }

    private void EnsurePanelInBounds()
    {
        if (_panelRoot == null)
        {
            return;
        }

        _panelRoot.Position = ClampPanelPosition(_panelRoot.Position);
        _lastPanelPosition = _panelRoot.Position;
    }

    private Vector2 GetDefaultPanelPosition()
    {
        if (_lastPanelPosition.HasValue)
        {
            return ClampPanelPosition(_lastPanelPosition.Value);
        }

        Vector2 viewportSize = GetViewportRect().Size;
        Vector2 desired = new(viewportSize.X - PanelWidth - PanelMargin, PanelMargin);
        return ClampPanelPosition(desired);
    }

    private Vector2 ClampPanelPosition(Vector2 target)
    {
        Vector2 viewportSize = GetViewportRect().Size;
        float maxX = Math.Max(PanelMargin, viewportSize.X - PanelWidth - PanelMargin);
        float maxY = Math.Max(PanelMargin, viewportSize.Y - PanelHeight - PanelMargin);
        return new Vector2(
            Mathf.Clamp(target.X, PanelMargin, maxX),
            Mathf.Clamp(target.Y, PanelMargin, maxY));
    }

    private static bool IsSelectable(NMerchantSlot slot)
    {
        return slot.IsInsideTree() && slot.Visible && slot.Entry.IsStocked;
    }

    private static int ApplyMembershipDiscount(int price, decimal membershipMultiplier)
    {
        return (int)(price * membershipMultiplier);
    }

    private static decimal GetOwnedMembershipMultiplier(Player? player)
    {
        MembershipCard? membershipCard = player?.GetRelic<MembershipCard>();
        if (membershipCard == null)
        {
            return 1m;
        }

        return membershipCard.DynamicVars["Discount"].BaseValue / 100m;
    }

    private static bool TryGetMembershipMultiplier(MerchantEntry entry, out decimal multiplier)
    {
        if (entry is MerchantRelicEntry { Model: MembershipCard membershipCard })
        {
            multiplier = membershipCard.DynamicVars["Discount"].BaseValue / 100m;
            return true;
        }

        multiplier = 1m;
        return false;
    }

    private static string GetDisplayName(MerchantEntry entry)
    {
        return entry switch
        {
            MerchantCardEntry { CreationResult: { Card: CardModel card } } => FormatText("MATH_MERCHANT.ITEM.CARD", ("Name", card.Title)),
            MerchantPotionEntry { Model: PotionModel potion } => FormatText("MATH_MERCHANT.ITEM.POTION", ("Name", potion.Title.GetFormattedText())),
            MerchantRelicEntry { Model: RelicModel relic } => FormatText("MATH_MERCHANT.ITEM.RELIC", ("Name", relic.Title.GetFormattedText())),
            MerchantCardRemovalEntry => new LocString(LocTable, "MERCHANT.cardRemovalService.title").GetFormattedText(),
            _ => string.Empty
        };
    }

    private static string GetText(string key)
    {
        return new LocString(LocTable, key).GetFormattedText();
    }

    private static string FormatText(string key, params (string Name, object Value)[] variables)
    {
        LocString locString = new(LocTable, key);
        foreach ((string name, object value) in variables)
        {
            switch (value)
            {
                case int intValue:
                    locString.Add(name, intValue);
                    break;
                case decimal decimalValue:
                    locString.Add(name, decimalValue);
                    break;
                case bool boolValue:
                    locString.Add(name, boolValue);
                    break;
                case string stringValue:
                    locString.Add(name, stringValue);
                    break;
                case IList<string> listValue:
                    locString.Add(name, listValue);
                    break;
                case LocString locValue:
                    locString.Add(name, locValue);
                    break;
                default:
                    locString.Add(name, value.ToString() ?? string.Empty);
                    break;
            }
        }

        return locString.GetFormattedText();
    }
}
