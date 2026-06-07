using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace MathMod;

[HarmonyPatch]
internal static class MathMapOddsHoverPatch
{
    private const string OddsTipName = "MathMapOddsTip";

    [HarmonyPatch(typeof(NMapPoint), "OnFocus")]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void MapPointFocusPostfix(NMapPoint __instance)
    {
        if (!MathMapOddsPrediction.TryBuildDescription(__instance, out string title, out string description))
        {
            return;
        }

        if (MathHoverTipAppendHelper.TryGetActiveHoverTipSet(__instance, out NHoverTipSet? tipSet) && tipSet != null)
        {
            // 同一个 owner 不能同时挂两个 NHoverTipSet；如果别的 Mod 已经创建了 Tooltip，就把我们的预测块拼进去。
            MathHoverTipAppendHelper.UpsertTextTip(tipSet, OddsTipName, title, description);
            return;
        }

        HoverTip tip = new(GetFallbackTitle(__instance.Point.PointType), description)
        {
            Id = $"{MainFile.ModId}_{__instance.Point.coord.row}_{__instance.Point.coord.col}_map_odds"
        };
        NHoverTipSet.CreateAndShow(__instance, tip, HoverTip.GetHoverTipAlignment(__instance));
    }

    private static LocString GetFallbackTitle(MapPointType pointType)
    {
        return pointType switch
        {
            MapPointType.Monster => new LocString("map", "LEGEND_ENEMY.title"),
            MapPointType.Elite => new LocString("map", "LEGEND_ELITE.title"),
            _ => new LocString("map", "LEGEND_UNKNOWN.title")
        };
    }
}
