using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Nodes;

namespace MathMod;

// 影子推演会真实调用原版出牌/受击流程，这里拦掉震屏、hit stop 和角色动作，避免玩家看到或记录到预测副作用。
[HarmonyPatch]
internal static class MathVisualEffectSuppressionPatch
{
    [HarmonyPatch(typeof(NGame), nameof(NGame.ScreenShake))]
    [HarmonyPrefix]
    private static bool ScreenShakePrefix()
    {
        return !MathPredictionEngine.IsSimulationActive;
    }

    [HarmonyPatch(typeof(NGame), nameof(NGame.ScreenRumble))]
    [HarmonyPrefix]
    private static bool ScreenRumblePrefix()
    {
        return !MathPredictionEngine.IsSimulationActive;
    }

    [HarmonyPatch(typeof(NGame), nameof(NGame.ScreenShakeTrauma))]
    [HarmonyPrefix]
    private static bool ScreenShakeTraumaPrefix()
    {
        return !MathPredictionEngine.IsSimulationActive;
    }

    [HarmonyPatch(typeof(NGame), nameof(NGame.DoHitStop))]
    [HarmonyPrefix]
    private static bool DoHitStopPrefix()
    {
        return !MathPredictionEngine.IsSimulationActive;
    }

    [HarmonyPatch(typeof(CreatureCmd), nameof(CreatureCmd.TriggerAnim))]
    [HarmonyPrefix]
    private static bool TriggerAnimPrefix(ref Task __result, Creature creature, string triggerName, float waitTime)
    {
        if (!MathPredictionEngine.IsSimulationActive)
        {
            return true;
        }

        __result = Task.CompletedTask;
        return false;
    }
}
