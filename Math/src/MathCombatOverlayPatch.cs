using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace MathMod;

[HarmonyPatch]
internal static class MathCombatOverlayPatch
{
    private const string OverlayNodeName = "MathCombatOverlay";

    [HarmonyPatch(typeof(NCombatRoom), "_Ready")]
    [HarmonyPostfix]
    private static void CombatRoomReadyPostfix(NCombatRoom __instance)
    {
        if (__instance.GetNodeOrNull<MathCombatOverlay>(OverlayNodeName) != null)
        {
            return;
        }

        // 数学提示只依赖当前战斗房间，因此直接挂在战斗场景根节点即可，跨房间自然销毁。
        MathCombatOverlay overlay = new()
        {
            Name = OverlayNodeName
        };
        __instance.AddChild(overlay);
        __instance.MoveChild(overlay, __instance.GetChildCount() - 1);
    }
}
