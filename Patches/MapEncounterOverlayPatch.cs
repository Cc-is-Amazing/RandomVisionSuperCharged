using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using RandomVisionSuperCharged.Services;

namespace RandomVisionSuperCharged.Patches;

internal static class MapEncounterOverlayDiagnostics
{
    internal static void LogPatchStatus()
    {
        MainFile.LogInfo("map-encounter diagnostics start");
        LogTarget("NMapScreen.SetMap", AccessTools.Method(typeof(NMapScreen), nameof(NMapScreen.SetMap)));
        LogTarget("NMapScreen.Open", AccessTools.Method(typeof(NMapScreen), nameof(NMapScreen.Open)));
        LogTarget("NMapScreen.Close", AccessTools.Method(typeof(NMapScreen), nameof(NMapScreen.Close)));
        MainFile.LogInfo("map-encounter diagnostics done");
    }

    private static void LogTarget(string label, MethodBase? target)
    {
        if (target is null)
        {
            MainFile.LogInfo($"map-encounter patch target missing: {label}");
            return;
        }

        var owners = Harmony.GetPatchInfo(target)?.Owners;
        var ownerText = owners is null || owners.Count == 0
            ? "<none>"
            : string.Join(", ", owners);
        MainFile.LogInfo($"map-encounter patch target found: {label} owners={ownerText}");
    }
}

[HarmonyPatch(typeof(NMapScreen))]
internal static class MapEncounterOverlayPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(NMapScreen.SetMap))]
    private static void AfterSetMap(NMapScreen __instance)
    {
        MainFile.LogInfo($"map-encounter SetMap postfix is-open={__instance.IsOpen}");
        ScheduleRefresh(__instance, "SetMap");
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(NMapScreen.Open))]
    private static void AfterOpen(NMapScreen __instance)
    {
        MainFile.LogInfo($"map-encounter Open postfix is-open={__instance.IsOpen}");
        ScheduleRefresh(__instance, "Open");
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(NMapScreen.Close))]
    private static void AfterClose(NMapScreen __instance)
    {
        MainFile.LogInfo($"map-encounter Close postfix is-open={__instance.IsOpen}");
        RandomVisionSuperChargedMapEncounterOverlay.Remove(__instance);
    }

    private static void ScheduleRefresh(NMapScreen screen, string source)
    {
        try
        {
            Callable.From(() => RefreshDeferred(screen, source)).CallDeferred();
            MainFile.LogInfo($"map-encounter scheduled refresh source={source}");
        }
        catch (Exception exception)
        {
            MainFile.LogError($"Failed to schedule map encounter overlay source={source}", exception);
        }
    }

    private static void RefreshDeferred(NMapScreen screen, string source)
    {
        if (!GodotObject.IsInstanceValid(screen))
        {
            MainFile.LogInfo($"map-encounter refresh skipped source={source}: screen invalid");
            return;
        }

        MainFile.LogInfo($"map-encounter refresh source={source} is-open={screen.IsOpen} visible={screen.Visible}");
        RandomVisionSuperChargedMapEncounterOverlay.AttachOrRefresh(screen);
    }
}
