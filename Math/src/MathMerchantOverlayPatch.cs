using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;

namespace MathMod;

[HarmonyPatch]
internal static class MathMerchantOverlayPatch
{
    [HarmonyPatch(typeof(NMerchantRoom), "_Ready")]
    [HarmonyPostfix]
    private static void MerchantRoomReadyPostfix(NMerchantRoom __instance)
    {
        if (__instance.GetNodeOrNull<MathMerchantOverlay>(MathMerchantOverlay.OverlayNodeName) != null)
        {
            return;
        }

        // 商店提示只在当前商店房间内生效，直接挂在房间根节点最省心，也能随房间切换自动销毁。
        MathMerchantOverlay overlay = new()
        {
            Name = MathMerchantOverlay.OverlayNodeName
        };
        __instance.AddChild(overlay);
        __instance.MoveChild(overlay, __instance.GetChildCount() - 1);
    }

    [HarmonyPatch(typeof(NClickableControl), "_GuiInput")]
    [HarmonyPostfix]
    private static void MerchantHitboxGuiInputPostfix(NClickableControl __instance, InputEvent inputEvent)
    {
        if (inputEvent is not InputEventMouseButton { ButtonIndex: MouseButton.Middle, Pressed: false })
        {
            return;
        }

        NMerchantSlot? slot = __instance.GetAncestorOfType<NMerchantSlot>();
        if (slot == null)
        {
            return;
        }

        MathMerchantOverlay? overlay = NMerchantRoom.Instance?.GetNodeOrNull<MathMerchantOverlay>(MathMerchantOverlay.OverlayNodeName);
        if (overlay == null)
        {
            return;
        }

        overlay.ToggleSlotSelection(slot);
        __instance.GetViewport().SetInputAsHandled();
    }
}
