using LethalSeedSimulator.Rules;

namespace LethalSeedSimulator.Core;

public sealed class HazardPropSimulator
{
    public HazardPropReport Simulate(RulePack rules, LevelRule level, int runSeed)
    {
        var outsideHazardRandom = new Random(runSeed + 2);
        var mapObjectsRandom = new Random(runSeed + rules.Offsets.SpawnMapObjectsRandom);
        var powerRandom = new Random(runSeed + 3);
        var valveRandom = new Random(runSeed + 513);

        var mapObjects = new List<MapObjectSpawn>();
        foreach (var mapObject in level.SpawnableMapObjects)
        {
            var count = mapObject.MaxObjectsEstimate <= 0
                ? 0
                : mapObjectsRandom.Next(0, mapObject.MaxObjectsEstimate + 1);
            mapObjects.Add(new MapObjectSpawn
            {
                ObjectId = mapObject.Id,
                Count = count
            });
        }

        var estimatedOutsideHazards = level.SpawnableOutsideObjectsCount <= 0
            ? 0
            : outsideHazardRandom.Next(0, level.SpawnableOutsideObjectsCount + 1);

        return new HazardPropReport
        {
            PowerOffAtStart = powerRandom.NextDouble() < rules.GlobalRules.PowerOffAtStartChance,
            EstimatedOutsideHazards = estimatedOutsideHazards,
            MapObjects = mapObjects,
            SteamValveBurstMin = valveRandom.Next(12, 40),
            SteamValveBurstMax = valveRandom.Next(45, 120)
        };
    }
}
