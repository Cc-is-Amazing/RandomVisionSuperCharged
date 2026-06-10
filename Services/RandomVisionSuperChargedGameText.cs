using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

namespace RandomVisionSuperCharged.Services;

internal static class RandomVisionSuperChargedGameText
{
    private static readonly Regex ImageTagRegex = new(
        @"\[(?:img|image)(?:=(?<attr>[^\]]+))?\](?<body>.*?)\[/\s*(?:img|image)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ResourcePathRegex = new(
        @"res://[^\s\]\[\)""']+?\.(?:png|webp|svg)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BbCodeRegex = new(@"\[[^\]]+\]", RegexOptions.Compiled);
    private static readonly Regex MultiSpaceRegex = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly HashSet<string> StyleTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "gold", "blue", "red", "green", "purple", "white", "gray", "grey"
    };
    private static readonly HashSet<string> KnownRawTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "colorless",
        "ironclad",
        "silent",
        "defect",
        "regent",
        "necrobinder",
        "attack",
        "skill",
        "power",
        "common",
        "uncommon",
        "rare"
    };

    public static string ResolveCardTitle(CardModel card)
    {
        if (!RandomVisionSuperChargedI18n.IsChinese() && !LocString.IsNullOrWhitespace(card.TitleLocString))
        {
            var localizedTitle = ResolveLocString(card.TitleLocString, card.DynamicVars);
            if (!string.IsNullOrWhiteSpace(localizedTitle))
            {
                return localizedTitle;
            }
        }

        if (!string.IsNullOrWhiteSpace(card.Title))
        {
            return card.Title.Trim();
        }

        return ResolveLocString(card.TitleLocString, card.DynamicVars);
    }

    public static string ResolveRelicTitle(RelicModel relic)
    {
        return ResolveLocString(relic.Title, relic.DynamicVars);
    }

    public static string ResolveCardDescription(CardModel card)
    {
        try
        {
            return ResolveWithRandomVisionSuperChargedLanguage(() => Clean(card.GetDescriptionForPile(PileType.None)));
        }
        catch
        {
            return ResolveLocString(card.Description, card.DynamicVars);
        }
    }

    public static string ResolvePotionTitle(PotionModel potion)
    {
        return ResolveLocString(potion.Title, potion.DynamicVars);
    }

    public static string ResolveModelTitle(AbstractModel? model)
    {
        return model switch
        {
            CardModel card => ResolveCardTitle(card),
            RelicModel relic => ResolveRelicTitle(relic),
            PotionModel potion => ResolvePotionTitle(potion),
            PowerModel power => ResolveLocString(power.Title, power.DynamicVars),
            EnchantmentModel enchantment => ResolveLocString(enchantment.Title, enchantment.DynamicVars),
            AfflictionModel affliction => ResolveLocString(affliction.Title),
            OrbModel orb => ResolveLocString(orb.Title),
            _ => string.Empty
        };
    }

    public static string ResolveLocString(LocString? locString, DynamicVarSet? dynamicVars = null)
    {
        return ResolveWithRandomVisionSuperChargedLanguage(() => ResolveLocStringCore(locString, dynamicVars));
    }

    private static string ResolveLocStringCore(LocString? locString, DynamicVarSet? dynamicVars = null)
    {
        if (LocString.IsNullOrWhitespace(locString))
        {
            return string.Empty;
        }

        var variables = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (locString?.Variables is not null)
        {
            foreach (var pair in locString.Variables)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
                {
                    variables[pair.Key] = pair.Value;
                }
            }
        }

        if (dynamicVars is not null)
        {
            foreach (var pair in dynamicVars)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key))
                {
                    variables[pair.Key] = pair.Value;
                }
            }
        }

        if (LocManager.Instance is not null)
        {
            try
            {
                if (variables.Count > 0 && locString is not null)
                {
                    return Clean(LocManager.Instance.SmartFormat(locString, variables));
                }
            }
            catch
            {
            }
        }

        try
        {
            return Clean(locString!.GetFormattedText());
        }
        catch
        {
            try
            {
                return Clean(locString!.GetRawText());
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    private static string ResolveWithRandomVisionSuperChargedLanguage(Func<string> resolve)
    {
        var locManager = LocManager.Instance;
        if (locManager is null ||
            RandomVisionSuperChargedI18n.IsChinese() ||
            locManager.OverridesActive ||
            IsEnglishLanguage(locManager.Language))
        {
            return resolve();
        }

        try
        {
            locManager.StartOverridingLanguageAsEnglish();
            return resolve();
        }
        catch
        {
            return resolve();
        }
        finally
        {
            try
            {
                locManager.StopOverridingLanguageAsEnglish();
            }
            catch
            {
            }
        }
    }

    private static bool IsEnglishLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return false;
        }

        var normalized = language.Trim().Replace('-', '_').ToLowerInvariant();
        return normalized is "en" or "eng" or "en_us" or "en_gb";
    }

    public static IReadOnlyList<string> SummarizeHoverTips(IEnumerable<IHoverTip>? hoverTips)
    {
        var lines = new List<string>();
        if (hoverTips is null)
        {
            return lines;
        }

        foreach (var hoverTip in IHoverTip.RemoveDupes(hoverTips))
        {
            var line = SummarizeHoverTip(hoverTip);
            if (!string.IsNullOrWhiteSpace(line) &&
                !lines.Contains(line, StringComparer.OrdinalIgnoreCase))
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    public static IReadOnlyList<EventPreviewEntity> ExtractPreviewEntities(IEnumerable<IHoverTip>? hoverTips)
    {
        var entities = new List<EventPreviewEntity>();
        if (hoverTips is null)
        {
            return entities;
        }

        foreach (var hoverTip in IHoverTip.RemoveDupes(hoverTips))
        {
            switch (hoverTip)
            {
                case CardHoverTip cardHoverTip:
                    AddEntity(entities, CreateCardEntity(cardHoverTip.Card));
                    break;
                default:
                    AddEntity(entities, CreateEntityFromModel(hoverTip.CanonicalModel));
                    break;
            }
        }

        return entities;
    }

    private static string? SummarizeHoverTip(IHoverTip hoverTip)
    {
        switch (hoverTip)
        {
            case CardHoverTip cardHoverTip:
                var title = ResolveCardTitle(cardHoverTip.Card);
                var description = ResolveCardDescription(cardHoverTip.Card);
                var prefix = RandomVisionSuperChargedI18n.Pick("Card:", "\u5361\u724c\uff1a");
                return string.IsNullOrWhiteSpace(description)
                    ? $"{prefix}{title}"
                    : $"{prefix}{title} - {description}";
            case HoverTip textHoverTip:
                if (!string.IsNullOrWhiteSpace(textHoverTip.Title))
                {
                    return Clean(textHoverTip.Title);
                }

                if (!string.IsNullOrWhiteSpace(textHoverTip.Description))
                {
                    return Clean(textHoverTip.Description);
                }

                break;
        }

        var modelTitle = ResolveModelTitle(hoverTip.CanonicalModel);
        if (!string.IsNullOrWhiteSpace(modelTitle))
        {
            return modelTitle;
        }

        return null;
    }

    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var cleaned = text
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        cleaned = ImageTagRegex.Replace(cleaned, ReplaceImageTag);
        cleaned = ResourcePathRegex.Replace(cleaned, ReplaceResourcePath);
        cleaned = BbCodeRegex.Replace(cleaned, string.Empty).Trim();
        cleaned = NormalizeIconText(cleaned);
        cleaned = MultiSpaceRegex.Replace(cleaned, " ").Trim();
        cleaned = NormalizeLooseTokens(cleaned);
        return cleaned;
    }

    private static string ReplaceImageTag(Match match)
    {
        var source = $"{match.Groups["attr"].Value} {match.Groups["body"].Value}";
        return ReplaceImageSource(source);
    }

    private static string ReplaceResourcePath(Match match)
    {
        return ReplaceImageSource(match.Value);
    }

    private static string ReplaceImageSource(string source)
    {
        if (source.Contains("energy_icon", StringComparison.OrdinalIgnoreCase))
        {
            return $" {RandomVisionSuperChargedI18n.Pick("Energy", "\u80fd\u91cf")} ";
        }

        return " ";
    }

    private static string NormalizeIconText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var energy = RandomVisionSuperChargedI18n.Pick("Energy", "\u80fd\u91cf");
        var normalized = Regex.Replace(
            text,
            $@"(?:{Regex.Escape(energy)}\s*){{2,}}",
            match =>
            {
                var count = Regex.Matches(match.Value, Regex.Escape(energy)).Count;
                return $"{count} {energy} ";
            },
            RegexOptions.CultureInvariant);

        if (RandomVisionSuperChargedI18n.IsChinese())
        {
            normalized = Regex.Replace(normalized, @"获得\s*(?<count>\d+)\s*能量", "获得${count}点能量", RegexOptions.CultureInvariant);
            normalized = Regex.Replace(normalized, @"获得\s*能量", "获得1点能量", RegexOptions.CultureInvariant);
        }
        else
        {
            normalized = Regex.Replace(normalized, @"\bGain\s+Energy\b", "Gain 1 Energy", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return normalized;
    }

    private static EventPreviewEntity CreateCardEntity(CardModel card)
    {
        var hoverTips = IHoverTip.RemoveDupes(new IHoverTip[] { new CardHoverTip(card) }.Concat(card.HoverTips));
        return new EventPreviewEntity($"card:{card.Id}:{card.IsUpgraded}", ResolveCardTitle(card), hoverTips);
    }

    private static EventPreviewEntity? CreateEntityFromModel(AbstractModel? model)
    {
        return model switch
        {
            CardModel card => CreateCardEntity(card),
            RelicModel relic => new EventPreviewEntity($"relic:{relic.Id}", ResolveRelicTitle(relic), IHoverTip.RemoveDupes(relic.HoverTips)),
            PotionModel potion => new EventPreviewEntity($"potion:{potion.Id}", ResolvePotionTitle(potion), IHoverTip.RemoveDupes(potion.HoverTips)),
            _ => null
        };
    }

    private static void AddEntity(ICollection<EventPreviewEntity> entities, EventPreviewEntity? entity)
    {
        if (entity is null)
        {
            return;
        }

        if (entities.Any(existing => string.Equals(existing.Key, entity.Key, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        entities.Add(entity);
    }

    private static string NormalizeLooseTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (text.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF))
        {
            return text;
        }

        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return string.Empty;
        }

        if (tokens.All(token => StyleTokens.Contains(token)))
        {
            return string.Empty;
        }

        if (tokens.All(token => StyleTokens.Contains(token) || KnownRawTokens.Contains(token)))
        {
            var normalized = tokens
                .Where(token => !StyleTokens.Contains(token))
                .Select(RandomVisionSuperChargedI18n.LocalizeToken)
                .ToArray();
            return normalized.Length == 0 ? string.Empty : string.Join(" ", normalized);
        }

        return text;
    }
}
