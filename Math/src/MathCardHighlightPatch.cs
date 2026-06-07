using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;

namespace MathMod;

[HarmonyPatch(typeof(NHandCardHolder), nameof(NHandCardHolder.UpdateCard))]
internal static class MathCardHighlightPatch
{
    [HarmonyPostfix]
    private static void UpdateCardPostfix(NHandCardHolder __instance)
    {
        if (__instance.CardNode?.Model == null)
        {
            return;
        }

        if (!MathCardHighlightState.TryGetColor(__instance.CardNode.Model, out Color color))
        {
            return;
        }

        // 这里借原版的高亮节点与动画，只在原版刷新完后覆写颜色，避免互相抢状态。
        __instance.CardNode.CardHighlight.Modulate = color;
        __instance.CardNode.CardHighlight.AnimShow();
    }
}
