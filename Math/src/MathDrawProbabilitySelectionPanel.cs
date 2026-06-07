using Godot;
using MegaCrit.Sts2.Core.Localization;

namespace MathMod;

internal sealed partial class MathDrawProbabilitySelectionPanel : PanelContainer
{
    private readonly Dictionary<int, Button> _buttons = new();

    private bool _isRefreshing;
    private Label? _titleLabel;

    public override void _Ready()
    {
        Name = "MathDrawProbabilitySelectionPanel";
        MouseFilter = MouseFilterEnum.Stop;
        ProcessMode = ProcessModeEnum.Always;
        SetAnchorsAndOffsetsPreset(LayoutPreset.CenterBottom);
        OffsetLeft = -240f;
        OffsetTop = -190f;
        OffsetRight = 240f;
        OffsetBottom = -92f;
        ZIndex = 30;

        AddThemeStyleboxOverride("panel", CreatePanelStyle());

        VBoxContainer root = new()
        {
            MouseFilter = MouseFilterEnum.Stop,
            Alignment = BoxContainer.AlignmentMode.Center
        };
        AddChild(root);

        MarginContainer headerMargin = new();
        headerMargin.AddThemeConstantOverride("margin_left", 16);
        headerMargin.AddThemeConstantOverride("margin_top", 12);
        headerMargin.AddThemeConstantOverride("margin_right", 16);
        headerMargin.AddThemeConstantOverride("margin_bottom", 6);
        root.AddChild(headerMargin);

        _titleLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _titleLabel.AddThemeFontSizeOverride("font_size", 24);
        _titleLabel.AddThemeColorOverride("font_color", Colors.White);
        _titleLabel.AddThemeColorOverride("font_outline_color", Colors.Black);
        _titleLabel.AddThemeConstantOverride("outline_size", 3);
        headerMargin.AddChild(_titleLabel);

        MarginContainer gridMargin = new();
        gridMargin.AddThemeConstantOverride("margin_left", 16);
        gridMargin.AddThemeConstantOverride("margin_top", 0);
        gridMargin.AddThemeConstantOverride("margin_right", 16);
        gridMargin.AddThemeConstantOverride("margin_bottom", 12);
        root.AddChild(gridMargin);

        GridContainer grid = new()
        {
            Columns = 5,
            MouseFilter = MouseFilterEnum.Stop
        };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 10);
        gridMargin.AddChild(grid);

        for (int drawCount = 1; drawCount <= 10; drawCount++)
        {
            int currentDrawCount = drawCount;
            Button button = CreateDrawCountButton(currentDrawCount);
            button.Toggled += isPressed => OnButtonToggled(currentDrawCount, isPressed);
            _buttons[currentDrawCount] = button;
            grid.AddChild(button);
        }

        MathModConfig.Updated += OnConfigUpdated;
        LocString.SubscribeToLocaleChange(OnLocaleChanged);
        RefreshLocalizedText();
        RefreshFromConfig();
    }

    public override void _ExitTree()
    {
        MathModConfig.Updated -= OnConfigUpdated;
        LocString.UnsubscribeToLocaleChange(OnLocaleChanged);
    }

    private void OnConfigUpdated()
    {
        if (!IsInsideTree())
        {
            return;
        }

        RefreshFromConfig();
    }

    private void OnLocaleChanged()
    {
        if (!IsInsideTree())
        {
            return;
        }

        RefreshLocalizedText();
    }

    private void RefreshLocalizedText()
    {
        if (_titleLabel == null)
        {
            return;
        }

        // 面板长期挂在抽牌堆界面里，切语言时同步刷新标题，避免同一局里中英混杂。
        _titleLabel.Text = L10N("MATH_DRAW_PROBABILITY.PANEL_TITLE").GetFormattedText();
    }

    private void RefreshFromConfig()
    {
        _isRefreshing = true;
        HashSet<int> selectedCounts = MathModConfig.SelectedDrawProbabilityCounts.ToHashSet();
        foreach ((int drawCount, Button button) in _buttons)
        {
            button.ButtonPressed = selectedCounts.Contains(drawCount);
            ApplyButtonVisual(button, button.ButtonPressed);
        }

        _isRefreshing = false;
    }

    private void OnButtonToggled(int drawCount, bool isPressed)
    {
        ApplyButtonVisual(_buttons[drawCount], isPressed);
        if (_isRefreshing)
        {
            return;
        }

        MathModConfig.SetDrawProbabilityEnabled(drawCount, isPressed);
        MathDrawProbabilityTooltipController.RefreshVisibleTooltips();
    }

    private static Button CreateDrawCountButton(int drawCount)
    {
        Button button = new()
        {
            Text = drawCount.ToString(),
            ToggleMode = true,
            CustomMinimumSize = new Vector2(72f, 44f),
            FocusMode = FocusModeEnum.None,
            MouseDefaultCursorShape = CursorShape.PointingHand,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Stop
        };

        button.AddThemeFontSizeOverride("font_size", 22);
        button.AddThemeStyleboxOverride("normal", CreateButtonStyle(new Color(0.12f, 0.12f, 0.16f, 0.95f), new Color(0.48f, 0.48f, 0.55f, 1f)));
        button.AddThemeStyleboxOverride("hover", CreateButtonStyle(new Color(0.18f, 0.18f, 0.24f, 0.98f), new Color(0.72f, 0.72f, 0.78f, 1f)));
        button.AddThemeStyleboxOverride("pressed", CreateButtonStyle(new Color(0.38f, 0.63f, 0.24f, 0.98f), new Color(0.84f, 0.96f, 0.72f, 1f)));
        button.AddThemeStyleboxOverride("hover_pressed", CreateButtonStyle(new Color(0.44f, 0.7f, 0.28f, 0.98f), new Color(0.92f, 1f, 0.82f, 1f)));
        button.AddThemeStyleboxOverride("focus", CreateButtonStyle(new Color(0.38f, 0.63f, 0.24f, 0.98f), new Color(0.96f, 1f, 0.9f, 1f)));
        ApplyButtonVisual(button, isSelected: false);
        return button;
    }

    private static void ApplyButtonVisual(Button button, bool isSelected)
    {
        button.AddThemeColorOverride("font_color", isSelected ? Colors.White : new Color(0.93f, 0.93f, 0.93f));
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeColorOverride("font_pressed_color", Colors.White);
        button.AddThemeColorOverride("font_focus_color", Colors.White);
        button.AddThemeColorOverride("font_outline_color", Colors.Black);
        button.AddThemeConstantOverride("outline_size", 3);
    }

    private static LocString L10N(string key)
    {
        return new LocString("static_hover_tips", key);
    }

    private static StyleBoxFlat CreatePanelStyle()
    {
        return new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0.72f),
            BorderColor = new Color(0.82f, 0.74f, 0.46f, 0.95f),
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 10,
            CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10,
            CornerRadiusBottomRight = 10,
            ShadowColor = new Color(0f, 0f, 0f, 0.35f),
            ShadowSize = 6,
            ContentMarginLeft = 0,
            ContentMarginTop = 0,
            ContentMarginRight = 0,
            ContentMarginBottom = 0
        };
    }

    private static StyleBoxFlat CreateButtonStyle(Color backgroundColor, Color borderColor)
    {
        return new StyleBoxFlat
        {
            BgColor = backgroundColor,
            BorderColor = borderColor,
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 8,
            ContentMarginTop = 4,
            ContentMarginRight = 8,
            ContentMarginBottom = 4
        };
    }
}
