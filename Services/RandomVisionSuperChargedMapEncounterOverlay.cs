using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace RandomVisionSuperCharged.Services;

internal static partial class RandomVisionSuperChargedMapEncounterOverlay
{
    private const string OverlayLayerName = "RandomVisionSuperChargedMapEncounterLayer";
    private const string OverlayRootName = "RandomVisionSuperChargedMapEncounterRoot";
    private const string OverlayName = "RandomVisionSuperChargedMapEncounterOverlay";
    private const string MarginName = "Margin";
    private const string RootName = "Root";
    private const string TitleBarName = "TitleBar";
    private const string CollapseButtonName = "CollapseButton";
    private const string ContentName = "Content";
    private const float DefaultTopOffset = 40f;
    private const float DefaultRightOffset = 24f;
    private const float ViewportPadding = 12f;
    private const float PanelWidth = 680f;
    private const float PanelHeight = 360f;
    private const float CollapsedHeight = 42f;
    private const int MaxNormalRows = 10;
    private const int MaxEliteRows = 5;
    private const int MaxEventRows = 30;
    private static Vector2? _lastPanelPosition;
    private static NMapScreen? _activeScreen;

    private static readonly AccessTools.FieldRef<ActModel, RoomSet> RoomsRef =
        AccessTools.FieldRefAccess<ActModel, RoomSet>("_rooms");

    private static readonly AccessTools.FieldRef<AncientEventModel, List<EventOption>> AncientGeneratedOptionsRef =
        AccessTools.FieldRefAccess<AncientEventModel, List<EventOption>>("_generatedOptions");

    private static readonly AccessTools.FieldRef<EventModel, Player> EventOwnerRef =
        AccessTools.FieldRefAccess<EventModel, Player>("<Owner>k__BackingField");

    private static readonly AccessTools.FieldRef<EventModel, Rng> EventRngRef =
        AccessTools.FieldRefAccess<EventModel, Rng>("<Rng>k__BackingField");

    private static readonly System.Reflection.MethodInfo GenerateInitialOptionsWrapperMethod =
        AccessTools.Method(typeof(AncientEventModel), "GenerateInitialOptionsWrapper");

    public static void AttachOrRefresh(NMapScreen screen)
    {
        if (!GodotObject.IsInstanceValid(screen))
        {
            MainFile.LogInfo("map-encounter overlay skipped: screen invalid");
            return;
        }

        if (!screen.IsOpen)
        {
            MainFile.LogInfo("map-encounter overlay skipped: map screen is not open");
            Remove(screen);
            return;
        }

        IRunState? runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
        {
            MainFile.LogInfo("map-encounter overlay skipped: run state missing");
            Remove(screen);
            return;
        }

        MainFile.LogInfo($"map-encounter overlay attach-start act={runState.CurrentActIndex} floor={runState.ActFloor}");
        MapEncounterPreview preview;
        using (RandomVisionSuperChargedPredictionRefreshCoordinator.SuppressRngRefresh())
        {
            preview = BuildPreview(runState);
        }

        var host = EnsureOverlayHost(screen);
        var panel = host.GetNodeOrNull<RandomVisionSuperChargedOverlayPanel>(OverlayName) ?? CreatePanel(host);
        RefreshPanel(panel, preview);
        _activeScreen = screen;
        MainFile.LogInfo($"map-encounter overlay attach-done normal={preview.NormalEncounters.Count} elite={preview.EliteEncounters.Count} events={preview.Events.Count} ancients={preview.Ancients.Count}");
    }

    public static void Remove(NMapScreen screen)
    {
        if (!GodotObject.IsInstanceValid(screen))
        {
            return;
        }

        if (_activeScreen == screen)
        {
            _activeScreen = null;
        }

        screen.GetNodeOrNull<CanvasLayer>(OverlayLayerName)?.QueueFree();
        MainFile.LogInfo("map-encounter overlay remove");
    }

    public static bool HasActivePreview()
    {
        return _activeScreen is not null &&
            GodotObject.IsInstanceValid(_activeScreen) &&
            _activeScreen.IsOpen &&
            _activeScreen.GetNodeOrNull<CanvasLayer>(OverlayLayerName)?
                .GetNodeOrNull<Control>(OverlayRootName)?
                .GetNodeOrNull<RandomVisionSuperChargedOverlayPanel>(OverlayName) is not null;
    }

    public static bool RefreshActiveFromRngChange()
    {
        if (!HasActivePreview() || _activeScreen is null)
        {
            return false;
        }

        MainFile.LogInfo("map-encounter overlay rng-refresh");
        AttachOrRefresh(_activeScreen);
        return true;
    }

    private static MapEncounterPreview BuildPreview(IRunState runState)
    {
        RoomSet rooms = RoomsRef(runState.Act);
        MainFile.LogInfo(
            $"map-encounter roomset normal-count={rooms.normalEncounters.Count} normal-visited={rooms.normalEncountersVisited} elite-count={rooms.eliteEncounters.Count} elite-visited={rooms.eliteEncountersVisited} event-count={rooms.events.Count} event-visited={rooms.eventsVisited}");

        var normal = rooms.normalEncounters
            .Skip(Math.Clamp(rooms.normalEncountersVisited, 0, rooms.normalEncounters.Count))
            .Take(MaxNormalRows)
            .Select((encounter, index) => BuildEncounterPreview(runState, encounter, index + 1, new LocString("map", "LEGEND_ENEMY.title")))
            .ToList();

        var elite = rooms.eliteEncounters
            .Skip(Math.Clamp(rooms.eliteEncountersVisited, 0, rooms.eliteEncounters.Count))
            .Take(MaxEliteRows)
            .Select((encounter, index) => BuildEncounterPreview(runState, encounter, index + 1, new LocString("map", "LEGEND_ELITE.title")))
            .ToList();

        var events = rooms.events
            .Skip(Math.Clamp(rooms.eventsVisited, 0, rooms.events.Count))
            .Take(MaxEventRows)
            .Select((eventModel, index) => BuildEventPreview(runState, eventModel, index + 1))
            .ToList();

        var ancients = BuildAncientPreviews(runState);

        MainFile.LogInfo($"map-encounter preview-built normal={normal.Count} elite={elite.Count} events={events.Count} ancients={ancients.Count}");
        return new MapEncounterPreview(normal, elite, events, ancients);
    }

    private static MapPreviewRow BuildEncounterPreview(IRunState runState, EncounterModel encounter, int order, LocString hoverTitle)
    {
        var title = RandomVisionSuperChargedGameText.ResolveLocString(encounter.Title);
        var monsters = GetPreviewMonsters(runState, encounter)
            .GroupBy(static monster => monster.Id.Entry)
            .Select(static group => group.First())
            .Select(monster => RandomVisionSuperChargedGameText.ResolveLocString(monster.Title))
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        var details = monsters.Count == 0
            ? RandomVisionSuperChargedI18n.Pick($"Encounter: {encounter.Id.Entry}", $"遭遇：{encounter.Id.Entry}")
            : RandomVisionSuperChargedI18n.Pick(
                $"Encounter: {encounter.Id.Entry}\nMonsters: {string.Join(" / ", monsters)}",
                $"遭遇：{encounter.Id.Entry}\n敌人：{string.Join(" / ", monsters)}");

        MainFile.LogInfo($"map-encounter row type=encounter order={order} id={encounter.Id.Entry} title=\"{title}\" details=\"{details.Replace('\n', '|')}\"");
        return new MapPreviewRow(order, title, hoverTitle, details, false);
    }

    private static MapPreviewRow BuildEventPreview(IRunState runState, EventModel eventModel, int order)
    {
        var title = RandomVisionSuperChargedGameText.ResolveLocString(eventModel.Title, eventModel.DynamicVars);
        if (string.IsNullOrWhiteSpace(title))
        {
            title = eventModel.Id.Entry;
        }

        var isAllowed = TryEvaluateEventAllowed(runState, eventModel, out var liveAllowedLine);
        var requirements = DescribeEventRequirements(eventModel, liveAllowedLine);
        var details = RandomVisionSuperChargedI18n.Pick(
            $"Event: {eventModel.Id.Entry}\nTitle: {title}\nRequirements:\n- {string.Join("\n- ", requirements)}",
            $"事件：{eventModel.Id.Entry}\n标题：{title}\n出现条件：\n- {string.Join("\n- ", requirements)}");
        MainFile.LogInfo($"map-encounter row type=event order={order} id={eventModel.Id.Entry} title=\"{title}\" allowed={isAllowed?.ToString() ?? "<unknown>"} requirements=\"{string.Join(" | ", requirements)}\"");
        return new MapPreviewRow(order, title, new LocString("map", "LEGEND_UNKNOWN.title"), details, isAllowed == true);
    }

    private static IReadOnlyList<MapPreviewRow> BuildAncientPreviews(IRunState runState)
    {
        var rows = new List<MapPreviewRow>();

        for (var actIndex = 0; actIndex < runState.Acts.Count; actIndex++)
        {
            var act = runState.Acts[actIndex];
            RoomSet rooms;
            try
            {
                rooms = RoomsRef(act);
            }
            catch (Exception exception)
            {
                MainFile.LogError($"Failed to read act room set for ancient preview act={actIndex + 1}", exception);
                continue;
            }

            if (!rooms.HasAncient || rooms.Ancient is null)
            {
                MainFile.LogInfo($"map-encounter ancient act={actIndex + 1} skipped: no ancient");
                continue;
            }

            try
            {
                if (actIndex == 0)
                {
                    MainFile.LogInfo($"map-encounter ancient act={actIndex + 1} skipped: initial ancient is handled by event overlay");
                    continue;
                }

                rows.Add(BuildAncientPreview(runState, act, rooms.Ancient, actIndex + 1));
            }
            catch (Exception exception)
            {
                MainFile.LogError($"Failed to build ancient row act={actIndex + 1} id={rooms.Ancient.Id.Entry}", exception);
                rows.Add(BuildAncientErrorPreview(act, rooms.Ancient, actIndex + 1));
            }
        }

        return rows;
    }

    private static MapPreviewRow BuildAncientErrorPreview(ActModel act, AncientEventModel ancient, int actNumber)
    {
        var actTitle = RandomVisionSuperChargedGameText.ResolveLocString(act.Title);
        var ancientTitle = RandomVisionSuperChargedGameText.ResolveLocString(ancient.Title);
        if (string.IsNullOrWhiteSpace(ancientTitle))
        {
            ancientTitle = ancient.Id.Entry;
        }

        var rowTitle = RandomVisionSuperChargedI18n.Pick(
            $"Act {actNumber}: {ancientTitle}",
            $"第 {actNumber} 幕：{ancientTitle}");
        var details = RandomVisionSuperChargedI18n.Pick(
            $"Act: {(string.IsNullOrWhiteSpace(actTitle) ? act.Id.Entry : actTitle)} [{act.Id.Entry}]\nAncient: {ancientTitle} [{ancient.Id.Entry}]\nOptions: failed to read option titles; see RandomVisionSuperCharged log.",
            $"幕：{(string.IsNullOrWhiteSpace(actTitle) ? act.Id.Entry : actTitle)} [{act.Id.Entry}]\n远古：{ancientTitle} [{ancient.Id.Entry}]\n选项：读取选项标题失败，请查看 RandomVisionSuperCharged 日志。");
        return new MapPreviewRow(actNumber, rowTitle, ancient.Title, details, false,
            RandomVisionSuperChargedI18n.Pick("Options unavailable", "选项不可用"));
    }

    private static MapPreviewRow BuildAncientPreview(
        IRunState runState,
        ActModel act,
        AncientEventModel ancient,
        int actNumber)
    {
        var actTitle = RandomVisionSuperChargedGameText.ResolveLocString(act.Title);
        var ancientTitle = RandomVisionSuperChargedGameText.ResolveLocString(ancient.Title);
        if (string.IsNullOrWhiteSpace(ancientTitle))
        {
            ancientTitle = ancient.Id.Entry;
        }

        var rowTitle = RandomVisionSuperChargedI18n.Pick(
            $"Act {actNumber}: {ancientTitle}",
            $"第 {actNumber} 幕：{ancientTitle}");
        var detailLines = new List<string>
        {
            RandomVisionSuperChargedI18n.Pick(
                $"Act: {(string.IsNullOrWhiteSpace(actTitle) ? act.Id.Entry : actTitle)} [{act.Id.Entry}]",
                $"幕：{(string.IsNullOrWhiteSpace(actTitle) ? act.Id.Entry : actTitle)} [{act.Id.Entry}]"),
            RandomVisionSuperChargedI18n.Pick(
                $"Ancient: {ancientTitle} [{ancient.Id.Entry}]",
                $"远古：{ancientTitle} [{ancient.Id.Entry}]")
        };

        var optionTitles = PredictAncientOptionTitles(runState, ancient, actNumber);
        if (optionTitles.Count == 0)
        {
            detailLines.Add(RandomVisionSuperChargedI18n.Pick(
                "Options: prediction unavailable; see RandomVisionSuperCharged log.",
                "选项：预测不可用，请查看 RandomVisionSuperCharged 日志。"));
        }
        else
        {
            detailLines.Add(RandomVisionSuperChargedI18n.Pick("Options:", "选项："));
            for (var optionIndex = 0; optionIndex < optionTitles.Count; optionIndex++)
            {
                detailLines.Add($"{optionIndex + 1}. {optionTitles[optionIndex]}");
            }
        }

        var subtitle = optionTitles.Count == 0
            ? RandomVisionSuperChargedI18n.Pick("Options unavailable", "选项不可用")
            : string.Join('\n', optionTitles.Select((title, index) => $"{index + 1}. {title}"));
        MainFile.LogInfo($"map-encounter ancient act={actNumber} id={ancient.Id.Entry} title=\"{ancientTitle}\" options={optionTitles.Count} option-titles=\"{subtitle}\"");
        return new MapPreviewRow(actNumber, rowTitle, ancient.Title, string.Join('\n', detailLines), false, subtitle);
    }

    private static IReadOnlyList<string> PredictAncientOptionTitles(IRunState runState, AncientEventModel ancient, int actNumber)
    {
        try
        {
            var player = runState.Players.FirstOrDefault();
            if (player is null)
            {
                MainFile.LogInfo($"map-encounter ancient act={actNumber} id={ancient.Id.Entry} prediction skipped: no player");
                return Array.Empty<string>();
            }

            var previewAncient = ancient.ToMutable() as AncientEventModel;
            if (previewAncient is null)
            {
                MainFile.LogInfo($"map-encounter ancient act={actNumber} id={ancient.Id.Entry} prediction skipped: clone failed");
                return Array.Empty<string>();
            }

            EventOwnerRef(previewAncient) = player;
            EventRngRef(previewAncient) = CreateAncientPreviewRng(runState, player, previewAncient);
            previewAncient.CalculateVars();
            MainFile.LogInfo(
                $"map-encounter ancient act={actNumber} id={previewAncient.Id.Entry} predict-start rng-seed={previewAncient.Rng.Seed} rng-counter={previewAncient.Rng.Counter}");

            var generatedByWrapper = GenerateInitialOptionsWrapperMethod.Invoke(previewAncient, Array.Empty<object>()) as IReadOnlyList<EventOption>;
            var generatedOptions = AncientGeneratedOptionsRef(previewAncient);
            var options = generatedOptions is { Count: > 0 }
                ? generatedOptions
                : generatedByWrapper?.ToList() ?? new List<EventOption>();
            var titles = options
                .Select(option => ResolveAncientOptionTitle(previewAncient, option))
                .Where(static title => !string.IsNullOrWhiteSpace(title))
                .ToList();

            MainFile.LogInfo(
                $"map-encounter ancient act={actNumber} id={previewAncient.Id.Entry} predict-done options={titles.Count} option-titles=\"{string.Join(" / ", titles)}\"");
            return titles;
        }
        catch (Exception exception)
        {
            MainFile.LogError($"Failed to predict ancient options act={actNumber} id={ancient.Id.Entry}", exception);
            return ReadGeneratedAncientOptionTitles(ancient);
        }
    }

    private static Rng CreateAncientPreviewRng(IRunState runState, Player player, EventModel eventModel)
    {
        var eventHash = (ulong)StringHelper.GetDeterministicHashCode(eventModel.Id.Entry);
        var seed = (uint)(runState.Rng.Seed + player.NetId + eventHash);
        return new Rng(seed, 0);
    }

    private static IReadOnlyList<string> ReadGeneratedAncientOptionTitles(AncientEventModel ancient)
    {
        List<EventOption>? generatedOptions = null;
        try
        {
            generatedOptions = AncientGeneratedOptionsRef(ancient);
        }
        catch (Exception exception)
        {
            MainFile.LogError($"Failed to read generated ancient option list for {ancient.Id.Entry}", exception);
        }

        if (generatedOptions is { Count: > 0 })
        {
            return generatedOptions
                .Select(option => ResolveAncientOptionTitle(ancient, option))
                .Where(static title => !string.IsNullOrWhiteSpace(title))
                .ToList();
        }

        return Array.Empty<string>();
    }

    private static string ResolveAncientOptionTitle(AncientEventModel ancient, EventOption option)
    {
        try
        {
            var title = RandomVisionSuperChargedGameText.ResolveLocString(option.Title, ancient.DynamicVars);
            if (!string.IsNullOrWhiteSpace(title))
            {
                return option.IsLocked
                    ? RandomVisionSuperChargedI18n.Pick($"{title} [LOCKED]", $"{title} [锁定]")
                    : title;
            }
        }
        catch (Exception exception)
        {
            MainFile.LogError($"Failed to resolve ancient option title event={ancient.Id.Entry} key={option.TextKey}", exception);
        }

        return string.IsNullOrWhiteSpace(option.TextKey) ? "<option>" : option.TextKey;
    }

    private static IReadOnlyList<string> DescribeEventRequirements(EventModel eventModel, string liveAllowedLine)
    {
        var lines = new List<string>
        {
            RandomVisionSuperChargedI18n.Pick(
                "Map node must be an Event room, or an Unknown room that resolves to Event. Ancient map nodes use the Ancient event pool.",
                "地图节点必须是事件房，或解析为事件的未知房。远古节点使用远古事件池。")
        };

        lines.AddRange(eventModel.GetType().Name switch
        {
            "Amalgamator" => new[]
            {
                RandomVisionSuperChargedI18n.Pick(
                    "Every player needs at least 2 removable non-Basic Strike-tag cards and 2 removable non-Basic Defend-tag cards.",
                    "每名玩家都需要至少 2 张可移除的非基础 Strike 标签牌，以及 2 张可移除的非基础 Defend 标签牌。")
            },
            "BrainLeech" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Only before Act 3.", "仅在第 3 幕之前出现。")
            },
            "ByrdonisNest" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Every player must have no event pet.", "每名玩家都不能已有事件宠物。")
            },
            "ColorfulPhilosophers" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Every player must have more than one character card pool unlocked.", "每名玩家都必须已解锁超过 1 个角色牌池。")
            },
            "ColossalFlower" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Every player must have at least 19 current HP.", "每名玩家当前生命必须至少为 19。")
            },
            "CrystalSphere" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Requires Act 2 or later.", "需要第 2 幕或更晚。"),
                RandomVisionSuperChargedI18n.Pick("Every player must have at least 100 Gold.", "每名玩家必须至少有 100 金币。")
            },
            "DenseVegetation" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Single-player runs require enough current HP to pay this event's HP-loss value.", "单人游戏需要当前生命高于该事件的失血数值。")
            },
            "DollRoom" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Only in Act 2.", "仅在第 2 幕出现。")
            },
            "EndlessConveyor" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Every player must have at least 120 Gold.", "每名玩家必须至少有 120 金币。")
            },
            "FakeMerchant" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Requires Act 2 or later.", "需要第 2 幕或更晚。"),
                RandomVisionSuperChargedI18n.Pick("Single-player only.", "仅限单人游戏。"),
                RandomVisionSuperChargedI18n.Pick("Player must have at least 100 Gold or at least one potion.", "玩家必须至少有 100 金币，或至少拥有 1 瓶药水。")
            },
            "FieldOfManSizedHoles" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Every player must have at least one deck card that can receive the event enchantment.", "每名玩家牌组中都必须至少有 1 张可被该事件附魔的牌。")
            },
            "GraveOfTheForgotten" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Every player must have at least one deck card that can be enchanted.", "每名玩家牌组中都必须至少有 1 张可附魔的牌。")
            },
            "LuminousChoir" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Every player must have enough Gold for this event's Gold cost.", "每名玩家必须有足够金币支付该事件的金币费用。"),
                RandomVisionSuperChargedI18n.Pick("Relic reward pool must still contain available relics.", "遗物奖励池中必须仍有可获得遗物。")
            },
            "MorphicGrove" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Every player must have at least 100 Gold.", "每名玩家必须至少有 100 金币。"),
                RandomVisionSuperChargedI18n.Pick("Every player must have at least 2 transformable deck cards.", "每名玩家牌组中必须至少有 2 张可变化的牌。")
            },
            "PotionCourier" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Requires Act 2 or later.", "需要第 2 幕或更晚。")
            },
            "PunchOff" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Requires total floor 6 or later.", "需要总楼层至少为 6。")
            },
            "RanwidTheElder" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Requires Act 2 or later.", "需要第 2 幕或更晚。"),
                RandomVisionSuperChargedI18n.Pick("Every player must have at least one tradable relic.", "每名玩家必须至少有 1 个可交易遗物。"),
                RandomVisionSuperChargedI18n.Pick("Every player must have at least 100 Gold.", "每名玩家必须至少有 100 金币。"),
                RandomVisionSuperChargedI18n.Pick("Every player must have at least one potion.", "每名玩家必须至少有 1 瓶药水。")
            },
            "RelicTrader" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Requires Act 2 or later.", "需要第 2 幕或更晚。"),
                RandomVisionSuperChargedI18n.Pick("Every player must have at least 5 tradable relics.", "每名玩家必须至少有 5 个可交易遗物。")
            },
            "RoomFullOfCheese" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Only before Act 3.", "仅在第 3 幕之前出现。")
            },
            "RoundTeaParty" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Every player must have at least 12 current HP.", "每名玩家当前生命必须至少为 12。")
            },
            "SlipperyBridge" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Requires total floor after 6.", "需要总楼层大于 6。"),
                RandomVisionSuperChargedI18n.Pick("Every player must have at least one removable deck card.", "每名玩家牌组中必须至少有 1 张可移除的牌。")
            },
            "SpiralingWhirlpool" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Every player must have at least one deck card that can receive the event enchantment.", "每名玩家牌组中都必须至少有 1 张可被该事件附魔的牌。")
            },
            "StoneOfAllTime" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Only in Act 2.", "仅在第 2 幕出现。"),
                RandomVisionSuperChargedI18n.Pick("Every player must have at least one potion.", "每名玩家必须至少有 1 瓶药水。")
            },
            "Symbiote" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Requires Act 2 or later.", "需要第 2 幕或更晚。")
            },
            "TeaMaster" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Only before Act 3.", "仅在第 3 幕之前出现。"),
                RandomVisionSuperChargedI18n.Pick("Every player must have at least 150 Gold.", "每名玩家必须至少有 150 金币。")
            },
            "TheFutureOfPotions" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Every player must have at least 2 potions.", "每名玩家必须至少有 2 瓶药水。")
            },
            "TheLegendsWereTrue" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Only in Act 1.", "仅在第 1 幕出现。"),
                RandomVisionSuperChargedI18n.Pick("Every player must have at least one card in deck.", "每名玩家牌组中必须至少有 1 张牌。"),
                RandomVisionSuperChargedI18n.Pick("Every player must have at least 10 current HP.", "每名玩家当前生命必须至少为 10。")
            },
            "TrashHeap" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Every player must have at least 5 current HP.", "每名玩家当前生命必须至少为 5。")
            },
            "UnrestSite" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Every player must be at or below 70% current HP.", "每名玩家当前生命必须不高于最大生命的 70%。")
            },
            "WarHistorianRepy" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Base game IsAllowed currently returns false; it should not naturally enter the event pool unless forced by another rule.", "基础游戏的 IsAllowed 当前恒为 false；除非被其他规则强制加入，否则不会自然进入事件池。")
            },
            "WaterloggedScriptorium" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Every player must have at least 55 Gold.", "每名玩家必须至少有 55 金币。")
            },
            "WelcomeToWongos" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Only in Act 2.", "仅在第 2 幕出现。"),
                RandomVisionSuperChargedI18n.Pick("Every player must have at least 100 Gold.", "每名玩家必须至少有 100 金币。")
            },
            "WhisperingHollow" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Every player must have at least 44 Gold.", "每名玩家必须至少有 44 金币。")
            },
            "WoodCarvings" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Every player must have at least one removable non-Basic deck card.", "每名玩家牌组中必须至少有 1 张可移除的非基础牌。")
            },
            "ZenWeaver" => new[]
            {
                RandomVisionSuperChargedI18n.Pick("Every player must have enough Gold for this event's Emotional Awareness cost.", "每名玩家必须有足够金币支付该事件的情绪觉察费用。")
            },
            _ => new[]
            {
                RandomVisionSuperChargedI18n.Pick(
                    "No extra IsAllowed requirement found in the event class; normal act event-pool filtering still applies.",
                    "未在该事件类中发现额外 IsAllowed 条件；仍会受到普通幕事件池筛选影响。")
            }
        });

        lines.Add(liveAllowedLine);
        return lines;
    }

    private static bool? TryEvaluateEventAllowed(IRunState runState, EventModel eventModel, out string liveAllowedLine)
    {
        try
        {
            var allowed = eventModel.IsAllowed(runState);
            liveAllowedLine = RandomVisionSuperChargedI18n.Pick(
                $"Current IsAllowed check: {(allowed ? "passes" : "fails")}.",
                $"当前 IsAllowed 检查：{(allowed ? "通过" : "不通过")}。");
            return allowed;
        }
        catch (Exception exception)
        {
            MainFile.LogError($"Failed to evaluate event IsAllowed for {eventModel.Id.Entry}", exception);
            liveAllowedLine = RandomVisionSuperChargedI18n.Pick(
                "Current IsAllowed check could not be evaluated safely.",
                "当前 IsAllowed 检查无法安全评估。");
            return null;
        }
    }

    private static IReadOnlyList<MonsterModel> GetPreviewMonsters(IRunState runState, EncounterModel encounter)
    {
        try
        {
            var mutable = encounter.ToMutable();
            mutable.GenerateMonstersWithSlots(runState);
            var generated = mutable.MonstersWithSlots.Select(static item => item.Item1).ToList();
            if (generated.Count > 0)
            {
                return generated;
            }
        }
        catch (Exception exception)
        {
            MainFile.LogError($"Failed to generate encounter monsters for {encounter.Id.Entry}", exception);
        }

        return encounter.AllPossibleMonsters.ToList();
    }

    private static Control EnsureOverlayHost(NMapScreen screen)
    {
        var layer = screen.GetNodeOrNull<CanvasLayer>(OverlayLayerName);
        if (layer is null)
        {
            layer = new CanvasLayer
            {
                Name = OverlayLayerName,
                Layer = 132
            };
            screen.AddChild(layer);
        }

        var root = layer.GetNodeOrNull<Control>(OverlayRootName);
        if (root is not null)
        {
            return root;
        }

        root = new Control
        {
            Name = OverlayRootName,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(root);
        return root;
    }

    private static RandomVisionSuperChargedOverlayPanel CreatePanel(Control host)
    {
        MainFile.LogInfo("map-encounter overlay create-panel");
        var panel = new RandomVisionSuperChargedOverlayPanel
        {
            Name = OverlayName,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ZIndex = 120,
            ClipContents = true,
            CustomMinimumSize = new Vector2(PanelWidth, PanelHeight),
            Size = new Vector2(PanelWidth, PanelHeight)
        };
        panel.SetAnchorsPreset(Control.LayoutPreset.TopLeft, keepOffsets: false);
        panel.Position = ResolveInitialPanelPosition(host, panel.Size);
        panel.ConfigureDrag(28f, ViewportPadding, 28f);
        panel.PositionCommitted += position => _lastPanelPosition = SnapToPixel(position);
        panel.AddThemeStyleboxOverride("panel", CreatePanelStyle());

        var margin = new MarginContainer
        {
            Name = MarginName,
            MouseFilter = Control.MouseFilterEnum.Pass
        };
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect, keepOffsets: false);
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_top", 7);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_bottom", 8);

        var root = new VBoxContainer
        {
            Name = RootName,
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        root.AddThemeConstantOverride("separation", 6);

        var titleBar = CreateTitleBar();
        titleBar.Name = TitleBarName;
        root.AddChild(titleBar);

        var content = new HBoxContainer
        {
            Name = ContentName,
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        content.AddThemeConstantOverride("separation", 6);

        root.AddChild(content);
        margin.AddChild(root);
        panel.AddChild(margin);
        host.AddChild(panel);

        var collapseButton = panel.GetNode<Button>($"{MarginName}/{RootName}/{TitleBarName}/{CollapseButtonName}");
        collapseButton.Pressed += () =>
        {
            panel.SetCollapsed(!panel.IsCollapsed);
            RefreshPanelGeometry(panel);
        };

        host.Resized += () =>
        {
            if (!GodotObject.IsInstanceValid(panel))
            {
                return;
            }

            panel.Position = ClampPanelPosition(panel.Position, ResolveHostSize(host), panel.Size);
        };

        return panel;
    }

    private static Control CreateTitleBar()
    {
        var titleBar = new HBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            CustomMinimumSize = new Vector2(0f, 24f)
        };
        titleBar.AddThemeConstantOverride("separation", 6);

        var title = new Label
        {
            Text = RandomVisionSuperChargedI18n.Pick("Encounter Preview", "遭遇预览"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        title.AddThemeColorOverride("font_color", new Color("E8C56A"));
        title.AddThemeFontSizeOverride("font_size", 14);

        var collapseButton = new Button
        {
            Name = CollapseButtonName,
            Text = "-",
            Flat = true,
            FocusMode = Control.FocusModeEnum.None,
            MouseFilter = Control.MouseFilterEnum.Stop,
            TooltipText = RandomVisionSuperChargedI18n.Pick("Minimize preview", "最小化预览"),
            CustomMinimumSize = new Vector2(24f, 22f)
        };
        collapseButton.AddThemeColorOverride("font_color", new Color("D7E4F0"));
        collapseButton.AddThemeFontSizeOverride("font_size", 12);
        collapseButton.AddThemeStyleboxOverride("normal", CreateButtonStyle(0.16f));
        collapseButton.AddThemeStyleboxOverride("hover", CreateButtonStyle(0.28f));
        collapseButton.AddThemeStyleboxOverride("pressed", CreateButtonStyle(0.38f));
        collapseButton.AddThemeStyleboxOverride("focus", CreateButtonStyle(0.28f));

        titleBar.AddChild(title);
        titleBar.AddChild(collapseButton);
        return titleBar;
    }

    private static void RefreshPanel(RandomVisionSuperChargedOverlayPanel panel, MapEncounterPreview preview)
    {
        MainFile.LogInfo("map-encounter overlay render");
        var content = panel.GetNode<HBoxContainer>($"{MarginName}/{RootName}/{ContentName}");
        foreach (var child in content.GetChildren().OfType<Node>().ToArray())
        {
            child.Free();
        }

        content.AddChild(CreateColumn(RandomVisionSuperChargedI18n.Pick("Normal encounters", "普通遭遇"), preview.NormalEncounters));
        content.AddChild(CreateColumn(RandomVisionSuperChargedI18n.Pick("Events", "事件"), preview.Events));
        content.AddChild(CreateStackedColumn(
            CreateColumn(RandomVisionSuperChargedI18n.Pick("Elite encounters", "精英遭遇"), preview.EliteEncounters),
            CreateColumn(RandomVisionSuperChargedI18n.Pick("Ancients", "远古"), preview.Ancients)));
        RefreshPanelGeometry(panel);
    }

    private static Control CreateStackedColumn(params Control[] children)
    {
        var stack = new VBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        stack.AddThemeConstantOverride("separation", 6);

        foreach (var child in children)
        {
            child.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            stack.AddChild(child);
        }

        return stack;
    }

    private static Control CreateColumn(string title, IReadOnlyList<MapPreviewRow> rows)
    {
        var panel = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            ClipContents = true
        };
        panel.AddThemeStyleboxOverride("panel", CreateColumnStyle());

        var margin = new MarginContainer
        {
            MouseFilter = Control.MouseFilterEnum.Pass
        };
        margin.AddThemeConstantOverride("margin_left", 7);
        margin.AddThemeConstantOverride("margin_top", 6);
        margin.AddThemeConstantOverride("margin_right", 7);
        margin.AddThemeConstantOverride("margin_bottom", 6);

        var root = new VBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        root.AddThemeConstantOverride("separation", 5);

        var header = new Label
        {
            Text = title,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        header.AddThemeColorOverride("font_color", new Color("F5F0DE"));
        header.AddThemeFontSizeOverride("font_size", 11);
        root.AddChild(header);

        var scroll = new ScrollContainer
        {
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };

        var list = new VBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        list.AddThemeConstantOverride("separation", 4);

        if (rows.Count == 0)
        {
            list.AddChild(CreateEmptyLabel());
        }
        else
        {
            foreach (var row in rows)
            {
                list.AddChild(CreatePreviewRow(row));
            }
        }

        scroll.AddChild(list);
        root.AddChild(scroll);
        margin.AddChild(root);
        panel.AddChild(margin);
        return panel;
    }

    private static Control CreatePreviewRow(MapPreviewRow row)
    {
        var block = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        block.AddThemeStyleboxOverride("panel", CreateRowStyle());

        var margin = new MarginContainer
        {
            MouseFilter = Control.MouseFilterEnum.Pass
        };
        margin.AddThemeConstantOverride("margin_left", 5);
        margin.AddThemeConstantOverride("margin_top", 4);
        margin.AddThemeConstantOverride("margin_right", 5);
        margin.AddThemeConstantOverride("margin_bottom", 4);

        var title = new Label
        {
            Text = $"{row.Order}. {row.Title}",
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        title.AddThemeColorOverride("font_color", row.IsAllowedEvent ? new Color("8EE58E") : new Color("F4E7C5"));
        title.AddThemeFontSizeOverride("font_size", 10);

        if (string.IsNullOrWhiteSpace(row.Subtitle))
        {
            margin.AddChild(title);
        }
        else
        {
            var textStack = new VBoxContainer
            {
                MouseFilter = Control.MouseFilterEnum.Ignore,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            textStack.AddThemeConstantOverride("separation", 1);
            var subtitle = new Label
            {
                Text = row.Subtitle,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                MaxLinesVisible = 4,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            subtitle.AddThemeColorOverride("font_color", new Color("AFC7D8"));
            subtitle.AddThemeFontSizeOverride("font_size", 9);
            textStack.AddChild(title);
            textStack.AddChild(subtitle);
            margin.AddChild(textStack);
        }

        block.AddChild(margin);
        return block;
    }

    private static Label CreateEmptyLabel()
    {
        var label = new Label
        {
            Text = RandomVisionSuperChargedI18n.Pick("No remaining encounters.", "没有剩余遭遇。"),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        label.AddThemeColorOverride("font_color", new Color("8FB8D8"));
        label.AddThemeFontSizeOverride("font_size", 10);
        return label;
    }

    private static void RefreshPanelGeometry(RandomVisionSuperChargedOverlayPanel panel)
    {
        var content = panel.GetNode<HBoxContainer>($"{MarginName}/{RootName}/{ContentName}");
        var collapseButton = panel.GetNode<Button>($"{MarginName}/{RootName}/{TitleBarName}/{CollapseButtonName}");
        content.Visible = !panel.IsCollapsed;
        collapseButton.Text = panel.IsCollapsed ? "+" : "-";
        collapseButton.TooltipText = panel.IsCollapsed
            ? RandomVisionSuperChargedI18n.Pick("Expand preview", "展开预览")
            : RandomVisionSuperChargedI18n.Pick("Minimize preview", "最小化预览");

        var nextSize = panel.IsCollapsed
            ? new Vector2(PanelWidth, CollapsedHeight)
            : new Vector2(PanelWidth, PanelHeight);
        panel.CustomMinimumSize = nextSize;
        panel.Size = nextSize;
        if (panel.GetParent() is Control host)
        {
            panel.Position = ClampPanelPosition(panel.Position, ResolveHostSize(host), nextSize);
        }
    }

    private static Vector2 ResolveInitialPanelPosition(Control host, Vector2 panelSize)
    {
        var hostSize = ResolveHostSize(host);
        if (_lastPanelPosition is { } savedPosition)
        {
            return ClampPanelPosition(savedPosition, hostSize, panelSize);
        }

        return ClampPanelPosition(new Vector2(
            Mathf.Max(ViewportPadding, hostSize.X - panelSize.X - DefaultRightOffset),
            DefaultTopOffset), hostSize, panelSize);
    }

    private static Vector2 ResolveHostSize(Control host)
    {
        return host.Size != Vector2.Zero ? host.Size : host.GetViewportRect().Size;
    }

    private static Vector2 ClampPanelPosition(Vector2 position, Vector2 hostSize, Vector2 panelSize)
    {
        var maxX = Mathf.Max(ViewportPadding, hostSize.X - panelSize.X - ViewportPadding);
        var maxY = Mathf.Max(ViewportPadding, hostSize.Y - panelSize.Y - ViewportPadding);
        return SnapToPixel(new Vector2(
            Mathf.Clamp(position.X, ViewportPadding, maxX),
            Mathf.Clamp(position.Y, ViewportPadding, maxY)));
    }

    private static Vector2 SnapToPixel(Vector2 position)
    {
        return new Vector2(Mathf.Round(position.X), Mathf.Round(position.Y));
    }

    private static StyleBoxFlat CreatePanelStyle()
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.06f, 0.08f, 0.11f, 0.58f),
            BorderColor = new Color("B8924C")
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(10);
        return style;
    }

    private static StyleBoxFlat CreateColumnStyle()
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.09f, 0.12f, 0.16f, 0.52f),
            BorderColor = new Color(0.33f, 0.4f, 0.49f, 0.58f)
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(8);
        return style;
    }

    private static StyleBoxFlat CreateRowStyle()
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.16f, 0.21f, 0.48f),
            BorderColor = new Color(0.38f, 0.46f, 0.55f, 0.42f)
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(5);
        return style;
    }

    private static StyleBoxFlat CreateTooltipStyle()
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.055f, 0.075f, 0.94f),
            BorderColor = new Color("D8B15C")
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(6);
        return style;
    }

    private static StyleBoxFlat CreateButtonStyle(float alpha)
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.16f, 0.23f, 0.31f, alpha),
            BorderColor = new Color("5B86A6")
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(5);
        style.ContentMarginLeft = 5f;
        style.ContentMarginRight = 5f;
        style.ContentMarginTop = 2f;
        style.ContentMarginBottom = 2f;
        return style;
    }

    private readonly record struct MapEncounterPreview(
        IReadOnlyList<MapPreviewRow> NormalEncounters,
        IReadOnlyList<MapPreviewRow> EliteEncounters,
        IReadOnlyList<MapPreviewRow> Events,
        IReadOnlyList<MapPreviewRow> Ancients);

    private readonly record struct MapPreviewRow(
        int Order,
        string Title,
        LocString HoverTitle,
        string Details,
        bool IsAllowedEvent,
        string? Subtitle = null);
}
