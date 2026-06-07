using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace MathMod;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "Math";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } =
        new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        // 先加载配置，再让各个 Patch 进入运行，避免首帧就读到默认值把玩家配置覆盖掉。
        MathModConfig.Load();

        Harmony harmony = new(ModId);
        harmony.PatchAll();
    }
}
