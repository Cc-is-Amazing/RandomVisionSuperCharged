using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Random;
using RandomVisionSuperCharged.Services;

namespace RandomVisionSuperCharged.Patches;

internal static class RngRefreshDiagnostics
{
    internal static void LogPatchStatus()
    {
        MainFile.LogInfo("rng-refresh diagnostics start");
        LogTarget("Rng.Counter setter", AccessTools.PropertySetter(typeof(Rng), nameof(Rng.Counter)));
        LogTarget(
            "Rng.NextGaussianInt",
            AccessTools.Method(
                typeof(Rng),
                nameof(Rng.NextGaussianInt),
                new[] { typeof(int), typeof(int), typeof(int), typeof(int) }));
        MainFile.LogInfo("rng-refresh diagnostics done");
    }

    private static void LogTarget(string label, MethodBase? target)
    {
        if (target is null)
        {
            MainFile.LogInfo($"rng-refresh patch target missing: {label}");
            return;
        }

        var owners = Harmony.GetPatchInfo(target)?.Owners;
        var ownerText = owners is null || owners.Count == 0
            ? "<none>"
            : string.Join(", ", owners);
        MainFile.LogInfo($"rng-refresh patch target found: {label} owners={ownerText}");
    }
}

[HarmonyPatch]
internal static class RngCounterRefreshPatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.PropertySetter(typeof(Rng), nameof(Rng.Counter))
            ?? throw new MissingMethodException(typeof(Rng).FullName, "set_Counter");
    }

    private static void Postfix(Rng __instance)
    {
        RandomVisionSuperChargedPredictionRefreshCoordinator.OnRngConsumed(__instance, "Rng.Counter");
    }
}

[HarmonyPatch(
    typeof(Rng),
    nameof(Rng.NextGaussianInt),
    new[] { typeof(int), typeof(int), typeof(int), typeof(int) })]
internal static class RngGaussianIntRefreshPatch
{
    private static void Postfix(Rng __instance)
    {
        RandomVisionSuperChargedPredictionRefreshCoordinator.OnRngConsumed(__instance, nameof(Rng.NextGaussianInt));
    }
}
