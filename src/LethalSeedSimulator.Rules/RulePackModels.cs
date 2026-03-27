using System.Text.Json.Serialization;

namespace LethalSeedSimulator.Rules;

public sealed class RulePack
{
    public required RulePackMetadata Metadata { get; init; }

    public required RngOffsets Offsets { get; init; }

    public required List<LevelRule> Levels { get; init; }
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

    public bool IsChallengeFile { get; init; }

    public List<ScrapRule> SpawnableScrap { get; init; } = [];

    public List<WeatherRule> Weathers { get; init; } = [];
}

public sealed class ScrapRule
{
    public required int ItemId { get; init; }

    public required string ItemName { get; init; }

    public required int Rarity { get; init; }

    public int MinValueInclusive { get; init; }

    public int MaxValueExclusive { get; init; }

    public bool TwoHanded { get; init; }
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

    public required int ScrapCount { get; init; }

    public required int TotalScrapValue { get; init; }

    public required List<ScrapRollResult> ScrapRolls { get; init; }

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
