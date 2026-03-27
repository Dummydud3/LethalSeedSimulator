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
        var enemyByGuid = ParseEnemyNames(monoBehaviourPath);
        var prefabByGuid = ParsePrefabNames(sourceRootPath);
        var dungeonFlowCatalog = ParseDungeonFlowCatalog(sourceRootPath, sampleScene, monoBehaviourPath);
        var itemGroupNames = ParseItemGroupNames(monoBehaviourPath);
        var placementRules = ParsePlacementRules(sourceRootPath, itemGroupNames);
        var levels = ParseLevelsFromAssets(monoBehaviourPath, itemByGuid, enemyByGuid, prefabByGuid);
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
            GlobalRules = ParseGlobalRules(roundManager, sampleScene, placementRules, itemByGuid, dungeonFlowCatalog)
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
                TwoHanded = twoHanded,
                SpawnPositionGroupIds = ParseSpawnPositionGroups(content)
            };
        }

        return map;
    }

    private static List<LevelRule> ParseLevelsFromAssets(
        string monoBehaviourPath,
        IReadOnlyDictionary<string, ScrapRule> itemByGuid,
        IReadOnlyDictionary<string, string> enemyByGuid,
        IReadOnlyDictionary<string, string> prefabByGuid)
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
                InsideEnemies = ParseEnemyRules(content, "Enemies:", "OutsideEnemies:", enemyByGuid),
                OutsideEnemies = ParseEnemyRules(content, "OutsideEnemies:", "DaytimeEnemies:", enemyByGuid),
                DaytimeEnemies = ParseEnemyRules(content, "DaytimeEnemies:", "maxOutsideEnemyPowerCount:", enemyByGuid),
                SpawnableMapObjects = ParseMapObjectRules(content, prefabByGuid),
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
                TwoHanded = item.TwoHanded,
                SpawnPositionGroupIds = item.SpawnPositionGroupIds
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

    private static List<EnemyRule> ParseEnemyRules(
        string levelAssetContent,
        string start,
        string end,
        IReadOnlyDictionary<string, string> enemyByGuid)
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
                Name = enemyByGuid.TryGetValue(match.Groups[1].Value, out var name) ? name : match.Groups[1].Value,
                Rarity = int.Parse(match.Groups[2].Value)
            });
        }

        return list;
    }

    private static List<MapObjectRule> ParseMapObjectRules(
        string levelAssetContent,
        IReadOnlyDictionary<string, string> prefabByGuid)
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
                Name = prefabByGuid.TryGetValue(id.Groups[1].Value, out var name) ? name : id.Groups[1].Value,
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

    private static GlobalRules ParseGlobalRules(
        string roundManagerCode,
        string sampleScene,
        PlacementRules placementRules,
        IReadOnlyDictionary<string, ScrapRule> itemByGuid,
        IReadOnlyList<DungeonFlowDefinition> dungeonFlowCatalog)
    {
        var sceneSettings = ParseSceneRoundManagerSettings(sampleScene);
        var scrapAmountFromCode = ParseFloat(roundManagerCode, "scrapAmountMultiplier\\s*=\\s*([0-9]+(?:\\.[0-9]+)?)", 1f);
        var apparatusItem = itemByGuid.Values.FirstOrDefault(x =>
            string.Equals(x.ItemName, "Apparatus", StringComparison.OrdinalIgnoreCase));
        return new GlobalRules
        {
            ScrapAmountMultiplier = sceneSettings.ScrapAmountMultiplier ?? scrapAmountFromCode,
            ScrapValueMultiplier = sceneSettings.ScrapValueMultiplier ?? 0.4f,
            MapSizeMultiplier = sceneSettings.MapSizeMultiplier ?? 1f,
            HourTimeBetweenEnemySpawnBatches = sceneSettings.HourTimeBetweenEnemySpawnBatches ?? 2f,
            MinEnemiesToSpawn = sceneSettings.MinEnemiesToSpawn ?? 0,
            MinOutsideEnemiesToSpawn = sceneSettings.MinOutsideEnemiesToSpawn ?? 0,
            PowerOffAtStartChance = 0.08f,
            TotalRandomScrapSpawnPoints = placementRules.TotalRandomScrapSpawnPoints,
            EstimatedLockableDoorCount = placementRules.EstimatedLockableDoorCount,
            EstimatedApparatusSpawnerCount = placementRules.EstimatedApparatusSpawnerCount,
            SpawnGroupCapacities = placementRules.SpawnGroupCapacities,
            ApparatusItemId = apparatusItem?.ItemId ?? 3,
            ApparatusItemName = apparatusItem?.ItemName ?? "Apparatus",
            ApparatusMinValueInclusive = apparatusItem?.MinValueInclusive ?? 0,
            ApparatusMaxValueExclusive = apparatusItem?.MaxValueExclusive ?? 0,
            DungeonFlows = dungeonFlowCatalog.ToList()
        };
    }

    private static List<string> ParseSpawnPositionGroups(string itemAssetContent)
    {
        var section = ExtractSection(itemAssetContent, "spawnPositionTypes:", "twoHanded:");
        if (string.IsNullOrWhiteSpace(section))
        {
            return [];
        }

        var matches = Regex.Matches(
            section,
            "guid:\\s*([0-9a-f]{32})",
            RegexOptions.IgnoreCase);
        var groups = new List<string>();
        foreach (Match match in matches)
        {
            groups.Add(match.Groups[1].Value);
        }

        return groups;
    }

    private static Dictionary<string, string> ParseItemGroupNames(string monoBehaviourPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assetPath in Directory.GetFiles(monoBehaviourPath, "*.asset"))
        {
            var metaPath = assetPath + ".meta";
            if (!File.Exists(metaPath))
            {
                continue;
            }

            var content = File.ReadAllText(assetPath);
            if (!content.Contains("itemSpawnTypeName:", StringComparison.Ordinal))
            {
                continue;
            }

            var guid = ExtractMetaGuid(metaPath);
            if (string.IsNullOrWhiteSpace(guid))
            {
                continue;
            }

            var name = ExtractStringField(content, "itemSpawnTypeName");
            if (string.IsNullOrWhiteSpace(name))
            {
                name = Path.GetFileNameWithoutExtension(assetPath);
            }

            result[guid] = name;
        }

        return result;
    }

    private static Dictionary<string, string> ParseEnemyNames(string monoBehaviourPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assetPath in Directory.GetFiles(monoBehaviourPath, "*.asset"))
        {
            var metaPath = assetPath + ".meta";
            if (!File.Exists(metaPath))
            {
                continue;
            }

            var content = File.ReadAllText(assetPath);
            if (!content.Contains("enemyName:", StringComparison.Ordinal))
            {
                continue;
            }

            var guid = ExtractMetaGuid(metaPath);
            if (string.IsNullOrWhiteSpace(guid))
            {
                continue;
            }

            var name = ExtractStringField(content, "enemyName");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            result[guid] = name;
        }

        return result;
    }

    private static Dictionary<string, string> ParsePrefabNames(string sourceRootPath)
    {
        var gameObjectPath = Path.Combine(sourceRootPath, "Assets", "GameObject");
        if (!Directory.Exists(gameObjectPath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prefabPath in Directory.GetFiles(gameObjectPath, "*.prefab", SearchOption.AllDirectories))
        {
            var metaPath = prefabPath + ".meta";
            if (!File.Exists(metaPath))
            {
                continue;
            }

            var guid = ExtractMetaGuid(metaPath);
            if (string.IsNullOrWhiteSpace(guid))
            {
                continue;
            }

            result[guid] = Path.GetFileNameWithoutExtension(prefabPath);
        }

        return result;
    }

    private static List<DungeonFlowDefinition> ParseDungeonFlowCatalog(string sourceRootPath, string sampleScene, string monoBehaviourPath)
    {
        if (string.IsNullOrWhiteSpace(sampleScene))
        {
            return [];
        }

        var guidToMonoAssetPath = BuildGuidToAssetPathMap(monoBehaviourPath, "*.asset");
        var gameObjectPath = Path.Combine(sourceRootPath, "Assets", "GameObject");
        var guidToPrefabPath = BuildGuidToAssetPathMap(gameObjectPath, "*.prefab");
        var spawnSyncedGuid = ParseGuidFromMeta(Path.Combine(sourceRootPath, "Assets", "Scripts", "Assembly-CSharp", "SpawnSyncedObject.cs.meta"));
        var doorLockGuid = ParseGuidFromMeta(Path.Combine(sourceRootPath, "Assets", "Scripts", "Assembly-CSharp", "DoorLock.cs.meta"));
        var apparatusPrefabGuid = ParseGuidFromMeta(Path.Combine(gameObjectPath, "LungApparatus.prefab.meta"));

        var blockMatch = Regex.Match(
            sampleScene,
            "dungeonFlowTypes:\\s*(?<body>[\\s\\S]*?)\\s*dungeonGenerator:\\s*\\{",
            RegexOptions.IgnoreCase);
        if (!blockMatch.Success)
        {
            return [];
        }

        var body = blockMatch.Groups["body"].Value;
        var flowMatches = Regex.Matches(
            body,
            "-\\s*dungeonFlow:\\s*\\{fileID:\\s*\\d+,\\s*guid:\\s*([0-9a-f]{32}),\\s*type:\\s*2\\}",
            RegexOptions.IgnoreCase);

        var result = new List<DungeonFlowDefinition>();
        var id = 0;
        foreach (Match match in flowMatches)
        {
            var guid = match.Groups[1].Value;
            var flowPath = guidToMonoAssetPath.TryGetValue(guid, out var p) ? p : null;
            var flowContent = flowPath is not null && File.Exists(flowPath) ? File.ReadAllText(flowPath) : string.Empty;
            var name = ExtractStringField(flowContent, "m_Name");
            if (string.IsNullOrWhiteSpace(name))
            {
                name = flowPath is not null ? Path.GetFileNameWithoutExtension(flowPath) : guid;
            }

            var tilePoolPrefabs = CollectFlowTilePoolPrefabs(flowContent, guidToMonoAssetPath);
            var lockCount = 0;
            var apparatusCount = 0;
            foreach (var prefabGuid in tilePoolPrefabs)
            {
                if (!guidToPrefabPath.TryGetValue(prefabGuid, out var prefabPath))
                {
                    continue;
                }

                var prefabContent = File.ReadAllText(prefabPath);
                lockCount += CountLockableDoorMarkers(prefabContent, doorLockGuid);
                apparatusCount += CountApparatusMarkers(prefabContent, spawnSyncedGuid, apparatusPrefabGuid);
            }

            result.Add(new DungeonFlowDefinition
            {
                Id = id,
                Name = name,
                Theme = ClassifyDungeonFlowTheme(name),
                EstimatedLockableDoorCount = lockCount,
                EstimatedApparatusSpawnerCount = apparatusCount,
                TilePoolPrefabCount = tilePoolPrefabs.Count
            });
            id++;
        }

        return result;
    }

    private static Dictionary<string, string> BuildGuidToAssetPathMap(string rootPath, string pattern)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(rootPath))
        {
            return result;
        }

        foreach (var assetPath in Directory.GetFiles(rootPath, pattern, SearchOption.AllDirectories))
        {
            var guid = ExtractMetaGuid(assetPath + ".meta");
            if (string.IsNullOrWhiteSpace(guid))
            {
                continue;
            }

            result[guid] = assetPath;
        }

        return result;
    }

    private static HashSet<string> CollectFlowTilePoolPrefabs(
        string flowContent,
        IReadOnlyDictionary<string, string> guidToMonoAssetPath)
    {
        var prefabGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(flowContent))
        {
            return prefabGuids;
        }

        var tileSetGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(
                     flowContent,
                     "DungeonArchetypes:\\s*(?:\\r?\\n\\s*-\\s*\\{fileID:\\s*\\d+,\\s*guid:\\s*([0-9a-f]{32}),\\s*type:\\s*2\\})+",
                     RegexOptions.IgnoreCase))
        {
            var block = match.Value;
            foreach (Match guidMatch in Regex.Matches(block, "guid:\\s*([0-9a-f]{32})", RegexOptions.IgnoreCase))
            {
                var archetypeGuid = guidMatch.Groups[1].Value;
                if (!guidToMonoAssetPath.TryGetValue(archetypeGuid, out var archetypePath))
                {
                    continue;
                }

                var archetypeContent = File.ReadAllText(archetypePath);
                foreach (Match tileSetMatch in Regex.Matches(archetypeContent, "guid:\\s*([0-9a-f]{32})", RegexOptions.IgnoreCase))
                {
                    tileSetGuids.Add(tileSetMatch.Groups[1].Value);
                }
            }
        }

        foreach (Match nodeTileSetMatch in Regex.Matches(
                     flowContent,
                     "TileSets:\\s*(?:\\r?\\n\\s*-\\s*\\{fileID:\\s*\\d+,\\s*guid:\\s*([0-9a-f]{32}),\\s*type:\\s*2\\})+",
                     RegexOptions.IgnoreCase))
        {
            foreach (Match guidMatch in Regex.Matches(nodeTileSetMatch.Value, "guid:\\s*([0-9a-f]{32})", RegexOptions.IgnoreCase))
            {
                tileSetGuids.Add(guidMatch.Groups[1].Value);
            }
        }

        foreach (var tileSetGuid in tileSetGuids)
        {
            if (!guidToMonoAssetPath.TryGetValue(tileSetGuid, out var tileSetPath))
            {
                continue;
            }

            var tileSetContent = File.ReadAllText(tileSetPath);
            foreach (Match tileMatch in Regex.Matches(
                         tileSetContent,
                         "Value:\\s*\\{fileID:\\s*\\d+,\\s*guid:\\s*([0-9a-f]{32}),\\s*type:\\s*2\\}",
                         RegexOptions.IgnoreCase))
            {
                prefabGuids.Add(tileMatch.Groups[1].Value);
            }
        }

        return prefabGuids;
    }

    private static int CountLockableDoorMarkers(string prefabContent, string? doorLockGuid)
    {
        if (string.IsNullOrWhiteSpace(doorLockGuid))
        {
            return 0;
        }

        var doorMatches = Regex.Matches(
            prefabContent,
            $"m_Script:\\s*\\{{fileID:\\s*11500000,\\s*guid:\\s*{Regex.Escape(doorLockGuid)},\\s*type:\\s*3\\}}[\\s\\S]*?canBeLocked:\\s*(\\d+)",
            RegexOptions.IgnoreCase);
        var count = 0;
        foreach (Match match in doorMatches)
        {
            if (int.Parse(match.Groups[1].Value) == 1)
            {
                count++;
            }
        }

        return count;
    }

    private static int CountApparatusMarkers(string prefabContent, string? spawnSyncedGuid, string? apparatusPrefabGuid)
    {
        if (string.IsNullOrWhiteSpace(spawnSyncedGuid) || string.IsNullOrWhiteSpace(apparatusPrefabGuid))
        {
            return 0;
        }

        return Regex.Matches(
            prefabContent,
            $"m_Script:\\s*\\{{fileID:\\s*11500000,\\s*guid:\\s*{Regex.Escape(spawnSyncedGuid)},\\s*type:\\s*3\\}}[\\s\\S]*?spawnPrefab:\\s*\\{{fileID:\\s*1709254077376921,\\s*guid:\\s*{Regex.Escape(apparatusPrefabGuid)},\\s*type:\\s*2\\}}",
            RegexOptions.IgnoreCase).Count;
    }

    private static string ClassifyDungeonFlowTheme(string flowName)
    {
        if (flowName.Contains("Level2Flow", StringComparison.OrdinalIgnoreCase))
        {
            return "Mansion";
        }

        if (flowName.Contains("Level3Flow", StringComparison.OrdinalIgnoreCase))
        {
            return "Mineshaft";
        }

        if (flowName.Contains("Level1Flow", StringComparison.OrdinalIgnoreCase))
        {
            return "Factory";
        }

        return "Unknown";
    }

    private static PlacementRules ParsePlacementRules(string sourceRootPath, IReadOnlyDictionary<string, string> itemGroupNames)
    {
        var gameObjectPath = Path.Combine(sourceRootPath, "Assets", "GameObject");
        if (!Directory.Exists(gameObjectPath))
        {
            return new PlacementRules();
        }

        var randomScrapGuid = ParseGuidFromMeta(Path.Combine(sourceRootPath, "Assets", "Scripts", "Assembly-CSharp", "RandomScrapSpawn.cs.meta"));
        var spawnSyncedGuid = ParseGuidFromMeta(Path.Combine(sourceRootPath, "Assets", "Scripts", "Assembly-CSharp", "SpawnSyncedObject.cs.meta"));
        var doorLockGuid = ParseGuidFromMeta(Path.Combine(sourceRootPath, "Assets", "Scripts", "Assembly-CSharp", "DoorLock.cs.meta"));
        var apparatusPrefabGuid = ParseGuidFromMeta(Path.Combine(gameObjectPath, "LungApparatus.prefab.meta"));

        var totalRandomScrap = 0;
        var groupCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var estimatedLocks = 0;
        var estimatedApparatusSpawners = 0;

        foreach (var prefabPath in Directory.GetFiles(gameObjectPath, "*.prefab"))
        {
            var content = File.ReadAllText(prefabPath);

            if (!string.IsNullOrWhiteSpace(randomScrapGuid))
            {
                var matches = Regex.Matches(
                    content,
                    $"m_Script:\\s*\\{{fileID:\\s*11500000,\\s*guid:\\s*{Regex.Escape(randomScrapGuid)},\\s*type:\\s*3\\}}[\\s\\S]*?spawnableItems:\\s*\\{{fileID:\\s*\\d+,\\s*guid:\\s*([0-9a-f]{{32}}),\\s*type:\\s*2\\}}",
                    RegexOptions.IgnoreCase);
                foreach (Match match in matches)
                {
                    totalRandomScrap++;
                    var groupGuid = match.Groups[1].Value;
                    groupCounts[groupGuid] = groupCounts.TryGetValue(groupGuid, out var c) ? c + 1 : 1;
                }
            }

            if (!string.IsNullOrWhiteSpace(doorLockGuid))
            {
                var doorMatches = Regex.Matches(
                    content,
                    $"m_Script:\\s*\\{{fileID:\\s*11500000,\\s*guid:\\s*{Regex.Escape(doorLockGuid)},\\s*type:\\s*3\\}}[\\s\\S]*?canBeLocked:\\s*(\\d+)",
                    RegexOptions.IgnoreCase);
                foreach (Match match in doorMatches)
                {
                    if (int.Parse(match.Groups[1].Value) == 1)
                    {
                        estimatedLocks++;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(spawnSyncedGuid) && !string.IsNullOrWhiteSpace(apparatusPrefabGuid))
            {
                var apparatusMatches = Regex.Matches(
                    content,
                    $"m_Script:\\s*\\{{fileID:\\s*11500000,\\s*guid:\\s*{Regex.Escape(spawnSyncedGuid)},\\s*type:\\s*3\\}}[\\s\\S]*?spawnPrefab:\\s*\\{{fileID:\\s*1709254077376921,\\s*guid:\\s*{Regex.Escape(apparatusPrefabGuid)},\\s*type:\\s*2\\}}",
                    RegexOptions.IgnoreCase);
                estimatedApparatusSpawners += apparatusMatches.Count;
            }
        }

        var capacities = groupCounts
            .OrderByDescending(x => x.Value)
            .Select(x => new SpawnGroupCapacityRule
            {
                GroupId = x.Key,
                GroupName = itemGroupNames.TryGetValue(x.Key, out var name) ? name : x.Key,
                Count = x.Value
            })
            .ToList();

        return new PlacementRules
        {
            TotalRandomScrapSpawnPoints = totalRandomScrap,
            EstimatedLockableDoorCount = estimatedLocks,
            EstimatedApparatusSpawnerCount = estimatedApparatusSpawners,
            SpawnGroupCapacities = capacities
        };
    }

    private static string? ParseGuidFromMeta(string metaPath)
    {
        if (!File.Exists(metaPath))
        {
            return null;
        }

        var match = Regex.Match(File.ReadAllText(metaPath), "^guid:\\s*([0-9a-f]{32})", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
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

    private sealed class PlacementRules
    {
        public int TotalRandomScrapSpawnPoints { get; init; }

        public int EstimatedLockableDoorCount { get; init; }

        public int EstimatedApparatusSpawnerCount { get; init; }

        public List<SpawnGroupCapacityRule> SpawnGroupCapacities { get; init; } = [];
    }
}
