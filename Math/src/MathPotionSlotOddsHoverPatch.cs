using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Potions;

namespace MathMod;

[HarmonyPatch]
internal static class MathPotionSlotOddsHoverPatch
{
    private const string OddsTipName = "MathPotionSlotOddsTip";

    [HarmonyPatch(typeof(NPotionHolder), "OnFocus")]
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void PotionHolderFocusPostfix(NPotionHolder __instance)
    {
        if (__instance.HasPotion)
        {
            return;
        }

        if (!MathPotionOddsPrediction.TryBuildEmptySlotTip(out string title, out string description))
        {
            return;
        }

        if (MathHoverTipAppendHelper.TryGetActiveHoverTipSet(__instance, out NHoverTipSet? tipSet) && tipSet != null)
        {
            // 原版空药水槽会先显示“药水栏位”的静态说明，这里只追加一块掉率预测，保留原文案。
            MathHoverTipAppendHelper.UpsertTextTip(tipSet, OddsTipName, title, description);
        }
    }
}
