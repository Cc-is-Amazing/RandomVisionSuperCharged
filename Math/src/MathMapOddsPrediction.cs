using System.Text;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace MathMod;

internal static class MathMapOddsPrediction
{
    private const string LocTable = "map";

    private static readonly RoomType[] OrderedUnknownNonEventRoomTypes =
    {
        RoomType.Monster,
        RoomType.Elite,
        RoomType.Treasure,
        RoomType.Shop
    };

    public static bool TryBuildDescription(NMapPoint point, out string title, out string description)
    {
        title = GetText("MATH_MAP_ODDS.PREDICTION_TITLE");
        description = string.Empty;

        // 这里只展示“当前这一步真的会按这个值结算”的节点，避免把当前保底值误读成几层之后的精确预测。
        if (point.State != MapPointState.Travelable)
        {
            return false;
        }

        IRunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
        {
            return false;
        }

        switch (point.Point.PointType)
        {
            case MapPointType.Unknown:
                description = BuildUnknownNodeDescription(runState);
                return !string.IsNullOrWhiteSpace(description);
            case MapPointType.Monster:
                return TryBuildPotionDescription(runState, RoomType.Monster, out description);
            case MapPointType.Elite:
                return TryBuildPotionDescription(runState, RoomType.Elite, out description);
            default:
                return false;
        }
    }

    private static string BuildUnknownNodeDescription(IRunState runState)
    {
        if (TryGetTutorialUnknownEncounterDescription(runState, out string tutorialDescription))
        {
            return tutorialDescription;
        }

        Dictionary<RoomType, float> roomTypeProbabilities = CalculateUnknownRoomTypeProbabilities(runState);
        float monsterChance = roomTypeProbabilities.GetValueOrDefault(RoomType.Monster);
        float eliteChance = roomTypeProbabilities.GetValueOrDefault(RoomType.Elite);
        float encounterChance = monsterChance + eliteChance;

        StringBuilder builder = new();
        builder.Append(FormatText("MATH_MAP_ODDS.UNKNOWN.ENCOUNTER_CHANCE", ("Chance", FormatPercent(encounterChance))));
        builder.Append('\n');
        builder.Append(FormatText("MATH_MAP_ODDS.UNKNOWN.MONSTER_CHANCE", ("Chance", FormatPercent(monsterChance))));
        if (eliteChance > 0f)
        {
            builder.Append('\n');
            builder.Append(FormatText("MATH_MAP_ODDS.UNKNOWN.ELITE_CHANCE", ("Chance", FormatPercent(eliteChance))));
        }

        List<string> otherOutcomes = new();
        AppendOutcome(otherOutcomes, GetText("MATH_MAP_ODDS.UNKNOWN.EVENT_LABEL"), roomTypeProbabilities.GetValueOrDefault(RoomType.Event));
        AppendOutcome(otherOutcomes, GetText("MATH_MAP_ODDS.UNKNOWN.SHOP_LABEL"), roomTypeProbabilities.GetValueOrDefault(RoomType.Shop));
        AppendOutcome(otherOutcomes, GetText("MATH_MAP_ODDS.UNKNOWN.TREASURE_LABEL"), roomTypeProbabilities.GetValueOrDefault(RoomType.Treasure));
        if (otherOutcomes.Count > 0)
        {
            builder.Append('\n');
            builder.Append(string.Join(GetText("MATH_MAP_ODDS.UNKNOWN.OUTCOME_SEPARATOR"), otherOutcomes));
        }

        return builder.ToString();
    }

    private static bool TryBuildPotionDescription(IRunState runState, RoomType roomType, out string description)
    {
        description = string.Empty;

        Player? localPlayer = LocalContext.GetMe(runState);
        if (localPlayer?.PlayerOdds?.PotionReward == null)
        {
            return false;
        }

        float baseChance = Clamp01(localPlayer.PlayerOdds.PotionReward.CurrentValue);
        float eliteBonus = roomType == RoomType.Elite ? 0.125f : 0f;
        bool isForcedDrop = Hook.ShouldForcePotionReward(runState, localPlayer, roomType);
        float actualChance = isForcedDrop ? 1f : Clamp01(baseChance + eliteBonus);
        float nextBaseOnDrop = Clamp01(baseChance - 0.1f);
        float nextBaseOnMiss = Clamp01(baseChance + 0.1f);

        StringBuilder builder = new();
        builder.Append(FormatText("MATH_MAP_ODDS.POTION.CURRENT_CHANCE", ("Chance", FormatPercent(actualChance))));
        if (isForcedDrop)
        {
            builder.Append('\n');
            builder.Append(GetText("MATH_MAP_ODDS.POTION.FORCED_DROP"));
            builder.Append('\n');
            builder.Append(GetText("MATH_MAP_ODDS.POTION.FORCED_DROP_NOTE"));
        }
        else if (roomType == RoomType.Elite)
        {
            builder.Append('\n');
            builder.Append(FormatText(
                "MATH_MAP_ODDS.POTION.ELITE_BONUS",
                ("BaseChance", FormatPercent(baseChance)),
                ("BonusChance", FormatPercent(eliteBonus))));
        }

        builder.Append('\n');
        builder.Append(FormatText(
            "MATH_MAP_ODDS.POTION.NEXT_BASE",
            ("OnDrop", FormatPercent(nextBaseOnDrop)),
            ("OnMiss", FormatPercent(nextBaseOnMiss))));
        description = builder.ToString();
        return true;
    }

    private static Dictionary<RoomType, float> CalculateUnknownRoomTypeProbabilities(IRunState runState)
    {
        HashSet<RoomType> blacklist = RunManager.BuildRoomTypeBlacklist(
            runState.CurrentMapPointHistoryEntry,
            runState.CurrentMapPoint?.Children ?? new HashSet<MapPoint>());

        IReadOnlySet<RoomType> availableRoomTypes = new HashSet<RoomType>
        {
            RoomType.Monster,
            RoomType.Elite,
            RoomType.Treasure,
            RoomType.Shop,
            RoomType.Event
        };
        availableRoomTypes = availableRoomTypes.Except(blacklist).ToHashSet();
        availableRoomTypes = Hook.ModifyUnknownMapPointRoomTypes(runState, availableRoomTypes);

        Dictionary<RoomType, float> probabilities = new();
        if (availableRoomTypes.Count == 0)
        {
            return probabilities;
        }

        RoomType defaultRoomType = availableRoomTypes.Contains(RoomType.Event)
            ? RoomType.Event
            : availableRoomTypes.Order().First();
        probabilities[defaultRoomType] = 1f;

        UnknownRoomOddsSnapshot odds = new(runState.Odds.UnknownMapPoint);
        foreach (RoomType roomType in OrderedUnknownNonEventRoomTypes)
        {
            if (!availableRoomTypes.Contains(roomType))
            {
                continue;
            }

            float roomTypeChance = odds.GetChance(roomType);
            if (roomTypeChance < 0f)
            {
                continue;
            }

            probabilities[defaultRoomType] = probabilities.GetValueOrDefault(defaultRoomType) - roomTypeChance;
            probabilities[roomType] = probabilities.GetValueOrDefault(roomType) + roomTypeChance;
        }

        foreach (RoomType roomType in probabilities.Keys.ToList())
        {
            probabilities[roomType] = Clamp01(probabilities[roomType]);
        }

        return probabilities;
    }

    private static bool TryGetTutorialUnknownEncounterDescription(IRunState runState, out string description)
    {
        description = string.Empty;
        if (runState.UnlockState.NumberOfRuns != 0)
        {
            return false;
        }

        int previousUnknownCount = runState.MapPointHistory
            .SelectMany(static entries => entries)
            .Count(static entry => entry.MapPointType == MapPointType.Unknown);

        if (previousUnknownCount < 2)
        {
            description = GetText("MATH_MAP_ODDS.UNKNOWN.TUTORIAL_FIRST_TWO");
            return true;
        }

        if (previousUnknownCount == 2)
        {
            description = GetText("MATH_MAP_ODDS.UNKNOWN.TUTORIAL_THIRD");
            return true;
        }

        return false;
    }

    private static void AppendOutcome(List<string> outcomes, string label, float chance)
    {
        if (chance <= 0f)
        {
            return;
        }

        outcomes.Add(FormatText("MATH_MAP_ODDS.UNKNOWN.OTHER_OUTCOME", ("Label", label), ("Chance", FormatPercent(chance))));
    }

    private static string GetText(string key)
    {
        return new LocString(LocTable, key).GetFormattedText();
    }

    private static string FormatText(string key, params (string Name, string Value)[] variables)
    {
        LocString locString = new(LocTable, key);
        foreach ((string name, string value) in variables)
        {
            locString.Add(name, value);
        }

        return locString.GetFormattedText();
    }

    private static float Clamp01(float value)
    {
        return Math.Clamp(value, 0f, 1f);
    }

    private static string FormatPercent(float value)
    {
        float percent = MathF.Round(Clamp01(value) * 1000f) / 10f;
        return percent % 1f == 0f ? $"{percent:0}%" : $"{percent:0.#}%";
    }

    private readonly record struct UnknownRoomOddsSnapshot(float Monster, float Elite, float Treasure, float Shop)
    {
        public UnknownRoomOddsSnapshot(MegaCrit.Sts2.Core.Odds.UnknownMapPointOdds odds)
            : this(odds.MonsterOdds, odds.EliteOdds, odds.TreasureOdds, odds.ShopOdds)
        {
        }

        public float GetChance(RoomType roomType)
        {
            return roomType switch
            {
                RoomType.Monster => Monster,
                RoomType.Elite => Elite,
                RoomType.Treasure => Treasure,
                RoomType.Shop => Shop,
                _ => 0f
            };
        }
    }
}
