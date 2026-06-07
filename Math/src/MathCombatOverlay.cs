using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Runs;

namespace MathMod;

internal sealed partial class MathCombatOverlay : Control
{
    private const string LocTable = "static_hover_tips";
    private const string EnemyNameBbColor = "#ff5c5c";
    private const float EnemyHintMaxWidth = 360f;

    private static readonly Color DefenseHighlightColor = new(0.35f, 1f, 0.45f, 0.98f);

    private static readonly Color LethalHighlightColor = new(1f, 0.18f, 0.18f, 0.98f);

    private readonly MathRelicHintController _relicHintController = new();

    private readonly Dictionary<Creature, Label> _lethalLabels = new();

    private readonly Dictionary<Creature, MathPredictionEngine.LethalResult> _lethalPlans = new();

    private readonly Dictionary<CardModel, Color> _activeCardHighlights = new();

    private HBoxContainer? _incomingDamageContainer;
    private Label? _incomingDamageLabel;
    private RichTextLabel? _incomingDamageCostLabel;
    private MathPredictionEngine.DefenseResult? _defensePlan;
    private int _refreshVersion;
    private bool _refreshQueued;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 500;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        _incomingDamageContainer = new HBoxContainer
        {
            Name = "IncomingDamageContainer",
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
            Alignment = BoxContainer.AlignmentMode.Center
        };
        _incomingDamageLabel = CreateLabel(fontSize: 48, new Color(1f, 0.25f, 0.25f), Colors.Black, "IncomingDamage");
        _incomingDamageCostLabel = CreateRichTextLabel(fontSize: 24, new Color(0.35f, 1f, 0.45f), Colors.Black, "IncomingDamageCost");
        _incomingDamageContainer.AddChild(_incomingDamageLabel);
        _incomingDamageContainer.AddChild(_incomingDamageCostLabel);
        AddChild(_incomingDamageContainer);

        CombatManager.Instance.StateTracker.CombatStateChanged += OnCombatStateChanged;
        ActiveScreenContext.Instance.Updated += OnScreenContextUpdated;
        LocString.SubscribeToLocaleChange(OnLocaleChanged);

        UpdateOverlayVisibility();
        RequestRefresh();
    }

    public override void _ExitTree()
    {
        CombatManager.Instance.StateTracker.CombatStateChanged -= OnCombatStateChanged;
        ActiveScreenContext.Instance.Updated -= OnScreenContextUpdated;
        LocString.UnsubscribeToLocaleChange(OnLocaleChanged);
        _relicHintController.Reset();
        ClearCardHighlights();
        ResetEnemyHealthBarFlashes();
    }

    public override void _Process(double delta)
    {
        UpdateOverlayVisibility();
        if (!Visible)
        {
            return;
        }

        UpdateOverlayPositions();
        UpdateRelicHints();
        UpdateCardHighlights();
        UpdateEnemyHealthBarFlashes();
    }

    private void OnCombatStateChanged(CombatState _)
    {
        RequestRefresh();
    }

    private void OnScreenContextUpdated()
    {
        UpdateOverlayVisibility();
        RequestRefresh();
    }

    private void OnLocaleChanged()
    {
        // 战斗提示本身是动态拼装文本；语言切换后主动刷新一次，避免必须等状态变化才更新文案。
        RequestRefresh();
    }

    private void RequestRefresh()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        CallDeferred(nameof(RefreshPredictionsDeferred));
    }

    private async void RefreshPredictionsDeferred()
    {
        _refreshQueued = false;
        int refreshVersion = ++_refreshVersion;
        SceneTree? tree = GetTree();
        if (tree == null)
        {
            return;
        }

        await ToSignal(tree, SceneTree.SignalName.ProcessFrame);

        if (refreshVersion != _refreshVersion || !IsInsideTree())
        {
            return;
        }

        RefreshPredictions();
    }

    private void RefreshPredictions()
    {
        if (!ShouldDisplayOverlay())
        {
            HideTransientUi();
            return;
        }

        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? localPlayer = combatState == null ? null : MegaCrit.Sts2.Core.Context.LocalContext.GetMe(combatState);
        // 这里只在玩家行动阶段刷新提示，避免把抽牌堆、地图、暂停菜单等盖层中的状态误当成可出牌态。
        if (combatState == null || localPlayer?.PlayerCombatState == null || combatState.CurrentSide != CombatSide.Player)
        {
            HideTransientUi();
            return;
        }

        MathPredictionEngine.IncomingDamageResult incomingDamage = MathPredictionEngine.CalculateIncomingDamage(combatState, localPlayer);
        _defensePlan = incomingDamage.HpLoss > 0 ? MathPredictionEngine.CalculateDefensePlan(combatState, localPlayer) : null;
        if (_incomingDamageContainer != null && _incomingDamageLabel != null && _incomingDamageCostLabel != null)
        {
            _incomingDamageContainer.Visible = incomingDamage.HpLoss > 0;
            _incomingDamageLabel.Visible = incomingDamage.HpLoss > 0;
            _incomingDamageLabel.Text = incomingDamage.HpLoss > 0 ? $"-{incomingDamage.HpLoss}" : string.Empty;
            _incomingDamageCostLabel.Text = _defensePlan != null ? FormatDefenseCost(_defensePlan, richText: true) : string.Empty;
            _incomingDamageCostLabel.Visible = !string.IsNullOrEmpty(_incomingDamageCostLabel.Text);
        }

        _lethalPlans.Clear();
        foreach ((Creature target, MathPredictionEngine.LethalResult result) in MathPredictionEngine.CalculateLethalPlans(combatState, localPlayer))
        {
            _lethalPlans[target] = result;
        }

        UpdateLethalLabels();
        UpdateOverlayPositions();
    }

    private bool ShouldDisplayOverlay()
    {
        NCombatRoom? room = NCombatRoom.Instance;
        return room != null && ActiveScreenContext.Instance.IsCurrent(room);
    }

    private void UpdateOverlayVisibility()
    {
        bool shouldDisplay = ShouldDisplayOverlay();
        Visible = shouldDisplay;
        if (!shouldDisplay)
        {
            HideTransientUi();
        }
    }

    private void HideTransientUi()
    {
        _lethalPlans.Clear();
        _defensePlan = null;
        _relicHintController.Reset();
        if (_incomingDamageContainer != null)
        {
            _incomingDamageContainer.Visible = false;
        }

        foreach (Label label in _lethalLabels.Values)
        {
            label.Visible = false;
        }

        ClearCardHighlights();
        ResetEnemyHealthBarFlashes();
    }

    private void UpdateRelicHints()
    {
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? localPlayer = combatState == null ? null : MegaCrit.Sts2.Core.Context.LocalContext.GetMe(combatState);
        _relicHintController.Update(localPlayer);
    }

    private void UpdateLethalLabels()
    {
        NCombatRoom? room = NCombatRoom.Instance;
        if (room == null)
        {
            return;
        }

        foreach (Creature enemy in room.CreatureNodes.Where(node => node.Entity.IsEnemy).Select(node => node.Entity))
        {
            Label label = GetOrCreateEnemyLabel(enemy);
            label.Visible = _lethalPlans.TryGetValue(enemy, out MathPredictionEngine.LethalResult? result) && result.Cards.Count > 0;
            label.Text = label.Visible ? FormatLethalHint(result!) : string.Empty;
            label.Size = new Vector2(EnemyHintMaxWidth, 0f);
        }
    }

    private void UpdateOverlayPositions()
    {
        NCombatRoom? room = NCombatRoom.Instance;
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? localPlayer = combatState == null ? null : MegaCrit.Sts2.Core.Context.LocalContext.GetMe(combatState);
        if (room == null)
        {
            return;
        }

        if (_incomingDamageContainer != null && localPlayer != null)
        {
            NCreature? playerNode = room.GetCreatureNode(localPlayer.Creature);
            if (playerNode != null)
            {
                _incomingDamageContainer.GlobalPosition = playerNode.GetTopOfHitbox() + new Vector2(-20f, -84f);
            }
        }

        foreach ((Creature enemy, Label label) in _lethalLabels)
        {
            NCreature? node = room.GetCreatureNode(enemy);
            if (node == null)
            {
                label.Visible = false;
                continue;
            }

            Vector2 labelSize = label.GetCombinedMinimumSize();
            // 敌人头顶提示固定宽度后向上生长，避免英文长句横向串到相邻敌人身上，也尽量不往下挡住战场。
            label.GlobalPosition = node.GetTopOfHitbox() + new Vector2(-EnemyHintMaxWidth * 0.5f, -140f - labelSize.Y);
        }
    }

    private void UpdateCardHighlights()
    {
        NCombatRoom? room = NCombatRoom.Instance;
        CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
        Player? localPlayer = combatState == null ? null : MegaCrit.Sts2.Core.Context.LocalContext.GetMe(combatState);
        if (room?.Ui?.Hand == null || localPlayer?.PlayerCombatState == null)
        {
            ClearCardHighlights();
            return;
        }

        Dictionary<CardModel, Color> desiredHighlights = new();
        if (_defensePlan != null)
        {
            foreach (CardModel card in _defensePlan.Cards.Distinct())
            {
                desiredHighlights[card] = DefenseHighlightColor;
            }
        }

        Creature? preferredTarget = GetPreferredHighlightTarget(room);
        if (preferredTarget != null && _lethalPlans.TryGetValue(preferredTarget, out MathPredictionEngine.LethalResult? lethalResult))
        {
            foreach (CardModel card in lethalResult.Cards.Distinct())
            {
                // 同一张牌若既能防御又能斩杀，优先保留红色，避免攻击牌被防御提示误染成绿色。
                desiredHighlights[card] = LethalHighlightColor;
            }
        }

        MathCardHighlightState.Replace(desiredHighlights);

        // 只在状态切换时还原原版颜色，平时持续覆写目标卡牌的颜色，避免被原版 UpdateCard 抢回去。
        foreach (CardModel staleCard in _activeCardHighlights.Keys.Except(desiredHighlights.Keys).ToList())
        {
            if (room.Ui.Hand.GetCard(staleCard) is NCard staleNode)
            {
                RestoreBaseCardHighlight(staleNode);
            }

            _activeCardHighlights.Remove(staleCard);
        }

        foreach (CardModel card in localPlayer.PlayerCombatState.Hand.Cards)
        {
            NCard? cardNode = room.Ui.Hand.GetCard(card);
            if (cardNode == null)
            {
                continue;
            }

            if (desiredHighlights.TryGetValue(card, out Color color))
            {
                bool needsAnimate = !_activeCardHighlights.TryGetValue(card, out Color previousColor)
                    || previousColor != color
                    || cardNode.CardHighlight.Modulate != color;
                ApplyMathCardHighlight(cardNode, color, needsAnimate);
                _activeCardHighlights[card] = color;
                continue;
            }

            if (_activeCardHighlights.Remove(card))
            {
                RestoreBaseCardHighlight(cardNode);
            }
        }
    }

    private void UpdateEnemyHealthBarFlashes()
    {
        NCombatRoom? room = NCombatRoom.Instance;
        if (room == null)
        {
            return;
        }

        float flash = (Mathf.Sin((float)(Time.GetTicksMsec() / 1000.0 * 8.0)) + 1f) * 0.5f;
        Color flashColor = Colors.White.Lerp(new Color(1f, 0.35f, 0.35f), flash * 0.85f);
        foreach (Creature enemy in room.CreatureNodes.Where(node => node.Entity.IsEnemy).Select(node => node.Entity))
        {
            NCreature? enemyNode = room.GetCreatureNode(enemy);
            if (enemyNode == null)
            {
                continue;
            }

            bool shouldFlash = _lethalPlans.TryGetValue(enemy, out MathPredictionEngine.LethalResult? result) && result.IsLethal;
            SetEnemyHealthBarColor(enemyNode, shouldFlash ? flashColor : Colors.White);
        }
    }

    private void ResetEnemyHealthBarFlashes()
    {
        NCombatRoom? room = NCombatRoom.Instance;
        if (room == null)
        {
            return;
        }

        foreach (NCreature enemyNode in room.CreatureNodes.Where(node => node.Entity.IsEnemy))
        {
            SetEnemyHealthBarColor(enemyNode, Colors.White);
        }
    }

    private static void SetEnemyHealthBarColor(NCreature creatureNode, Color color)
    {
        NCreatureStateDisplay? stateDisplay = creatureNode.GetNodeOrNull<NCreatureStateDisplay>("%HealthBar");
        NHealthBar? healthBar = stateDisplay?.GetNodeOrNull<NHealthBar>("%HealthBar");
        if (healthBar == null)
        {
            return;
        }

        healthBar.HpBarContainer.SelfModulate = color;
    }

    private Creature? GetPreferredHighlightTarget(NCombatRoom room)
    {
        Creature? hoveredEnemy = room.CreatureNodes
            .Where(node => node.Entity.IsEnemy && node.Entity.IsAlive)
            .Select(node => node.Hitbox.HasFocus() || node.Hitbox.GetGlobalRect().HasPoint(GetViewport().GetMousePosition()) ? node.Entity : null)
            .FirstOrDefault(enemy => enemy != null);
        if (hoveredEnemy != null
            && _lethalPlans.TryGetValue(hoveredEnemy, out MathPredictionEngine.LethalResult? hoveredResult)
            && hoveredResult.IsLethal)
        {
            return hoveredEnemy;
        }

        return _lethalPlans.Values
            .Where(static result => result.IsLethal)
            .OrderBy(result => result.EnergySpent + result.StarsSpent)
            .ThenByDescending(result => result.AttackValue)
            .ThenBy(result => result.Target.CurrentHp)
            .Select(result => result.Target)
            .FirstOrDefault();
    }

    private static void ApplyMathCardHighlight(NCard cardNode, Color color, bool animate)
    {
        cardNode.CardHighlight.Modulate = color;
        if (animate)
        {
            cardNode.CardHighlight.AnimShow();
        }
    }

    private static void RestoreBaseCardHighlight(NCard cardNode)
    {
        CardModel? card = cardNode.Model;
        if (card == null || !CombatManager.Instance.IsInProgress)
        {
            cardNode.CardHighlight.AnimHideInstantly();
            return;
        }

        if (card.CanPlay() || card.ShouldGlowRed || card.ShouldGlowGold)
        {
            cardNode.CardHighlight.Modulate = GetBaseHighlightColor(card);
            cardNode.CardHighlight.AnimShow();
            return;
        }

        cardNode.CardHighlight.AnimHideInstantly();
    }

    private static Color GetBaseHighlightColor(CardModel card)
    {
        if (card.ShouldGlowRed)
        {
            return NCardHighlight.red;
        }

        if (card.ShouldGlowGold)
        {
            return NCardHighlight.gold;
        }

        return NCardHighlight.playableColor;
    }

    private void ClearCardHighlights()
    {
        NCombatRoom? room = NCombatRoom.Instance;
        if (room?.Ui?.Hand != null)
        {
            foreach (CardModel card in _activeCardHighlights.Keys.ToList())
            {
                if (room.Ui.Hand.GetCard(card) is NCard cardNode)
                {
                    RestoreBaseCardHighlight(cardNode);
                }
            }
        }

        _activeCardHighlights.Clear();
        MathCardHighlightState.Clear();
    }

    private Label GetOrCreateEnemyLabel(Creature enemy)
    {
        if (_lethalLabels.TryGetValue(enemy, out Label? existing))
        {
            return existing;
        }

        Label label = CreateLabel(fontSize: 18, LethalHighlightColor, Colors.Black, $"Lethal_{enemy.CombatId}");
        label.Size = new Vector2(EnemyHintMaxWidth, 0f);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _lethalLabels[enemy] = label;
        AddChild(label);
        return label;
    }

    private static string FormatDefenseCost(MathPredictionEngine.DefenseResult result, bool richText = false)
    {
        string contributionText = FormatDefenseContributions(result, richText);
        if (result.RemainingHpLoss <= 0)
        {
            int overflow = System.Math.Max(0, result.DefenseValue - result.IncomingHpLoss);
            return FormatText("MATH_COMBAT.DEFENSE.OVERFLOW", ("Contributions", contributionText), ("Overflow", overflow));
        }

        return FormatText("MATH_COMBAT.DEFENSE.MISSING", ("Contributions", contributionText), ("Missing", result.RemainingHpLoss));
    }

    private static string FormatDefenseContributions(MathPredictionEngine.DefenseResult result, bool richText = false)
    {
        if (result.Contributions.Count == 0)
        {
            return FormatText("MATH_COMBAT.DEFENSE.FALLBACK", ("Spend", FormatDefenseSpend(result)), ("DefenseValue", result.DefenseValue));
        }

        return string.Join(GetText("MATH_COMBAT.SEPARATOR"), result.Contributions.Select(contribution => FormatDefenseContribution(contribution, richText)));
    }

    private static string FormatDefenseContribution(MathPredictionEngine.DefenseContribution contribution, bool richText = false)
    {
        string spendText = FormatSpend(contribution.EnergySpent, contribution.StarsSpent);
        string targetName = FormatEnemyName(contribution.TargetName, richText);
        return contribution.Kind switch
        {
            "格挡" => FormatText("MATH_COMBAT.DEFENSE.CONTRIBUTION.BLOCK", ("Spend", spendText), ("Value", contribution.Value)),
            "击杀" when !string.IsNullOrWhiteSpace(contribution.TargetName)
                => FormatText("MATH_COMBAT.DEFENSE.CONTRIBUTION.KILL", ("Spend", spendText), ("Target", targetName), ("Value", contribution.Value)),
            "虚弱" when !string.IsNullOrWhiteSpace(contribution.TargetName)
                => FormatText("MATH_COMBAT.DEFENSE.CONTRIBUTION.WEAK", ("Spend", spendText), ("Target", targetName), ("Value", contribution.Value)),
            "击杀" => FormatText("MATH_COMBAT.DEFENSE.CONTRIBUTION.KILL_FALLBACK", ("Spend", spendText), ("Value", contribution.Value)),
            "虚弱" => FormatText("MATH_COMBAT.DEFENSE.CONTRIBUTION.WEAK_FALLBACK", ("Spend", spendText), ("Value", contribution.Value)),
            _ => FormatText("MATH_COMBAT.DEFENSE.CONTRIBUTION.FALLBACK", ("Spend", spendText), ("Kind", contribution.Kind), ("Value", contribution.Value))
        };
    }

    private static string FormatEnemyName(string? enemyName, bool richText)
    {
        if (string.IsNullOrWhiteSpace(enemyName))
        {
            return string.Empty;
        }

        if (!richText)
        {
            return enemyName;
        }

        return $"[color={EnemyNameBbColor}]{enemyName}[/color]";
    }

    private static string FormatLethalHint(MathPredictionEngine.LethalResult result)
    {
        string spendText = FormatLethalSpend(result);
        List<string> effectTags = new();
        if (result.UsesVulnerable)
        {
            effectTags.Add(GetText("MATH_COMBAT.EFFECT.VULNERABLE"));
        }

        if (result.UsesPoison)
        {
            effectTags.Add(GetText("MATH_COMBAT.EFFECT.POISON"));
        }

        string suffix = FormatEffectSuffix(effectTags);
        if (result.IsLethal)
        {
            return FormatText("MATH_COMBAT.LETHAL.OVERFLOW", ("Spend", spendText), ("AttackValue", result.AttackValue), ("OverflowValue", result.OverflowValue), ("Suffix", suffix));
        }

        return FormatText("MATH_COMBAT.LETHAL.REMAIN", ("Spend", spendText), ("AttackValue", result.AttackValue), ("RemainingHp", result.RemainingHp), ("Suffix", suffix));
    }

    private static string FormatDefenseSpend(MathPredictionEngine.DefenseResult result)
    {
        return FormatSpend(result.EnergySpent, result.StarsSpent);
    }

    private static string FormatLethalSpend(MathPredictionEngine.LethalResult result)
    {
        return FormatText("MATH_COMBAT.LETHAL.SPEND_MAX", ("Spend", FormatSpend(result.EnergySpent, result.StarsSpent)));
    }

    private static string FormatSpend(int energySpent, int starsSpent)
    {
        if (starsSpent > 0)
        {
            return FormatText("MATH_COMBAT.SPEND.ENERGY_STARS", ("Energy", energySpent), ("Stars", starsSpent));
        }

        return FormatText("MATH_COMBAT.SPEND.ENERGY", ("Energy", energySpent));
    }

    private static string FormatEffectSuffix(IEnumerable<string> effects)
    {
        string[] tags = effects.Where(static effect => !string.IsNullOrWhiteSpace(effect)).Distinct().ToArray();
        if (tags.Length == 0)
        {
            return string.Empty;
        }

        return FormatText("MATH_COMBAT.EFFECT_SUFFIX", ("Effects", string.Join(GetText("MATH_COMBAT.EFFECT_SEPARATOR"), tags)));
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
                case LocString nestedLocString:
                    locString.Add(name, nestedLocString);
                    break;
                default:
                    locString.Add(name, value.ToString() ?? string.Empty);
                    break;
            }
        }

        return locString.GetFormattedText();
    }

    private static Label CreateLabel(int fontSize, Color fontColor, Color outlineColor, string name)
    {
        Label label = new()
        {
            Name = name,
            MouseFilter = MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visible = false,
            Text = string.Empty
        };

        label.AddThemeColorOverride("font_color", fontColor);
        label.AddThemeColorOverride("font_outline_color", outlineColor);
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeConstantOverride("outline_size", fontSize >= 36 ? 8 : 6);
        return label;
    }

    private static RichTextLabel CreateRichTextLabel(int fontSize, Color fontColor, Color outlineColor, string name)
    {
        RichTextLabel label = new()
        {
            Name = name,
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
            Text = string.Empty,
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.Off
        };

        label.AddThemeColorOverride("default_color", fontColor);
        label.AddThemeColorOverride("font_outline_color", outlineColor);
        label.AddThemeFontSizeOverride("normal_font_size", fontSize);
        label.AddThemeConstantOverride("outline_size", fontSize >= 36 ? 8 : 6);
        return label;
    }
}
