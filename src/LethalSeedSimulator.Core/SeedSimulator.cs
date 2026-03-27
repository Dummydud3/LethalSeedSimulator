using LethalSeedSimulator.Rules;

namespace LethalSeedSimulator.Core;

public sealed class SeedSimulator
{
    public SeedReport Simulate(RulePack rules, string levelId, int seed)
    {
        var level = rules.Levels.FirstOrDefault(x => string.Equals(x.Id, levelId, StringComparison.OrdinalIgnoreCase))
            ?? rules.Levels.FirstOrDefault(x => string.Equals(x.Name, levelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown level id '{levelId}'.");

        if (level.SpawnableScrap.Count == 0)
        {
            throw new InvalidOperationException($"Level '{levelId}' has no spawnable scrap configured.");
        }

        var anomalyRandom = new Random(seed + rules.Offsets.AnomalyRandom);
        var weatherRandom = new Random(seed + rules.Offsets.WeatherRandom);

        var weather = RollWeather(level, weatherRandom);
        var scrapCount = anomalyRandom.Next(level.MinScrap, level.MaxScrapExclusive);

        var fixedIndex = RollForcedScrapIndex(level, anomalyRandom);
        var scrapWeights = level.SpawnableScrap.Select(x => x.Rarity).ToArray();

        var results = new List<ScrapRollResult>(scrapCount);
        var total = 0;
        for (var i = 0; i < scrapCount; i++)
        {
            var index = fixedIndex ?? WeightedPicker.GetRandomWeightedIndex(scrapWeights, anomalyRandom);
            var scrap = level.SpawnableScrap[index];
            var value = anomalyRandom.Next(scrap.MinValueInclusive, scrap.MaxValueExclusive);

            results.Add(new ScrapRollResult
            {
                ItemId = scrap.ItemId,
                ItemName = scrap.ItemName,
                Value = value
            });

            total += value;
        }

        return new SeedReport
        {
            Version = rules.Metadata.RuleSetVersion,
            LevelId = levelId,
            Seed = seed,
            Weather = weather,
            ScrapCount = scrapCount,
            TotalScrapValue = total,
            ScrapRolls = results
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

    private static string RollWeather(LevelRule level, Random weatherRandom)
    {
        if (level.Weathers.Count == 0)
        {
            return "None";
        }

        var index = WeightedPicker.GetRandomWeightedIndex(level.Weathers.Select(x => x.Weight).ToArray(), weatherRandom);
        return level.Weathers[index].WeatherType;
    }
}
