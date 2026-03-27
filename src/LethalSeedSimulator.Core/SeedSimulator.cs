using LethalSeedSimulator.Rules;

namespace LethalSeedSimulator.Core;

public sealed class SeedSimulator
{
    private readonly WeatherSimulator weatherSimulator = new();
    private readonly EnemySpawnSimulator enemySpawnSimulator = new();
    private readonly HazardPropSimulator hazardPropSimulator = new();
    private readonly KeySimulator keySimulator = new();

    public SeedReport Simulate(RulePack rules, string levelId, int seed) =>
        Simulate(rules, new SimulationRequest
        {
            LevelId = levelId,
            RunSeed = seed,
            WeatherSeed = Math.Max(seed - 1, 0),
            IsChallengeFile = false,
            ConnectedPlayersOnServer = 0,
            DaysPlayersSurvivedInARow = 0
        });

    public SeedReport Simulate(RulePack rules, SimulationRequest request)
    {
        var level = rules.Levels.FirstOrDefault(x => string.Equals(x.Id, request.LevelId, StringComparison.OrdinalIgnoreCase))
            ?? rules.Levels.FirstOrDefault(x => string.Equals(x.Name, request.LevelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown level id '{request.LevelId}'.");

        if (level.SpawnableScrap.Count == 0)
        {
            throw new InvalidOperationException($"Level '{request.LevelId}' has no spawnable scrap configured.");
        }

        var runSeed = request.RunSeed;
        var weatherSeed = request.WeatherSeed ?? Math.Max(runSeed - 1, 0);
        var anomalyRandom = new Random(runSeed + rules.Offsets.AnomalyRandom);
        var levelRandom = new Random(runSeed + rules.Offsets.LevelRandom);
        var weatherReport = weatherSimulator.Simulate(rules, new SimulationRequest
        {
            LevelId = request.LevelId,
            RunSeed = request.RunSeed,
            WeatherSeed = weatherSeed,
            IsChallengeFile = request.IsChallengeFile,
            ConnectedPlayersOnServer = request.ConnectedPlayersOnServer,
            DaysPlayersSurvivedInARow = request.DaysPlayersSurvivedInARow
        });

        var weather = weatherReport.AssignedWeatherByLevelId.TryGetValue(level.Id, out var w) ? w : "None";
        var currentDungeonType = RollDungeonType(level, levelRandom);
        var increasedScrapSpawnRateIndex = request.IsChallengeFile && level.SpawnableScrap.Count > 0
            ? anomalyRandom.Next(0, level.SpawnableScrap.Count)
            : -1;

        var scrapCount = (int)(anomalyRandom.Next(level.MinScrap, level.MaxScrapExclusive) * rules.GlobalRules.ScrapAmountMultiplier);
        if (currentDungeonType == 4)
        {
            scrapCount += 6;
        }
        scrapCount = Math.Max(scrapCount, 0);

        var fixedIndex = RollForcedScrapIndex(level, anomalyRandom);
        var scrapWeights = BuildScrapWeights(level, increasedScrapSpawnRateIndex);
        var spawnGroupCapacities = rules.GlobalRules.SpawnGroupCapacities
            .ToDictionary(x => x.GroupId, x => x.Count, StringComparer.OrdinalIgnoreCase);

        var results = new List<ScrapRollResult>(scrapCount);
        var values = new List<int>(scrapCount);
        for (var i = 0; i < scrapCount; i++)
        {
            var index = fixedIndex ?? WeightedPicker.GetRandomWeightedIndex(scrapWeights, anomalyRandom);
            var scrap = level.SpawnableScrap[index];
            if (!CanSpawnScrap(scrap, fixedIndex is not null, rules.GlobalRules.TotalRandomScrapSpawnPoints, spawnGroupCapacities))
            {
                continue;
            }

            var rawValue = anomalyRandom.Next(scrap.MinValueInclusive, scrap.MaxValueExclusive);
            var value = (int)(rawValue * rules.GlobalRules.ScrapValueMultiplier);
            if (fixedIndex is not null)
            {
                value = Math.Clamp(value, 50, 170);
            }

            results.Add(new ScrapRollResult
            {
                ItemId = scrap.ItemId,
                ItemName = scrap.ItemName,
                Value = value
            });
            values.Add(value);
        }

        if (fixedIndex is not null && values.Count > 0)
        {
            var fixedItem = level.SpawnableScrap[fixedIndex.Value];
            var minTotal = fixedItem.TwoHanded ? 1500f : 600f;
            var totalFixed = values.Sum();
            if (totalFixed > 4500)
            {
                for (var i = 0; i < values.Count; i++)
                {
                    values[i] = (int)(values[i] * 0.7f);
                }
            }
            else if (totalFixed < minTotal)
            {
                for (var i = 0; i < values.Count; i++)
                {
                    values[i] = (int)(values[i] * 1.4f);
                }
            }

            for (var i = 0; i < results.Count; i++)
            {
                results[i] = new ScrapRollResult
                {
                    ItemId = results[i].ItemId,
                    ItemName = results[i].ItemName,
                    Value = values[i]
                };
            }
        }

        var flowProfile = rules.GlobalRules.DungeonFlows.FirstOrDefault(x => x.Id == currentDungeonType);
        var apparatusSpawnerCount = flowProfile?.EstimatedApparatusSpawnerCount ?? rules.GlobalRules.EstimatedApparatusSpawnerCount;
        var apparatusSpawned = apparatusSpawnerCount > 0;
        var apparatusValue = 0;
        if (apparatusSpawned && rules.GlobalRules.ApparatusMaxValueExclusive > rules.GlobalRules.ApparatusMinValueInclusive)
        {
            var rawApparatusValue = anomalyRandom.Next(
                rules.GlobalRules.ApparatusMinValueInclusive,
                rules.GlobalRules.ApparatusMaxValueExclusive);
            apparatusValue = (int)(rawApparatusValue * rules.GlobalRules.ScrapValueMultiplier);
            values.Add(apparatusValue);
            results.Add(new ScrapRollResult
            {
                ItemId = rules.GlobalRules.ApparatusItemId,
                ItemName = rules.GlobalRules.ApparatusItemName,
                Value = apparatusValue
            });
        }

        var total = values.Sum();

        return new SeedReport
        {
            Version = rules.Metadata.RuleSetVersion,
            LevelId = level.Id,
            Seed = runSeed,
            RunSeed = runSeed,
            WeatherSeed = weatherSeed,
            Weather = weather,
            ScrapCount = scrapCount,
            TotalScrapValue = total,
            ScrapRolls = results,
            EnemySpawn = enemySpawnSimulator.Simulate(rules, level, runSeed),
            HazardProp = hazardPropSimulator.Simulate(rules, level, runSeed),
            WeatherReport = weatherReport,
            Keys = keySimulator.Simulate(level, rules.GlobalRules, currentDungeonType, runSeed, rules.Offsets.LevelRandom),
            Apparatus = new ApparatusReport
            {
                SpawnedFromSyncedProps = apparatusSpawned,
                EstimatedSpawnerCount = apparatusSpawnerCount,
                Value = apparatusValue
            }
        };
    }

    private static int? RollForcedScrapIndex(LevelRule level, Random anomalyRandom)
    {
        if (anomalyRandom.Next(0, 500) > 20)
        {
            return null;
        }

        var candidate = anomalyRandom.Next(0, level.SpawnableScrap.Count);
        var foundRareSmallItem = false;
        for (var i = 0; i < 2; i++)
        {
            var scrap = level.SpawnableScrap[candidate];
            if (scrap.Rarity >= 5 && !scrap.TwoHanded)
            {
                foundRareSmallItem = true;
                break;
            }

            candidate = anomalyRandom.Next(0, level.SpawnableScrap.Count);
        }

        if (!foundRareSmallItem && anomalyRandom.Next(0, 100) < 60)
        {
            return null;
        }

        return candidate;
    }

    private static int[] BuildScrapWeights(LevelRule level, int increasedScrapSpawnRateIndex)
    {
        var weights = new int[level.SpawnableScrap.Count];
        for (var i = 0; i < level.SpawnableScrap.Count; i++)
        {
            if (i == increasedScrapSpawnRateIndex)
            {
                weights[i] = 100;
                continue;
            }

            var baseRarity = level.SpawnableScrap[i].Rarity;
            var isGoldBar = level.SpawnableScrap[i].ItemId == 152767;
            weights[i] = isGoldBar
                ? Math.Min(baseRarity + 30, 99)
                : baseRarity;
        }

        return weights;
    }

    private static int RollDungeonType(LevelRule level, Random levelRandom)
    {
        if (level.DungeonFlowTypes.Count == 0)
        {
            return 0;
        }

        var weights = level.DungeonFlowTypes.Select(x => x.Rarity).ToArray();
        var index = WeightedPicker.GetRandomWeightedIndex(weights, levelRandom);
        return level.DungeonFlowTypes[Math.Clamp(index, 0, level.DungeonFlowTypes.Count - 1)].Id;
    }

    private static bool CanSpawnScrap(
        ScrapRule scrap,
        bool forcedAnySpawner,
        int totalSpawnPoints,
        IReadOnlyDictionary<string, int> groupCapacities)
    {
        if (totalSpawnPoints <= 0)
        {
            return true;
        }

        if (forcedAnySpawner || scrap.SpawnPositionGroupIds.Count == 0)
        {
            return totalSpawnPoints > 0;
        }

        var compatible = 0;
        foreach (var group in scrap.SpawnPositionGroupIds)
        {
            if (groupCapacities.TryGetValue(group, out var count))
            {
                compatible += count;
            }
        }

        return compatible > 0;
    }

}
