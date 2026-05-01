using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public static partial class McpMod
{
    private static readonly object _autoSlayLock = new();
    private static readonly JsonSerializerOptions _jsonLineOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static CancellationTokenSource? _autoSlayLoopCts;
    private static Task? _autoSlayLoopTask;
    private static int _autoSlayIteration;
    private static string? _autoSlayCurrentSeed;
    private static string? _autoSlayCurrentLogFile;
    private static string? _autoSlayLastError;
    private static DateTime? _autoSlayStartedAtUtc;
    private static DateTime? _autoSlayLastRunStartedAtUtc;
    private static int _autoSlayTargetAscension;
    private static int _autoSlayUnlockedMaxAscension;
    private static string? _autoSlayProgressSavePath;

    private const string AutoSlayCharacterId = "CHARACTER.SILENT";
    private const int AutoSlayFallbackAscension = 0;

    private static readonly Regex _numberRegex = new(@"\d+", RegexOptions.Compiled);
    private static readonly HashSet<string> _defensiveIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "DEFEND_SILENT", "DEFLECT", "FOOTWORK", "CLOAK_AND_DAGGER", "BLUR", "DODGE_AND_ROLL",
        "BACKFLIP", "LEG_SWEEP", "PIERCING_WAIL", "WRAITH_FORM", "UNTOUCHABLE", "ESCAPE_PLAN",
        "WELL_LAID_PLANS", "AFTERIMAGE", "CRIPPLING_CLOUD", "MALAISE", "GHOSTLY_ARMOR"
    };

    private static readonly Dictionary<string, int> _cardPickupScores = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ADRENALINE"] = 120,
        ["WRAITH_FORM"] = 115,
        ["FOOTWORK"] = 105,
        ["PIERCING_WAIL"] = 100,
        ["MALAISE"] = 98,
        ["BACKFLIP"] = 90,
        ["NOXIOUS_FUMES"] = 90,
        ["BLADE_DANCE"] = 88,
        ["CLOAK_AND_DAGGER"] = 82,
        ["LEG_SWEEP"] = 82,
        ["DEADLY_POISON"] = 76,
        ["PREDATOR"] = 74,
        ["DAGGER_SPRAY"] = 72,
        ["CRIPPLING_CLOUD"] = 72,
        ["DEFLECT"] = 68,
        ["DODGE_AND_ROLL"] = 66,
        ["ACROBATICS"] = 66,
        ["EXPERTISE"] = 62,
        ["LEADING_STRIKE"] = 60,
        ["SNAKEBITE"] = 60,
        ["MEMENTO_MORI"] = 58,
        ["CATALYST"] = 58,
        ["ACCURACY"] = 48,
        ["INFINITE_BLADES"] = 42
    };

    private static Dictionary<string, object?> ExecuteAutoSlayStartLoop(Dictionary<string, JsonElement> data)
    {
        lock (_autoSlayLock)
        {
            if (_autoSlayLoopTask is { IsCompleted: false })
                return Error("AutoSlay loop is already running");

            string? seed = null;
            if (data.TryGetValue("seed", out var seedElem) && seedElem.ValueKind == JsonValueKind.String)
                seed = seedElem.GetString();

            _autoSlayLoopCts = new CancellationTokenSource();
            _autoSlayLoopTask = Task.Run(() => AutoSlayLoopAsync(seed, _autoSlayLoopCts.Token));
            _autoSlayStartedAtUtc = DateTime.UtcNow;
            _autoSlayIteration = 0;
            _autoSlayCurrentSeed = null;
            _autoSlayCurrentLogFile = null;
            _autoSlayLastError = null;
            _autoSlayTargetAscension = AutoSlayFallbackAscension;
            _autoSlayUnlockedMaxAscension = AutoSlayFallbackAscension;
            _autoSlayProgressSavePath = null;
        }

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "AutoSlay loop started"
        };
    }

    private static Dictionary<string, object?> ExecuteAutoSlayStop()
    {
        CancellationTokenSource? cts;

        lock (_autoSlayLock)
        {
            cts = _autoSlayLoopCts;
            _autoSlayLoopCts = null;
        }

        cts?.Cancel();

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "AutoSlay loop stop requested"
        };
    }

    private static async Task AutoSlayLoopAsync(string? initialSeed, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int iteration;
                string seed;
                string logFile;
                DateTime runStartedAtUtc = DateTime.UtcNow;
                Dictionary<string, object?>? finalSnapshot = null;
                bool runStarted = false;

                lock (_autoSlayLock)
                {
                    iteration = ++_autoSlayIteration;
                    seed = ResolveAutoSlaySeed(initialSeed, iteration);
                    logFile = BuildAutoSlayLogFilePath(iteration, seed, runStartedAtUtc);
                    _autoSlayCurrentSeed = seed;
                    _autoSlayCurrentLogFile = logFile;
                    _autoSlayLastRunStartedAtUtc = runStartedAtUtc;
                    _autoSlayLastError = null;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
                RefreshAutoSlayStrategyProfile();
                BeginAutoSlayRunStats(iteration, seed, runStartedAtUtc);
                AppendAutoSlayTrace($"[{DateTime.UtcNow:O}] Starting fair auto-run iteration {iteration} with seed={seed}");

                while (!ct.IsCancellationRequested)
                {
                    await RecoverUiForAutoSlayAsync(ct);

                    var snapshot = await RunOnMainThread(BuildGameState);
                    finalSnapshot = snapshot;
                    UpdateAutoSlayRunProgress(snapshot);
                    string stateType = GetString(snapshot, "state_type") ?? "unknown";

                    if (stateType == "menu")
                    {
                        if (!runStarted)
                        {
                            await StartFairSingleplayerRunAsync(seed, ct);
                            runStarted = true;
                            AppendAutoSlayTrace($"[{DateTime.UtcNow:O}] Started new singleplayer run");
                            await Task.Delay(400, ct);
                            continue;
                        }

                        MarkAutoSlayReturnedToMenu();
                        AppendAutoSlayTrace($"[{DateTime.UtcNow:O}] Run returned to menu");
                        break;
                    }

                    runStarted = true;

                    if (IsGameOverOverlay(snapshot))
                    {
                        MarkAutoSlayGameOver();
                        AppendAutoSlayTrace($"[{DateTime.UtcNow:O}] Run ended on game over overlay");
                        break;
                    }

                    string stateFingerprint = BuildAutoStepFingerprint(snapshot);
                    bool acted = await RunOnMainThread(() => ExecuteFairAutoRunStep(snapshot));
                    if (!acted)
                    {
                        AppendAutoSlayTrace($"[{DateTime.UtcNow:O}] No action taken for state_type={stateType}");
                        await Task.Delay(250, ct);
                    }
                    else
                    {
                        await WaitForAutoStateAdvanceAsync(stateFingerprint, ct);
                    }
                }

                finalSnapshot ??= await RunOnMainThread(BuildGameState);
                AppendAutoSlayRunSummary(
                    iteration,
                    seed,
                    logFile,
                    runStartedAtUtc,
                    DateTime.UtcNow,
                    finalSnapshot,
                    null);

                await RecoverUiForAutoSlayAsync(ct);
                await Task.Delay(500, ct);
            }
        }
        catch (OperationCanceledException)
        {
            AppendAutoSlayTrace($"[{DateTime.UtcNow:O}] Auto loop cancelled");
        }
        catch (Exception ex)
        {
            lock (_autoSlayLock)
                _autoSlayLastError = ex.ToString();

            AppendAutoSlayTrace($"[{DateTime.UtcNow:O}] Auto loop crashed: {ex}");

            try
            {
                var snapshot = await RunOnMainThread(BuildGameState);
                AppendAutoSlayRunSummary(
                    _autoSlayIteration,
                    _autoSlayCurrentSeed ?? "",
                    _autoSlayCurrentLogFile,
                    _autoSlayLastRunStartedAtUtc ?? DateTime.UtcNow,
                    DateTime.UtcNow,
                    snapshot,
                    ex.ToString());
            }
            catch (Exception nestedEx)
            {
                GD.PrintErr($"[STS2 MCP] Failed to record AutoSlay crash summary: {nestedEx}");
            }

            GD.PrintErr($"[STS2 MCP] AutoSlay loop crashed: {ex}");
        }
        finally
        {
            lock (_autoSlayLock)
            {
                _autoSlayLoopTask = null;
                _autoSlayLoopCts = null;
            }
        }
    }

    private static async Task StartFairSingleplayerRunAsync(string seed, CancellationToken ct)
    {
        await WaitUntilAsync(() => RunOnMainThread(() => NGame.Instance != null), ct);
        int ascension = ResolveAutoSlayTargetAscension();

        AppendAutoSlayTrace(
            $"[{DateTime.UtcNow:O}] Starting Silent run at ascension={ascension} progress_save={_autoSlayProgressSavePath ?? "n/a"}");

        var startTask = await RunOnMainThread(() =>
        {
            if (NGame.Instance == null)
                return Task.FromException<RunState>(new InvalidOperationException("NGame is not ready"));

            var acts = new List<ActModel>
            {
                ModelDb.Act<Underdocks>(),
                ModelDb.Act<Overgrowth>(),
                ModelDb.Act<Hive>()
            };

            return NGame.Instance.StartNewSingleplayerRun(
                ModelDb.Character<Silent>(),
                true,
                acts,
                Array.Empty<ModifierModel>(),
                seed,
                GameMode.Standard,
                ascension,
                null);
        });

        await startTask;
        await WaitUntilAsync(() => RunOnMainThread(() => RunManager.Instance.IsInProgress), ct);
    }

    private static int ResolveAutoSlayTargetAscension()
    {
        string? progressPath = FindAutoSlayProgressSavePath();
        int resolvedAscension = AutoSlayFallbackAscension;
        int unlockedMaxAscension = AutoSlayFallbackAscension;

        if (!string.IsNullOrWhiteSpace(progressPath) && File.Exists(progressPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(progressPath));
                if (doc.RootElement.TryGetProperty("character_stats", out var statsElem)
                    && statsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var stat in statsElem.EnumerateArray())
                    {
                        if (!string.Equals(ReadString(stat, "id"), AutoSlayCharacterId, StringComparison.OrdinalIgnoreCase))
                            continue;

                        unlockedMaxAscension = Math.Max(AutoSlayFallbackAscension, ReadInt(stat, "max_ascension"));
                        int preferredAscension = Math.Max(unlockedMaxAscension, ReadInt(stat, "preferred_ascension", unlockedMaxAscension));
                        resolvedAscension = Math.Max(unlockedMaxAscension, preferredAscension);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[STS2 MCP] Failed to read AutoSlay progress.save: {ex.Message}");
            }
        }

        lock (_autoSlayLock)
        {
            _autoSlayTargetAscension = resolvedAscension;
            _autoSlayUnlockedMaxAscension = unlockedMaxAscension;
            _autoSlayProgressSavePath = progressPath;
        }

        return resolvedAscension;
    }

    private static string? FindAutoSlayProgressSavePath()
    {
        string root = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "SlayTheSpire2");
        if (!Directory.Exists(root))
            return null;

        try
        {
            string moddedMarker = $"{Path.DirectorySeparatorChar}modded{Path.DirectorySeparatorChar}";
            return Directory
                .EnumerateFiles(root, "progress.save", SearchOption.AllDirectories)
                .OrderByDescending(path => path.Contains(moddedMarker, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(path => File.GetLastWriteTimeUtc(path))
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static bool ExecuteFairAutoRunStep(Dictionary<string, object?> snapshot)
    {
        string stateType = GetString(snapshot, "state_type") ?? "unknown";
        return stateType switch
        {
            "monster" or "elite" or "boss" => HandleCombatState(snapshot, stateType),
            "map" => HandleMapState(snapshot),
            "event" => HandleEventState(snapshot),
            "rest_site" => HandleRestSiteState(snapshot),
            "shop" or "fake_merchant" => HandleShopState(snapshot, stateType),
            "rewards" => HandleRewardsState(snapshot),
            "card_reward" => HandleCardRewardState(snapshot),
            "card_select" => HandleCardSelectState(snapshot),
            "hand_select" => HandleHandSelectState(snapshot),
            "bundle_select" => HandleBundleSelectState(snapshot),
            "relic_select" => HandleRelicSelectState(snapshot),
            "treasure" => HandleTreasureState(snapshot),
            "crystal_sphere" => HandleCrystalSphereState(snapshot),
            _ => false
        };
    }

    private static bool HandleCombatState(Dictionary<string, object?> snapshot, string stateType)
    {
        var player = GetDict(snapshot, "player");
        var battle = GetDict(snapshot, "battle");
        if (player == null || battle == null)
            return false;

        if (!GetBool(battle, "is_play_phase"))
            return false;

        var hand = GetDictList(player, "hand");
        if (hand.Count == 0)
            return TryExecuteAutoAction("end_turn", null, $"[{stateType}] end turn (empty hand)");

        var context = BuildAutoDecisionContext(snapshot, player, battle, stateType);

        if ((stateType == "elite" || stateType == "boss") && MaybeUseCombatPotion(player, battle, stateType, context))
            return true;

        var playable = hand
            .Where(card => GetBool(card, "can_play"))
            .OrderByDescending(card => ScoreCombatCard(card, context, battle))
            .ToList();

        if (playable.Count == 0)
            return TryExecuteAutoAction("end_turn", null, $"[{stateType}] end turn (no playable card)");

        var chosen = playable[0];
        int cardIndex = GetInt(chosen, "index");
        string cardName = GetString(chosen, "name") ?? GetString(chosen, "id") ?? $"card_{cardIndex}";

        object? payload = null;
        if ((GetString(chosen, "target_type") ?? "").Contains("Enemy", StringComparison.OrdinalIgnoreCase))
        {
            string? target = SelectEnemyTarget(chosen, battle);
            if (target == null)
                return TryExecuteAutoAction("end_turn", null, $"[{stateType}] end turn (no target)");
            payload = new { card_index = cardIndex, target };
            return TryExecuteAutoAction("play_card", payload, $"[{stateType}] play {cardName} -> {target}");
        }

        payload = new { card_index = cardIndex };
        return TryExecuteAutoAction("play_card", payload, $"[{stateType}] play {cardName}");
    }

    private static bool MaybeUseCombatPotion(
        Dictionary<string, object?> player,
        Dictionary<string, object?> battle,
        string stateType,
        AutoSlayDecisionContext context)
    {
        var potions = GetDictList(player, "potions");
        if (potions.Count == 0)
            return false;

        int hp = GetInt(player, "hp");
        int maxHp = Math.Max(1, GetInt(player, "max_hp"));
        bool urgent = hp * 100 / maxHp <= 55
            || stateType == "boss"
            || context.IncomingDamage >= hp / 2;
        if (!urgent)
            return false;

        foreach (var potion in potions)
        {
            if (!GetBool(potion, "can_use_in_combat"))
                continue;

            int slot = GetInt(potion, "slot");
            string targetType = GetString(potion, "target_type") ?? "";
            string potionName = GetString(potion, "name") ?? GetString(potion, "id") ?? $"potion_{slot}";

            if (targetType.Contains("Enemy", StringComparison.OrdinalIgnoreCase))
            {
                string? target = SelectEnemyTarget(null, battle);
                if (target == null)
                    continue;
                bool used = TryExecuteAutoAction("use_potion", new { slot, target }, $"[{stateType}] use potion {potionName} -> {target}");
                if (used)
                    RecordAutoSlayPotionUsed();
                return used;
            }

            bool selfUsed = TryExecuteAutoAction("use_potion", new { slot }, $"[{stateType}] use potion {potionName}");
            if (selfUsed)
                RecordAutoSlayPotionUsed();
            return selfUsed;
        }

        return false;
    }

    private static int ScoreCombatCard(
        Dictionary<string, object?> card,
        AutoSlayDecisionContext context,
        Dictionary<string, object?> battle)
    {
        string id = GetString(card, "id") ?? "";
        string type = GetString(card, "type") ?? "";
        string descriptionText = "";
        if (card != null)
            descriptionText = GetString(card, "description") ?? "";
        string description = descriptionText.ToLowerInvariant();
        int round = GetInt(battle, "round");
        int enemyCount = GetDictList(battle, "enemies").Count;
        int blockAmount = EstimatePrimaryEffectAmount(card!, "格挡", "block");
        int damageAmount = EstimatePrimaryEffectAmount(card!, "伤害", "damage");
        bool isAoe = description.Contains("all enemies") || description.Contains("所有敌人");
        bool isPower = type.Equals("Power", StringComparison.OrdinalIgnoreCase);
        bool isAttack = type.Equals("Attack", StringComparison.OrdinalIgnoreCase);
        bool isSkill = type.Equals("Skill", StringComparison.OrdinalIgnoreCase);

        double score = _cardPickupScores.GetValueOrDefault(id, 0);
        score += GetLearnedBias(context.Weights.LearnedCardBias, id);
        string? displayedCost = GetString(card!, "cost");
        score += ParseDisplayedCost(displayedCost);

        if (isPower)
            score += (round <= 3 ? 30 : 10) * context.Weights.PowerWeight;
        else if (isAttack)
            score += 18 * context.Weights.DamageWeight;
        else if (isSkill)
            score += (context.NeedBlock ? 16 : 8) * (context.NeedBlock ? context.Weights.BlockWeight : context.Weights.DrawWeight);

        if (_defensiveIds.Contains(id))
            score += (context.NeedBlock ? 28 : 10) * context.Weights.BlockWeight;

        if (damageAmount > 0)
            score += damageAmount * 1.6 * context.Weights.DamageWeight;
        if (blockAmount > 0)
        {
            double urgency = context.NeedBlock
                ? 1.0 + Math.Clamp((context.IncomingDamage - context.CurrentBlock) / 18.0, 0.0, 1.2)
                : 0.35;
            score += blockAmount * 1.5 * context.Weights.BlockWeight * urgency;
        }

        if (description.Contains("block") || description.Contains("格挡"))
            score += (context.NeedBlock ? 24 : 5) * context.Weights.BlockWeight;
        if (description.Contains("draw") || description.Contains("抽"))
            score += 14 * context.Weights.DrawWeight;
        if (description.Contains("poison") || description.Contains("中毒"))
            score += (enemyCount > 1 ? 16 : 10) * context.Weights.PoisonWeight
                + context.Deck.PoisonCards * 2;
        if (description.Contains("weak") || description.Contains("虚弱")
            || description.Contains("vulnerable") || description.Contains("易伤"))
            score += 12 * context.Weights.DebuffWeight;
        if (isAoe)
            score += (enemyCount > 1 ? 16 : 6) * context.Weights.AoeWeight;
        if (description.Contains("retain") || description.Contains("保留"))
            score += 4;
        if (description.Contains("exhaust") || description.Contains("消耗"))
            score -= 2;
        if (displayedCost == "0")
            score += 8 * context.Weights.ZeroCostWeight;
        if (!context.NeedBlock && isAttack)
            score += 8 * context.Weights.DamageWeight;
        if ((context.IsEliteFight || context.IsBossFight) && isPower && round <= 3)
            score += 14 * context.Weights.PowerWeight;
        if (CanLikelyKillTarget(card!, battle))
            score += 28 * context.Weights.KillWeight;

        if (id.Equals("BACKSTAB", StringComparison.OrdinalIgnoreCase))
            score += round == 1 ? 22 : -10;
        if (id.Equals("NEUTRALIZE", StringComparison.OrdinalIgnoreCase))
            score += 18;
        if (id.Equals("BLADE_DANCE", StringComparison.OrdinalIgnoreCase))
            score += 18;
        if (id.Equals("FOOTWORK", StringComparison.OrdinalIgnoreCase))
            score += round <= 4 ? 24 : 8;
        if (id.Equals("NOXIOUS_FUMES", StringComparison.OrdinalIgnoreCase))
            score += round <= 3 ? 22 : 8;
        if (id.Equals("WRAITH_FORM", StringComparison.OrdinalIgnoreCase))
            score += context.NeedBlock ? 50 : 25;

        if (context.Deck.CardCounts.GetValueOrDefault(id) >= 3 && _cardPickupScores.GetValueOrDefault(id, 0) < 80)
            score -= 6;

        return (int)Math.Round(score);
    }

    private static string? SelectEnemyTarget(
        Dictionary<string, object?>? card,
        Dictionary<string, object?> battle)
    {
        var enemies = GetDictList(battle, "enemies");
        if (enemies.Count == 0)
            return null;

        string descriptionText = "";
        if (card != null)
            descriptionText = GetString(card, "description") ?? "";
        string description = descriptionText.ToLowerInvariant();
        bool wantsSetup = description.Contains("poison") || description.Contains("中毒")
            || description.Contains("weak") || description.Contains("虚弱")
            || description.Contains("vulnerable") || description.Contains("易伤");

        var ordered = wantsSetup
            ? enemies.OrderByDescending(enemy => GetInt(enemy, "hp")).ThenByDescending(EstimateEnemyDanger)
            : enemies.OrderBy(enemy => GetInt(enemy, "hp")).ThenByDescending(EstimateEnemyDanger);

        return GetString(ordered.First(), "entity_id");
    }

    private static int EstimateEnemyDanger(Dictionary<string, object?> enemy)
    {
        int danger = GetInt(enemy, "hp");
        foreach (var intent in GetDictList(enemy, "intents"))
            danger += ParseIntentDamage(intent);
        return danger;
    }

    private static int EstimateIncomingDamage(Dictionary<string, object?> battle)
    {
        int damage = 0;
        foreach (var enemy in GetDictList(battle, "enemies"))
        {
            foreach (var intent in GetDictList(enemy, "intents"))
                damage += ParseIntentDamage(intent);
        }

        return damage;
    }

    private static int ParseIntentDamage(Dictionary<string, object?> intent)
    {
        string label = GetString(intent, "label") ?? "";
        var nums = _numberRegex.Matches(label).Select(match => int.Parse(match.Value)).ToList();
        if (nums.Count >= 2 && label.Contains('x'))
            return nums[0] * nums[1];
        if (nums.Count >= 1)
            return nums[0];
        return 0;
    }

    private static int ParseDisplayedCost(string? cost)
    {
        if (string.IsNullOrWhiteSpace(cost))
            return 0;
        if (cost == "0")
            return 18;
        if (cost == "X")
            return 10;
        return int.TryParse(cost, out int value) ? Math.Max(0, 10 - value * 2) : 0;
    }

    private static double GetLearnedBias(Dictionary<string, double>? biases, string? key)
    {
        if (biases == null || string.IsNullOrWhiteSpace(key))
            return 0;
        return biases.TryGetValue(key, out double value) ? value : 0;
    }

    private static int EstimatePrimaryEffectAmount(Dictionary<string, object?> card, params string[] markers)
    {
        string description = GetString(card, "description") ?? "";
        if (!markers.Any(marker => description.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return 0;

        var nums = _numberRegex.Matches(description)
            .Select(match => int.TryParse(match.Value, out int value) ? value : 0)
            .Where(value => value > 0)
            .ToList();
        return nums.Count > 0 ? nums.Max() : 0;
    }

    private static bool CanLikelyKillTarget(Dictionary<string, object?> card, Dictionary<string, object?> battle)
    {
        int damage = EstimatePrimaryEffectAmount(card, "伤害", "damage");
        if (damage <= 0)
            return false;

        foreach (var enemy in GetDictList(battle, "enemies"))
        {
            int effectiveHp = GetInt(enemy, "hp") + GetInt(enemy, "block");
            if (effectiveHp > 0 && damage >= effectiveHp)
                return true;
        }

        return false;
    }

    private static int ScoreCardRewardSynergy(Dictionary<string, object?> card, AutoSlayDecisionContext context)
    {
        string id = GetString(card, "id") ?? "";
        string type = GetString(card, "type") ?? "";
        string description = (GetString(card, "description") ?? "").ToLowerInvariant();
        int score = 0;

        if (ContainsAny(description, "poison", "中毒"))
            score += context.Deck.PoisonCards > 0 ? 14 + context.Deck.PoisonCards * 2 : 6;
        if (id.Equals("CATALYST", StringComparison.OrdinalIgnoreCase))
            score += context.Deck.PoisonCards > 1 ? 22 : -20;
        if (ContainsAny(description, "shiv", "飞刀", "小刀"))
            score += context.Deck.ShivCards > 0 ? 14 + context.Deck.ShivCards * 2 : 6;
        if (id.Equals("ACCURACY", StringComparison.OrdinalIgnoreCase))
            score += context.Deck.ShivCards >= 2 ? 18 : -14;
        if (ContainsAny(description, "draw", "抽"))
            score += context.Deck.DrawCards < 3 ? 12 : 6;
        if (ContainsAny(description, "block", "格挡"))
            score += context.Deck.BlockCards < 5 ? 12 : 4;
        if (type.Equals("Power", StringComparison.OrdinalIgnoreCase))
            score += context.Deck.PowerCount < 4 ? 8 : 2;
        if (_cardPickupScores.GetValueOrDefault(id, 0) >= 90)
            score += 10;
        if (context.Deck.CurseCount > 0 && ContainsAny(description, "remove", "purge", "discard", "移除", "丢弃"))
            score += 8;

        return score;
    }

    private static int ScoreRemovalTarget(Dictionary<string, object?> card, AutoSlayDecisionContext context)
    {
        string id = GetString(card, "id") ?? "";
        string type = GetString(card, "type") ?? "";
        int score = 0 - ScoreCardReward(card, context);

        if (type.Equals("Curse", StringComparison.OrdinalIgnoreCase) || type.Equals("Status", StringComparison.OrdinalIgnoreCase))
            score += 80;
        if (id.Contains("STRIKE", StringComparison.OrdinalIgnoreCase) || GetString(card, "name")?.Contains("打击") == true)
            score += 24;
        if (id.Contains("DEFEND", StringComparison.OrdinalIgnoreCase) || GetString(card, "name")?.Contains("防御") == true)
            score += context.Deck.BlockCards > 5 ? 18 : 8;
        if (_cardPickupScores.GetValueOrDefault(id, 0) >= 90)
            score -= 40;
        if (context.Deck.CardCounts.GetValueOrDefault(id) >= 3)
            score += 12;

        return score;
    }

    private static int ScoreUpgradeTarget(Dictionary<string, object?> card, AutoSlayDecisionContext context)
    {
        string id = GetString(card, "id") ?? "";
        int score = ScoreCardReward(card, context) + 12;
        if (_cardPickupScores.GetValueOrDefault(id, 0) >= 90)
            score += 16;
        if (ContainsAny(GetString(card, "description") ?? "", "格挡", "block", "伤害", "damage", "中毒", "poison"))
            score += 8;
        return score;
    }

    private static int ScoreRestOption(Dictionary<string, object?> option, AutoSlayDecisionContext context)
    {
        string id = (GetString(option, "id") ?? GetString(option, "name") ?? "").ToLowerInvariant();
        double score = id switch
        {
            var v when v.Contains("rest") => context.HpRatio < context.Weights.RestHealThreshold
                ? 85 + (context.Weights.RestPreference * 15) + (1.0 - context.HpRatio) * 25
                : 28,
            var v when v.Contains("smith") || v.Contains("upgrade") => 58
                + context.Weights.SmithPreference * 18
                + (context.Deck.UpgradableCards > 0 ? 16 : -20)
                + (context.HpRatio >= context.Weights.RestHealThreshold ? 12 : 0),
            var v when v.Contains("dig") => 52 + context.HpRatio * 14,
            var v when v.Contains("recall") => 42 + context.HpRatio * 12,
            _ => 30
        };

        score += GetLearnedBias(context.Weights.LearnedRestBias, id);
        return (int)Math.Round(score);
    }

    private static int ScoreRelicSelection(Dictionary<string, object?> relic)
    {
        string id = (GetString(relic, "id") ?? GetString(relic, "name") ?? "").ToLowerInvariant();
        string description = (GetString(relic, "description") ?? "").ToLowerInvariant();
        int score = 40;

        if (ContainsAny(id, "energy") || ContainsAny(description, "energy", "能量"))
            score += 25;
        if (ContainsAny(description, "draw", "抽"))
            score += 12;
        if (ContainsAny(description, "shop", "merchant", "商店"))
            score += 8;
        if (ContainsAny(description, "curse", "诅咒"))
            score -= 18;

        return score;
    }

    private static bool HandleMapState(Dictionary<string, object?> snapshot)
    {
        var map = GetDict(snapshot, "map");
        var player = GetDict(snapshot, "player");
        if (map == null || player == null)
            return false;

        var options = GetDictList(map, "next_options");
        if (options.Count == 0)
            return false;

        var context = BuildAutoDecisionContext(snapshot, player, null, "map");

        var best = options
            .OrderByDescending(option => ScoreMapNode(option, context))
            .First();

        int index = GetInt(best, "index");
        string nodeType = GetString(best, "type") ?? "unknown";
        bool chosen = TryExecuteAutoAction("choose_map_node", new { index }, $"[map] choose node {index} ({nodeType})");
        if (chosen)
            RecordAutoSlayMapChoice(nodeType);
        return chosen;
    }

    private static int ScoreMapNode(Dictionary<string, object?> option, AutoSlayDecisionContext context)
    {
        string type = (GetString(option, "type") ?? "").ToLowerInvariant();
        double hpRatio = context.HpRatio;
        double deckQuality = context.Deck.DeckQualityScore;
        double score = type switch
        {
            var t when t.Contains("treasure") => 110,
            var t when t.Contains("boss") => 100,
            var t when t.Contains("elite") => 50 + (hpRatio * 34) + (deckQuality * 0.35) + (context.Weights.ElitePreference * 18),
            var t when t.Contains("rest") => 38 + ((1.0 - hpRatio) * 70) + (context.Weights.RestPreference * 16),
            var t when t.Contains("merchant") || t.Contains("shop") => 24 + Math.Max(0, context.Gold - 80) * 0.18 + (context.Weights.ShopPreference * 18),
            var t when t.Contains("event") => 46 + (context.Weights.EventPreference * 12),
            var t when t.Contains("monster") => 44 + (context.Weights.MonsterPreference * 10),
            _ => 30
        };

        score += GetLearnedBias(context.Weights.LearnedNodeBias, type);

        foreach (var child in GetDictList(option, "leads_to"))
        {
            string childType = (GetString(child, "type") ?? "").ToLowerInvariant();
            if (childType.Contains("elite") && hpRatio >= 0.72 && deckQuality >= 28)
                score += 7;
            else if (childType.Contains("rest") && hpRatio < context.Weights.RestHealThreshold + 0.05)
                score += 8;
            else if (childType.Contains("merchant") && context.Gold >= 120)
                score += 5;
            else if (childType.Contains("treasure"))
                score += 8;
        }

        return (int)Math.Round(score);
    }

    private static bool HandleRewardsState(Dictionary<string, object?> snapshot)
    {
        var rewards = GetDict(snapshot, "rewards");
        if (rewards == null)
            return false;

        var items = GetDictList(rewards, "items");
        if (items.Count > 0)
        {
            var player = GetDict(snapshot, "player");
            var context = BuildAutoDecisionContext(snapshot, player, null, "rewards");
            var claimableItems = items
                .Where(item => !ShouldSkipRewardItem(item, context))
                .ToList();

            if (claimableItems.Count > 0)
            {
                var best = claimableItems.OrderByDescending(item => ScoreRewardItem(item, context)).First();
                int index = GetInt(best, "index");
                string type = GetString(best, "type") ?? "reward";
                bool claimed = TryExecuteAutoAction("claim_reward", new { index }, $"[rewards] claim {type} #{index}");
                if (claimed)
                    RecordAutoSlayRewardClaim(type);
                return claimed;
            }

            if (GetBool(rewards, "can_proceed"))
            {
                AppendAutoSlayTrace($"[{DateTime.UtcNow:O}] [rewards] skip blocked rewards and proceed");
                return TryExecuteAutoAction("proceed", null, "[rewards] proceed");
            }
        }

        if (GetBool(rewards, "can_proceed"))
            return TryExecuteAutoAction("proceed", null, "[rewards] proceed");

        return false;
    }

    private static bool ShouldSkipRewardItem(Dictionary<string, object?> item, AutoSlayDecisionContext context)
    {
        string type = GetString(item, "type") ?? "";
        return type == "potion" && context.PotionCount >= context.PotionSlotCount;
    }

    private static int ScoreRewardItem(Dictionary<string, object?> item, AutoSlayDecisionContext context)
    {
        string type = GetString(item, "type") ?? "";
        return type switch
        {
            "relic" => (int)Math.Round(90 * context.Weights.RewardRelicPreference),
            "gold" => (int)Math.Round(80 * context.Weights.RewardGoldPreference) + GetInt(item, "gold_amount") / 10,
            "potion" => context.PotionCount >= context.PotionSlotCount
                ? 8
                : (int)Math.Round(55 * context.Weights.RewardPotionPreference),
            "card" => 40,
            "special_card" => 42,
            _ => 20
        };
    }

    private static bool HandleCardRewardState(Dictionary<string, object?> snapshot)
    {
        var reward = GetDict(snapshot, "card_reward");
        var player = GetDict(snapshot, "player");
        if (reward == null || player == null)
            return false;

        var cards = GetDictList(reward, "cards");
        if (cards.Count == 0)
        {
            if (GetBool(reward, "can_skip"))
                return TryExecuteAutoAction("skip_card_reward", null, "[card_reward] skip empty reward");
            return false;
        }

        var context = BuildAutoDecisionContext(snapshot, player, null, "card_reward");
        var best = cards.OrderByDescending(card => ScoreCardReward(card, context)).First();
        int bestScore = ScoreCardReward(best, context);
        if (GetBool(reward, "can_skip") && bestScore < context.Weights.CardSkipThreshold)
        {
            bool skipped = TryExecuteAutoAction("skip_card_reward", null, "[card_reward] skip low-value reward");
            if (skipped)
                RecordAutoSlayCardSkipped("card_reward");
            return skipped;
        }

        int cardIndex = GetInt(best, "index");
        string name = GetString(best, "name") ?? GetString(best, "id") ?? $"card_{cardIndex}";
        bool picked = TryExecuteAutoAction("select_card_reward", new { card_index = cardIndex }, $"[card_reward] pick {name}");
        if (picked)
            RecordAutoSlayCardAdded(GetString(best, "id") ?? name);
        return picked;
    }

    private static int ScoreCardReward(Dictionary<string, object?> card, AutoSlayDecisionContext? context = null)
    {
        string id = GetString(card, "id") ?? "";
        double score = _cardPickupScores.GetValueOrDefault(id, 30);
        if (context != null)
            score += GetLearnedBias(context.Weights.LearnedCardBias, id);
        score += (GetString(card, "rarity") ?? "") switch
        {
            "Rare" => 18,
            "Uncommon" => 8,
            _ => 0
        };
        score += (GetString(card, "type") ?? "") switch
        {
            "Power" => 10,
            "Attack" => 4,
            "Skill" => 6,
            _ => 0
        };

        string description = (GetString(card, "description") ?? "").ToLowerInvariant();
        if (description.Contains("draw") || description.Contains("抽"))
            score += 8 * (context?.Weights.DrawWeight ?? 1.0);
        if (description.Contains("block") || description.Contains("格挡"))
            score += 5 * (context?.Weights.BlockWeight ?? 1.0);
        if (description.Contains("poison") || description.Contains("中毒"))
            score += 7 * (context?.Weights.PoisonWeight ?? 1.0);

        if (context != null)
        {
            score += ScoreCardRewardSynergy(card, context);
            if (context.Deck.DeckSize >= 17 && _cardPickupScores.GetValueOrDefault(id, 0) < 80)
                score -= 8;
            if (context.Deck.CardCounts.GetValueOrDefault(id) >= 2 && _cardPickupScores.GetValueOrDefault(id, 0) < 90)
                score -= 7;
        }

        return (int)Math.Round(score);
    }

    private static bool HandleEventState(Dictionary<string, object?> snapshot)
    {
        var eventState = GetDict(snapshot, "event");
        if (eventState == null)
            return false;

        if (GetBool(eventState, "is_ancient_dialogue"))
            return TryExecuteAutoAction("advance_dialogue", null, "[event] advance dialogue");

        var options = GetDictList(eventState, "options")
            .Where(opt => !opt.ContainsKey("is_locked") || !GetBool(opt, "is_locked"))
            .ToList();
        if (options.Count == 0)
            return false;

        var player = GetDict(snapshot, "player");
        var context = BuildAutoDecisionContext(snapshot, player, null, "event");
        var best = options.OrderByDescending(option => ScoreEventOption(option, context)).First();
        int index = GetInt(best, "index");
        string title = GetString(best, "title") ?? $"option_{index}";
        bool chosen = TryExecuteAutoAction("choose_event_option", new { index }, $"[event] choose {title}");
        if (chosen)
            RecordAutoSlayEventChoice(ScoreEventOption(best, context) >= 0);
        return chosen;
    }

    private static int ScoreEventOption(Dictionary<string, object?> option, AutoSlayDecisionContext context)
    {
        string title = (GetString(option, "title") ?? "").ToLowerInvariant();
        string description = (GetString(option, "description") ?? "").ToLowerInvariant();
        string text = $"{title} {description}";
        int score = 10 - GetInt(option, "index");

        if (option.ContainsKey("relic_name"))
            score += 40;

        if (ContainsAny(text, "remove", "purge", "transform", "upgrade", "gold", "relic", "potion",
                "移除", "删除", "变化", "升级", "强化", "金币", "遗物", "药水"))
            score += 18;

        if (ContainsAny(text, "lose", "max hp", "curse", "damage", "fight", "pay", "sacrifice",
                "失去", "最大生命", "诅咒", "伤害", "战斗", "付出", "献祭"))
            score -= 18;

        if (ContainsAny(text, "hp", "health", "生命"))
            score -= 8;

        if (context.HpRatio < 0.45 && ContainsAny(text, "heal", "max hp", "生命", "回复"))
            score += 10;
        if (context.Deck.CurseCount > 0 && ContainsAny(text, "remove", "purge", "删除", "移除"))
            score += 10;
        if (ContainsAny(text, "shop", "merchant", "商人"))
            score += (int)Math.Round(4 * context.Weights.ShopPreference);

        return score;
    }

    private static bool HandleRestSiteState(Dictionary<string, object?> snapshot)
    {
        var restSite = GetDict(snapshot, "rest_site");
        var player = GetDict(snapshot, "player");
        if (restSite == null || player == null)
            return false;

        if (GetBool(restSite, "can_proceed"))
            return TryExecuteAutoAction("proceed", null, "[rest_site] proceed");

        var options = GetDictList(restSite, "options")
            .Where(opt => GetBool(opt, "is_enabled"))
            .ToList();
        if (options.Count == 0)
            return false;

        var context = BuildAutoDecisionContext(snapshot, player, null, "rest_site");
        var best = options
            .OrderByDescending(opt => ScoreRestOption(opt, context))
            .First();

        int index = GetInt(best, "index");
        string optionId = GetString(best, "id") ?? GetString(best, "name") ?? $"option_{index}";
        bool chosen = TryExecuteAutoAction("choose_rest_option", new { index }, $"[rest_site] choose {optionId}");
        if (chosen)
            RecordAutoSlayRestChoice(optionId);
        return chosen;
    }

    private static bool HandleShopState(Dictionary<string, object?> snapshot, string stateType)
    {
        var shop = GetDict(snapshot, stateType);
        var player = GetDict(snapshot, "player");
        if (shop == null || player == null)
            return false;

        var items = GetDictList(shop, "items")
            .Where(item => GetBool(item, "is_stocked") && GetBool(item, "can_afford"))
            .ToList();

        if (items.Count == 0)
            return TryLeaveShop(stateType);

        var context = BuildAutoDecisionContext(snapshot, player, null, stateType);
        var best = items.OrderByDescending(item => ScoreShopItem(item, context)).First();
        if (ScoreShopItem(best, context) < 35)
            return TryLeaveShop(stateType);

        int index = GetInt(best, "index");
        string category = GetString(best, "category") ?? "item";
        bool bought = TryExecuteAutoAction("shop_purchase", new { index }, $"[{stateType}] buy {category} #{index}");
        if (bought)
        {
            string trackingKey = category;
            if (string.Equals(category, "card", StringComparison.OrdinalIgnoreCase))
            {
                string? cardId = GetString(best, "card_id");
                if (!string.IsNullOrWhiteSpace(cardId))
                {
                    RecordAutoSlayCardAdded(cardId);
                    trackingKey = $"card:{cardId}";
                }
            }
            else if (string.Equals(category, "card_removal", StringComparison.OrdinalIgnoreCase))
            {
                trackingKey = "card_removal";
            }

            RecordAutoSlayShopPurchase(trackingKey);
        }
        return bought;
    }

    private static bool TryLeaveShop(string stateType)
    {
        if (stateType == "shop" && NMerchantRoom.Instance is { } merchRoom)
        {
            if (merchRoom.Inventory?.IsOpen == true)
            {
                var backBtn = FindFirst<NBackButton>(merchRoom);
                if (backBtn is { IsEnabled: true })
                {
                    backBtn.ForceClick();
                    AppendAutoSlayTrace($"[{DateTime.UtcNow:O}] [shop] close inventory");
                    return true;
                }
            }

            if (merchRoom.ProceedButton.IsEnabled)
            {
                merchRoom.ProceedButton.ForceClick();
                AppendAutoSlayTrace($"[{DateTime.UtcNow:O}] [shop] proceed");
                return true;
            }
        }

        if (stateType == "fake_merchant" && NEventRoom.Instance is { } eventRoom)
        {
            var fakeMerchant = FindFirst<NFakeMerchant>(eventRoom);
            if (fakeMerchant != null)
            {
                var inventory = FindFirst<NMerchantInventory>(fakeMerchant);
                if (inventory is { IsOpen: true })
                {
                    var backBtn = FindFirst<NBackButton>(fakeMerchant);
                    if (backBtn is { IsEnabled: true })
                    {
                        backBtn.ForceClick();
                        AppendAutoSlayTrace($"[{DateTime.UtcNow:O}] [fake_merchant] close inventory");
                        return true;
                    }
                }

                var proceedBtn = FindFirst<NProceedButton>(fakeMerchant);
                if (proceedBtn is { IsEnabled: true })
                {
                    proceedBtn.ForceClick();
                    AppendAutoSlayTrace($"[{DateTime.UtcNow:O}] [fake_merchant] proceed");
                    return true;
                }
            }
        }

        return TryExecuteAutoAction("proceed", null, $"[{stateType}] proceed");
    }

    private static int ScoreShopItem(Dictionary<string, object?> item, AutoSlayDecisionContext context)
    {
        string category = GetString(item, "category") ?? "";
        int price = GetInt(item, "price");
        double baseScore = category switch
        {
            "relic" => 90 * context.Weights.ShopRelicPreference + GetLearnedBias(context.Weights.LearnedShopBias, "relic"),
            "card_removal" => (72 + Math.Max(0, context.Deck.DeckSize - 12) * 3 + context.Deck.CurseCount * 12)
                * context.Weights.CardRemovalPreference
                + GetLearnedBias(context.Weights.LearnedShopBias, "card_removal"),
            "card" => ScoreCardReward(new Dictionary<string, object?>
            {
                ["id"] = GetString(item, "card_id"),
                ["name"] = GetString(item, "card_name"),
                ["type"] = GetString(item, "card_type"),
                ["description"] = GetString(item, "card_description"),
                ["rarity"] = GetString(item, "card_rarity")
            }, context) * context.Weights.ShopCardPreference + GetLearnedBias(context.Weights.LearnedShopBias, "card"),
            "potion" => ((context.PotionCount >= context.PotionSlotCount ? 10 : 30) * context.Weights.ShopPotionPreference)
                + GetLearnedBias(context.Weights.LearnedShopBias, "potion"),
            _ => 10
        };

        if (GetBool(item, "on_sale"))
            baseScore += 12;

        return (int)Math.Round(baseScore - price / 12.0);
    }

    private static bool HandleTreasureState(Dictionary<string, object?> snapshot)
    {
        var treasure = GetDict(snapshot, "treasure");
        if (treasure == null)
            return false;

        var relics = GetDictList(treasure, "relics");
        if (relics.Count > 0)
        {
            int index = GetInt(relics[0], "index");
            string name = GetString(relics[0], "name") ?? $"relic_{index}";
            return TryExecuteAutoAction("claim_treasure_relic", new { index }, $"[treasure] claim {name}");
        }

        if (GetBool(treasure, "can_proceed"))
            return TryExecuteAutoAction("proceed", null, "[treasure] proceed");

        return false;
    }

    private static bool HandleCardSelectState(Dictionary<string, object?> snapshot)
    {
        var cardSelect = GetDict(snapshot, "card_select");
        var player = GetDict(snapshot, "player");
        if (cardSelect == null || player == null)
            return false;

        if (GetBool(cardSelect, "preview_showing"))
        {
            if (GetBool(cardSelect, "can_confirm"))
                return TryExecuteAutoAction("confirm_selection", null, "[card_select] confirm preview");
            if (GetBool(cardSelect, "can_cancel"))
                return TryExecuteAutoAction("cancel_selection", null, "[card_select] cancel preview");
            return false;
        }

        var cards = GetDictList(cardSelect, "cards");
        if (cards.Count == 0)
            return false;

        string screenType = GetString(cardSelect, "screen_type") ?? "";
        string prompt = GetString(cardSelect, "prompt") ?? "";
        string selectionMode = ResolveSelectionMode(screenType, prompt);
        var context = BuildAutoDecisionContext(snapshot, player, null, "card_select");

        IEnumerable<Dictionary<string, object?>> ordered = selectionMode switch
        {
            "upgrade" => cards.OrderByDescending(card => ScoreUpgradeTarget(card, context)),
            "remove" or "transform" => cards.OrderByDescending(card => ScoreRemovalTarget(card, context)),
            _ => cards.OrderByDescending(card => ScoreCardReward(card, context))
        };

        var chosen = ordered.First();
        int index = GetInt(chosen, "index");
        string name = GetString(chosen, "name") ?? GetString(chosen, "id") ?? $"card_{index}";

        bool singlePickThenConfirm = screenType.Contains("DeckEnchant", StringComparison.OrdinalIgnoreCase)
            || ContainsAny(prompt, "选择1张牌", "select 1 card", "choose 1 card", "附魔");

        if (!singlePickThenConfirm && GetBool(cardSelect, "can_confirm"))
            return TryExecuteAutoAction("confirm_selection", null, "[card_select] confirm");

        if (!singlePickThenConfirm)
            return TryExecuteAutoAction("select_card", new { index }, $"[card_select] {selectionMode} {name}");

        var selectResult = ExecuteAction("select_card", BuildActionData(new { index }));
        if (!string.Equals(GetString(selectResult, "status"), "ok", StringComparison.OrdinalIgnoreCase))
        {
            string? selectError = GetString(selectResult, "error");
            if (!string.IsNullOrWhiteSpace(selectError) && !IsTransientAutoError(selectError))
                AppendAutoSlayTrace($"[{DateTime.UtcNow:O}] action=select_card failed: {selectError}");
            return false;
        }

        AppendAutoSlayTrace($"[{DateTime.UtcNow:O}] [card_select] {selectionMode} {name}");

        var confirmResult = ExecuteAction("confirm_selection", BuildActionData(null));
        if (string.Equals(GetString(confirmResult, "status"), "ok", StringComparison.OrdinalIgnoreCase))
        {
            AppendAutoSlayTrace($"[{DateTime.UtcNow:O}] [card_select] confirm");
            return true;
        }

        string? confirmError = GetString(confirmResult, "error");
        if (!string.IsNullOrWhiteSpace(confirmError) && !IsTransientAutoError(confirmError))
            AppendAutoSlayTrace($"[{DateTime.UtcNow:O}] action=confirm_selection failed: {confirmError}");
        return true;
    }

    private static bool HandleHandSelectState(Dictionary<string, object?> snapshot)
    {
        var handSelect = GetDict(snapshot, "hand_select");
        var player = GetDict(snapshot, "player");
        if (handSelect == null || player == null)
            return false;

        if (GetBool(handSelect, "can_confirm") && GetDictList(handSelect, "selected_cards").Count > 0)
            return TryExecuteAutoAction("combat_confirm_selection", null, "[hand_select] confirm");

        var cards = GetDictList(handSelect, "cards");
        if (cards.Count == 0)
            return false;

        string prompt = (GetString(handSelect, "prompt") ?? "").ToLowerInvariant();
        var context = BuildAutoDecisionContext(snapshot, player, GetDict(snapshot, "battle"), "hand_select");
        bool prefersBest = ContainsAny(prompt, "upgrade", "play", "retain", "keep", "升级", "打出", "保留");
        var chosen = prefersBest
            ? cards.OrderByDescending(card => ScoreCardReward(card, context)).First()
            : cards.OrderBy(card => ScoreRemovalTarget(card, context)).First();

        int index = GetInt(chosen, "index");
        string name = GetString(chosen, "name") ?? GetString(chosen, "id") ?? $"card_{index}";
        return TryExecuteAutoAction("combat_select_card", new { card_index = index }, $"[hand_select] choose {name}");
    }

    private static bool HandleBundleSelectState(Dictionary<string, object?> snapshot)
    {
        var bundleSelect = GetDict(snapshot, "bundle_select");
        var player = GetDict(snapshot, "player");
        if (bundleSelect == null || player == null)
            return false;

        if (GetBool(bundleSelect, "preview_showing"))
        {
            if (GetBool(bundleSelect, "can_confirm"))
                return TryExecuteAutoAction("confirm_bundle_selection", null, "[bundle_select] confirm");
            if (GetBool(bundleSelect, "can_cancel"))
                return TryExecuteAutoAction("cancel_bundle_selection", null, "[bundle_select] cancel");
            return false;
        }

        var bundles = GetDictList(bundleSelect, "bundles");
        if (bundles.Count == 0)
            return false;

        var context = BuildAutoDecisionContext(snapshot, player, null, "bundle_select");
        var best = bundles.OrderByDescending(bundle =>
            GetDictList(bundle, "cards").Sum(card => ScoreCardReward(card, context))).First();
        int index = GetInt(best, "index");
        return TryExecuteAutoAction("select_bundle", new { index }, $"[bundle_select] preview bundle #{index}");
    }

    private static bool HandleRelicSelectState(Dictionary<string, object?> snapshot)
    {
        var relicSelect = GetDict(snapshot, "relic_select");
        if (relicSelect == null)
            return false;

        var relics = GetDictList(relicSelect, "relics");
        if (relics.Count == 0)
        {
            if (GetBool(relicSelect, "can_skip"))
                return TryExecuteAutoAction("skip_relic_selection", null, "[relic_select] skip");
            return false;
        }

        var best = relics.OrderByDescending(ScoreRelicSelection).First();
        int index = GetInt(best, "index");
        string name = GetString(best, "name") ?? $"relic_{index}";
        return TryExecuteAutoAction("select_relic", new { index }, $"[relic_select] choose {name}");
    }

    private static bool HandleCrystalSphereState(Dictionary<string, object?> snapshot)
    {
        var sphere = GetDict(snapshot, "crystal_sphere");
        if (sphere == null)
            return false;

        if (GetBool(sphere, "can_proceed"))
            return TryExecuteAutoAction("crystal_sphere_proceed", null, "[crystal_sphere] proceed");

        if (GetBool(sphere, "can_use_small_tool") && !string.Equals(GetString(sphere, "tool"), "small", StringComparison.OrdinalIgnoreCase))
            return TryExecuteAutoAction("crystal_sphere_set_tool", new { tool = "small" }, "[crystal_sphere] set small tool");

        var cells = GetDictList(sphere, "clickable_cells");
        if (cells.Count == 0)
            return false;

        int x = GetInt(cells[0], "x");
        int y = GetInt(cells[0], "y");
        return TryExecuteAutoAction("crystal_sphere_click_cell", new { x, y }, $"[crystal_sphere] click ({x},{y})");
    }

    private static string ResolveSelectionMode(string screenType, string prompt)
    {
        string text = $"{screenType} {prompt}".ToLowerInvariant();
        if (ContainsAny(text, "upgrade", "smith", "强化", "升级", "锻造"))
            return "upgrade";
        if (ContainsAny(text, "remove", "purge", "删除", "移除"))
            return "remove";
        if (ContainsAny(text, "transform", "变化"))
            return "transform";
        return "select";
    }

    private static bool TryExecuteAutoAction(string action, object? payload, string logMessage)
    {
        var result = ExecuteAction(action, BuildActionData(payload));
        string? status = GetString(result, "status");
        string? error = GetString(result, "error");

        if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            AppendAutoSlayTrace($"[{DateTime.UtcNow:O}] {logMessage}");
            return true;
        }

        if (!string.IsNullOrWhiteSpace(error) && !IsTransientAutoError(error))
            AppendAutoSlayTrace($"[{DateTime.UtcNow:O}] action={action} failed: {error}");

        return false;
    }

    private static bool IsTransientAutoError(string error)
    {
        return error.Contains("not ready", StringComparison.OrdinalIgnoreCase)
            || error.Contains("currently disabled", StringComparison.OrdinalIgnoreCase)
            || error.Contains("loading", StringComparison.OrdinalIgnoreCase)
            || error.Contains("waiting", StringComparison.OrdinalIgnoreCase)
            || error.Contains("not open", StringComparison.OrdinalIgnoreCase)
            || error.Contains("No proceed button", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, JsonElement> BuildActionData(object? payload)
    {
        if (payload == null)
            return new Dictionary<string, JsonElement>();

        using var doc = JsonSerializer.SerializeToDocument(payload, _jsonOptions);
        var data = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in doc.RootElement.EnumerateObject())
            data[property.Name] = property.Value.Clone();
        return data;
    }

    private static Dictionary<string, object?>? GetDict(Dictionary<string, object?> source, string key)
    {
        return source.TryGetValue(key, out var value) ? value as Dictionary<string, object?> : null;
    }

    private static List<Dictionary<string, object?>> GetDictList(Dictionary<string, object?> source, string key)
    {
        return source.TryGetValue(key, out var value) && value is List<Dictionary<string, object?>> list
            ? list
            : new List<Dictionary<string, object?>>();
    }

    private static string? GetString(Dictionary<string, object?> source, string key)
    {
        return source.TryGetValue(key, out var value) ? value?.ToString() : null;
    }

    private static int GetInt(Dictionary<string, object?> source, string key)
    {
        if (!source.TryGetValue(key, out var value) || value == null)
            return 0;
        return value switch
        {
            int i => i,
            long l => (int)l,
            uint u => (int)u,
            JsonElement e when e.TryGetInt32(out int i) => i,
            _ when int.TryParse(value.ToString(), out int parsed) => parsed,
            _ => 0
        };
    }

    private static bool GetBool(Dictionary<string, object?> source, string key)
    {
        if (!source.TryGetValue(key, out var value) || value == null)
            return false;
        return value switch
        {
            bool b => b,
            JsonElement e when e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False => e.GetBoolean(),
            _ when bool.TryParse(value.ToString(), out bool parsed) => parsed,
            _ => false
        };
    }

    private static bool ContainsAny(string source, params string[] values)
    {
        return values.Any(value => source.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsGameOverOverlay(Dictionary<string, object?> snapshot)
    {
        if (!string.Equals(GetString(snapshot, "state_type"), "overlay", StringComparison.OrdinalIgnoreCase))
            return false;

        var overlay = GetDict(snapshot, "overlay");
        string? screenType = overlay != null ? GetString(overlay, "screen_type") : null;
        return string.Equals(screenType, "NGameOverScreen", StringComparison.Ordinal);
    }

    private static void AppendAutoSlayTrace(string line)
    {
        string? logFile;
        lock (_autoSlayLock)
            logFile = _autoSlayCurrentLogFile;

        if (string.IsNullOrWhiteSpace(logFile))
            return;

        try
        {
            File.AppendAllText(logFile, line + System.Environment.NewLine);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] Failed to append auto-run log: {ex.Message}");
        }
    }

    private static async Task RecoverUiForAutoSlayAsync(CancellationToken ct)
    {
        var topOverlay = await RunOnMainThread(() => MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack.Instance?.Peek());
        if (topOverlay is NGameOverScreen gameOverScreen)
        {
            bool clicked = false;
            for (int attempt = 0; attempt < 40 && !ct.IsCancellationRequested; attempt++)
            {
                clicked = await RunOnMainThread(() => TryDismissGameOverOverlay(gameOverScreen));
                if (clicked)
                    break;

                await Task.Delay(250, ct);
                topOverlay = await RunOnMainThread(() => MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NOverlayStack.Instance?.Peek());
                if (topOverlay is not NGameOverScreen nextGameOverScreen)
                    break;

                gameOverScreen = nextGameOverScreen;
            }

            if (!clicked)
            {
                AppendAutoSlayTrace($"[{DateTime.UtcNow:O}] [overlay] game over buttons unavailable, forcing main menu");
                await (await RunOnMainThread(() => NGame.Instance != null ? NGame.Instance.ReturnToMainMenu() : Task.CompletedTask));
            }

            for (int attempt = 0; attempt < 80 && !ct.IsCancellationRequested; attempt++)
            {
                bool atMenu = await RunOnMainThread(() => !RunManager.Instance.IsInProgress && NGame.Instance?.MainMenu != null);
                if (atMenu)
                    return;
                await Task.Delay(100, ct);
            }
            return;
        }

        bool inProgress = await RunOnMainThread(() => RunManager.Instance.IsInProgress);
        bool hasMainMenu = await RunOnMainThread(() => NGame.Instance?.MainMenu != null);
        if (!inProgress && !hasMainMenu)
        {
            await (await RunOnMainThread(() => NGame.Instance != null ? NGame.Instance.ReturnToMainMenu() : Task.CompletedTask));
            await WaitUntilAsync(() => RunOnMainThread(() => NGame.Instance?.MainMenu != null), ct);
        }
    }

    private static bool TryDismissGameOverOverlay(NGameOverScreen gameOverScreen)
    {
        var mainMenuButton = FindFirst<NReturnToMainMenuButton>(gameOverScreen)
            ?? typeof(NGameOverScreen)
                .GetField("_mainMenuButton", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.GetValue(gameOverScreen) as NReturnToMainMenuButton;
        if (mainMenuButton is { IsEnabled: true })
        {
            mainMenuButton.ForceClick();
            return true;
        }

        var continueButton = typeof(NGameOverScreen)
            .GetField("_continueButton", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(gameOverScreen) as NGameOverContinueButton;
        if (continueButton is { IsEnabled: true })
        {
            continueButton.ForceClick();
            return true;
        }

        return false;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (await condition())
                return;

            await Task.Delay(100, ct);
        }
    }

    private static string ResolveAutoSlaySeed(string? initialSeed, int iteration)
    {
        if (!string.IsNullOrWhiteSpace(initialSeed) && iteration == 1)
            return initialSeed!;

        return Guid.NewGuid().ToString("N")[..12];
    }

    private static async Task WaitForAutoStateAdvanceAsync(string previousFingerprint, CancellationToken ct)
    {
        // Prevent repeated clicks while the UI is still resolving the previous action.
        for (int i = 0; i < 12; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(100, ct);

            var snapshot = await RunOnMainThread(BuildGameState);
            if (!string.Equals(BuildAutoStepFingerprint(snapshot), previousFingerprint, StringComparison.Ordinal))
                return;
        }
    }

    private static string BuildAutoStepFingerprint(Dictionary<string, object?> snapshot)
    {
        var parts = new List<string>
        {
            GetString(snapshot, "state_type") ?? "unknown"
        };

        var run = GetDict(snapshot, "run");
        if (run != null)
        {
            parts.Add($"act:{GetInt(run, "act")}");
            parts.Add($"floor:{GetInt(run, "floor")}");
        }

        var player = GetDict(snapshot, "player");
        if (player != null)
        {
            parts.Add($"hp:{GetInt(player, "hp")}");
            parts.Add($"block:{GetInt(player, "block")}");
            parts.Add($"energy:{GetInt(player, "energy")}");

            foreach (var card in GetDictList(player, "hand"))
            {
                parts.Add($"hand:{GetInt(card, "index")}:{GetString(card, "id")}:{GetBool(card, "can_play")}");
            }
        }

        var battle = GetDict(snapshot, "battle");
        if (battle != null)
        {
            parts.Add($"round:{GetInt(battle, "round")}");
            parts.Add($"turn:{GetString(battle, "turn")}");
            parts.Add($"play:{GetBool(battle, "is_play_phase")}");
            foreach (var enemy in GetDictList(battle, "enemies"))
            {
                parts.Add($"enemy:{GetString(enemy, "entity_id")}:{GetInt(enemy, "hp")}:{GetInt(enemy, "block")}");
            }
        }

        var cardSelect = GetDict(snapshot, "card_select");
        if (cardSelect != null)
        {
            parts.Add($"card_select:{GetString(cardSelect, "screen_type")}");
            parts.Add($"confirm:{GetBool(cardSelect, "can_confirm")}");
            parts.Add($"preview:{GetBool(cardSelect, "preview_showing")}");
            parts.Add($"count:{GetDictList(cardSelect, "cards").Count}");
        }

        return string.Join("|", parts);
    }

    private static string GetAutoSlayLearningRoot()
    {
        string modDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
            ?? AppContext.BaseDirectory;
        string root = Path.Combine(modDir, "STS2_MCP.learning");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "run-logs"));
        return root;
    }

    private static string BuildAutoSlayLogFilePath(int iteration, string seed, DateTime startedAtUtc)
    {
        string sanitizedSeed = string.IsNullOrWhiteSpace(seed) ? "random" : seed;
        string fileName = $"{startedAtUtc:yyyyMMdd-HHmmss}_run{iteration:D5}_{sanitizedSeed}.log";
        return Path.Combine(GetAutoSlayLearningRoot(), "run-logs", fileName);
    }

    private static void AppendAutoSlayRunSummary(
        int iteration,
        string seed,
        string? logFile,
        DateTime startedAtUtc,
        DateTime finishedAtUtc,
        Dictionary<string, object?> snapshot,
        string? error)
    {
        string root = GetAutoSlayLearningRoot();
        string summaryPath = Path.Combine(root, "run-summaries.jsonl");
        string aggregatePath = Path.Combine(root, "aggregate.json");
        string strategyPath = Path.Combine(root, "strategy-profile.json");
        var runStats = _autoSlayRunStats;
        var playerData = snapshot.TryGetValue("player", out var playerObj) && playerObj is Dictionary<string, object?> playerDict
            ? playerDict
            : null;
        var deckProfile = BuildDeckProfile(playerData);
        string result = DetermineAutoSlayResult(snapshot);

        var summary = new Dictionary<string, object?>
        {
            ["iteration"] = iteration,
            ["seed"] = seed,
            ["started_at_utc"] = startedAtUtc,
            ["finished_at_utc"] = finishedAtUtc,
            ["duration_seconds"] = Math.Round((finishedAtUtc - startedAtUtc).TotalSeconds, 3),
            ["log_file"] = logFile,
            ["final_state_type"] = snapshot.GetValueOrDefault("state_type"),
            ["error"] = error,
            ["result"] = result,
            ["highest_act"] = runStats?.HighestAct,
            ["highest_floor"] = runStats?.HighestFloor,
            ["map_choice_counts"] = runStats?.MapChoiceCounts,
            ["rest_choice_counts"] = runStats?.RestChoiceCounts,
            ["shop_purchase_counts"] = runStats?.ShopPurchaseCounts,
            ["cards_added"] = runStats?.CardsAdded,
            ["cards_skipped"] = runStats?.CardsSkipped,
            ["reward_claim_counts"] = runStats?.RewardClaimCounts,
            ["positive_event_choices"] = runStats?.PositiveEventChoices,
            ["negative_event_choices"] = runStats?.NegativeEventChoices,
            ["potions_used"] = runStats?.PotionsUsed,
            ["potions_discarded"] = runStats?.PotionsDiscarded,
            ["deck_profile_end"] = BuildDeckProfileSummary(deckProfile),
            ["strategy_weights"] = runStats != null ? BuildWeightsSummary(runStats.StrategyWeights) : null,
            ["strategy_profile"] = strategyPath
        };

        if (snapshot.TryGetValue("run", out var runObj) && runObj is Dictionary<string, object?> runData)
        {
            summary["act"] = runData.GetValueOrDefault("act");
            summary["floor"] = runData.GetValueOrDefault("floor");
            summary["ascension"] = runData.GetValueOrDefault("ascension");
        }

        if (snapshot.TryGetValue("overlay", out var overlayObj) && overlayObj is Dictionary<string, object?> overlayData)
            summary["final_overlay"] = overlayData.GetValueOrDefault("screen_type");

        if (playerData != null)
        {
            summary["player_character"] = playerData.GetValueOrDefault("character");
            summary["player_hp"] = playerData.GetValueOrDefault("hp");
            summary["player_max_hp"] = playerData.GetValueOrDefault("max_hp");
            summary["player_gold"] = playerData.GetValueOrDefault("gold");
        }

        File.AppendAllText(summaryPath, JsonSerializer.Serialize(summary, _jsonLineOptions) + System.Environment.NewLine);

        var aggregate = BuildAutoSlayAggregate(summaryPath);
        File.WriteAllText(aggregatePath, JsonSerializer.Serialize(aggregate, _jsonOptions));
        _autoSlayRunStats = null;
    }

    private static Dictionary<string, object?> BuildAutoSlayAggregate(string summaryPath)
    {
        int totalRuns = 0;
        int errorRuns = 0;
        int bestFloor = 0;
        string? bestCharacter = null;
        var finalStates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int winRuns = 0;
        int earlyLossRuns = 0;
        double floorSum = 0;
        double goldEndSum = 0;
        int goldEndCount = 0;
        double deckSizeSum = 0;
        int deckSizeCount = 0;
        double cardsAddedSum = 0;
        int cardsAddedCount = 0;
        double potionsUsedSum = 0;
        int potionsUsedCount = 0;
        var recentFloors = new Queue<int>();

        if (File.Exists(summaryPath))
        {
            foreach (string line in File.ReadLines(summaryPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    totalRuns++;

                    if (root.TryGetProperty("error", out var errorElem) && errorElem.ValueKind != JsonValueKind.Null)
                        errorRuns++;

                    int floor = ReadInt(root, "highest_floor", ReadInt(root, "floor"));
                    floorSum += floor;
                    recentFloors.Enqueue(floor);
                    while (recentFloors.Count > 20)
                        recentFloors.Dequeue();

                    if (floor > bestFloor)
                    {
                        bestFloor = floor;
                        if (root.TryGetProperty("player_character", out var charElem) && charElem.ValueKind == JsonValueKind.String)
                            bestCharacter = charElem.GetString();
                    }

                    if (string.Equals(ReadString(root, "result"), "win", StringComparison.OrdinalIgnoreCase))
                        winRuns++;
                    if (string.Equals(ReadString(root, "result"), "loss", StringComparison.OrdinalIgnoreCase) && floor <= 10)
                        earlyLossRuns++;

                    int goldEnd = ReadInt(root, "player_gold", int.MinValue);
                    if (goldEnd != int.MinValue)
                    {
                        goldEndSum += goldEnd;
                        goldEndCount++;
                    }

                    int deckSize = ReadNestedInt(root, "deck_profile_end", "deck_size");
                    if (deckSize > 0)
                    {
                        deckSizeSum += deckSize;
                        deckSizeCount++;
                    }

                    int added = ReadNestedDictTotal(root, "cards_added");
                    cardsAddedSum += added;
                    cardsAddedCount++;

                    int potionsUsed = ReadInt(root, "potions_used");
                    potionsUsedSum += potionsUsed;
                    potionsUsedCount++;

                    string finalState = root.TryGetProperty("final_state_type", out var stateElem) && stateElem.ValueKind == JsonValueKind.String
                        ? stateElem.GetString() ?? "unknown"
                        : "unknown";
                    finalStates[finalState] = finalStates.GetValueOrDefault(finalState) + 1;
                }
                catch
                {
                    errorRuns++;
                }
            }
        }

        return new Dictionary<string, object?>
        {
            ["knowledge_root"] = Path.GetDirectoryName(summaryPath),
            ["total_runs"] = totalRuns,
            ["error_runs"] = errorRuns,
            ["best_floor"] = bestFloor,
            ["best_character"] = bestCharacter,
            ["average_floor"] = totalRuns > 0 ? Math.Round(floorSum / totalRuns, 2) : 0,
            ["recent_average_floor"] = recentFloors.Count > 0 ? Math.Round(recentFloors.Average(), 2) : 0,
            ["win_rate"] = totalRuns > 0 ? Math.Round(winRuns / (double)totalRuns, 4) : 0,
            ["early_loss_rate"] = totalRuns > 0 ? Math.Round(earlyLossRuns / (double)totalRuns, 4) : 0,
            ["average_gold_end"] = goldEndCount > 0 ? Math.Round(goldEndSum / goldEndCount, 2) : 0,
            ["average_deck_size_end"] = deckSizeCount > 0 ? Math.Round(deckSizeSum / deckSizeCount, 2) : 0,
            ["average_cards_added"] = cardsAddedCount > 0 ? Math.Round(cardsAddedSum / cardsAddedCount, 2) : 0,
            ["average_potions_used"] = potionsUsedCount > 0 ? Math.Round(potionsUsedSum / potionsUsedCount, 2) : 0,
            ["final_state_counts"] = finalStates
        };
    }

    private static Dictionary<string, object?> BuildAutoSlayState()
    {
        AutoSlayStrategyProfile strategy;
        lock (_autoSlayLock)
        {
            lock (_autoSlayStrategyLock)
                strategy = _autoSlayStrategy;

            return new Dictionary<string, object?>
            {
                ["is_active"] = _autoSlayLoopTask is { IsCompleted: false },
                ["iteration"] = _autoSlayIteration,
                ["started_at_utc"] = _autoSlayStartedAtUtc,
                ["current_seed"] = _autoSlayCurrentSeed,
                ["current_log_file"] = _autoSlayCurrentLogFile,
                ["knowledge_root"] = GetAutoSlayLearningRoot(),
                ["last_error"] = _autoSlayLastError,
                ["target_ascension"] = _autoSlayTargetAscension,
                ["unlocked_max_ascension"] = _autoSlayUnlockedMaxAscension,
                ["progress_save_path"] = _autoSlayProgressSavePath,
                ["learning"] = new Dictionary<string, object?>
                {
                    ["total_runs"] = strategy.Learning.TotalRuns,
                    ["best_floor"] = strategy.Learning.BestFloor,
                    ["average_floor"] = strategy.Learning.AverageFloor,
                    ["recent_average_floor"] = strategy.Learning.RecentAverageFloor,
                    ["win_rate"] = strategy.Learning.WinRate
                },
                ["strategy_weights"] = BuildWeightsSummary(strategy.Weights)
            };
        }
    }
}
