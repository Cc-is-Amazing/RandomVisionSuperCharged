using Godot;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;

namespace MathMod;

internal sealed partial class MathDrawProbabilityTooltip : Panel
{
    private const float HorizontalPadding = 10f;
    private const float VerticalPadding = 8f;
    private const float LeftInset = 6f;

    private readonly Label _textLabel;

    private NCardHolder? _owner;

    public MathDrawProbabilityTooltip()
    {
        Name = "MathDrawProbabilityTooltip";
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 1000;
        ProcessMode = ProcessModeEnum.Always;
        AnchorRight = 0f;
        AnchorBottom = 0f;

        AddThemeStyleboxOverride("panel", CreatePanelStyle());

        _textLabel = new Label
        {
            MouseFilter = MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Position = new Vector2(HorizontalPadding, VerticalPadding)
        };
        _textLabel.AddThemeFontSizeOverride("font_size", 18);
        _textLabel.AddThemeColorOverride("font_color", Colors.White);
        _textLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        _textLabel.AddThemeConstantOverride("outline_size", 3);
        AddChild(_textLabel);
    }

    public override void _Process(double delta)
    {
        if (_owner == null || !GodotObject.IsInstanceValid(_owner) || !GodotObject.IsInstanceValid(_owner.Hitbox))
        {
            QueueFree();
            return;
        }

        UpdatePlacement();
    }

    public void AttachTo(NCardHolder owner, string text)
    {
        _owner = owner;
        if (GetParent() != owner)
        {
            Reparent(owner);
            owner.MoveChild(this, owner.GetChildCount() - 1);
        }

        _textLabel.Text = text;
        RefreshSize();
        UpdatePlacement();
    }

    private void RefreshSize()
    {
        if (_owner == null)
        {
            return;
        }

        Vector2 hitboxSize = _owner.Hitbox.Size;
        float availableWidth = Mathf.Max(120f, hitboxSize.X - LeftInset - HorizontalPadding * 2f - 4f);

        Font font = _textLabel.GetThemeFont("font");
        int fontSize = _textLabel.GetThemeFontSize("font_size");
        Vector2 textSize = font.GetMultilineStringSize(
            _textLabel.Text,
            HorizontalAlignment.Left,
            availableWidth,
            fontSize,
            -1);

        float textWidth = Mathf.Min(availableWidth, Mathf.Ceil(textSize.X));
        float textHeight = Mathf.Ceil(textSize.Y);

        _textLabel.Size = new Vector2(textWidth, textHeight);
        Size = new Vector2(textWidth + HorizontalPadding * 2f, textHeight + VerticalPadding * 2f);
        CustomMinimumSize = Size;
    }

    private void UpdatePlacement()
    {
        if (_owner == null)
        {
            return;
        }

        Control hitbox = _owner.Hitbox;
        Vector2 hitboxSize = hitbox.Size;
        Vector2 size = Size == Vector2.Zero ? GetCombinedMinimumSize() : Size;

        float targetX = hitbox.Position.X + LeftInset;
        float targetY = hitbox.Position.Y + hitboxSize.Y - size.Y - 20f;

        float minY = hitbox.Position.Y + 16f;
        float maxY = hitbox.Position.Y + hitboxSize.Y - size.Y - 12f;
        Position = new Vector2(targetX, Mathf.Clamp(targetY, minY, maxY));
    }

    private static StyleBoxFlat CreatePanelStyle()
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.08f, 0.08f, 0.88f),
            BorderColor = new Color(0.76f, 0.71f, 0.56f, 0.95f),
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ShadowColor = new Color(0f, 0f, 0f, 0.25f),
            ShadowSize = 4
        };
    }
}
