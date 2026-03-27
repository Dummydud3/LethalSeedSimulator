using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using LethalSeedSimulator.Rules;

namespace LethalSeedSimulator.Extractor;

public sealed class CurrentDumpExtractorAdapter : IRuleExtractorAdapter
{
    public const string AdapterVersion = "decompiled-current";

    public string VersionKey => AdapterVersion;

    public RulePack Extract(string sourceRootPath)
    {
        var roundManagerPath = Path.Combine(sourceRootPath, "Assembly-CSharp", "RoundManager.cs");
        var levelTypePath = Path.Combine(sourceRootPath, "Assembly-CSharp", "SelectableLevel.cs");
        var monoBehaviourPath = Path.Combine(sourceRootPath, "Assets", "MonoBehaviour");
        var sampleScenePath = Path.Combine(sourceRootPath, "Assets", "Scenes", "SampleSceneRelay.unity");

        if (!File.Exists(roundManagerPath))
        {
            throw new FileNotFoundException("RoundManager.cs not found", roundManagerPath);
        }

        if (!File.Exists(levelTypePath))
        {
            throw new FileNotFoundException("SelectableLevel.cs not found", levelTypePath);
        }

        if (!Directory.Exists(monoBehaviourPath))
        {
            throw new DirectoryNotFoundException($"Assets MonoBehaviour folder not found: {monoBehaviourPath}");
        }

        var roundManager = File.ReadAllText(roundManagerPath);
        var levelType = File.ReadAllText(levelTypePath);
        var sampleScene = File.Exists(sampleScenePath) ? File.ReadAllText(sampleScenePath) : string.Empty;

        var offsets = new RngOffsets
        {
            AnomalyRandom = ParseOffset(roundManager, "AnomalyRandom\\s*=\\s*new Random\\(this\\.playersManager\\.randomMapSeed \\+ (\\d+)\\)"),
            EnemySpawnRandom = ParseOffset(roundManager, "EnemySpawnRandom\\s*=\\s*new Random\\(this\\.playersManager\\.randomMapSeed \\+ (\\d+)\\)"),
            OutsideEnemySpawnRandom = ParseOffset(roundManager, "OutsideEnemySpawnRandom\\s*=\\s*new Random\\(this\\.playersManager\\.randomMapSeed \\+ (\\d+)\\)"),
            BreakerBoxRandom = ParseOffset(roundManager, "BreakerBoxRandom\\s*=\\s*new Random\\(this\\.playersManager\\.randomMapSeed \\+ (\\d+)\\)"),
            ScrapValuesRandom = ParseOffset(roundManager, "ScrapValuesRandom\\s*=\\s*new Random\\(StartOfRound\\.Instance\\.randomMapSeed \\+ (\\d+)\\)"),
            SpawnMapObjectsRandom = ParseOffset(roundManager, "SpawnMapObjects\\(\\)[\\s\\S]*?new Random\\(StartOfRound\\.Instance\\.randomMapSeed \\+ (\\d+)\\)"),
            WeatherRandom = ParseOffsetFromFile(Path.Combine(sourceRootPath, "Assembly-CSharp", "StartOfRound.cs"), "new Random\\(this\\.randomMapSeed \\+ (\\d+)\\)")
        };

        var itemByGuid = ParseScrapItems(monoBehaviourPath);
        var levels = ParseLevelsFromAssets(monoBehaviourPath, itemByGuid);
        if (levels.Count == 0)
        {
            throw new InvalidOperationException("No SelectableLevel assets were parsed from Assets/MonoBehaviour.");
        }

        return new RulePack
        {
            Metadata = new RulePackMetadata
            {
                RuleSetVersion = AdapterVersion,
                GameVersionLabel = "decompiled-dump",
                SourceSignature = BuildSourceSignature(roundManager, levelType, sampleScene, string.Join("|", itemByGuid.Keys.OrderBy(x => x)), string.Join("|", levels.Select(x => x.Id))),
                ExtractedAtUtc = DateTimeOffset.UtcNow
            },
            Offsets = offsets,
            Levels = levels,
            GlobalRules = ParseGlobalRules(roundManager, sampleScene)
        };
    }

    private static int ParseOffsetFromFile(string path, string pattern)
    {
        var text = File.ReadAllText(path);
        return ParseOffset(text, pattern);
    }

    private static int ParseOffset(string input, string pattern)
    {
        var match = Regex.Match(input, pattern);
        if (!match.Success)
        {
            return 0;
        }

        return int.Parse(match.Groups[1].Value);
    }

    private static Dictionary<string, ScrapRule> ParseScrapItems(string monoBehaviourPath)
    {
        var map = new Dictionary<string, ScrapRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var assetPath in Directory.GetFiles(monoBehaviourPath, "*.asset"))
        {
            var metaPath = assetPath + ".meta";
            if (!File.Exists(metaPath))
            {
                continue;
            }

            var content = File.ReadAllText(assetPath);
            if (!content.Contains("isScrap: 1", StringComparison.Ordinal))
            {
                continue;
            }

            var guid = ExtractMetaGuid(metaPath);
            if (guid is null)
            {
                continue;
            }

            var itemName = ExtractStringField(content, "itemName") ?? Path.GetFileNameWithoutExtension(assetPath);
            var twoHanded = ExtractIntField(content, "twoHanded") == 1;
            var rawItemId = ExtractIntField(content, "itemId");
            var minValue = ExtractIntField(content, "minValue");
            var maxValue = ExtractIntField(content, "maxValue");
            if (maxValue <= minValue)
            {
                maxValue = minValue + 1;
            }

            var itemId = rawItemId > 0 ? rawItemId : BuildStableItemId(guid);
            map[guid] = new ScrapRule
            {
                ItemId = itemId,
                ItemName = itemName,
                Rarity = 1,
                MinValueInclusive = minValue,
                MaxValueExclusive = maxValue,
                TwoHanded = twoHanded
            };
        }

        return map;
    }

    private static List<LevelRule> ParseLevelsFromAssets(string monoBehaviourPath, IReadOnlyDictionary<string, ScrapRule> itemByGuid)
    {
        var levels = new List<LevelRule>();
        foreach (var levelAsset in Directory.GetFiles(monoBehaviourPath, "*Level.asset"))
        {
            var content = File.ReadAllText(levelAsset);
            if (!content.Contains("m_Script: {fileID: 11500000, guid: 89cc72f2cd6e5da392d2eb839dbf2eba, type: 3}", StringComparison.Ordinal))
            {
                continue;
            }

            var levelId = ExtractIntField(content, "levelID");
            var planetName = ExtractStringField(content, "PlanetName") ?? Path.GetFileNameWithoutExtension(levelAsset);
            var minScrap = ExtractIntField(content, "minScrap");
            var maxScrap = ExtractIntField(content, "maxScrap");
            if (maxScrap <= minScrap)
            {
                maxScrap = minScrap + 1;
            }

            var level = new LevelRule
            {
                Id = levelId.ToString(),
                Name = planetName,
                MinScrap = minScrap,
                MaxScrapExclusive = maxScrap,
                MinTotalScrapValue = ExtractIntField(content, "minTotalScrapValue"),
                MaxTotalScrapValue = ExtractIntField(content, "maxTotalScrapValue"),
                IsChallengeFile = false,
                SpawnableScrap = ParseSpawnableScrap(content, itemByGuid),
                Weathers = ParseWeatherRules(content),
                OverrideWeather = ExtractIntField(content, "overrideWeather") == 1,
                OverrideWeatherType = WeatherName(ExtractIntField(content, "overrideWeatherType")),
                InsideEnemies = ParseEnemyRules(content, "Enemies:", "OutsideEnemies:"),
                OutsideEnemies = ParseEnemyRules(content, "OutsideEnemies:", "DaytimeEnemies:"),
                DaytimeEnemies = ParseEnemyRules(content, "DaytimeEnemies:", "maxOutsideEnemyPowerCount:"),
                SpawnableMapObjects = ParseMapObjectRules(content),
                SpawnableOutsideObjectsCount = ParseOutsideObjectCount(content),
                DungeonFlowTypes = ParseDungeonFlowRules(content)
            };

            if (level.SpawnableScrap.Count > 0)
            {
                levels.Add(level);
            }
        }

        return levels.OrderBy(x => int.TryParse(x.Id, out var id) ? id : int.MaxValue).ToList();
    }

    private static List<ScrapRule> ParseSpawnableScrap(string levelAssetContent, IReadOnlyDictionary<string, ScrapRule> itemByGuid)
    {
        var section = ExtractSection(levelAssetContent, "spawnableScrap:", "minScrap:");
        if (string.IsNullOrWhiteSpace(section))
        {
            return [];
        }

        var results = new List<ScrapRule>();
        var matches = Regex.Matches(
            section,
            "-\\s*spawnableItem:\\s*\\{fileID:\\s*\\d+,\\s*guid:\\s*([0-9a-f]{32}),\\s*type:\\s*2\\}\\s*\\r?\\n\\s*rarity:\\s*(\\d+)",
            RegexOptions.IgnoreCase);

        foreach (Match match in matches)
        {
            var guid = match.Groups[1].Value;
            var rarity = int.Parse(match.Groups[2].Value);
            if (!itemByGuid.TryGetValue(guid, out var item))
            {
                continue;
            }

            results.Add(new ScrapRule
            {
                ItemId = item.ItemId,
                ItemName = item.ItemName,
                Rarity = rarity,
                MinValueInclusive = item.MinValueInclusive,
                MaxValueExclusive = item.MaxValueExclusive,
                TwoHanded = item.TwoHanded
            });
        }

        return results;
    }

    private static List<WeatherRule> ParseWeatherRules(string levelAssetContent)
    {
        var section = ExtractSection(levelAssetContent, "randomWeathers:", "overrideWeather:");
        if (string.IsNullOrWhiteSpace(section))
        {
            return [];
        }

        var matches = Regex.Matches(section, "weatherType:\\s*(-?\\d+)");
        var list = new List<WeatherRule>();
        foreach (Match match in matches)
        {
            var weatherType = int.Parse(match.Groups[1].Value);
            list.Add(new WeatherRule
            {
                WeatherType = WeatherName(weatherType),
                Weight = 1
            });
        }

        return list;
    }

    private static List<EnemyRule> ParseEnemyRules(string levelAssetContent, string start, string end)
    {
        var section = ExtractSection(levelAssetContent, start, end);
        if (string.IsNullOrWhiteSpace(section))
        {
            return [];
        }

        var matches = Regex.Matches(
            section,
            "-\\s*enemyType:\\s*\\{fileID:\\s*\\d+,\\s*guid:\\s*([0-9a-f]{32}),\\s*type:\\s*2\\}\\s*\\r?\\n\\s*rarity:\\s*(\\d+)",
            RegexOptions.IgnoreCase);
        var list = new List<EnemyRule>();
        foreach (Match match in matches)
        {
            list.Add(new EnemyRule
            {
                Id = match.Groups[1].Value,
                Rarity = int.Parse(match.Groups[2].Value)
            });
        }

        return list;
    }

    private static List<MapObjectRule> ParseMapObjectRules(string levelAssetContent)
    {
        var section = ExtractSection(levelAssetContent, "spawnableMapObjects:", "spawnableOutsideObjects:");
        if (string.IsNullOrWhiteSpace(section))
        {
            return [];
        }

        var idMatches = Regex.Matches(
            section,
            "-\\s*prefabToSpawn:\\s*\\{fileID:\\s*\\d+,\\s*guid:\\s*([0-9a-f]{32}),\\s*type:\\s*2\\}",
            RegexOptions.IgnoreCase);
        var keys = Regex.Matches(section, "value:\\s*(-?\\d+(?:\\.\\d+)?)");
        var maxObjectsEstimate = 0;
        foreach (Match key in keys)
        {
            var v = double.Parse(key.Groups[1].Value, CultureInfo.InvariantCulture);
            maxObjectsEstimate = Math.Max(maxObjectsEstimate, (int)Math.Round(v));
        }
        maxObjectsEstimate = Math.Max(maxObjectsEstimate, 1);

        var list = new List<MapObjectRule>();
        foreach (Match id in idMatches)
        {
            list.Add(new MapObjectRule
            {
                Id = id.Groups[1].Value,
                MaxObjectsEstimate = maxObjectsEstimate
            });
        }

        return list;
    }

    private static List<DungeonFlowRule> ParseDungeonFlowRules(string levelAssetContent)
    {
        var section = ExtractSection(levelAssetContent, "dungeonFlowTypes:", "spawnableMapObjects:");
        if (string.IsNullOrWhiteSpace(section))
        {
            return [];
        }

        var matches = Regex.Matches(
            section,
            "-\\s*id:\\s*(-?\\d+)\\s*\\r?\\n\\s*rarity:\\s*(\\d+)",
            RegexOptions.IgnoreCase);
        var list = new List<DungeonFlowRule>();
        foreach (Match match in matches)
        {
            list.Add(new DungeonFlowRule
            {
                Id = int.Parse(match.Groups[1].Value),
                Rarity = int.Parse(match.Groups[2].Value)
            });
        }

        return list;
    }

    private static int ParseOutsideObjectCount(string levelAssetContent)
    {
        var section = ExtractSection(levelAssetContent, "spawnableOutsideObjects:", "spawnableOutsideWaterObjects:");
        if (string.IsNullOrWhiteSpace(section))
        {
            return 0;
        }

        var matches = Regex.Matches(
            section,
            "-\\s*spawnableObject:\\s*\\{fileID:\\s*\\d+,\\s*guid:\\s*([0-9a-f]{32}),\\s*type:\\s*2\\}",
            RegexOptions.IgnoreCase);
        return matches.Count;
    }

    private static GlobalRules ParseGlobalRules(string roundManagerCode, string sampleScene)
    {
        var sceneSettings = ParseSceneRoundManagerSettings(sampleScene);
        var scrapAmountFromCode = ParseFloat(roundManagerCode, "scrapAmountMultiplier\\s*=\\s*([0-9]+(?:\\.[0-9]+)?)", 1f);
        return new GlobalRules
        {
            ScrapAmountMultiplier = sceneSettings.ScrapAmountMultiplier ?? scrapAmountFromCode,
            ScrapValueMultiplier = sceneSettings.ScrapValueMultiplier ?? 0.4f,
            MapSizeMultiplier = sceneSettings.MapSizeMultiplier ?? 1f,
            HourTimeBetweenEnemySpawnBatches = sceneSettings.HourTimeBetweenEnemySpawnBatches ?? 2f,
            MinEnemiesToSpawn = sceneSettings.MinEnemiesToSpawn ?? 0,
            MinOutsideEnemiesToSpawn = sceneSettings.MinOutsideEnemiesToSpawn ?? 0,
            PowerOffAtStartChance = 0.08f
        };
    }

    private static SceneRoundManagerSettings ParseSceneRoundManagerSettings(string sampleScene)
    {
        if (string.IsNullOrWhiteSpace(sampleScene))
        {
            return new SceneRoundManagerSettings();
        }

        var blockMatch = Regex.Match(
            sampleScene,
            "m_Script:\\s*\\{fileID:\\s*11500000,\\s*guid:\\s*c772563b62eda8b681c04c691cd6f847,\\s*type:\\s*3\\}(?<body>[\\s\\S]*?)---\\s*!u!",
            RegexOptions.IgnoreCase);

        var block = blockMatch.Success ? blockMatch.Groups["body"].Value : sampleScene;
        return new SceneRoundManagerSettings
        {
            ScrapValueMultiplier = ParseFloat(block, "^\\s*scrapValueMultiplier:\\s*(-?\\d+(?:\\.\\d+)?)$", null, true),
            ScrapAmountMultiplier = ParseFloat(block, "^\\s*scrapAmountMultiplier:\\s*(-?\\d+(?:\\.\\d+)?)$", null, true),
            MapSizeMultiplier = ParseFloat(block, "^\\s*mapSizeMultiplier:\\s*(-?\\d+(?:\\.\\d+)?)$", null, true),
            HourTimeBetweenEnemySpawnBatches = ParseFloat(block, "^\\s*hourTimeBetweenEnemySpawnBatches:\\s*(-?\\d+(?:\\.\\d+)?)$", null, true),
            MinEnemiesToSpawn = ParseInt(block, "^\\s*minEnemiesToSpawn:\\s*(-?\\d+)$"),
            MinOutsideEnemiesToSpawn = ParseInt(block, "^\\s*minOutsideEnemiesToSpawn:\\s*(-?\\d+)$")
        };
    }

    private static string ExtractSection(string content, string startMarker, string endMarker)
    {
        var start = content.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        start += startMarker.Length;
        var end = content.IndexOf(endMarker, start, StringComparison.Ordinal);
        if (end < 0)
        {
            end = content.Length;
        }

        return content[start..end];
    }

    private static string? ExtractMetaGuid(string metaPath)
    {
        var match = Regex.Match(File.ReadAllText(metaPath), "^guid:\\s*([0-9a-f]{32})", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ExtractStringField(string content, string fieldName)
    {
        var match = Regex.Match(content, $"^\\s*{Regex.Escape(fieldName)}:\\s*(.+)$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim().Trim('"') : null;
    }

    private static int ExtractIntField(string content, string fieldName)
    {
        var match = Regex.Match(content, $"^\\s*{Regex.Escape(fieldName)}:\\s*(-?\\d+)$", RegexOptions.Multiline);
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    private static float ParseFloat(string input, string pattern, float fallback)
    {
        var match = Regex.Match(input, pattern);
        return match.Success ? float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : fallback;
    }

    private static float? ParseFloat(string input, string pattern, float? fallback, bool multiline)
    {
        var options = multiline ? RegexOptions.Multiline : RegexOptions.None;
        var match = Regex.Match(input, pattern, options);
        return match.Success ? float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : fallback;
    }

    private static int? ParseInt(string input, string pattern)
    {
        var match = Regex.Match(input, pattern, RegexOptions.Multiline);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    private static int BuildStableItemId(string guid)
    {
        var prefix = guid[..8];
        return Convert.ToInt32(prefix, 16) & 0x7FFFFFFF;
    }

    private static string WeatherName(int weatherType) => weatherType switch
    {
        -1 => "None",
        0 => "DustClouds",
        1 => "Rainy",
        2 => "Stormy",
        3 => "Foggy",
        4 => "Flooded",
        5 => "Eclipsed",
        _ => $"Weather{weatherType}"
    };

    private static string BuildSourceSignature(params string[] filesContent)
    {
        using var sha = SHA256.Create();
        var blob = string.Join("\n--\n", filesContent);
        var bytes = Encoding.UTF8.GetBytes(blob);
        return Convert.ToHexString(sha.ComputeHash(bytes));
    }

    private sealed class SceneRoundManagerSettings
    {
        public float? ScrapValueMultiplier { get; init; }

        public float? ScrapAmountMultiplier { get; init; }

        public float? MapSizeMultiplier { get; init; }

        public float? HourTimeBetweenEnemySpawnBatches { get; init; }

        public int? MinEnemiesToSpawn { get; init; }

        public int? MinOutsideEnemiesToSpawn { get; init; }
    }
}
