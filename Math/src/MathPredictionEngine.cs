using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace MathMod;

internal static class MathPredictionEngine
{
    private const int MaxSearchNodesPerEnemy = 180;

    private static readonly FieldInfo HistoryEntriesField =
        AccessTools.Field(typeof(CombatHistory), "_entries")
        ?? throw new MissingFieldException(typeof(CombatHistory).FullName, "_entries");

    private static readonly FieldInfo CardCurrentTargetField =
        AccessTools.Field(typeof(CardModel), "_currentTarget")
        ?? throw new MissingFieldException(typeof(CardModel).FullName, "_currentTarget");

    private static readonly FieldInfo TemporaryStarCostsField =
        AccessTools.Field(typeof(CardModel), "_temporaryStarCosts")
        ?? throw new MissingFieldException(typeof(CardModel).FullName, "_temporaryStarCosts");

    private static readonly FieldInfo PlayedField =
        AccessTools.Field(typeof(CardModel), "Played")
        ?? throw new MissingFieldException(typeof(CardModel).FullName, "Played");

    private static readonly FieldInfo StarCostChangedField =
        AccessTools.Field(typeof(CardModel), "StarCostChanged")
        ?? throw new MissingFieldException(typeof(CardModel).FullName, "StarCostChanged");

    private static readonly FieldInfo MonsterCreatureField =
        AccessTools.Field(typeof(MonsterModel), "_creature")
        ?? throw new MissingFieldException(typeof(MonsterModel).FullName, "_creature");

    private static readonly FieldInfo MonsterRunRngField =
        AccessTools.Field(typeof(MonsterModel), "_runRng")
        ?? throw new MissingFieldException(typeof(MonsterModel).FullName, "_runRng");

    private static readonly FieldInfo MonsterRngField =
        AccessTools.Field(typeof(MonsterModel), "_rng")
        ?? throw new MissingFieldException(typeof(MonsterModel).FullName, "_rng");

    private static readonly FieldInfo OrbOwnerField =
        AccessTools.Field(typeof(OrbModel), "_owner")
        ?? throw new MissingFieldException(typeof(OrbModel).FullName, "_owner");

    private static readonly MethodInfo CardGetResultPileTypeMethod =
        AccessTools.Method(typeof(CardModel), "GetResultPileType")
        ?? throw new MissingMethodException(typeof(CardModel).FullName, "GetResultPileType");

    private static readonly MethodInfo CardOnPlayMethod =
        AccessTools.Method(typeof(CardModel), "OnPlay")
        ?? throw new MissingMethodException(typeof(CardModel).FullName, "OnPlay");

    private static readonly HashSet<ModelId> UnsupportedChoiceCards = new();

    public sealed record IncomingDamageResult(int TotalDamage, int BlockedDamage, int HpLoss);

    public sealed record LethalResult(
        Creature Target,
        IReadOnlyList<CardModel> Cards,
        int EnergySpent,
        int StarsSpent,
        int AttackValue,
        int RemainingHp,
        int OverflowValue,
        bool IsLethal,
        bool UsesVulnerable,
        bool UsesPoison,
        int ExploredNodes);

    public sealed record DefenseResult(
        IReadOnlyList<CardModel> Cards,
        int EnergySpent,
        int StarsSpent,
        int IncomingHpLoss,
        int DefenseValue,
        int RemainingHpLoss,
        IReadOnlyList<DefenseContribution> Contributions,
        int ExploredNodes);

    public sealed record DefenseContribution(
        string Kind,
        string? TargetName,
        int EnergySpent,
        int StarsSpent,
        int Value);

    private sealed record PlayDecision(int CardIndex, int? EnemyIndex);

    private sealed class SearchState
    {
        public required CombatSnapshot Snapshot { get; init; }

        public required int EnemyIndex { get; init; }

        public int ExploredNodes { get; set; }
    }

    private sealed class DefenseSearchState
    {
        public required CombatSnapshot Snapshot { get; init; }

        public int ExploredNodes { get; set; }
    }

    private sealed class CombatSnapshot
    {
        public required SerializableRun RunSave { get; init; }

        public required List<CreatureSnapshot> EnemySnapshots { get; init; }

        public required List<CreatureSnapshot> AllySnapshots { get; init; }

        public required Dictionary<ulong, PlayerCombatSnapshot> PlayerSnapshots { get; init; }

        public required ulong LocalPlayerId { get; init; }
    }

    private sealed class CreatureSnapshot
    {
        public required Creature Source { get; init; }

        public required int CurrentHp { get; init; }

        public required int MaxHp { get; init; }

        public required int Block { get; init; }

        public required IReadOnlyList<PowerModel> Powers { get; init; }

        public required bool IsPlayer { get; init; }

        public required bool IsPet { get; init; }

        public ulong? PetOwnerId { get; init; }
    }

    private sealed class PlayerCombatSnapshot
    {
        public required int Energy { get; init; }

        public required int Stars { get; init; }

        public required IReadOnlyDictionary<PileType, IReadOnlyList<CardStateSnapshot>> Piles { get; init; }

        public required IReadOnlyList<OrbModel> Orbs { get; init; }
    }

    private sealed class CardStateSnapshot
    {
        public required SerializableCard Save { get; init; }

        public AfflictionModel? Affliction { get; init; }

        public int AfflictionAmount { get; init; }

        public required IReadOnlySet<CardKeyword> Keywords { get; init; }
    }

    internal sealed class ShadowCombat : IDisposable
    {
        private readonly List<CardModel> _subscribedCards = new();

        public required RunState RunState { get; init; }

        public required CombatState CombatState { get; init; }

        public required Player LocalPlayer { get; init; }

        public required IReadOnlyList<Creature> Enemies { get; init; }

        public required List<CardModel> InitialHand { get; init; }

        public void TrackSubscribedCard(CardModel card)
        {
            _subscribedCards.Add(card);
        }

        public void Dispose()
        {
            CombatStateTracker tracker = CombatManager.Instance.StateTracker;
            foreach (CardModel card in _subscribedCards)
            {
                tracker.Unsubscribe(card);
            }

            foreach (Player player in RunState.Players)
            {
                if (player.PlayerCombatState == null)
                {
                    continue;
                }

                tracker.Unsubscribe(player.PlayerCombatState);
                foreach (CardPile pile in player.PlayerCombatState.AllPiles)
                {
                    tracker.Unsubscribe(pile);
                }
            }
        }
    }

    public static IncomingDamageResult CalculateIncomingDamage(CombatState combatState, Player player)
    {
        int totalDamage = 0;
        foreach (Creature enemy in combatState.Enemies.Where(e => e.IsAlive))
        {
            if (WillDieBeforeActing(enemy))
            {
                continue;
            }

            if (enemy.Monster?.NextMove == null)
            {
                continue;
            }

            totalDamage += enemy.Monster.NextMove.Intents
                .OfType<AttackIntent>()
                .Sum(intent => intent.GetTotalDamage(combatState.Allies, enemy));
        }

        int blockedDamage = System.Math.Min(player.Creature.Block, totalDamage);
        return new IncomingDamageResult(totalDamage, blockedDamage, System.Math.Max(0, totalDamage - player.Creature.Block));
    }

    public static IReadOnlyDictionary<Creature, LethalResult> CalculateLethalPlans(CombatState combatState, Player player)
    {
        return MathPurePredictionPlanner.CalculateLethalPlans(combatState, player);
    }

    public static DefenseResult? CalculateDefensePlan(CombatState combatState, Player player)
    {
        return MathPurePredictionPlanner.CalculateDefensePlan(combatState, player);
    }

    private static bool TryFindLethalRecursive(SearchState searchState, List<int> chosenIndices, out List<int> result)
    {
        result = null!;
        if (searchState.ExploredNodes >= MaxSearchNodesPerEnemy)
        {
            return false;
        }

        searchState.ExploredNodes++;
        // 搜索过程中会走大量原版 hook 和 history，必须把全局副作用限制在作用域内。
        using PredictionSimulationScope _ = new();
        using ShadowCombat? shadow = BuildShadowCombat(searchState.Snapshot, chosenIndices, searchState.EnemyIndex);
        if (shadow == null)
        {
            return false;
        }

        Creature target = shadow.Enemies[searchState.EnemyIndex];
        if (WillDieBeforeActing(target))
        {
            result = chosenIndices.ToList();
            return true;
        }

        IReadOnlyList<CardModel> orderedCandidates = GetCandidateCards(shadow, target, chosenIndices);
        if (orderedCandidates.Count == 0 || !CouldStillKillTarget(orderedCandidates, target))
        {
            return false;
        }

        foreach (CardModel candidate in orderedCandidates)
        {
            int originalIndex = shadow.InitialHand.IndexOf(candidate);
            if (originalIndex < 0)
            {
                continue;
            }

            chosenIndices.Add(originalIndex);
            if (TryFindLethalRecursive(searchState, chosenIndices, out result))
            {
                return true;
            }

            chosenIndices.RemoveAt(chosenIndices.Count - 1);
        }

        return false;
    }

    private static bool TryFindDefenseRecursive(DefenseSearchState searchState, List<PlayDecision> chosenPlays, out List<PlayDecision> result)
    {
        result = null!;
        if (searchState.ExploredNodes >= MaxSearchNodesPerEnemy)
        {
            return false;
        }

        searchState.ExploredNodes++;
        using PredictionSimulationScope _ = new();
        int replayEnergySpent = 0;
        int replayStarsSpent = 0;
        using ShadowCombat? shadow = BuildShadowCombat(searchState.Snapshot, chosenPlays, out replayEnergySpent, out replayStarsSpent);
        if (shadow == null)
        {
            return false;
        }

        if (CalculateIncomingDamage(shadow.CombatState, shadow.LocalPlayer).HpLoss <= 0)
        {
            result = chosenPlays.ToList();
            return true;
        }

        IReadOnlyList<PlayDecision> options = GetDefensivePlayOptions(shadow, chosenPlays);
        if (options.Count == 0)
        {
            return false;
        }

        foreach (PlayDecision option in options)
        {
            chosenPlays.Add(option);
            if (TryFindDefenseRecursive(searchState, chosenPlays, out result))
            {
                return true;
            }

            chosenPlays.RemoveAt(chosenPlays.Count - 1);
        }

        return false;
    }

    private static IReadOnlyList<PlayDecision> GetDefensivePlayOptions(ShadowCombat shadow, IReadOnlyCollection<PlayDecision> chosenPlays)
    {
        HashSet<int> usedIndices = chosenPlays.Select(play => play.CardIndex).ToHashSet();
        List<(PlayDecision Decision, decimal Score, int Cost)> options = new();
        foreach (CardModel card in shadow.InitialHand)
        {
            if (card.Pile?.Type != PileType.Hand)
            {
                continue;
            }

            int cardIndex = shadow.InitialHand.IndexOf(card);
            if (cardIndex < 0 || usedIndices.Contains(cardIndex))
            {
                continue;
            }

            foreach (int? enemyIndex in EnumeratePlayableEnemyIndices(shadow, card))
            {
                Creature? target = enemyIndex.HasValue ? shadow.Enemies[enemyIndex.Value] : null;
                if (!CanUseCardForSearch(card, target ?? card.Owner.Creature))
                {
                    continue;
                }

                decimal score = EstimateDefensiveImpact(shadow.CombatState, shadow.LocalPlayer, card, target, enemyIndex);
                (int energyCost, int starCost) = CalculateCurrentResourceSpend(card);
                options.Add((new PlayDecision(cardIndex, enemyIndex), score, energyCost + starCost));
            }
        }

        return options
            .OrderBy(option => option.Cost)
            .ThenByDescending(option => option.Score)
            .ThenBy(option => shadow.InitialHand[option.Decision.CardIndex].Id.Entry)
            .Select(option => option.Decision)
            .Take(12)
            .ToList();
    }

    private static IEnumerable<int?> EnumeratePlayableEnemyIndices(ShadowCombat shadow, CardModel card)
    {
        switch (card.TargetType)
        {
            case TargetType.AnyEnemy:
                for (int enemyIndex = 0; enemyIndex < shadow.Enemies.Count; enemyIndex++)
                {
                    Creature enemy = shadow.Enemies[enemyIndex];
                    if (enemy.IsAlive && card.CanPlayTargeting(enemy))
                    {
                        yield return enemyIndex;
                    }
                }
                yield break;
            case TargetType.AnyAlly:
                if (card.CanPlayTargeting(card.Owner.Creature))
                {
                    yield return null;
                }
                yield break;
            default:
                if (card.CanPlay())
                {
                    yield return null;
                }
                yield break;
        }
    }

    private static decimal EstimateDefensiveImpact(CombatState combatState, Player player, CardModel card, Creature? target, int? enemyIndex)
    {
        decimal estimate = 0m;
        card.DynamicVars.ClearPreview();
        card.UpdateDynamicVarPreview(CardPreviewMode.Normal, target, card.DynamicVars);

        if (card.DynamicVars.TryGetValue(CalculatedBlockVar.defaultName, out DynamicVar? calculatedBlock))
        {
            estimate += calculatedBlock.PreviewValue;
        }
        if (card.DynamicVars.TryGetValue(BlockVar.defaultName, out DynamicVar? block))
        {
            estimate += block.PreviewValue;
        }
        if (card.DynamicVars.TryGetValue("WeakPower", out DynamicVar? weak))
        {
            estimate += weak.PreviewValue * 3m;
        }

        if (enemyIndex.HasValue)
        {
            Creature enemy = combatState.Enemies[enemyIndex.Value];
            decimal damageEstimate = EstimateCardImpact(card, enemy);
            if (damageEstimate >= enemy.CurrentHp + enemy.Block)
            {
                estimate += GetEnemyAttackDamage(combatState, enemy);
            }
        }

        if (card.Keywords.Contains(CardKeyword.Exhaust))
        {
            estimate -= 0.25m;
        }

        return estimate;
    }

    private static int GetEnemyAttackDamage(CombatState combatState, Creature enemy)
    {
        if (enemy.Monster?.NextMove == null)
        {
            return 0;
        }

        return enemy.Monster.NextMove.Intents
            .OfType<AttackIntent>()
            .Sum(intent => intent.GetTotalDamage(combatState.Allies, enemy));
    }

    private static ShadowCombat? BuildShadowCombat(CombatSnapshot snapshot, IReadOnlyList<int> chosenIndices, int targetEnemyIndex)
    {
        RunState runState = RunState.FromSerializable(snapshot.RunSave);
        Dictionary<ulong, Player> playersById = runState.Players.ToDictionary(player => player.NetId);
        foreach (Player player in runState.Players)
        {
            player.ResetCombatState();
        }

        EncounterModel? encounter = snapshot.EnemySnapshots.FirstOrDefault()?.Source.CombatState?.Encounter;
        CombatState shadowCombatState = new(encounter, runState, runState.Modifiers, runState.MultiplayerScalingModel)
        {
            RoundNumber = snapshot.EnemySnapshots.FirstOrDefault()?.Source.CombatState?.RoundNumber ?? 1,
            CurrentSide = CombatSide.Player
        };

        foreach (Player player in runState.Players)
        {
            shadowCombatState.AddPlayer(player);
        }

        foreach ((ulong netId, PlayerCombatSnapshot playerSnapshot) in snapshot.PlayerSnapshots)
        {
            Player shadowPlayer = playersById[netId];
            shadowPlayer.PlayerCombatState!.Energy = playerSnapshot.Energy;
            shadowPlayer.PlayerCombatState.Stars = playerSnapshot.Stars;
            CopyCreatureState(snapshot.AllySnapshots.First(creature => creature.Source.IsPlayer && creature.Source.Player?.NetId == netId), shadowPlayer.Creature);
            RebuildCombatPiles(shadowCombatState, shadowPlayer, playerSnapshot);
            RebuildOrbs(shadowPlayer, playerSnapshot.Orbs);
        }

        foreach (CreatureSnapshot allySnapshot in snapshot.AllySnapshots.Where(creature => creature.IsPet))
        {
            Creature pet = shadowCombatState.CreateCreature(CloneMonsterModel(allySnapshot.Source), allySnapshot.Source.Side, allySnapshot.Source.SlotName);
            if (allySnapshot.PetOwnerId.HasValue && playersById.TryGetValue(allySnapshot.PetOwnerId.Value, out Player? owner))
            {
                pet.PetOwner = owner;
                owner.PlayerCombatState?.AddPetInternal(pet);
            }

            CopyCreatureState(allySnapshot, pet);
        }

        List<Creature> shadowEnemies = new();
        foreach (CreatureSnapshot enemySnapshot in snapshot.EnemySnapshots)
        {
            Creature shadowEnemy = shadowCombatState.CreateCreature(CloneMonsterModel(enemySnapshot.Source), enemySnapshot.Source.Side, enemySnapshot.Source.SlotName);
            CopyCreatureState(enemySnapshot, shadowEnemy);
            shadowEnemies.Add(shadowEnemy);
        }

        Player localPlayer = playersById[snapshot.LocalPlayerId];
        ShadowCombat shadowCombat = new()
        {
            RunState = runState,
            CombatState = shadowCombatState,
            LocalPlayer = localPlayer,
            Enemies = shadowEnemies,
            InitialHand = localPlayer.PlayerCombatState!.Hand.Cards.ToList()
        };

        foreach (Player player in runState.Players)
        {
            foreach (CardModel card in player.PlayerCombatState!.AllCards)
            {
                shadowCombat.TrackSubscribedCard(card);
            }
        }

        int pathStep = 0;
        foreach (int originalIndex in chosenIndices)
        {
            if (originalIndex < 0 || originalIndex >= shadowCombat.InitialHand.Count)
            {
                return null;
            }

            CardModel card = shadowCombat.InitialHand[originalIndex];
            Creature? target = ResolveManualTarget(card, shadowCombat.Enemies.ElementAtOrDefault(targetEnemyIndex));
            if (!TryPlayCardWithoutUi(card, target, out _, out _))
            {
                return null;
            }

            pathStep++;
        }

        return shadowCombat;
    }

    private static ShadowCombat? BuildShadowCombat(CombatSnapshot snapshot, IReadOnlyList<PlayDecision> chosenPlays, out int energySpent, out int starsSpent)
    {
        energySpent = 0;
        starsSpent = 0;

        RunState runState = RunState.FromSerializable(snapshot.RunSave);
        Dictionary<ulong, Player> playersById = runState.Players.ToDictionary(player => player.NetId);
        foreach (Player player in runState.Players)
        {
            player.ResetCombatState();
        }

        EncounterModel? encounter = snapshot.EnemySnapshots.FirstOrDefault()?.Source.CombatState?.Encounter;
        CombatState shadowCombatState = new(encounter, runState, runState.Modifiers, runState.MultiplayerScalingModel)
        {
            RoundNumber = snapshot.EnemySnapshots.FirstOrDefault()?.Source.CombatState?.RoundNumber ?? 1,
            CurrentSide = CombatSide.Player
        };

        foreach (Player player in runState.Players)
        {
            shadowCombatState.AddPlayer(player);
        }

        foreach ((ulong netId, PlayerCombatSnapshot playerSnapshot) in snapshot.PlayerSnapshots)
        {
            Player shadowPlayer = playersById[netId];
            shadowPlayer.PlayerCombatState!.Energy = playerSnapshot.Energy;
            shadowPlayer.PlayerCombatState.Stars = playerSnapshot.Stars;
            CopyCreatureState(snapshot.AllySnapshots.First(creature => creature.Source.IsPlayer && creature.Source.Player?.NetId == netId), shadowPlayer.Creature);
            RebuildCombatPiles(shadowCombatState, shadowPlayer, playerSnapshot);
            RebuildOrbs(shadowPlayer, playerSnapshot.Orbs);
        }

        foreach (CreatureSnapshot allySnapshot in snapshot.AllySnapshots.Where(creature => creature.IsPet))
        {
            Creature pet = shadowCombatState.CreateCreature(CloneMonsterModel(allySnapshot.Source), allySnapshot.Source.Side, allySnapshot.Source.SlotName);
            if (allySnapshot.PetOwnerId.HasValue && playersById.TryGetValue(allySnapshot.PetOwnerId.Value, out Player? owner))
            {
                pet.PetOwner = owner;
                owner.PlayerCombatState?.AddPetInternal(pet);
            }

            CopyCreatureState(allySnapshot, pet);
        }

        List<Creature> shadowEnemies = new();
        foreach (CreatureSnapshot enemySnapshot in snapshot.EnemySnapshots)
        {
            Creature shadowEnemy = shadowCombatState.CreateCreature(CloneMonsterModel(enemySnapshot.Source), enemySnapshot.Source.Side, enemySnapshot.Source.SlotName);
            CopyCreatureState(enemySnapshot, shadowEnemy);
            shadowEnemies.Add(shadowEnemy);
        }

        Player localPlayer = playersById[snapshot.LocalPlayerId];
        ShadowCombat shadowCombat = new()
        {
            RunState = runState,
            CombatState = shadowCombatState,
            LocalPlayer = localPlayer,
            Enemies = shadowEnemies,
            InitialHand = localPlayer.PlayerCombatState!.Hand.Cards.ToList()
        };

        foreach (Player player in runState.Players)
        {
            foreach (CardModel card in player.PlayerCombatState!.AllCards)
            {
                shadowCombat.TrackSubscribedCard(card);
            }
        }

        foreach (PlayDecision play in chosenPlays)
        {
            if (play.CardIndex < 0 || play.CardIndex >= shadowCombat.InitialHand.Count)
            {
                return null;
            }

            CardModel card = shadowCombat.InitialHand[play.CardIndex];
            Creature? target = ResolveManualTarget(card, play.EnemyIndex.HasValue ? shadowCombat.Enemies.ElementAtOrDefault(play.EnemyIndex.Value) : null);
            if (!TryPlayCardWithoutUi(card, target, out int spentEnergy, out int spentStars))
            {
                return null;
            }

            energySpent += spentEnergy;
            starsSpent += spentStars;
        }

        return shadowCombat;
    }

    private static CombatSnapshot CreateSnapshot(CombatState combatState, Player player)
    {
        List<CreatureSnapshot> allies = combatState.Allies
            .Select(CreateCreatureSnapshot)
            .ToList();
        List<CreatureSnapshot> enemies = combatState.Enemies
            .Select(CreateCreatureSnapshot)
            .ToList();

        Dictionary<ulong, PlayerCombatSnapshot> playerSnapshots = combatState.Players.ToDictionary(
            combatPlayer => combatPlayer.NetId,
            CreatePlayerCombatSnapshot);

        return new CombatSnapshot
        {
            RunSave = RunManager.Instance.ToSave(null),
            AllySnapshots = allies,
            EnemySnapshots = enemies,
            PlayerSnapshots = playerSnapshots,
            LocalPlayerId = player.NetId
        };
    }

    private static CreatureSnapshot CreateCreatureSnapshot(Creature creature)
    {
        return new CreatureSnapshot
        {
            Source = creature,
            CurrentHp = creature.CurrentHp,
            MaxHp = creature.MaxHp,
            Block = creature.Block,
            Powers = creature.Powers.Select(power => (PowerModel)power.ClonePreservingMutability()).ToList(),
            IsPlayer = creature.IsPlayer,
            IsPet = creature.IsPet,
            PetOwnerId = creature.PetOwner?.NetId
        };
    }

    private static PlayerCombatSnapshot CreatePlayerCombatSnapshot(Player player)
    {
        PlayerCombatState combatState = player.PlayerCombatState ?? throw new InvalidOperationException("Player combat state is missing.");
        Dictionary<PileType, IReadOnlyList<CardStateSnapshot>> piles = new()
        {
            [PileType.Hand] = combatState.Hand.Cards.Select(CreateCardSnapshot).ToList(),
            [PileType.Draw] = combatState.DrawPile.Cards.Select(CreateCardSnapshot).ToList(),
            [PileType.Discard] = combatState.DiscardPile.Cards.Select(CreateCardSnapshot).ToList(),
            [PileType.Exhaust] = combatState.ExhaustPile.Cards.Select(CreateCardSnapshot).ToList(),
            [PileType.Play] = combatState.PlayPile.Cards.Select(CreateCardSnapshot).ToList()
        };

        return new PlayerCombatSnapshot
        {
            Energy = combatState.Energy,
            Stars = combatState.Stars,
            Piles = piles,
            Orbs = combatState.OrbQueue.Orbs.Select(CloneOrb).ToList()
        };
    }

    private static CardStateSnapshot CreateCardSnapshot(CardModel card)
    {
        return new CardStateSnapshot
        {
            Save = card.ToSerializable(),
            Affliction = card.Affliction != null ? (AfflictionModel)card.Affliction.ClonePreservingMutability() : null,
            AfflictionAmount = card.Affliction?.Amount ?? 0,
            Keywords = card.Keywords.ToHashSet()
        };
    }

    private static void RebuildCombatPiles(CombatState combatState, Player player, PlayerCombatSnapshot snapshot)
    {
        foreach ((PileType pileType, IReadOnlyList<CardStateSnapshot> cards) in snapshot.Piles)
        {
            CardPile pile = pileType.GetPile(player);
            foreach (CardStateSnapshot cardSnapshot in cards)
            {
                CardModel card = CardModel.FromSerializable(cardSnapshot.Save);
                combatState.AddCard(card, player);
                SynchronizeCardAfflictionAndKeywords(card, cardSnapshot);
                pile.AddInternal(card, silent: true);
            }
        }
    }

    private static void SynchronizeCardAfflictionAndKeywords(CardModel card, CardStateSnapshot snapshot)
    {
        if (snapshot.Affliction != null && card.Affliction == null)
        {
            AfflictionModel affliction = (AfflictionModel)snapshot.Affliction.ClonePreservingMutability();
            card.AfflictInternal(affliction, snapshot.AfflictionAmount);
        }

        foreach (CardKeyword keyword in card.Keywords.Except(snapshot.Keywords).ToList())
        {
            card.RemoveKeyword(keyword);
        }

        foreach (CardKeyword keyword in snapshot.Keywords.Except(card.Keywords).ToList())
        {
            card.AddKeyword(keyword);
        }
    }

    private static void RebuildOrbs(Player player, IReadOnlyList<OrbModel> orbs)
    {
        OrbQueue orbQueue = player.PlayerCombatState!.OrbQueue;
        orbQueue.Clear();
        orbQueue.AddCapacity(player.BaseOrbSlotCount);
        foreach (OrbModel orb in orbs)
        {
            orb.Owner = player;
            orbQueue.Insert(orbQueue.Orbs.Count, orb);
        }
    }

    private static void CopyCreatureState(CreatureSnapshot snapshot, Creature creature)
    {
        creature.SetMaxHpInternal(snapshot.MaxHp);
        creature.SetCurrentHpInternal(snapshot.CurrentHp);
        if (snapshot.Block > 0)
        {
            creature.GainBlockInternal(snapshot.Block);
        }

        foreach (PowerModel power in snapshot.Powers)
        {
            PowerModel shadowPower = (PowerModel)power.ClonePreservingMutability();
            shadowPower.ApplyInternal(creature, shadowPower.Amount, silent: true);
        }
    }

    private static MonsterModel CloneMonsterModel(Creature source)
    {
        MonsterModel clonedMonster = (MonsterModel)source.Monster!.ClonePreservingMutability();
        MonsterCreatureField.SetValue(clonedMonster, null);
        MonsterRunRngField.SetValue(clonedMonster, null);
        MonsterRngField.SetValue(clonedMonster, null);
        return clonedMonster;
    }

    private static OrbModel CloneOrb(OrbModel source)
    {
        OrbModel clone = (OrbModel)source.ClonePreservingMutability();
        OrbOwnerField.SetValue(clone, null);
        return clone;
    }

    private static IReadOnlyList<CardModel> GetCandidateCards(ShadowCombat shadow, Creature target, IReadOnlyCollection<int> chosenIndices)
    {
        PlayerCombatState playerCombatState = shadow.LocalPlayer.PlayerCombatState!;
        return shadow.InitialHand
            .Where(card => card.Pile?.Type == PileType.Hand)
            .Where(card => shadow.InitialHand.IndexOf(card) is var index && index >= 0 && !chosenIndices.Contains(index))
            .Where(card => CanUseCardForSearch(card, target))
            .OrderBy(card => card.EnergyCost.GetWithModifiers(CostModifiers.All))
            .ThenByDescending(card => EstimateCardImpact(card, target))
            .ThenBy(card => card.Id.Entry)
            .ToList();
    }

    private static bool CouldStillKillTarget(IReadOnlyList<CardModel> candidates, Creature target)
    {
        decimal totalEstimate = target.GetPower<PoisonPower>()?.CalculateTotalDamageNextTurn() ?? 0;
        foreach (CardModel card in candidates)
        {
            totalEstimate += EstimateCardImpact(card, target);
        }

        return totalEstimate >= target.CurrentHp + target.Block;
    }

    private static bool CanUseCardForSearch(CardModel card, Creature target)
    {
        if (!card.CanPlay(out _, out _))
        {
            return false;
        }

        if (card.TargetType == TargetType.AnyEnemy)
        {
            return card.CanPlayTargeting(target);
        }

        if (card.TargetType == TargetType.AnyAlly)
        {
            return card.CanPlayTargeting(card.Owner.Creature);
        }

        return card.CanPlay();
    }

    private static decimal EstimateCardImpact(CardModel card, Creature target)
    {
        decimal estimate = 0m;
        card.DynamicVars.ClearPreview();
        card.UpdateDynamicVarPreview(CardPreviewMode.Normal, target, card.DynamicVars);

        if (card.DynamicVars.TryGetValue(CalculatedDamageVar.defaultName, out DynamicVar? calculatedDamage))
        {
            estimate += calculatedDamage.PreviewValue;
        }
        if (card.DynamicVars.TryGetValue("Damage", out DynamicVar? damage))
        {
            estimate += damage.PreviewValue;
        }
        if (card.DynamicVars.TryGetValue("OstyDamage", out DynamicVar? ostyDamage))
        {
            estimate += ostyDamage.PreviewValue;
        }
        if (card.DynamicVars.TryGetValue("PoisonPower", out DynamicVar? poison))
        {
            estimate += poison.PreviewValue;
        }
        if (card.DynamicVars.TryGetValue("VulnerablePower", out DynamicVar? vulnerable))
        {
            estimate += vulnerable.PreviewValue * 2m;
        }

        return estimate;
    }

    private static Creature? ResolveManualTarget(CardModel card, Creature? targetEnemy)
    {
        return card.TargetType switch
        {
            TargetType.AnyEnemy => targetEnemy,
            TargetType.AnyAlly => card.Owner.Creature,
            _ => null
        };
    }

    private static bool TryPlayCardWithoutUi(CardModel card, Creature? target, out int energySpent, out int starsSpent)
    {
        energySpent = 0;
        starsSpent = 0;
        if (!CanUseCardForSearch(card, target ?? card.Owner.Creature))
        {
            return false;
        }

        try
        {
            // 这里直接按影子战斗里的当前费用模型估算，避免某些重放路径下 SpendResources() 的返回值被吞成 0。
            (energySpent, starsSpent) = CalculateCurrentResourceSpend(card);
            card.SpendResources().GetAwaiter().GetResult();
            ResourceInfo resources = new()
            {
                EnergySpent = energySpent,
                EnergyValue = energySpent,
                StarsSpent = starsSpent,
                StarValue = starsSpent
            };

            SimulateManualPlayAsync(card, target, new ThrowingPlayerChoiceContext(), resources).GetAwaiter().GetResult();
            return true;
        }
        catch (NotImplementedException)
        {
            UnsupportedChoiceCards.Add(card.Id);
            return false;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("player choice", StringComparison.OrdinalIgnoreCase))
        {
            UnsupportedChoiceCards.Add(card.Id);
            return false;
        }
    }

    private static (int EnergySpent, int StarsSpent) CalculateCurrentResourceSpend(CardModel card)
    {
        PlayerCombatState playerCombatState = card.Owner.PlayerCombatState
            ?? throw new InvalidOperationException("Card owner combat state is missing during prediction.");

        CombatState combatState = card.CombatState ?? card.Owner.Creature.CombatState
            ?? throw new InvalidOperationException("Card combat state is missing during prediction.");

        int energySpent = System.Math.Max(0, card.EnergyCost.GetAmountToSpend());
        int starsSpent = System.Math.Max(0, card.GetStarCostWithModifiers());
        if (energySpent > playerCombatState.Energy && Hook.ShouldPayExcessEnergyCostWithStars(combatState, card.Owner))
        {
            starsSpent += (energySpent - playerCombatState.Energy) * 2;
            energySpent = playerCombatState.Energy;
        }

        return (energySpent, starsSpent);
    }

    private static async Task SimulateManualPlayAsync(CardModel card, Creature? target, PlayerChoiceContext choiceContext, ResourceInfo resources)
    {
        CombatState combatState = card.CombatState ?? card.Owner.Creature.CombatState ?? throw new InvalidOperationException("Shadow card lost combat state during prediction.");
        // 不走真实手动出牌 UI，而是最小复刻 OnPlayWrapper(false) 的核心流程。
        choiceContext.PushModel(card);
        await CombatManager.Instance.WaitForUnpause();
        CardCurrentTargetField.SetValue(card, target);

        CardPile? oldPile = card.Pile;
        card.RemoveFromCurrentPile();
        PileType.Play.GetPile(card.Owner).AddInternal(card, silent: true);
        await Hook.AfterCardChangedPiles(card.Owner.RunState, combatState, card, oldPile?.Type ?? PileType.None, null);

        PileType defaultResultPile = (PileType)(CardGetResultPileTypeMethod.Invoke(card, null) ?? PileType.Discard);
        (PileType resultPileType, CardPilePosition resultPilePosition) = Hook.ModifyCardPlayResultPileTypeAndPosition(
            combatState,
            card,
            isAutoPlay: false,
            resources,
            defaultResultPile,
            CardPilePosition.Bottom,
            out IEnumerable<AbstractModel> resultModifiers);

        foreach (AbstractModel modifier in resultModifiers)
        {
            await modifier.AfterModifyingCardPlayResultPileOrPosition(card, resultPileType, resultPilePosition);
        }

        int playCount = card.GetEnchantedReplayCount() + 1;
        playCount = Hook.ModifyCardPlayCount(combatState, card, playCount, target, out List<AbstractModel> playCountModifiers);
        await Hook.AfterModifyingCardPlayCount(combatState, card, playCountModifiers);

        for (int playIndex = 0; playIndex < playCount; playIndex++)
        {
            CardPlay cardPlay = new()
            {
                Card = card,
                Target = target,
                ResultPile = resultPileType,
                Resources = resources,
                IsAutoPlay = false,
                PlayIndex = playIndex,
                PlayCount = playCount
            };

            await Hook.BeforeCardPlayed(combatState, cardPlay);
            CombatManager.Instance.History.CardPlayStarted(combatState, cardPlay);
            await InvokeCardOnPlayAsync(card, choiceContext, cardPlay);
            card.InvokeExecutionFinished();

            if (card.Enchantment != null)
            {
                await card.Enchantment.OnPlay(choiceContext, cardPlay);
                card.Enchantment.InvokeExecutionFinished();
            }

            if (card.Affliction != null)
            {
                await card.Affliction.OnPlay(choiceContext, target);
                card.Affliction.InvokeExecutionFinished();
            }

            CombatManager.Instance.History.CardPlayFinished(combatState, cardPlay);
            if (CombatManager.Instance.IsInProgress)
            {
                await Hook.AfterCardPlayed(combatState, choiceContext, cardPlay);
            }
        }

        if (card.Pile?.Type == PileType.Play)
        {
            switch (resultPileType)
            {
                case PileType.None:
                    await CardPileCmd.RemoveFromCombat(card, isBeingPlayed: true, skipVisuals: true);
                    break;
                case PileType.Exhaust:
                    await CardCmd.Exhaust(choiceContext, card, causedByEthereal: false, skipVisuals: true);
                    break;
                default:
                    await CardPileCmd.Add(card, resultPileType, resultPilePosition, null, skipVisuals: true);
                    break;
            }
        }

        await CombatManager.Instance.CheckForEmptyHand(choiceContext, card.Owner);
        if (card.EnergyCost.AfterCardPlayedCleanup())
        {
            card.InvokeEnergyCostChanged();
        }
        if (TemporaryStarCostsField.GetValue(card) is List<TemporaryCardCost> temporaryStarCosts
            && temporaryStarCosts.RemoveAll(static cost => cost.ClearsWhenCardIsPlayed) > 0)
        {
            InvokeCardEvent(card, StarCostChangedField);
        }

        CardCurrentTargetField.SetValue(card, null);
        InvokeCardEvent(card, PlayedField);
        choiceContext.PopModel(card);
    }

    private static async Task InvokeCardOnPlayAsync(CardModel card, PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        if (CardOnPlayMethod.Invoke(card, new object?[] { choiceContext, cardPlay }) is Task task)
        {
            await task;
        }
    }

    private static void InvokeCardEvent(CardModel card, FieldInfo eventField)
    {
        if (eventField.GetValue(card) is Action action)
        {
            action.Invoke();
        }
    }

    private static bool WillDieBeforeActing(Creature creature)
    {
        if (!creature.IsAlive)
        {
            return true;
        }

        // 这里只预结算“敌人行动前一定会发生”的伤害；中毒算，Doom 这类回合结束触发的不算。
        PoisonPower? poison = creature.GetPower<PoisonPower>();
        if (poison == null)
        {
            return false;
        }

        return poison.CalculateTotalDamageNextTurn() >= creature.CurrentHp;
    }

        public static bool IsSimulationActive => _predictionSimulationDepth > 0;

    private static int _predictionSimulationDepth;

    private sealed class PredictionSimulationScope : IDisposable
    {
        private readonly Func<bool> _previousAutoSlayerCheck;

        private readonly CombatHistory _history;

        private readonly List<CombatHistoryEntry> _entriesSnapshot;

        public PredictionSimulationScope()
        {
            _predictionSimulationDepth++;
            _previousAutoSlayerCheck = NonInteractiveMode.AutoSlayerCheck;
            NonInteractiveMode.AutoSlayerCheck = static () => true;

            _history = CombatManager.Instance.History;
            _entriesSnapshot = _history.Entries.ToList();
        }

        public void Dispose()
        {
            _predictionSimulationDepth = System.Math.Max(0, _predictionSimulationDepth - 1);
            NonInteractiveMode.AutoSlayerCheck = _previousAutoSlayerCheck;
            if (HistoryEntriesField.GetValue(_history) is List<CombatHistoryEntry> entries)
            {
                entries.Clear();
                entries.AddRange(_entriesSnapshot);
            }
        }
    }
}
