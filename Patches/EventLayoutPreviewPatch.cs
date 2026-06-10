using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Events;
using RandomVisionSuperCharged.Services;

namespace RandomVisionSuperCharged.Patches;

[HarmonyPatch(typeof(NEventLayout))]
internal static class EventLayoutPreviewPatch
{
    private static readonly AccessTools.FieldRef<NEventLayout, EventModel> EventRef =
        AccessTools.FieldRefAccess<NEventLayout, EventModel>("_event");

    [HarmonyPostfix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(nameof(NEventLayout.ClearOptions))]
    private static void AfterClearOptions(NEventLayout __instance)
    {
        RandomVisionSuperChargedEventOverlay.RefreshOrRemoveDeferred(__instance, EventRef(__instance), "clear-options");
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(nameof(NEventLayout.AddOptions))]
    private static void AfterAddOptions(NEventLayout __instance)
    {
        try
        {
            RandomVisionSuperChargedEventOverlay.AttachOrRefresh(__instance, EventRef(__instance));
        }
        catch (Exception ex)
        {
            MainFile.LogError("Failed to refresh event preview overlay", ex);
        }
    }
}
