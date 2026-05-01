using System;
using System.Collections.Generic;
using System.Text.Json;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

public static partial class McpMod
{
    private static Dictionary<string, object?> ExecuteReturnToMainMenu()
    {
        if (NGame.Instance == null)
            return Error("NGame is not ready");

        _ = NGame.Instance.ReturnToMainMenu();

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Returning to main menu"
        };
    }

    private static Dictionary<string, object?> ExecuteStartSingleplayerRun(Dictionary<string, JsonElement> data)
    {
        if (NGame.Instance == null)
            return Error("NGame is not ready");
        if (RunManager.Instance.IsInProgress)
            return Error("A run is already in progress. Use return_to_main_menu first.");

        string character = ReadString(data, "character") ?? "ironclad";
        string seed = ReadString(data, "seed") ?? Guid.NewGuid().ToString("N")[..12];
        int ascension = ReadInt(data, "ascension", 0);
        if (ascension < 0)
            return Error("ascension must be >= 0");

        var acts = new List<ActModel>
        {
            ModelDb.Act<Underdocks>(),
            ModelDb.Act<Overgrowth>(),
            ModelDb.Act<Hive>()
        };

        string normalized = character.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "ironclad":
            case "the ironclad":
            case "铁甲战士":
            case "战士":
                _ = NGame.Instance.StartNewSingleplayerRun(
                    ModelDb.Character<Ironclad>(),
                    true,
                    acts,
                    Array.Empty<ModifierModel>(),
                    seed,
                    GameMode.Standard,
                    ascension,
                    null);
                character = "ironclad";
                break;

            case "silent":
            case "the silent":
            case "猎人":
            case "静默猎手":
                _ = NGame.Instance.StartNewSingleplayerRun(
                    ModelDb.Character<Silent>(),
                    true,
                    acts,
                    Array.Empty<ModifierModel>(),
                    seed,
                    GameMode.Standard,
                    ascension,
                    null);
                character = "silent";
                break;

            default:
                return Error($"Unsupported character: {character}");
        }

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Starting {character} run at ascension {ascension}",
            ["seed"] = seed,
            ["ascension"] = ascension,
            ["character"] = character
        };
    }

    private static string? ReadString(Dictionary<string, JsonElement> data, string key)
    {
        return data.TryGetValue(key, out var elem) && elem.ValueKind == JsonValueKind.String
            ? elem.GetString()
            : null;
    }

    private static int ReadInt(Dictionary<string, JsonElement> data, string key, int fallback)
    {
        if (!data.TryGetValue(key, out var elem))
            return fallback;

        if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out int value))
            return value;

        if (elem.ValueKind == JsonValueKind.String && int.TryParse(elem.GetString(), out value))
            return value;

        return fallback;
    }
}
