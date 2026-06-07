using HarmonyLib;
using Godot;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.HoverTips;

namespace MathMod;

internal static class MathHoverTipAppendHelper
{
    private const float HoverTipSpacing = 5f;
    private const float HoverTipWidth = 360f;

    private static readonly AccessTools.FieldRef<NHoverTipSet, VFlowContainer> HoverTipTextContainerField =
        AccessTools.FieldRefAccess<NHoverTipSet, VFlowContainer>("_textHoverTipContainer");

    private static readonly System.Reflection.FieldInfo ActiveHoverTipsField =
        AccessTools.Field(typeof(NHoverTipSet), "_activeHoverTips")!;

    public static bool TryGetActiveHoverTipSet(Control owner, out NHoverTipSet? tipSet)
    {
        tipSet = null;
        if (ActiveHoverTipsField.GetValue(null) is not Dictionary<Control, NHoverTipSet> activeHoverTips)
        {
            return false;
        }

        return activeHoverTips.TryGetValue(owner, out tipSet);
    }

    public static void UpsertTextTip(NHoverTipSet tipSet, string tipName, string title, string description)
    {
        VFlowContainer container = HoverTipTextContainerField(tipSet);
        Control? existing = container.GetChildren()
            .OfType<Control>()
            .FirstOrDefault(child => child.Name == tipName);
        if (existing != null)
        {
            UpdateTipControl(existing, title, description);
            return;
        }

        Control tipControl = CreateTipControl(tipName, title, description);
        container.AddChild(tipControl);

        float nextHeight = container.Size.Y + tipControl.Size.Y + HoverTipSpacing;
        float viewportHeight = NGame.Instance?.GetViewportRect().Size.Y ?? 1080f;
        if (nextHeight < viewportHeight - 50f)
        {
            container.Size = new Vector2(Math.Max(container.Size.X, HoverTipWidth), nextHeight);
        }
        else
        {
            container.Alignment = FlowContainer.AlignmentMode.Center;
        }
    }

    public static IReadOnlyList<Control> GetTextTipControls(NHoverTipSet tipSet)
    {
        return HoverTipTextContainerField(tipSet)
            .GetChildren()
            .OfType<Control>()
            .ToList();
    }

    public static void RemoveTextTip(NHoverTipSet tipSet, string tipName)
    {
        VFlowContainer container = HoverTipTextContainerField(tipSet);
        Control? existing = container.GetChildren()
            .OfType<Control>()
            .FirstOrDefault(child => child.Name == tipName);
        if (existing == null)
        {
            return;
        }

        container.RemoveChild(existing);
        existing.QueueFree();
        container.ResetSize();
    }

    private static Control CreateTipControl(string tipName, string title, string description)
    {
        Control control = PreloadManager.Cache
            .GetScene("res://scenes/ui/hover_tip.tscn")
            .Instantiate<Control>(PackedScene.GenEditState.Disabled);
        control.Name = tipName;
        UpdateTipControl(control, title, description);
        return control;
    }

    private static void UpdateTipControl(Control control, string title, string description)
    {
        control.GetNode<MegaLabel>("%Title").SetTextAutoSize(title);
        control.GetNode<MegaRichTextLabel>("%Description").Text = description;

        TextureRect icon = control.GetNode<TextureRect>("%Icon");
        icon.Texture = null;
        icon.Visible = false;

        control.ResetSize();
    }
}
