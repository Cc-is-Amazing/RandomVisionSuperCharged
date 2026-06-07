using System.Text;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace MathMod;

internal static class MathPotionOddsPrediction
{
    private const string LocTable = "static_hover_tips";

    public static bool TryBuildEmptySlotTip(out string title, out string description)
    {
        title = GetText("MATH_POTION_ODDS.PREDICTION_TITLE");
        description = string.Empty;

        IRunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
        {
            return false;
        }

        Player? localPlayer = LocalContext.GetMe(runState);
        if (localPlayer?.PlayerOdds?.PotionReward == null)
        {
            return false;
        }

        float baseChance = Clamp01(localPlayer.PlayerOdds.PotionReward.CurrentValue);
        float monsterChance = BuildActualChance(runState, localPlayer, RoomType.Monster, baseChance);
        float eliteChance = BuildActualChance(runState, localPlayer, RoomType.Elite, baseChance);
        float nextBaseOnDrop = Clamp01(baseChance - 0.1f);
        float nextBaseOnMiss = Clamp01(baseChance + 0.1f);
        bool forcedOnMonster = Hook.ShouldForcePotionReward(runState, localPlayer, RoomType.Monster);
        bool forcedOnElite = Hook.ShouldForcePotionReward(runState, localPlayer, RoomType.Elite);

        StringBuilder builder = new();
        builder.Append(FormatText("MATH_POTION_ODDS.EMPTY_SLOT.BASE", ("Chance", FormatPercent(baseChance))));
        builder.Append('\n');
        builder.Append(FormatText("MATH_POTION_ODDS.EMPTY_SLOT.MONSTER", ("Chance", FormatPercent(monsterChance))));
        builder.Append('\n');
        builder.Append(FormatText("MATH_POTION_ODDS.EMPTY_SLOT.ELITE", ("Chance", FormatPercent(eliteChance))));
        if (forcedOnMonster || forcedOnElite)
        {
            builder.Append('\n');
            // 空药水槽没有上下文目标房间，这里只提示“当前存在保底/强制效果”，避免误写成特定节点结果。
            builder.Append(GetText("MATH_POTION_ODDS.EMPTY_SLOT.FORCED_NOTE"));
        }

        builder.Append('\n');
        builder.Append(FormatText(
            "MATH_POTION_ODDS.EMPTY_SLOT.NEXT_BASE",
            ("OnDrop", FormatPercent(nextBaseOnDrop)),
            ("OnMiss", FormatPercent(nextBaseOnMiss))));

        description = builder.ToString();
        return true;
    }

    private static float BuildActualChance(IRunState runState, Player player, RoomType roomType, float baseChance)
    {
        if (Hook.ShouldForcePotionReward(runState, player, roomType))
        {
            return 1f;
        }

        float eliteBonus = roomType == RoomType.Elite ? 0.125f : 0f;
        return Clamp01(baseChance + eliteBonus);
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
}
