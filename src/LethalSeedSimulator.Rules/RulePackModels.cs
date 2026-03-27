using System.Text.Json.Serialization;

namespace LethalSeedSimulator.Rules;

public sealed class RulePack
{
    public required RulePackMetadata Metadata { get; init; }

    public required RngOffsets Offsets { get; init; }

    public required List<LevelRule> Levels { get; init; }

    public GlobalRules GlobalRules { get; init; } = new();
}

public sealed class RulePackMetadata
{
    public required string RuleSetVersion { get; init; }

    public required string GameVersionLabel { get; init; }

    public required string SourceSignature { get; init; }

    public required DateTimeOffset ExtractedAtUtc { get; init; }
}

public sealed class RngOffsets
{
    public int LevelRandom { get; init; } = 0;

    public int AnomalyRandom { get; init; } = 5;

    public int EnemySpawnRandom { get; init; } = 40;

    public int OutsideEnemySpawnRandom { get; init; } = 41;

    public int BreakerBoxRandom { get; init; } = 20;

    public int ScrapValuesRandom { get; init; } = 210;

    public int SpawnMapObjectsRandom { get; init; } = 587;

    public int WeatherRandom { get; init; } = 35;
}

public sealed class LevelRule
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public int MinScrap { get; init; }

    public int MaxScrapExclusive { get; init; }

    public int MinTotalScrapValue { get; init; }

    public int MaxTotalScrapValue { get; init; }

    public bool IsChallengeFile { get; init; }

    public List<ScrapRule> SpawnableScrap { get; init; } = [];

    public List<WeatherRule> Weathers { get; init; } = [];

    public bool OverrideWeather { get; init; }

    public string OverrideWeatherType { get; init; } = "None";

    public List<EnemyRule> InsideEnemies { get; init; } = [];

    public List<EnemyRule> OutsideEnemies { get; init; } = [];

    public List<EnemyRule> DaytimeEnemies { get; init; } = [];

    public List<MapObjectRule> SpawnableMapObjects { get; init; } = [];

    public int SpawnableOutsideObjectsCount { get; init; }

    public List<DungeonFlowRule> DungeonFlowTypes { get; init; } = [];
}

public sealed class ScrapRule
{
    public required int ItemId { get; init; }

    public required string ItemName { get; init; }

    public required int Rarity { get; init; }

    public int MinValueInclusive { get; init; }

    public int MaxValueExclusive { get; init; }

    public bool TwoHanded { get; init; }

    public List<string> SpawnPositionGroupIds { get; init; } = [];
}

public sealed class WeatherRule
{
    public required string WeatherType { get; init; }

    public required int Weight { get; init; }
}

public sealed class SeedReport
{
    public required string Version { get; init; }

    public required string LevelId { get; init; }

    public required int Seed { get; init; }

    public required string Weather { get; init; }

    public required int RunSeed { get; init; }

    public required int WeatherSeed { get; init; }

    public required int ScrapCount { get; init; }

    public required int TotalScrapValue { get; init; }

    public required List<ScrapRollResult> ScrapRolls { get; init; }

    public required EnemySpawnReport EnemySpawn { get; init; }

    public required HazardPropReport HazardProp { get; init; }

    public required WeatherReport WeatherReport { get; init; }

    public required KeySpawnReport Keys { get; init; }

    public required ApparatusReport Apparatus { get; init; }

    [JsonIgnore]
    public IReadOnlyDictionary<int, int> ItemCounts =>
        ScrapRolls.GroupBy(x => x.ItemId).ToDictionary(x => x.Key, x => x.Count());
}

public sealed class ScrapRollResult
{
    public required int ItemId { get; init; }

    public required string ItemName { get; init; }

    public required int Value { get; init; }
}

public sealed class GlobalRules
{
    public float ScrapAmountMultiplier { get; init; } = 1f;

    public float ScrapValueMultiplier { get; init; } = 0.4f;

    public float MapSizeMultiplier { get; init; } = 1f;

    public float HourTimeBetweenEnemySpawnBatches { get; init; } = 2f;

    public int MinEnemiesToSpawn { get; init; }

    public int MinOutsideEnemiesToSpawn { get; init; }

    public float PowerOffAtStartChance { get; init; } = 0.08f;

    public int TotalRandomScrapSpawnPoints { get; init; }

    public int EstimatedLockableDoorCount { get; init; }

    public int EstimatedApparatusSpawnerCount { get; init; }

    public List<SpawnGroupCapacityRule> SpawnGroupCapacities { get; init; } = [];

    public int ApparatusItemId { get; init; } = 3;

    public string ApparatusItemName { get; init; } = "Apparatus";

    public int ApparatusMinValueInclusive { get; init; }

    public int ApparatusMaxValueExclusive { get; init; }

    public List<DungeonFlowDefinition> DungeonFlows { get; init; } = [];
}

public sealed class EnemyRule
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required int Rarity { get; init; }
}

public sealed class MapObjectRule
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public int MaxObjectsEstimate { get; init; }
}

public sealed class DungeonFlowRule
{
    public int Id { get; init; }

    public int Rarity { get; init; }
}

public sealed class SimulationRequest
{
    public required string LevelId { get; init; }

    public required int RunSeed { get; init; }

    public int? WeatherSeed { get; init; }

    public bool IsChallengeFile { get; init; }

    public int ConnectedPlayersOnServer { get; init; }

    public int DaysPlayersSurvivedInARow { get; init; }
}

public sealed class EnemySpawnReport
{
    public required List<EnemySpawnRoll> Inside { get; init; }

    public required List<EnemySpawnRoll> Outside { get; init; }

    public required List<EnemySpawnRoll> Daytime { get; init; }
}

public sealed class EnemySpawnRoll
{
    public required string EnemyName { get; init; }

    public required string Category { get; init; }
}

public sealed class HazardPropReport
{
    public bool PowerOffAtStart { get; init; }

    public int EstimatedOutsideHazards { get; init; }

    public List<MapObjectSpawn> MapObjects { get; init; } = [];

    public int SteamValveBurstMin { get; init; }

    public int SteamValveBurstMax { get; init; }
}

public sealed class MapObjectSpawn
{
    public required string ObjectName { get; init; }

    public int Count { get; init; }
}

public sealed class WeatherReport
{
    public required Dictionary<string, string> AssignedWeatherByLevelId { get; init; }
}

public sealed class KeySpawnReport
{
    public int KeyCount { get; init; }

    public int DungeonSeed { get; init; }

    public int DungeonFlowId { get; init; }

    public string DungeonFlowName { get; init; } = "Unknown";

    public string DungeonFlowTheme { get; init; } = "Unknown";

    public List<KeySpawnResult> Placements { get; init; } = [];
}

public sealed class KeySpawnResult
{
    public required string PlacementId { get; init; }

    public int KeyItemId { get; init; } = 14;

    public string KeyItemName { get; init; } = "Key";
}

public sealed class SpawnGroupCapacityRule
{
    public required string GroupId { get; init; }

    public required string GroupName { get; init; }

    public int Count { get; init; }
}

public sealed class ApparatusReport
{
    public bool SpawnedFromSyncedProps { get; init; }

    public int EstimatedSpawnerCount { get; init; }

    public int Value { get; init; }
}

public sealed class DungeonFlowDefinition
{
    public int Id { get; init; }

    public required string Name { get; init; }

    public required string Theme { get; init; }

    public int EstimatedLockableDoorCount { get; init; }

    public int EstimatedApparatusSpawnerCount { get; init; }

    public int TilePoolPrefabCount { get; init; }
}
