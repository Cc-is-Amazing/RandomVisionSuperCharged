using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using RandomVisionSuperCharged.Patches;
using RandomVisionSuperCharged.Services;

namespace RandomVisionSuperCharged;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "RandomVisionSuperCharged";

    public static void Initialize()
    {
        GD.Print($"{ModId}: initializing");
        RandomVisionSuperChargedI18n.Initialize();

        var harmony = new Harmony(ModId);
        harmony.PatchAll(typeof(MainFile).Assembly);
        OrderedDrawPileDiagnostics.LogPatchStatus();
        MapEncounterOverlayDiagnostics.LogPatchStatus();
        RngRefreshDiagnostics.LogPatchStatus();
    }

    public static void LogInfo(string message)
    {
        GD.Print($"{ModId}: {message}");
    }

    public static void LogError(string context, Exception exception)
    {
        GD.PrintErr($"{ModId}: {context}: {exception}");
    }
}
