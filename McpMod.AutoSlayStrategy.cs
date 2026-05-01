using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace STS2_MCP;

public static partial class McpMod
{
    private static readonly object _autoSlayStrategyLock = new();
    private static AutoSlayStrategyProfile _autoSlayStrategy = AutoSlayStrategyProfile.CreateDefault();
    private static AutoSlayRunStats? _autoSlayRunStats;

    private sealed class AutoSlayStrategyProfile
    {
        public int Version { get; set; } = 1;
        public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
        public string? KnowledgeRoot { get; set; }
        public AutoSlayLearningMetrics Learning { get; set; } = new();
        public AutoSlayWeights Weights { get; set; } = AutoSlayWeights.CreateDefault();

        public static AutoSlayStrategyProfile CreateDefault()
        {
            return new AutoSlayStrategyProfile
            {
                Weights = AutoSlayWeights.CreateDefault()
            };
        }
    }

    private sealed class AutoSlayLearningMetrics
    {
        public int TotalRuns { get; set; }
        public int ErrorRuns { get; set; }
        public int BestFloor { get; set; }
        public double AverageFloor { get; set; }
        public double RecentAverageFloor { get; set; }
        public double WinRate { get; set; }
        public double EarlyLossRate { get; set; }
        public double AverageGoldEnd { get; set; }
        public double AverageDeckSizeEnd { get; set; }
        public double AverageCardsAdded { get; set; }
        public double AveragePotionsUsed { get; set; }
    }

    private sealed class AutoSlayWeights
    {
        public double DamageWeight { get; set; } = 1.0;
        public double BlockWeight { get; set; } = 1.0;
        public double DrawWeight { get; set; } = 1.0;
        public double DebuffWeight { get; set; } = 1.0;
        public double PoisonWeight { get; set; } = 1.0;
        public double PowerWeight { get; set; } = 1.0;
        public double AoeWeight { get; set; } = 1.0;
        public double KillWeight { get; set; } = 1.0;
        public double ZeroCostWeight { get; set; } = 1.0;
        public double ElitePreference { get; set; } = 1.0;
        public double RestPreference { get; set; } = 1.0;
        public double ShopPreference { get; set; } = 1.0;
        public double EventPreference { get; set; } = 1.0;
        public double MonsterPreference { get; set; } = 1.0;
        public double SmithPreference { get; set; } = 1.0;
        public double CardRemovalPreference { get; set; } = 1.0;
        public double ShopRelicPreference { get; set; } = 1.0;
        public double ShopCardPreference { get; set; } = 1.0;
        public double ShopPotionPreference { get; set; } = 1.0;
        public double RewardGoldPreference { get; set; } = 1.0;
        public double RewardPotionPreference { get; set; } = 1.0;
        public double RewardRelicPreference { get; set; } = 1.0;
        public double CardSkipThreshold { get; set; } = 32.0;
        public double RestHealThreshold { get; set; } = 0.55;
        public Dictionary<string, double> LearnedCardBias { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> LearnedNodeBias { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> LearnedShopBias { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> LearnedRestBias { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public static AutoSlayWeights CreateDefault() => new();
    }

    private sealed class AutoSlayRunStats
    {
        public int Iteration { get; init; }
        public string Seed { get; init; } = "";
        public DateTime StartedAtUtc { get; init; }
        public AutoSlayWeights StrategyWeights { get; init; } = AutoSlayWeights.CreateDefault();
        public int HighestAct { get; set; }
        public int HighestFloor { get; set; }
        public string? LastStateType { get; set; }
        public bool SawGameOverOverlay { get; set; }
        public bool ReturnedToMenuAfterRun { get; set; }
        public Dictionary<string, int> MapChoiceCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> RestChoiceCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> ShopPurchaseCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> CardsAdded { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> CardsSkipped { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> RewardClaimCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int PositiveEventChoices { get; set; }
        public int NegativeEventChoices { get; set; }
        public int PotionsUsed { get; set; }
        public int PotionsDiscarded { get; set; }
    }

    private sealed class AutoSlayDeckProfile
    {
        public int DeckSize { get; set; }
        public int AttackCount { get; set; }
        public int SkillCount { get; set; }
        public int PowerCount { get; set; }
        public int CurseCount { get; set; }
        public int StatusCount { get; set; }
        public int ZeroCostCount { get; set; }
        public int BlockCards { get; set; }
        public int DrawCards { get; set; }
        public int PoisonCards { get; set; }
        public int ShivCards { get; set; }
        public int DebuffCards { get; set; }
        public int ScalingCards { get; set; }
        public int PremiumCards { get; set; }
        public int UpgradableCards { get; set; }
        public Dictionary<string, int> CardCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RelicIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public double DeckQualityScore =>
            PremiumCards * 7
            + DrawCards * 4
            + PoisonCards * 4
            + ShivCards * 3
            + BlockCards * 2
            + ScalingCards * 4
            - CurseCount * 10
            - Math.Max(0, DeckSize - 16) * 2;
    }

    private sealed class AutoSlayDecisionContext
    {
        public required AutoSlayWeights Weights { get; init; }
        public required AutoSlayDeckProfile Deck { get; init; }
        public double HpRatio { get; init; }
        public int Gold { get; init; }
        public int Act { get; init; }
        public int Floor { get; init; }
        public int EnemyCount { get; init; }
        public int IncomingDamage { get; init; }
        public int CurrentBlock { get; init; }
        public bool NeedBlock { get; init; }
        public bool IsEliteFight { get; init; }
        public bool IsBossFight { get; init; }
        public int PotionCount { get; init; }
        public int PotionSlotCount { get; init; }
    }

    private sealed class LearningRunEntry
    {
        public int Floor { get; set; }
        public string Result { get; set; } = "unknown";
        public int GoldEnd { get; set; }
        public int DeckSizeEnd { get; set; }
        public int CardsAddedCount { get; set; }
        public int PotionsUsed { get; set; }
        public HashSet<string> CardsAdded { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> MapCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> ShopCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> RestCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static void RefreshAutoSlayStrategyProfile()
    {
        string root = GetAutoSlayLearningRoot();
        string summaryPath = Path.Combine(root, "run-summaries.jsonl");
        string aggregatePath = Path.Combine(root, "aggregate.json");
        string strategyPath = Path.Combine(root, "strategy-profile.json");

        var profile = BuildAutoSlayStrategyProfile(root, summaryPath, aggregatePath);
        File.WriteAllText(strategyPath, JsonSerializer.Serialize(profile, _jsonOptions));

        lock (_autoSlayStrategyLock)
            _autoSlayStrategy = profile;
    }

    private static AutoSlayStrategyProfile BuildAutoSlayStrategyProfile(string root, string summaryPath, string aggregatePath)
    {
        var profile = AutoSlayStrategyProfile.CreateDefault();
        profile.KnowledgeRoot = root;

        if (File.Exists(aggregatePath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(aggregatePath));
                var agg = doc.RootElement;
                profile.Learning.TotalRuns = ReadInt(agg, "total_runs");
                profile.Learning.ErrorRuns = ReadInt(agg, "error_runs");
                profile.Learning.BestFloor = ReadInt(agg, "best_floor");
                profile.Learning.AverageFloor = ReadDouble(agg, "average_floor");
                profile.Learning.RecentAverageFloor = ReadDouble(agg, "recent_average_floor");
                profile.Learning.WinRate = ReadDouble(agg, "win_rate");
                profile.Learning.EarlyLossRate = ReadDouble(agg, "early_loss_rate");
                profile.Learning.AverageGoldEnd = ReadDouble(agg, "average_gold_end");
                profile.Learning.AverageDeckSizeEnd = ReadDouble(agg, "average_deck_size_end");
                profile.Learning.AverageCardsAdded = ReadDouble(agg, "average_cards_added");
                profile.Learning.AveragePotionsUsed = ReadDouble(agg, "average_potions_used");
            }
            catch
            {
                // Ignore malformed aggregate files and fall back to defaults.
            }
        }

        var runs = LoadLearningRuns(summaryPath);
        PopulateLearningMetricsFromRuns(profile.Learning, runs);
        ApplyAggregateDrivenWeightAdjustments(profile.Weights, profile.Learning);
        ApplyRunDrivenWeightAdjustments(profile.Weights, runs, profile.Learning);
        profile.GeneratedAtUtc = DateTime.UtcNow;
        return profile;
    }

    private static List<LearningRunEntry> LoadLearningRuns(string summaryPath)
    {
        var runs = new List<LearningRunEntry>();
        if (!File.Exists(summaryPath))
            return runs;

        foreach (string line in File.ReadLines(summaryPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                int floor = ReadInt(root, "highest_floor", ReadInt(root, "floor"));
                string result = ReadString(root, "result") ?? InferLegacyResult(root, floor);
                var run = new LearningRunEntry
                {
                    Floor = floor,
                    Result = result,
                    GoldEnd = ReadInt(root, "player_gold"),
                    DeckSizeEnd = ReadNestedInt(root, "deck_profile_end", "deck_size"),
                    CardsAddedCount = ReadNestedDictTotal(root, "cards_added"),
                    PotionsUsed = ReadInt(root, "potions_used")
                };

                foreach (string id in ReadNestedDictKeys(root, "cards_added"))
                    run.CardsAdded.Add(id);

                CopyJsonCounts(root, "map_choice_counts", run.MapCounts);
                CopyJsonCounts(root, "shop_purchase_counts", run.ShopCounts);
                CopyJsonCounts(root, "rest_choice_counts", run.RestCounts);
                runs.Add(run);
            }
            catch
            {
                // Ignore malformed lines.
            }
        }

        return runs;
    }

    private static string InferLegacyResult(JsonElement root, int floor)
    {
        string? overlay = ReadString(root, "final_overlay");
        if (string.Equals(overlay, "NGameOverScreen", StringComparison.OrdinalIgnoreCase))
            return "loss";

        string? finalState = ReadString(root, "final_state_type");
        if (string.Equals(finalState, "menu", StringComparison.OrdinalIgnoreCase) && floor >= 45)
            return "win";

        return "unknown";
    }

    private static void PopulateLearningMetricsFromRuns(AutoSlayLearningMetrics learning, List<LearningRunEntry> runs)
    {
        if (runs.Count == 0)
            return;

        learning.TotalRuns = Math.Max(learning.TotalRuns, runs.Count);
        learning.BestFloor = Math.Max(learning.BestFloor, runs.Max(run => run.Floor));
        learning.AverageFloor = Math.Round(runs.Average(run => run.Floor), 2);
        learning.RecentAverageFloor = Math.Round(runs.TakeLast(Math.Min(20, runs.Count)).Average(run => run.Floor), 2);
        learning.WinRate = Math.Round(runs.Count(run => string.Equals(run.Result, "win", StringComparison.OrdinalIgnoreCase)) / (double)runs.Count, 4);
        learning.EarlyLossRate = Math.Round(runs.Count(run =>
            string.Equals(run.Result, "loss", StringComparison.OrdinalIgnoreCase) && run.Floor <= 10) / (double)runs.Count, 4);

        if (runs.Any(run => run.GoldEnd > 0))
            learning.AverageGoldEnd = Math.Round(runs.Where(run => run.GoldEnd > 0).Average(run => run.GoldEnd), 2);
        if (runs.Any(run => run.DeckSizeEnd > 0))
            learning.AverageDeckSizeEnd = Math.Round(runs.Where(run => run.DeckSizeEnd > 0).Average(run => run.DeckSizeEnd), 2);

        learning.AverageCardsAdded = Math.Round(runs.Average(run => run.CardsAddedCount), 2);
        learning.AveragePotionsUsed = Math.Round(runs.Average(run => run.PotionsUsed), 2);
    }

    private static void ApplyAggregateDrivenWeightAdjustments(AutoSlayWeights weights, AutoSlayLearningMetrics learning)
    {
        if (learning.TotalRuns <= 0)
            return;

        if (learning.EarlyLossRate >= 0.45)
        {
            weights.BlockWeight += 0.22;
            weights.RestPreference += 0.25;
            weights.ElitePreference -= 0.22;
            weights.RestHealThreshold = Math.Min(0.72, weights.RestHealThreshold + 0.08);
        }

        if (learning.AverageDeckSizeEnd >= 17)
        {
            weights.CardSkipThreshold += 6;
            weights.CardRemovalPreference += 0.28;
            weights.ShopCardPreference -= 0.08;
        }
        else if (learning.AverageDeckSizeEnd > 0 && learning.AverageDeckSizeEnd <= 12 && learning.AverageFloor < 10)
        {
            weights.CardSkipThreshold -= 3;
            weights.ShopCardPreference += 0.06;
        }

        if (learning.AverageGoldEnd >= 120)
        {
            weights.ShopPreference += 0.18;
            weights.ShopRelicPreference += 0.14;
            weights.ShopCardPreference += 0.10;
        }

        if (learning.RecentAverageFloor >= learning.AverageFloor + 3)
        {
            weights.ElitePreference += 0.10;
            weights.SmithPreference += 0.12;
            weights.RestHealThreshold = Math.Max(0.45, weights.RestHealThreshold - 0.04);
        }
        else if (learning.RecentAverageFloor > 0 && learning.RecentAverageFloor <= learning.AverageFloor - 3)
        {
            weights.BlockWeight += 0.12;
            weights.RestPreference += 0.12;
        }

        if (learning.BestFloor >= 30 || learning.WinRate > 0.05)
        {
            weights.ElitePreference += 0.08;
            weights.PowerWeight += 0.10;
            weights.SmithPreference += 0.10;
        }
    }

    private static void ApplyRunDrivenWeightAdjustments(
        AutoSlayWeights weights,
        List<LearningRunEntry> runs,
        AutoSlayLearningMetrics learning)
    {
        if (runs.Count < 4)
            return;

        var topRuns = runs
            .OrderByDescending(run => run.Floor)
            .Take(Math.Max(3, runs.Count / 4))
            .ToList();

        double avgFloor = learning.AverageFloor > 0 ? learning.AverageFloor : runs.Average(run => run.Floor);

        foreach (var cardGroup in runs
                     .SelectMany(run => run.CardsAdded.Select(cardId => new { run.Floor, CardId = cardId, IsTop = topRuns.Contains(run) }))
                     .GroupBy(item => item.CardId, StringComparer.OrdinalIgnoreCase))
        {
            double withCardAvg = cardGroup.Average(item => item.Floor);
            double allRate = runs.Count == 0 ? 0 : runs.Count(run => run.CardsAdded.Contains(cardGroup.Key)) / (double)runs.Count;
            double topRate = topRuns.Count == 0 ? 0 : topRuns.Count(run => run.CardsAdded.Contains(cardGroup.Key)) / (double)topRuns.Count;
            double bias = Math.Clamp((withCardAvg - avgFloor) * 1.5 + (topRate - allRate) * 20.0, -18.0, 18.0);
            if (Math.Abs(bias) >= 1.0)
                weights.LearnedCardBias[cardGroup.Key] = Math.Round(bias, 2);
        }

        ApplyCountBias(weights.LearnedNodeBias, runs, topRuns, run => run.MapCounts, 8.0, 12.0);
        ApplyCountBias(weights.LearnedShopBias, runs, topRuns, run => run.ShopCounts, 10.0, 14.0);
        ApplyCountBias(weights.LearnedRestBias, runs, topRuns, run => run.RestCounts, 10.0, 14.0);
    }

    private static void ApplyCountBias(
        Dictionary<string, double> destination,
        List<LearningRunEntry> runs,
        List<LearningRunEntry> topRuns,
        Func<LearningRunEntry, Dictionary<string, int>> selector,
        double multiplier,
        double clamp)
    {
        foreach (string key in runs.SelectMany(run => selector(run).Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            double allAvg = runs.Average(run => selector(run).GetValueOrDefault(key));
            double topAvg = topRuns.Average(run => selector(run).GetValueOrDefault(key));
            double bias = Math.Clamp((topAvg - allAvg) * multiplier, -clamp, clamp);
            if (Math.Abs(bias) >= 0.5)
                destination[key] = Math.Round(bias, 2);
        }
    }

    private static AutoSlayDecisionContext BuildAutoDecisionContext(
        Dictionary<string, object?> snapshot,
        Dictionary<string, object?>? player,
        Dictionary<string, object?>? battle,
        string? stateType)
    {
        var weights = GetAutoSlayStrategyWeights();
        var deck = BuildDeckProfile(player);
        var run = GetDict(snapshot, "run");
        int hp = player != null ? GetInt(player, "hp") : 0;
        int maxHp = player != null ? Math.Max(1, GetInt(player, "max_hp")) : 1;

        return new AutoSlayDecisionContext
        {
            Weights = weights,
            Deck = deck,
            HpRatio = hp / (double)maxHp,
            Gold = player != null ? GetInt(player, "gold") : 0,
            Act = run != null ? GetInt(run, "act") : 0,
            Floor = run != null ? GetInt(run, "floor") : 0,
            EnemyCount = battle != null ? GetDictList(battle, "enemies").Count : 0,
            IncomingDamage = battle != null ? EstimateIncomingDamage(battle) : 0,
            CurrentBlock = player != null ? GetInt(player, "block") : 0,
            NeedBlock = battle != null && player != null && EstimateIncomingDamage(battle) > GetInt(player, "block"),
            IsEliteFight = string.Equals(stateType, "elite", StringComparison.OrdinalIgnoreCase),
            IsBossFight = string.Equals(stateType, "boss", StringComparison.OrdinalIgnoreCase),
            PotionCount = player != null ? GetDictList(player, "potions").Count : 0,
            PotionSlotCount = player != null ? Math.Max(1, GetInt(player, "potion_slot_count")) : 1
        };
    }

    private static AutoSlayDeckProfile BuildDeckProfile(Dictionary<string, object?>? player)
    {
        var profile = new AutoSlayDeckProfile();
        if (player == null)
            return profile;

        foreach (string relicId in GetDictList(player, "relics").Select(relic => GetString(relic, "id")).Where(id => !string.IsNullOrWhiteSpace(id))!)
            profile.RelicIds.Add(relicId!);

        var deckCards = GetDictList(player, "deck");
        if (deckCards.Count == 0)
        {
            deckCards = GetDictList(player, "hand")
                .Concat(GetDictList(player, "draw_pile"))
                .Concat(GetDictList(player, "discard_pile"))
                .Concat(GetDictList(player, "exhaust_pile"))
                .ToList();
        }

        foreach (var card in deckCards)
        {
            string id = GetString(card, "id") ?? GetString(card, "name") ?? "";
            string type = GetString(card, "type") ?? "";
            string description = (GetString(card, "description") ?? "").ToLowerInvariant();
            string cost = GetString(card, "cost") ?? "";

            profile.DeckSize++;
            profile.CardCounts[id] = profile.CardCounts.GetValueOrDefault(id) + 1;

            switch (type)
            {
                case "Attack":
                    profile.AttackCount++;
                    break;
                case "Skill":
                    profile.SkillCount++;
                    break;
                case "Power":
                    profile.PowerCount++;
                    break;
                case "Curse":
                    profile.CurseCount++;
                    break;
                case "Status":
                    profile.StatusCount++;
                    break;
            }

            if (!GetBool(card, "is_upgraded"))
                profile.UpgradableCards++;

            if (cost == "0")
                profile.ZeroCostCount++;
            if (ContainsAny(description, "格挡", "block"))
                profile.BlockCards++;
            if (ContainsAny(description, "抽", "draw"))
                profile.DrawCards++;
            if (ContainsAny(description, "中毒", "poison"))
                profile.PoisonCards++;
            if (ContainsAny(description, "shiv", "飞刀", "小刀"))
                profile.ShivCards++;
            if (ContainsAny(description, "虚弱", "易伤", "weak", "vulnerable", "frail"))
                profile.DebuffCards++;
            if (type == "Power" || ContainsAny(description, "每回合", "at the start", "永久", "每个回合", "retained"))
                profile.ScalingCards++;
            if (_cardPickupScores.GetValueOrDefault(id, 0) >= 80)
                profile.PremiumCards++;
        }

        return profile;
    }

    private static AutoSlayWeights GetAutoSlayStrategyWeights()
    {
        lock (_autoSlayStrategyLock)
            return _autoSlayStrategy.Weights;
    }

    private static Dictionary<string, object?> BuildDeckProfileSummary(AutoSlayDeckProfile profile)
    {
        return new Dictionary<string, object?>
        {
            ["deck_size"] = profile.DeckSize,
            ["attack_count"] = profile.AttackCount,
            ["skill_count"] = profile.SkillCount,
            ["power_count"] = profile.PowerCount,
            ["curse_count"] = profile.CurseCount,
            ["status_count"] = profile.StatusCount,
            ["zero_cost_count"] = profile.ZeroCostCount,
            ["block_cards"] = profile.BlockCards,
            ["draw_cards"] = profile.DrawCards,
            ["poison_cards"] = profile.PoisonCards,
            ["shiv_cards"] = profile.ShivCards,
            ["debuff_cards"] = profile.DebuffCards,
            ["scaling_cards"] = profile.ScalingCards,
            ["premium_cards"] = profile.PremiumCards,
            ["upgradable_cards"] = profile.UpgradableCards,
            ["deck_quality_score"] = Math.Round(profile.DeckQualityScore, 2)
        };
    }

    private static Dictionary<string, object?> BuildWeightsSummary(AutoSlayWeights weights)
    {
        return new Dictionary<string, object?>
        {
            ["damage_weight"] = weights.DamageWeight,
            ["block_weight"] = weights.BlockWeight,
            ["draw_weight"] = weights.DrawWeight,
            ["debuff_weight"] = weights.DebuffWeight,
            ["poison_weight"] = weights.PoisonWeight,
            ["power_weight"] = weights.PowerWeight,
            ["aoe_weight"] = weights.AoeWeight,
            ["kill_weight"] = weights.KillWeight,
            ["zero_cost_weight"] = weights.ZeroCostWeight,
            ["elite_preference"] = weights.ElitePreference,
            ["rest_preference"] = weights.RestPreference,
            ["shop_preference"] = weights.ShopPreference,
            ["event_preference"] = weights.EventPreference,
            ["monster_preference"] = weights.MonsterPreference,
            ["smith_preference"] = weights.SmithPreference,
            ["card_removal_preference"] = weights.CardRemovalPreference,
            ["shop_relic_preference"] = weights.ShopRelicPreference,
            ["shop_card_preference"] = weights.ShopCardPreference,
            ["shop_potion_preference"] = weights.ShopPotionPreference,
            ["card_skip_threshold"] = weights.CardSkipThreshold,
            ["rest_heal_threshold"] = weights.RestHealThreshold,
            ["learned_card_bias"] = weights.LearnedCardBias,
            ["learned_node_bias"] = weights.LearnedNodeBias,
            ["learned_shop_bias"] = weights.LearnedShopBias,
            ["learned_rest_bias"] = weights.LearnedRestBias
        };
    }

    private static void BeginAutoSlayRunStats(int iteration, string seed, DateTime startedAtUtc)
    {
        var weights = GetAutoSlayStrategyWeights();
        _autoSlayRunStats = new AutoSlayRunStats
        {
            Iteration = iteration,
            Seed = seed,
            StartedAtUtc = startedAtUtc,
            StrategyWeights = weights
        };
    }

    private static void UpdateAutoSlayRunProgress(Dictionary<string, object?> snapshot)
    {
        var stats = _autoSlayRunStats;
        if (stats == null)
            return;

        stats.LastStateType = GetString(snapshot, "state_type");
        var run = GetDict(snapshot, "run");
        if (run != null)
        {
            stats.HighestAct = Math.Max(stats.HighestAct, GetInt(run, "act"));
            stats.HighestFloor = Math.Max(stats.HighestFloor, GetInt(run, "floor"));
        }
    }

    private static void MarkAutoSlayGameOver() { if (_autoSlayRunStats != null) _autoSlayRunStats.SawGameOverOverlay = true; }
    private static void MarkAutoSlayReturnedToMenu() { if (_autoSlayRunStats != null) _autoSlayRunStats.ReturnedToMenuAfterRun = true; }

    private static void RecordAutoSlayMapChoice(string nodeType)
    {
        if (_autoSlayRunStats == null || string.IsNullOrWhiteSpace(nodeType))
            return;
        _autoSlayRunStats.MapChoiceCounts[nodeType] = _autoSlayRunStats.MapChoiceCounts.GetValueOrDefault(nodeType) + 1;
    }

    private static void RecordAutoSlayRestChoice(string optionId)
    {
        if (_autoSlayRunStats == null || string.IsNullOrWhiteSpace(optionId))
            return;
        _autoSlayRunStats.RestChoiceCounts[optionId] = _autoSlayRunStats.RestChoiceCounts.GetValueOrDefault(optionId) + 1;
    }

    private static void RecordAutoSlayShopPurchase(string key)
    {
        if (_autoSlayRunStats == null || string.IsNullOrWhiteSpace(key))
            return;
        _autoSlayRunStats.ShopPurchaseCounts[key] = _autoSlayRunStats.ShopPurchaseCounts.GetValueOrDefault(key) + 1;
    }

    private static void RecordAutoSlayCardAdded(string cardId)
    {
        if (_autoSlayRunStats == null || string.IsNullOrWhiteSpace(cardId))
            return;
        _autoSlayRunStats.CardsAdded[cardId] = _autoSlayRunStats.CardsAdded.GetValueOrDefault(cardId) + 1;
    }

    private static void RecordAutoSlayCardSkipped(string screenType)
    {
        if (_autoSlayRunStats == null)
            return;
        string key = string.IsNullOrWhiteSpace(screenType) ? "unknown" : screenType;
        _autoSlayRunStats.CardsSkipped[key] = _autoSlayRunStats.CardsSkipped.GetValueOrDefault(key) + 1;
    }

    private static void RecordAutoSlayRewardClaim(string rewardType)
    {
        if (_autoSlayRunStats == null || string.IsNullOrWhiteSpace(rewardType))
            return;
        _autoSlayRunStats.RewardClaimCounts[rewardType] = _autoSlayRunStats.RewardClaimCounts.GetValueOrDefault(rewardType) + 1;
    }

    private static void RecordAutoSlayEventChoice(bool positive)
    {
        if (_autoSlayRunStats == null)
            return;
        if (positive)
            _autoSlayRunStats.PositiveEventChoices++;
        else
            _autoSlayRunStats.NegativeEventChoices++;
    }

    private static void RecordAutoSlayPotionUsed()
    {
        if (_autoSlayRunStats != null)
            _autoSlayRunStats.PotionsUsed++;
    }

    private static void RecordAutoSlayPotionDiscarded()
    {
        if (_autoSlayRunStats != null)
            _autoSlayRunStats.PotionsDiscarded++;
    }

    private static string DetermineAutoSlayResult(Dictionary<string, object?> snapshot)
    {
        if (_autoSlayRunStats?.SawGameOverOverlay == true)
            return "loss";

        var overlay = GetDict(snapshot, "overlay");
        string? overlayType = overlay != null ? GetString(overlay, "screen_type") : null;
        if (string.Equals(overlayType, "NGameOverScreen", StringComparison.OrdinalIgnoreCase))
            return "loss";

        if (_autoSlayRunStats?.ReturnedToMenuAfterRun == true && _autoSlayRunStats.HighestFloor >= 45)
            return "win";

        if (_autoSlayRunStats?.ReturnedToMenuAfterRun == true)
            return "run_end";

        return "unknown";
    }

    private static int ReadInt(JsonElement element, string propertyName, int fallback = 0)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out int parsed) => parsed,
            JsonValueKind.String when int.TryParse(value.GetString(), out int parsed) => parsed,
            _ => fallback
        };
    }

    private static double ReadDouble(JsonElement element, string propertyName, double fallback = 0)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out double parsed) => parsed,
            JsonValueKind.String when double.TryParse(value.GetString(), out double parsed) => parsed,
            _ => fallback
        };
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        return value.GetString();
    }

    private static int ReadNestedInt(JsonElement element, string objectName, string propertyName)
    {
        if (!element.TryGetProperty(objectName, out var obj) || obj.ValueKind != JsonValueKind.Object)
            return 0;
        return ReadInt(obj, propertyName);
    }

    private static int ReadNestedDictTotal(JsonElement element, string objectName)
    {
        if (!element.TryGetProperty(objectName, out var obj) || obj.ValueKind != JsonValueKind.Object)
            return 0;
        int total = 0;
        foreach (var property in obj.EnumerateObject())
            total += property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out int count) ? count : 0;
        return total;
    }

    private static IEnumerable<string> ReadNestedDictKeys(JsonElement element, string objectName)
    {
        if (!element.TryGetProperty(objectName, out var obj) || obj.ValueKind != JsonValueKind.Object)
            return Array.Empty<string>();
        return obj.EnumerateObject().Select(property => property.Name).ToList();
    }

    private static void CopyJsonCounts(JsonElement element, string objectName, Dictionary<string, int> destination)
    {
        if (!element.TryGetProperty(objectName, out var obj) || obj.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in obj.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out int count))
                destination[property.Name] = count;
        }
    }
}
