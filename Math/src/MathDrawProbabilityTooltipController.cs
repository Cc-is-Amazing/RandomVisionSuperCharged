using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace MathMod;

internal static class MathDrawProbabilityTooltipController
{
    private static readonly Dictionary<NCardHolder, MathDrawProbabilityTooltip> _activeTooltips = new();

    public static void ShowFor(NCardHolder holder)
    {
        CleanupInvalidTooltips();

        string? text = BuildTooltipText(holder);
        if (string.IsNullOrWhiteSpace(text))
        {
            HideFor(holder);
            return;
        }

        if (!_activeTooltips.TryGetValue(holder, out MathDrawProbabilityTooltip? tooltip)
            || !GodotObject.IsInstanceValid(tooltip))
        {
            tooltip = new MathDrawProbabilityTooltip();
            holder.AddChild(tooltip);
            holder.MoveChild(tooltip, holder.GetChildCount() - 1);
            _activeTooltips[holder] = tooltip;
        }

        tooltip.AttachTo(holder, text);
    }

    public static void HideFor(NCardHolder holder)
    {
        CleanupInvalidTooltips();
        if (_activeTooltips.Remove(holder, out MathDrawProbabilityTooltip? tooltip) && GodotObject.IsInstanceValid(tooltip))
        {
            tooltip.QueueFree();
        }
    }

    public static void RefreshVisibleTooltips()
    {
        CleanupInvalidTooltips();
        foreach (NCardHolder holder in _activeTooltips.Keys.ToList())
        {
            ShowFor(holder);
        }
    }

    private static string? BuildTooltipText(NCardHolder holder)
    {
        if (!TryGetDrawPileScreen(holder, out NCardPileScreen? drawPileScreen) || drawPileScreen == null)
        {
            return null;
        }

        CardModel? card = holder.CardModel;
        if (card == null)
        {
            return null;
        }

        IReadOnlyList<int> selectedCounts = MathModConfig.SelectedDrawProbabilityCounts;
        if (selectedCounts.Count == 0)
        {
            return null;
        }

        IReadOnlyList<CardModel> pileCards = drawPileScreen.Pile.Cards;
        int pileCount = pileCards.Count;
        if (pileCount <= 0)
        {
            return null;
        }

        int equivalentCardCount = CountEquivalentCards(pileCards, card);
        if (equivalentCardCount <= 0)
        {
            return null;
        }
        StringBuilder builder = new();
        builder.Append(GetCountSummaryText(equivalentCardCount, pileCount));
        foreach (int drawCount in selectedCounts)
        {
            builder.Append('\n');
            builder.Append(GetProbabilityLineText(drawCount, pileCount, equivalentCardCount));
        }

        return builder.ToString();
    }

    private static int CountEquivalentCards(IEnumerable<CardModel> pileCards, CardModel targetCard)
    {
        string targetFingerprint = BuildEquivalenceFingerprint(targetCard);
        int count = 0;
        foreach (CardModel pileCard in pileCards)
        {
            if (BuildEquivalenceFingerprint(pileCard) == targetFingerprint)
            {
                count++;
            }
        }

        return count;
    }

    private static string GetCountSummaryText(int equivalentCardCount, int pileCount)
    {
        LocString locString = L10N("MATH_DRAW_PROBABILITY.CARDS_LINE");
        locString.Add("EquivalentCount", equivalentCardCount.ToString());
        locString.Add("PileCount", pileCount.ToString());
        return locString.GetFormattedText();
    }

    private static string GetProbabilityLineText(int drawCount, int pileCount, int equivalentCardCount)
    {
        LocString locString = L10N("MATH_DRAW_PROBABILITY.DRAW_LINE");
        locString.Add("DrawCount", drawCount.ToString());
        locString.Add("Chance", drawCount >= pileCount
            ? FormatProbabilityPercent(100d)
            : FormatProbability(drawCount, pileCount, equivalentCardCount));
        return locString.GetFormattedText();
    }

    private static string FormatProbability(int drawCount, int pileCount, int equivalentCardCount)
    {
        double probability = CalculateAtLeastOneHitProbability(drawCount, pileCount, equivalentCardCount) * 100d;
        return FormatProbabilityPercent(probability);
    }

    private static string FormatProbabilityPercent(double probability)
    {
        return $"{probability:0.##}%";
    }

    private static double CalculateAtLeastOneHitProbability(int drawCount, int pileCount, int equivalentCardCount)
    {
        if (drawCount <= 0 || equivalentCardCount <= 0 || pileCount <= 0)
        {
            return 0d;
        }

        if (drawCount >= pileCount)
        {
            return 1d;
        }

        if (equivalentCardCount >= pileCount)
        {
            return 1d;
        }

        // 抽牌是不放回抽样，因此这里算“前 N 抽一张等价牌都没见到”的概率，
        // 再用 1 减掉它，避免把多张等价牌误算成单张牌的 N/总数。
        double missProbability = 1d;
        for (int drawIndex = 0; drawIndex < drawCount; drawIndex++)
        {
            missProbability *= (double)(pileCount - equivalentCardCount - drawIndex) / (pileCount - drawIndex);
        }

        return 1d - missProbability;
    }

    private static string BuildEquivalenceFingerprint(CardModel card)
    {
        SerializableCard serializableCard = card.ToSerializable();
        return string.Join("|",
            $"id={serializableCard.Id}",
            $"upgrade={serializableCard.CurrentUpgradeLevel}",
            $"enchant={BuildEnchantmentFingerprint(serializableCard.Enchantment)}",
            $"props={BuildSavedPropertiesFingerprint(serializableCard.Props)}");
    }

    private static string BuildEnchantmentFingerprint(SerializableEnchantment? enchantment)
    {
        if (enchantment == null)
        {
            return "none";
        }

        return string.Join(",",
            $"id={enchantment.Id}",
            $"amount={enchantment.Amount}",
            $"props={BuildSavedPropertiesFingerprint(enchantment.Props)}");
    }

    private static string BuildSavedPropertiesFingerprint(SavedProperties? props)
    {
        if (props == null)
        {
            return "none";
        }

        StringBuilder builder = new();
        AppendIntProperties(builder, props.ints);
        AppendBoolProperties(builder, props.bools);
        AppendStringProperties(builder, props.strings);
        AppendIntArrayProperties(builder, props.intArrays);
        AppendModelIdProperties(builder, props.modelIds);
        AppendCardProperties(builder, props.cards);
        AppendCardArrayProperties(builder, props.cardArrays);
        return builder.ToString();
    }

    private static void AppendIntProperties(StringBuilder builder, IEnumerable<SavedProperties.SavedProperty<int>>? values)
    {
        foreach (SavedProperties.SavedProperty<int> item in values?.OrderBy(static entry => entry.name) ?? Enumerable.Empty<SavedProperties.SavedProperty<int>>())
        {
            builder.Append($"int:{item.name}={item.value};");
        }
    }

    private static void AppendBoolProperties(StringBuilder builder, IEnumerable<SavedProperties.SavedProperty<bool>>? values)
    {
        foreach (SavedProperties.SavedProperty<bool> item in values?.OrderBy(static entry => entry.name) ?? Enumerable.Empty<SavedProperties.SavedProperty<bool>>())
        {
            builder.Append($"bool:{item.name}={item.value};");
        }
    }

    private static void AppendStringProperties(StringBuilder builder, IEnumerable<SavedProperties.SavedProperty<string>>? values)
    {
        foreach (SavedProperties.SavedProperty<string> item in values?.OrderBy(static entry => entry.name) ?? Enumerable.Empty<SavedProperties.SavedProperty<string>>())
        {
            builder.Append($"string:{item.name}={item.value};");
        }
    }

    private static void AppendIntArrayProperties(StringBuilder builder, IEnumerable<SavedProperties.SavedProperty<int[]>>? values)
    {
        foreach (SavedProperties.SavedProperty<int[]> item in values?.OrderBy(static entry => entry.name) ?? Enumerable.Empty<SavedProperties.SavedProperty<int[]>>())
        {
            builder.Append($"int_array:{item.name}=[{string.Join(',', item.value)}];");
        }
    }

    private static void AppendModelIdProperties(StringBuilder builder, IEnumerable<SavedProperties.SavedProperty<ModelId>>? values)
    {
        foreach (SavedProperties.SavedProperty<ModelId> item in values?.OrderBy(static entry => entry.name) ?? Enumerable.Empty<SavedProperties.SavedProperty<ModelId>>())
        {
            builder.Append($"model:{item.name}={item.value};");
        }
    }

    private static void AppendCardProperties(StringBuilder builder, IEnumerable<SavedProperties.SavedProperty<SerializableCard>>? values)
    {
        foreach (SavedProperties.SavedProperty<SerializableCard> item in values?.OrderBy(static entry => entry.name) ?? Enumerable.Empty<SavedProperties.SavedProperty<SerializableCard>>())
        {
            builder.Append($"card:{item.name}={BuildSerializableCardFingerprint(item.value)};");
        }
    }

    private static void AppendCardArrayProperties(StringBuilder builder, IEnumerable<SavedProperties.SavedProperty<SerializableCard[]>>? values)
    {
        foreach (SavedProperties.SavedProperty<SerializableCard[]> item in values?.OrderBy(static entry => entry.name) ?? Enumerable.Empty<SavedProperties.SavedProperty<SerializableCard[]>>())
        {
            IEnumerable<string> cardFingerprints = item.value.Select(BuildSerializableCardFingerprint);
            builder.Append($"card_array:{item.name}=[{string.Join(',', cardFingerprints)}];");
        }
    }

    private static string BuildSerializableCardFingerprint(SerializableCard card)
    {
        return string.Join(",",
            $"id={card.Id}",
            $"upgrade={card.CurrentUpgradeLevel}",
            $"enchant={BuildEnchantmentFingerprint(card.Enchantment)}",
            $"props={BuildSavedPropertiesFingerprint(card.Props)}");
    }

    private static LocString L10N(string key)
    {
        return new LocString("static_hover_tips", key);
    }

    private static bool TryGetDrawPileScreen(NCardHolder holder, out NCardPileScreen? drawPileScreen)
    {
        Node? current = holder;
        while (current != null)
        {
            if (current is NCardPileScreen screen)
            {
                drawPileScreen = screen.Pile.Type == PileType.Draw ? screen : null;
                return drawPileScreen != null;
            }

            current = current.GetParent();
        }

        drawPileScreen = null;
        return false;
    }

    private static void CleanupInvalidTooltips()
    {
        foreach (NCardHolder holder in _activeTooltips.Keys.ToList())
        {
            bool invalidHolder = !GodotObject.IsInstanceValid(holder) || !holder.IsInsideTree();
            bool invalidTooltip = !_activeTooltips.TryGetValue(holder, out MathDrawProbabilityTooltip? tooltip)
                || !GodotObject.IsInstanceValid(tooltip)
                || !tooltip.IsInsideTree();
            if (!invalidHolder && !invalidTooltip)
            {
                continue;
            }

            if (_activeTooltips.Remove(holder, out MathDrawProbabilityTooltip? removedTooltip)
                && GodotObject.IsInstanceValid(removedTooltip))
            {
                removedTooltip.QueueFree();
            }
        }
    }
}
