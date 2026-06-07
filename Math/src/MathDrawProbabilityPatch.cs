using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace MathMod;

[HarmonyPatch]
internal static class MathDrawProbabilityPatch
{
    private const string SelectionPanelNodeName = "MathDrawProbabilitySelectionPanel";

    [HarmonyPatch(typeof(NCardPileScreen), "_Ready")]
    [HarmonyPostfix]
    private static void CardPileScreenReadyPostfix(NCardPileScreen __instance)
    {
        if (__instance.Pile.Type != PileType.Draw)
        {
            return;
        }

        if (__instance.GetNodeOrNull<MathDrawProbabilitySelectionPanel>(SelectionPanelNodeName) != null)
        {
            return;
        }

        MathDrawProbabilitySelectionPanel panel = new()
        {
            Name = SelectionPanelNodeName
        };
        __instance.AddChild(panel);
        __instance.MoveChild(panel, __instance.GetChildCount() - 1);
    }

    [HarmonyPatch(typeof(NCardHolder), "CreateHoverTips")]
    [HarmonyPostfix]
    private static void CreateHoverTipsPostfix(NCardHolder __instance)
    {
        MathDrawProbabilityTooltipController.ShowFor(__instance);
    }

    [HarmonyPatch(typeof(NCardHolder), "ClearHoverTips")]
    [HarmonyPostfix]
    private static void ClearHoverTipsPostfix(NCardHolder __instance)
    {
        MathDrawProbabilityTooltipController.HideFor(__instance);
    }
}
