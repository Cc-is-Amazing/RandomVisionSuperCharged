using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;

namespace RandomVision.Patches;

internal static class OrderedDrawPileDiagnostics
{
    internal static void LogPatchStatus()
    {
        MainFile.LogInfo("ordered-draw diagnostics start");
        LogTarget(
            "NCardPileScreen.OnPileContentsChanged",
            AccessTools.Method(typeof(NCardPileScreen), "OnPileContentsChanged"));
        LogTarget(
            "NCardPileScreen._Ready",
            AccessTools.Method(typeof(NCardPileScreen), nameof(NCardPileScreen._Ready)));
        LogTarget(
            "NCardPileScreen.ShowScreen",
            AccessTools.Method(typeof(NCardPileScreen), nameof(NCardPileScreen.ShowScreen)));
        LogTarget(
            "NCardGrid.SetCards",
            AccessTools.Method(
                typeof(NCardGrid),
                nameof(NCardGrid.SetCards),
                new[]
                {
                    typeof(IReadOnlyList<CardModel>),
                    typeof(PileType),
                    typeof(List<SortingOrders>),
                    typeof(Task)
                }));
        MainFile.LogInfo("ordered-draw diagnostics done");
    }

    private static void LogTarget(string label, MethodBase? target)
    {
        if (target is null)
        {
            MainFile.LogInfo($"ordered-draw patch target missing: {label}");
            return;
        }

        var owners = Harmony.GetPatchInfo(target)?.Owners;
        var ownerText = owners is null || owners.Count == 0
            ? "<none>"
            : string.Join(", ", owners);
        MainFile.LogInfo($"ordered-draw patch target found: {label} owners={ownerText}");
    }
}

[HarmonyPatch(typeof(NCardPileScreen), "OnPileContentsChanged")]
internal static class OrderedDrawPilePatch
{
    private static readonly AccessTools.FieldRef<NCardPileScreen, NCardGrid> GridRef =
        AccessTools.FieldRefAccess<NCardPileScreen, NCardGrid>("_grid");

    [HarmonyPrefix]
    private static bool Prefix(NCardPileScreen __instance)
    {
        MainFile.LogInfo($"ordered-draw OnPileContentsChanged prefix pile={__instance.Pile.Type} count={__instance.Pile.Cards.Count}");
        if (__instance.Pile.Type != PileType.Draw)
        {
            MainFile.LogInfo("ordered-draw OnPileContentsChanged skipped: pile is not Draw");
            return true;
        }

        try
        {
            ApplyOrderedDrawPile(__instance);
            return false;
        }
        catch (Exception exception)
        {
            MainFile.LogError("Render ordered draw pile", exception);
            return true;
        }
    }

    internal static void ApplyOrderedDrawPile(NCardPileScreen screen)
    {
        MainFile.LogInfo($"ordered-draw apply-start pile={screen.Pile.Type} count={screen.Pile.Cards.Count}");
        var grid = GridRef(screen);
        if (grid is null)
        {
            MainFile.LogInfo("Ordered draw pile skipped because grid was null.");
            return;
        }

        var cards = screen.Pile.Cards.ToList();
        var sortingPriority = new List<SortingOrders>(1) { SortingOrders.Ascending };
        grid.SetCards(cards, PileType.Draw, sortingPriority);
        MainFile.LogInfo($"ordered-draw apply-done count={cards.Count} next={(cards.FirstOrDefault()?.Id.Entry ?? "none")}");
    }
}

[HarmonyPatch(
    typeof(NCardGrid),
    nameof(NCardGrid.SetCards),
    new[]
    {
        typeof(IReadOnlyList<CardModel>),
        typeof(PileType),
        typeof(List<SortingOrders>),
        typeof(Task)
    })]
internal static class OrderedDrawPileGridPatch
{
    [HarmonyPriority(Priority.Last)]
    [HarmonyPrefix]
    private static void Prefix(
        ref IReadOnlyList<CardModel> cardsToDisplay,
        PileType pileType,
        ref List<SortingOrders> sortingPriority)
    {
        MainFile.LogInfo($"ordered-draw grid-prefix pile={pileType} count={cardsToDisplay.Count}");
        if (pileType != PileType.Draw)
        {
            MainFile.LogInfo("ordered-draw grid-prefix skipped: pile is not Draw");
            return;
        }

        if (NCapstoneContainer.Instance?.CurrentCapstoneScreen is not NCardPileScreen pileScreen)
        {
            MainFile.LogInfo("ordered-draw grid-prefix skipped: current capstone screen is not NCardPileScreen");
            return;
        }

        if (pileScreen.Pile.Type != PileType.Draw)
        {
            MainFile.LogInfo($"ordered-draw grid-prefix skipped: screen pile is {pileScreen.Pile.Type}");
            return;
        }

        cardsToDisplay = pileScreen.Pile.Cards.ToList();
        sortingPriority = new List<SortingOrders>(1) { SortingOrders.Ascending };
        MainFile.LogInfo($"ordered-draw grid-prefix applied count={cardsToDisplay.Count} next={(cardsToDisplay.FirstOrDefault()?.Id.Entry ?? "none")}");
    }
}

[HarmonyPatch(typeof(NCardPileScreen), nameof(NCardPileScreen._Ready))]
internal static class OrderedDrawPileReadyPatch
{
    [HarmonyPostfix]
    private static void Postfix(NCardPileScreen __instance)
    {
        MainFile.LogInfo($"ordered-draw _Ready postfix pile={__instance.Pile.Type} count={__instance.Pile.Cards.Count}");
        if (__instance.Pile.Type != PileType.Draw)
        {
            MainFile.LogInfo("ordered-draw _Ready skipped: pile is not Draw");
            return;
        }

        try
        {
            OrderedDrawPilePatch.ApplyOrderedDrawPile(__instance);
        }
        catch (Exception exception)
        {
            MainFile.LogError("Apply ordered draw pile after _Ready", exception);
        }
    }
}

[HarmonyPatch(typeof(NCardPileScreen), nameof(NCardPileScreen.ShowScreen))]
internal static class OrderedDrawPileShowScreenPatch
{
    [HarmonyPostfix]
    private static void Postfix(CardPile pile, NCardPileScreen __result)
    {
        MainFile.LogInfo($"ordered-draw ShowScreen postfix pile={pile.Type} result-null={__result is null} count={pile.Cards.Count}");
        if (pile.Type != PileType.Draw || __result is null)
        {
            MainFile.LogInfo("ordered-draw ShowScreen skipped: pile is not Draw or result is null");
            return;
        }

        try
        {
            OrderedDrawPilePatch.ApplyOrderedDrawPile(__result);
        }
        catch (Exception exception)
        {
            MainFile.LogError("Apply ordered draw pile after screen open", exception);
        }
    }
}
