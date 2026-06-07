using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;

namespace MathMod;

internal static class MathPurePredictionPlanner
{
    private const int MaxSearchNodesPerEnemy = 512;

    private sealed record PurePlayOption(
        int CardIndex,
        int? EnemyIndex,
        bool AppliesToAllEnemies,
        int EnergyCost,
        int StarCost,
        decimal DirectDamage,
        int Poison,
        int Vulnerable,
        int Weak,
        decimal BlockGain);

    private sealed class PureEnemyState
    {
        public required Creature Source { get; init; }

        public required decimal CurrentHp { get; set; }

        public required decimal Block { get; set; }

        public required int Poison { get; set; }

        public required bool BaseHasVulnerable { get; init; }

        public required bool HasVulnerable { get; set; }

        public required bool BaseHasWeak { get; init; }

        public required bool HasWeak { get; set; }

        public required int AttackDamage { get; init; }

        public PureEnemyState Clone()
        {
            return new PureEnemyState
            {
                Source = Source,
                CurrentHp = CurrentHp,
                Block = Block,
                Poison = Poison,
                BaseHasVulnerable = BaseHasVulnerable,
                HasVulnerable = HasVulnerable,
                BaseHasWeak = BaseHasWeak,
                HasWeak = HasWeak,
                AttackDamage = AttackDamage
            };
        }
    }

    private sealed class PureCombatState
    {
        public required List<PureEnemyState> Enemies { get; init; }

        public required decimal PlayerBlock { get; set; }

        public required int EnergyLeft { get; set; }

        public required int StarsLeft { get; set; }

        public required bool CanPayExcessEnergyWithStars { get; init; }

        public required int EnergySpent { get; set; }

        public required int StarsSpent { get; set; }

        public PureCombatState Clone()
        {
            return new PureCombatState
            {
                Enemies = Enemies.Select(static enemy => enemy.Clone()).ToList(),
                PlayerBlock = PlayerBlock,
                EnergyLeft = EnergyLeft,
                StarsLeft = StarsLeft,
                CanPayExcessEnergyWithStars = CanPayExcessEnergyWithStars,
                EnergySpent = EnergySpent,
                StarsSpent = StarsSpent
            };
        }
    }

    private sealed class SearchCounter
    {
        public int ExploredNodes { get; set; }
    }

    private sealed class LethalSearchBest
    {
        public List<PurePlayOption> Plan { get; set; } = new();

        public PureCombatState? FinalState { get; set; }

        public decimal RemainingMetric { get; set; } = decimal.MaxValue;

        public int EffectiveCost { get; set; } = int.MaxValue;

        public int EnergySpent { get; set; } = int.MaxValue;

        public int StarsSpent { get; set; } = int.MaxValue;
    }

    private sealed class DefenseSearchBest
    {
        public List<PurePlayOption> Plan { get; set; } = new();

        public PureCombatState? FinalState { get; set; }

        public int RemainingHpLoss { get; set; } = int.MaxValue;

        public int EffectiveCost { get; set; } = int.MaxValue;

        public int EnergySpent { get; set; } = int.MaxValue;

        public int StarsSpent { get; set; } = int.MaxValue;
    }

    public static IReadOnlyDictionary<Creature, MathPredictionEngine.LethalResult> CalculateLethalPlans(CombatState combatState, Player player)
    {
        if (combatState.CurrentSide != CombatSide.Player)
        {
            return new Dictionary<Creature, MathPredictionEngine.LethalResult>();
        }

        IReadOnlyList<CardModel> hand = player.PlayerCombatState?.Hand.Cards ?? Array.Empty<CardModel>();
        if (hand.Count == 0)
        {
            return new Dictionary<Creature, MathPredictionEngine.LethalResult>();
        }

        List<PurePlayOption> options = BuildPlayOptions(combatState, player, hand);
        PureCombatState initialState = CreateInitialState(combatState, player);
        Dictionary<Creature, MathPredictionEngine.LethalResult> results = new();
        for (int enemyIndex = 0; enemyIndex < initialState.Enemies.Count; enemyIndex++)
        {
            PureEnemyState enemyState = initialState.Enemies[enemyIndex];
            if (WillDieBeforeActing(enemyState))
            {
                continue;
            }

            SearchCounter counter = new();
            LethalSearchBest best = new();
            bool foundLethal = TryFindLethalRecursive(
                initialState,
                options,
                new HashSet<int>(),
                enemyIndex,
                new List<PurePlayOption>(),
                counter,
                best,
                out List<PurePlayOption>? plan,
                out PureCombatState? finalState);
            if (!foundLethal && best.FinalState == null)
            {
                continue;
            }

            IReadOnlyList<PurePlayOption> resolvedPlan = foundLethal ? plan! : best.Plan;
            PureCombatState resolvedState = foundLethal ? finalState! : best.FinalState!;
            results[enemyState.Source] = BuildLethalResult(hand, resolvedPlan, initialState, resolvedState, enemyIndex, counter.ExploredNodes);
        }

        return results;
    }

    private static MathPredictionEngine.LethalResult BuildLethalResult(
        IReadOnlyList<CardModel> hand,
        IReadOnlyList<PurePlayOption> plan,
        PureCombatState initialState,
        PureCombatState finalState,
        int enemyIndex,
        int exploredNodes)
    {
        IReadOnlyList<CardModel> cards = plan
            .Select(play => hand[play.CardIndex])
            .Distinct()
            .ToList();

        PureEnemyState initialEnemy = initialState.Enemies[enemyIndex];
        PureEnemyState finalEnemy = finalState.Enemies[enemyIndex];
        return new MathPredictionEngine.LethalResult(
            initialEnemy.Source,
            cards,
            finalState.EnergySpent,
            finalState.StarsSpent,
            CalculateAttackValue(initialEnemy, finalEnemy),
            GetDisplayedRemainingHp(finalEnemy),
            GetDisplayedOverflowValue(finalEnemy),
            WillDieBeforeActing(finalEnemy),
            plan.Any(static play => play.Vulnerable > 0),
            plan.Any(static play => play.Poison > 0),
            exploredNodes);
    }

    public static MathPredictionEngine.DefenseResult? CalculateDefensePlan(CombatState combatState, Player player)
    {
        if (combatState.CurrentSide != CombatSide.Player)
        {
            return null;
        }

        int incomingHpLoss = MathPredictionEngine.CalculateIncomingDamage(combatState, player).HpLoss;
        if (incomingHpLoss <= 0)
        {
            return null;
        }

        IReadOnlyList<CardModel> hand = player.PlayerCombatState?.Hand.Cards ?? Array.Empty<CardModel>();
        PureCombatState initialState = CreateInitialState(combatState, player);
        if (hand.Count == 0)
        {
            return BuildDefenseResult(hand, Array.Empty<PurePlayOption>(), initialState, initialState, incomingHpLoss, exploredNodes: 0);
        }

        List<PurePlayOption> options = BuildPlayOptions(combatState, player, hand);
        SearchCounter counter = new();
        DefenseSearchBest best = new();
        bool foundFullDefense = TryFindDefenseRecursive(
            initialState,
            options,
            new HashSet<int>(),
            new List<PurePlayOption>(),
            counter,
            best,
            out List<PurePlayOption>? plan,
            out PureCombatState? finalState);

        IReadOnlyList<PurePlayOption> resolvedPlan = foundFullDefense ? plan! : best.Plan;
        PureCombatState resolvedState = foundFullDefense ? finalState! : best.FinalState ?? initialState;
        return BuildDefenseResult(hand, resolvedPlan, initialState, resolvedState, incomingHpLoss, counter.ExploredNodes);
    }

    private static MathPredictionEngine.DefenseResult BuildDefenseResult(
        IReadOnlyList<CardModel> hand,
        IReadOnlyList<PurePlayOption> plan,
        PureCombatState initialState,
        PureCombatState finalState,
        int incomingHpLoss,
        int exploredNodes)
    {
        IReadOnlyList<CardModel> cards = plan
            .Select(play => hand[play.CardIndex])
            .Distinct()
            .ToList();
        IReadOnlyList<MathPredictionEngine.DefenseContribution> contributions = CalculateDefenseContributions(plan, initialState, finalState);

        // 防御提示既要覆盖“加了多少格挡”，也要覆盖“通过击杀/减伤少吃了多少伤害”。
        return new MathPredictionEngine.DefenseResult(
            cards,
            finalState.EnergySpent,
            finalState.StarsSpent,
            incomingHpLoss,
            contributions.Sum(static contribution => contribution.Value),
            CalculateProjectedHpLoss(finalState),
            contributions,
            exploredNodes);
    }

    private static List<PurePlayOption> BuildPlayOptions(CombatState combatState, Player player, IReadOnlyList<CardModel> hand)
    {
        List<PurePlayOption> options = new();
        for (int cardIndex = 0; cardIndex < hand.Count; cardIndex++)
        {
            CardModel card = hand[cardIndex];
            foreach ((int? enemyIndex, bool appliesToAllEnemies, Creature? target) in EnumeratePlayTargets(combatState, card))
            {
                if (!CanUseCard(card, target))
                {
                    continue;
                }

                card.DynamicVars.ClearPreview();
                card.UpdateDynamicVarPreview(CardPreviewMode.Normal, target, card.DynamicVars);

                decimal directDamage = GetDirectDamagePreview(card);
                int poison = GetPreviewInt(card, "PoisonPower");
                int vulnerable = GetPreviewInt(card, "VulnerablePower");
                int weak = GetPreviewInt(card, "WeakPower");
                decimal blockGain = GetBlockPreview(card);
                (int energyCost, int starCost) = GetBaseResourceCost(card);

                // 纯计算模式下只保留真正会改变战局的牌，避免空操作把搜索空间撑爆。
                if (directDamage <= 0 && poison <= 0 && vulnerable <= 0 && weak <= 0 && blockGain <= 0)
                {
                    continue;
                }

                options.Add(new PurePlayOption(
                    cardIndex,
                    enemyIndex,
                    appliesToAllEnemies,
                    energyCost,
                    starCost,
                    directDamage,
                    poison,
                    vulnerable,
                    weak,
                    blockGain));
            }
        }

        return options;
    }

    private static PureCombatState CreateInitialState(CombatState combatState, Player player)
    {
        return new PureCombatState
        {
            Enemies = combatState.Enemies
                .Select(enemy => new PureEnemyState
                {
                    Source = enemy,
                    CurrentHp = enemy.CurrentHp,
                    Block = enemy.Block,
                    Poison = enemy.GetPower<PoisonPower>()?.Amount ?? 0,
                    BaseHasVulnerable = (enemy.GetPower<VulnerablePower>()?.Amount ?? 0) > 0,
                    HasVulnerable = (enemy.GetPower<VulnerablePower>()?.Amount ?? 0) > 0,
                    BaseHasWeak = (enemy.GetPower<WeakPower>()?.Amount ?? 0) > 0,
                    HasWeak = (enemy.GetPower<WeakPower>()?.Amount ?? 0) > 0,
                    AttackDamage = GetEnemyAttackDamage(combatState, enemy)
                })
                .ToList(),
            PlayerBlock = player.Creature.Block,
            EnergyLeft = player.PlayerCombatState?.Energy ?? 0,
            StarsLeft = player.PlayerCombatState?.Stars ?? 0,
            CanPayExcessEnergyWithStars = Hook.ShouldPayExcessEnergyCostWithStars(combatState, player),
            EnergySpent = 0,
            StarsSpent = 0
        };
    }

    private static bool TryFindLethalRecursive(
        PureCombatState state,
        IReadOnlyList<PurePlayOption> options,
        HashSet<int> usedCardIndices,
        int targetEnemyIndex,
        List<PurePlayOption> chosen,
        SearchCounter counter,
        LethalSearchBest best,
        out List<PurePlayOption>? result,
        out PureCombatState? finalState)
    {
        result = null;
        finalState = null;
        if (counter.ExploredNodes >= MaxSearchNodesPerEnemy)
        {
            return false;
        }

        counter.ExploredNodes++;
        UpdateBestLethalCandidate(state, targetEnemyIndex, chosen, best);
        if (WillDieBeforeActing(state.Enemies[targetEnemyIndex]))
        {
            result = chosen.ToList();
            finalState = state;
            return true;
        }

        List<(PurePlayOption Option, PureCombatState State, decimal Score)> candidates = new();
        decimal currentMetric = GetEnemySurvivalMetric(state.Enemies[targetEnemyIndex]);
        foreach (PurePlayOption option in options)
        {
            if (usedCardIndices.Contains(option.CardIndex) || !OptionAffectsEnemy(option, targetEnemyIndex))
            {
                continue;
            }

            if (!TryApplyOption(state, option, out PureCombatState? nextState))
            {
                continue;
            }

            PureCombatState appliedState = nextState!;
            // 这里不能再用 decimal.MinValue 表示“已死”，否则做差时会直接溢出，导致整条斩杀搜索被异常打断。
            decimal score = currentMetric - GetEnemySurvivalMetric(appliedState.Enemies[targetEnemyIndex]);
            candidates.Add((option, appliedState, score));
        }

        foreach ((PurePlayOption option, PureCombatState nextState, decimal _) in candidates
                     .OrderBy(candidate => GetEffectiveTotalCost(state, candidate.Option))
                     .ThenByDescending(candidate => candidate.Score)
                     .ThenBy(candidate => candidate.Option.CardIndex)
                     .Take(12))
        {
            usedCardIndices.Add(option.CardIndex);
            chosen.Add(option);
            if (TryFindLethalRecursive(nextState, options, usedCardIndices, targetEnemyIndex, chosen, counter, best, out result, out finalState))
            {
                return true;
            }

            chosen.RemoveAt(chosen.Count - 1);
            usedCardIndices.Remove(option.CardIndex);
        }

        return false;
    }

    private static void UpdateBestLethalCandidate(
        PureCombatState state,
        int targetEnemyIndex,
        IReadOnlyList<PurePlayOption> chosen,
        LethalSearchBest best)
    {
        PureEnemyState enemy = state.Enemies[targetEnemyIndex];
        decimal remainingMetric = GetEnemySurvivalMetric(enemy);
        int effectiveCost = state.EnergySpent + state.StarsSpent;
        bool isBetter = best.FinalState == null
            || remainingMetric < best.RemainingMetric
            || (remainingMetric == best.RemainingMetric && effectiveCost < best.EffectiveCost)
            || (remainingMetric == best.RemainingMetric && effectiveCost == best.EffectiveCost && state.EnergySpent < best.EnergySpent)
            || (remainingMetric == best.RemainingMetric && effectiveCost == best.EffectiveCost && state.EnergySpent == best.EnergySpent && state.StarsSpent < best.StarsSpent)
            || (remainingMetric == best.RemainingMetric && effectiveCost == best.EffectiveCost && state.EnergySpent == best.EnergySpent && state.StarsSpent == best.StarsSpent && chosen.Count < best.Plan.Count);
        if (!isBetter)
        {
            return;
        }

        best.Plan = chosen.ToList();
        best.FinalState = state;
        best.RemainingMetric = remainingMetric;
        best.EffectiveCost = effectiveCost;
        best.EnergySpent = state.EnergySpent;
        best.StarsSpent = state.StarsSpent;
    }

    private static bool TryFindDefenseRecursive(
        PureCombatState state,
        IReadOnlyList<PurePlayOption> options,
        HashSet<int> usedCardIndices,
        List<PurePlayOption> chosen,
        SearchCounter counter,
        DefenseSearchBest best,
        out List<PurePlayOption>? result,
        out PureCombatState? finalState)
    {
        result = null;
        finalState = null;
        if (counter.ExploredNodes >= MaxSearchNodesPerEnemy)
        {
            return false;
        }

        counter.ExploredNodes++;
        UpdateBestDefenseCandidate(state, chosen, best);
        if (CalculateProjectedHpLoss(state) <= 0)
        {
            result = chosen.ToList();
            finalState = state;
            return true;
        }

        List<(PurePlayOption Option, PureCombatState State, decimal Score)> candidates = new();
        int currentHpLoss = CalculateProjectedHpLoss(state);
        foreach (PurePlayOption option in options)
        {
            if (usedCardIndices.Contains(option.CardIndex))
            {
                continue;
            }

            if (!TryApplyOption(state, option, out PureCombatState? nextState))
            {
                continue;
            }

            PureCombatState appliedState = nextState!;
            decimal score = currentHpLoss - CalculateProjectedHpLoss(appliedState);
            candidates.Add((option, appliedState, score));
        }

        foreach ((PurePlayOption option, PureCombatState nextState, decimal _) in candidates
                     .OrderBy(candidate => GetEffectiveTotalCost(state, candidate.Option))
                     .ThenByDescending(candidate => candidate.Score)
                     .ThenBy(candidate => candidate.Option.CardIndex)
                     .Take(12))
        {
            usedCardIndices.Add(option.CardIndex);
            chosen.Add(option);
            if (TryFindDefenseRecursive(nextState, options, usedCardIndices, chosen, counter, best, out result, out finalState))
            {
                return true;
            }

            chosen.RemoveAt(chosen.Count - 1);
            usedCardIndices.Remove(option.CardIndex);
        }

        return false;
    }

    private static void UpdateBestDefenseCandidate(
        PureCombatState state,
        IReadOnlyList<PurePlayOption> chosen,
        DefenseSearchBest best)
    {
        int remainingHpLoss = CalculateProjectedHpLoss(state);
        int effectiveCost = state.EnergySpent + state.StarsSpent;
        bool isBetter = best.FinalState == null
            || remainingHpLoss < best.RemainingHpLoss
            || (remainingHpLoss == best.RemainingHpLoss && effectiveCost < best.EffectiveCost)
            || (remainingHpLoss == best.RemainingHpLoss && effectiveCost == best.EffectiveCost && state.EnergySpent < best.EnergySpent)
            || (remainingHpLoss == best.RemainingHpLoss && effectiveCost == best.EffectiveCost && state.EnergySpent == best.EnergySpent && state.StarsSpent < best.StarsSpent)
            || (remainingHpLoss == best.RemainingHpLoss && effectiveCost == best.EffectiveCost && state.EnergySpent == best.EnergySpent && state.StarsSpent == best.StarsSpent && chosen.Count < best.Plan.Count);
        if (!isBetter)
        {
            return;
        }

        best.Plan = chosen.ToList();
        best.FinalState = state;
        best.RemainingHpLoss = remainingHpLoss;
        best.EffectiveCost = effectiveCost;
        best.EnergySpent = state.EnergySpent;
        best.StarsSpent = state.StarsSpent;
    }

    private static bool TryApplyOption(PureCombatState state, PurePlayOption option, out PureCombatState? nextState)
    {
        nextState = null;
        if (!TrySpendResources(state, option, out int energySpent, out int starsSpent))
        {
            return false;
        }

        PureCombatState clone = state.Clone();
        clone.EnergyLeft -= energySpent;
        clone.StarsLeft -= starsSpent;
        clone.EnergySpent += energySpent;
        clone.StarsSpent += starsSpent;

        if (option.BlockGain > 0)
        {
            clone.PlayerBlock += option.BlockGain;
        }

        if (option.AppliesToAllEnemies)
        {
            for (int enemyIndex = 0; enemyIndex < clone.Enemies.Count; enemyIndex++)
            {
                ApplyEnemyOption(clone.Enemies[enemyIndex], option);
            }
        }
        else if (option.EnemyIndex.HasValue)
        {
            PureEnemyState enemy = clone.Enemies[option.EnemyIndex.Value];
            if (enemy.CurrentHp <= 0)
            {
                return false;
            }

            ApplyEnemyOption(enemy, option);
        }

        nextState = clone;
        return true;
    }

    private static void ApplyEnemyOption(PureEnemyState enemy, PurePlayOption option)
    {
        decimal damage = option.DirectDamage;
        if (!enemy.BaseHasVulnerable && enemy.HasVulnerable)
        {
            damage = ApplyVulnerableMultiplier(damage);
        }

        if (damage > 0)
        {
            decimal blocked = System.Math.Min(enemy.Block, damage);
            enemy.Block -= blocked;
            enemy.CurrentHp -= damage - blocked;
        }

        if (option.Poison > 0)
        {
            enemy.Poison += option.Poison;
        }

        if (option.Vulnerable > 0)
        {
            enemy.HasVulnerable = true;
        }

        if (option.Weak > 0)
        {
            enemy.HasWeak = true;
        }
    }

    private static bool TrySpendResources(PureCombatState state, PurePlayOption option, out int energySpent, out int starsSpent)
    {
        energySpent = System.Math.Min(state.EnergyLeft, option.EnergyCost);
        starsSpent = option.StarCost;
        if (option.EnergyCost > state.EnergyLeft)
        {
            if (!state.CanPayExcessEnergyWithStars)
            {
                return false;
            }

            starsSpent += (option.EnergyCost - state.EnergyLeft) * 2;
        }

        return starsSpent <= state.StarsLeft;
    }

    private static int CalculateProjectedHpLoss(PureCombatState state)
    {
        int totalIncoming = CalculateTotalIncomingDamage(state);
        return System.Math.Max(0, (int)System.Math.Ceiling(totalIncoming - state.PlayerBlock));
    }

    private static int CalculateTotalIncomingDamage(PureCombatState state)
    {
        return state.Enemies
            .Where(static enemy => !WillDieBeforeActing(enemy))
            .Sum(GetEffectiveEnemyAttackDamage);
    }

    private static IReadOnlyList<MathPredictionEngine.DefenseContribution> CalculateDefenseContributions(
        IReadOnlyList<PurePlayOption> plan,
        PureCombatState initialState,
        PureCombatState finalState)
    {
        List<MathPredictionEngine.DefenseContribution> contributions = new();

        int blockGain = plan
            .Where(static play => play.BlockGain > 0)
            .Sum(static play => (int)System.Math.Floor(play.BlockGain));
        if (blockGain > 0)
        {
            (int blockEnergySpent, int blockStarsSpent) = SumContributionSpend(plan.Where(static play => play.BlockGain > 0));
            contributions.Add(new MathPredictionEngine.DefenseContribution("格挡", null, blockEnergySpent, blockStarsSpent, blockGain));
        }

        for (int enemyIndex = 0; enemyIndex < initialState.Enemies.Count; enemyIndex++)
        {
            PureEnemyState initialEnemy = initialState.Enemies[enemyIndex];
            if (WillDieBeforeActing(initialEnemy))
            {
                continue;
            }

            int initialAttackDamage = GetEffectiveEnemyAttackDamage(initialEnemy);
            if (initialAttackDamage <= 0)
            {
                continue;
            }

            PureEnemyState finalEnemy = finalState.Enemies[enemyIndex];
            if (WillDieBeforeActing(finalEnemy))
            {
                IReadOnlyList<PurePlayOption> contributingPlays = plan
                    .Where(play => OptionAffectsEnemy(play, enemyIndex) && (play.DirectDamage > 0 || play.Poison > 0 || play.Vulnerable > 0))
                    .ToList();
                (int killEnergySpent, int killStarsSpent) = SumContributionSpend(contributingPlays);
                contributions.Add(new MathPredictionEngine.DefenseContribution(
                    "击杀",
                    initialEnemy.Source.Name,
                    killEnergySpent,
                    killStarsSpent,
                    initialAttackDamage));
                continue;
            }

            int finalAttackDamage = GetEffectiveEnemyAttackDamage(finalEnemy);
            int weakPrevention = System.Math.Max(0, initialAttackDamage - finalAttackDamage);
            if (weakPrevention <= 0)
            {
                continue;
            }

            IReadOnlyList<PurePlayOption> weakPlays = plan
                .Where(play => play.Weak > 0 && OptionAffectsEnemy(play, enemyIndex))
                .ToList();
            (int weakEnergySpent, int weakStarsSpent) = SumContributionSpend(weakPlays);
            contributions.Add(new MathPredictionEngine.DefenseContribution(
                "虚弱",
                initialEnemy.Source.Name,
                weakEnergySpent,
                weakStarsSpent,
                weakPrevention));
        }

        return contributions
            .OrderBy(static contribution => GetDefenseContributionSortOrder(contribution.Kind))
            .ThenBy(static contribution => contribution.EnergySpent + contribution.StarsSpent)
            .ThenByDescending(static contribution => contribution.Value)
            .ToList();
    }

    private static (int EnergySpent, int StarsSpent) SumContributionSpend(IEnumerable<PurePlayOption> plays)
    {
        int energySpent = 0;
        int starsSpent = 0;
        foreach (PurePlayOption play in plays)
        {
            energySpent += play.EnergyCost;
            starsSpent += play.StarCost;
        }

        return (energySpent, starsSpent);
    }

    private static int GetDefenseContributionSortOrder(string kind)
    {
        return kind switch
        {
            "虚弱" => 0,
            "格挡" => 1,
            "击杀" => 2,
            _ => 99
        };
    }

    private static int CalculateAttackValue(PureEnemyState initialEnemy, PureEnemyState finalEnemy)
    {
        decimal progress = GetEnemySurvivalMetric(initialEnemy) - GetEnemySurvivalMetric(finalEnemy);
        return System.Math.Max(0, (int)System.Math.Ceiling(progress));
    }

    private static int GetDisplayedRemainingHp(PureEnemyState enemy)
    {
        if (WillDieBeforeActing(enemy))
        {
            return 0;
        }

        return System.Math.Max(0, (int)System.Math.Ceiling(enemy.CurrentHp));
    }

    private static int GetDisplayedOverflowValue(PureEnemyState enemy)
    {
        if (!WillDieBeforeActing(enemy))
        {
            return 0;
        }

        return System.Math.Max(0, (int)System.Math.Ceiling(-GetEnemySurvivalMetric(enemy)));
    }

    private static int GetEffectiveEnemyAttackDamage(PureEnemyState enemy)
    {
        if (!enemy.BaseHasWeak && enemy.HasWeak)
        {
            return ApplyWeakMultiplier(enemy.AttackDamage);
        }

        return enemy.AttackDamage;
    }

    private static decimal GetEnemySurvivalMetric(PureEnemyState enemy)
    {
        // 这里把当前血量、格挡和中毒都折算进同一个指标里，
        // 让“先破甲”“挂中毒”“补一刀”的组合也能得到稳定且有限的排序分数。
        return enemy.CurrentHp + enemy.Block - enemy.Poison;
    }

    private static bool WillDieBeforeActing(PureEnemyState enemy)
    {
        if (enemy.CurrentHp <= 0)
        {
            return true;
        }

        return enemy.Poison >= enemy.CurrentHp;
    }

    private static bool OptionAffectsEnemy(PurePlayOption option, int enemyIndex)
    {
        return option.AppliesToAllEnemies || option.EnemyIndex == enemyIndex;
    }

    private static int GetEffectiveTotalCost(PureCombatState state, PurePlayOption option)
    {
        if (!TrySpendResources(state, option, out int energySpent, out int starsSpent))
        {
            return int.MaxValue;
        }

        return energySpent + starsSpent;
    }

    private static IEnumerable<(int? EnemyIndex, bool AppliesToAllEnemies, Creature? Target)> EnumeratePlayTargets(CombatState combatState, CardModel card)
    {
        switch (card.TargetType)
        {
            case TargetType.AnyEnemy:
            case TargetType.RandomEnemy:
                for (int enemyIndex = 0; enemyIndex < combatState.Enemies.Count; enemyIndex++)
                {
                    Creature enemy = combatState.Enemies[enemyIndex];
                    if (enemy.IsAlive)
                    {
                        yield return (enemyIndex, false, enemy);
                    }
                }
                yield break;
            case TargetType.AllEnemies:
                yield return (null, true, combatState.Enemies.FirstOrDefault(static enemy => enemy.IsAlive));
                yield break;
            case TargetType.Self:
            case TargetType.AnyPlayer:
            case TargetType.AnyAlly:
            case TargetType.AllAllies:
                yield return (null, false, card.Owner.Creature);
                yield break;
            default:
                yield return (null, false, null);
                yield break;
        }
    }

    private static bool CanUseCard(CardModel card, Creature? target)
    {
        if (!card.CanPlay(out _, out _))
        {
            return false;
        }

        return card.TargetType switch
        {
            TargetType.AnyEnemy or TargetType.RandomEnemy => target != null && card.CanPlayTargeting(target),
            TargetType.AnyAlly or TargetType.AnyPlayer => target != null && card.CanPlayTargeting(target),
            _ => card.CanPlay()
        };
    }

    private static (int EnergyCost, int StarCost) GetBaseResourceCost(CardModel card)
    {
        return (System.Math.Max(0, card.EnergyCost.GetAmountToSpend()), System.Math.Max(0, card.GetStarCostWithModifiers()));
    }

    private static decimal GetDirectDamagePreview(CardModel card)
    {
        decimal calculatedDamage = GetPreviewDecimal(card, CalculatedDamageVar.defaultName);
        decimal plainDamage = GetPreviewDecimal(card, "Damage");
        decimal ostyDamage = GetPreviewDecimal(card, "OstyDamage");
        return System.Math.Max(calculatedDamage, plainDamage) + ostyDamage;
    }

    private static decimal GetBlockPreview(CardModel card)
    {
        decimal calculatedBlock = GetPreviewDecimal(card, CalculatedBlockVar.defaultName);
        decimal plainBlock = GetPreviewDecimal(card, BlockVar.defaultName);
        return System.Math.Max(calculatedBlock, plainBlock);
    }

    private static int GetPreviewInt(CardModel card, string key)
    {
        return (int)System.Math.Max(0, System.Math.Floor(GetPreviewDecimal(card, key)));
    }

    private static decimal GetPreviewDecimal(CardModel card, string key)
    {
        return card.DynamicVars.TryGetValue(key, out DynamicVar? variable)
            ? variable.PreviewValue
            : 0m;
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

    private static decimal ApplyVulnerableMultiplier(decimal amount)
    {
        return System.Math.Floor(amount * 1.5m);
    }

    private static int ApplyWeakMultiplier(int amount)
    {
        return (int)System.Math.Floor(amount * 0.75m);
    }
}
