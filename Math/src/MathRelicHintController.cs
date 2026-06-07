using System.Globalization;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Runs;

namespace MathMod;

internal sealed class MathRelicHintController
{
    private const string LocTable = "static_hover_tips";
    private const string TooltipOwnerNodeName = "MathPenNibTooltipOwner";

    private static readonly Color PenNibHighlightColor = new(1f, 0.92f, 0.4f, 1f);
    private static readonly Color PenNibFlashColor = new(1f, 0.52f, 0.18f, 1f);

    private NRelicInventoryHolder? _trackedPenNibHolder;
    private Control? _tooltipOwner;
    private NHoverTipSet? _tooltipSet;
    private PenNibHintState _penNibState;
    private string? _activeHintToken;
    private string? _dismissedHintToken;
    private string? _shownHintToken;

    public void Update(Player? player)
    {
        NRelicInventoryHolder? penNibHolder = TryGetPenNibHolder(player, out PenNib? penNib);
        if (!ReferenceEquals(_trackedPenNibHolder, penNibHolder))
        {
            RestorePenNibHint(_trackedPenNibHolder);
            ResetTooltipTracking();
            _trackedPenNibHolder = penNibHolder;
            _penNibState = PenNibHintState.None;
        }

        if (penNibHolder == null)
        {
            HideTooltip();
            return;
        }

        PenNibHintState desiredState = GetDesiredPenNibHintState(penNib, out int remainingAttacks);
        if (desiredState == PenNibHintState.None)
        {
            RestorePenNibHint(penNibHolder);
            ResetTooltipTracking();
            _penNibState = PenNibHintState.None;
            return;
        }

        ApplyPenNibHint(penNibHolder, desiredState);
        _penNibState = desiredState;

        string hintToken = BuildHintToken(desiredState, remainingAttacks);
        _activeHintToken = hintToken;
        if (_dismissedHintToken == hintToken)
        {
            HideTooltip();
            return;
        }

        if (_shownHintToken != hintToken || !IsTooltipValid())
        {
            ShowTooltip(penNibHolder, desiredState, remainingAttacks, hintToken);
        }
    }

    public void Reset()
    {
        RestorePenNibHint(_trackedPenNibHolder);
        _trackedPenNibHolder = null;
        _penNibState = PenNibHintState.None;
        ResetTooltipTracking();
    }

    private static NRelicInventoryHolder? TryGetPenNibHolder(Player? player, out PenNib? penNib)
    {
        penNib = player?.GetRelic<PenNib>();
        if (penNib == null)
        {
            return null;
        }

        PenNib trackedPenNib = penNib;
        return NRun.Instance?.GlobalUi?.RelicInventory?.RelicNodes.FirstOrDefault(node => node.Relic.Model == trackedPenNib);
    }

    private static PenNibHintState GetDesiredPenNibHintState(PenNib? penNib, out int remainingAttacks)
    {
        remainingAttacks = 0;
        if (penNib == null)
        {
            return PenNibHintState.None;
        }

        if (penNib.AttacksPlayed == 9)
        {
            return PenNibHintState.Flash;
        }

        // 钢笔尖面板显示的是“已打出的攻击数”，提示文案则强调“离触发还差几次”，更贴近出牌决策。
        remainingAttacks = 9 - penNib.AttacksPlayed;
        return remainingAttacks is > 0 and < 3 ? PenNibHintState.Highlight : PenNibHintState.None;
    }

    private static void ApplyPenNibHint(NRelicInventoryHolder holder, PenNibHintState state)
    {
        if (!IsHolderValid(holder))
        {
            return;
        }

        TextureRect icon = holder.Relic.Icon;
        TextureRect outline = holder.Relic.Outline;
        CanvasItem? amountLabel = holder.GetNodeOrNull<CanvasItem>("%AmountLabel");
        if (state == PenNibHintState.Highlight)
        {
            icon.Modulate = Colors.White.Lerp(PenNibHighlightColor, 0.38f);
            outline.Modulate = Colors.White.Lerp(PenNibHighlightColor, 0.88f);
            if (amountLabel != null)
            {
                amountLabel.Modulate = PenNibHighlightColor;
            }

            return;
        }

        float flash = (Mathf.Sin((float)(Time.GetTicksMsec() / 1000.0 * 9.0)) + 1f) * 0.5f;
        float iconWeight = 0.35f + flash * 0.45f;
        float outlineWeight = 0.55f + flash * 0.35f;
        icon.Modulate = Colors.White.Lerp(PenNibFlashColor, iconWeight);
        outline.Modulate = Colors.White.Lerp(PenNibFlashColor, outlineWeight);
        if (amountLabel != null)
        {
            amountLabel.Modulate = Colors.White.Lerp(PenNibFlashColor, outlineWeight);
        }
    }

    private void RestorePenNibHint(NRelicInventoryHolder? holder)
    {
        if (!IsHolderValid(holder))
        {
            HideTooltip();
            return;
        }

        holder!.Relic.Icon.Modulate = GetBaseRelicIconColor(holder.Relic.Model);
        holder.Relic.Outline.Modulate = Colors.White;
        CanvasItem? amountLabel = holder.GetNodeOrNull<CanvasItem>("%AmountLabel");
        if (amountLabel != null)
        {
            amountLabel.Modulate = Colors.White;
        }

        HideTooltip();
    }

    private void ShowTooltip(NRelicInventoryHolder holder, PenNibHintState state, int remainingAttacks, string hintToken)
    {
        HideTooltip();

        Control tooltipOwner = GetOrCreateTooltipOwner(holder);
        HoverTip hoverTip = new(
            new LocString(LocTable, "MATH_RELIC_HINT.PEN_NIB.TITLE"),
            BuildFullTooltipDescription(state, remainingAttacks));

        _tooltipSet = NHoverTipSet.CreateAndShow(tooltipOwner, hoverTip, HoverTipAlignment.None);
        _tooltipSet.SetAlignmentForRelic(holder.Relic);
        WireTooltipDismiss(_tooltipSet);
        _shownHintToken = hintToken;
    }

    private void HideTooltip()
    {
        if (_tooltipOwner != null && GodotObject.IsInstanceValid(_tooltipOwner))
        {
            NHoverTipSet.Remove(_tooltipOwner);
        }

        _tooltipSet = null;
        _shownHintToken = null;
    }

    private Control GetOrCreateTooltipOwner(NRelicInventoryHolder holder)
    {
        if (_tooltipOwner != null && GodotObject.IsInstanceValid(_tooltipOwner) && _tooltipOwner.GetParent() == holder)
        {
            return _tooltipOwner;
        }

        if (_tooltipOwner != null && GodotObject.IsInstanceValid(_tooltipOwner))
        {
            _tooltipOwner.QueueFree();
        }

        _tooltipOwner = new Control
        {
            Name = TooltipOwnerNodeName,
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AnchorRight = 0f,
            AnchorBottom = 0f,
            OffsetRight = 1f,
            OffsetBottom = 1f
        };
        holder.AddChild(_tooltipOwner);
        return _tooltipOwner;
    }

    private void WireTooltipDismiss(NHoverTipSet tooltipSet)
    {
        foreach (Control control in MathHoverTipAppendHelper.GetTextTipControls(tooltipSet))
        {
            control.MouseFilter = Control.MouseFilterEnum.Stop;
            control.Connect(Control.SignalName.GuiInput, Callable.From<InputEvent>(OnTooltipGuiInput));
        }
    }

    private void OnTooltipGuiInput(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
        {
            return;
        }

        OnTooltipDismissed();
    }

    private void OnTooltipDismissed()
    {
        // 关闭状态按“当前提示内容”记忆，同一计数阶段不反复弹窗；等钢笔尖计数变化后再重新提醒。
        _dismissedHintToken = _activeHintToken;
        HideTooltip();
    }

    private string BuildFullTooltipDescription(PenNibHintState state, int remainingAttacks)
    {
        return string.Join("\n",
            BuildHintDescription(state, remainingAttacks),
            GetText("MATH_RELIC_HINT.PEN_NIB.CLICK_TO_CLOSE"));
    }

    private static string BuildHintToken(PenNibHintState state, int remainingAttacks)
    {
        return state == PenNibHintState.Highlight
            ? $"highlight:{remainingAttacks.ToString(CultureInfo.InvariantCulture)}"
            : "flash";
    }

    private static string BuildHintDescription(PenNibHintState state, int remainingAttacks)
    {
        LocString description = new(LocTable, state == PenNibHintState.Highlight
            ? "MATH_RELIC_HINT.PEN_NIB.HIGHLIGHT"
            : "MATH_RELIC_HINT.PEN_NIB.FLASH");
        if (state == PenNibHintState.Highlight)
        {
            description.Add("Remaining", remainingAttacks.ToString(CultureInfo.InvariantCulture));
        }

        return description.GetFormattedText();
    }

    private static string GetText(string key)
    {
        return new LocString(LocTable, key).GetFormattedText();
    }

    private bool IsTooltipValid()
    {
        return _tooltipSet != null && GodotObject.IsInstanceValid(_tooltipSet) && _tooltipSet.IsInsideTree();
    }

    private void ResetTooltipTracking()
    {
        _activeHintToken = null;
        _dismissedHintToken = null;
        _shownHintToken = null;
        if (_tooltipOwner != null && GodotObject.IsInstanceValid(_tooltipOwner))
        {
            _tooltipOwner.QueueFree();
        }

        _tooltipOwner = null;
        _tooltipSet = null;
    }

    private static bool IsHolderValid(NRelicInventoryHolder? holder)
    {
        return holder != null && GodotObject.IsInstanceValid(holder) && holder.IsInsideTree();
    }

    private static Color GetBaseRelicIconColor(RelicModel relic)
    {
        if (!RunManager.Instance.IsInProgress)
        {
            return Colors.White;
        }

        return relic.Status == RelicStatus.Disabled ? new Color("#808080") : Colors.White;
    }

    private enum PenNibHintState
    {
        None,
        Highlight,
        Flash
    }
}
