namespace LethalSeedSimulator.Core;

public static class WeightedPicker
{
    public static int GetRandomWeightedIndex(IReadOnlyList<int> weights, Random random)
    {
        if (weights.Count == 0)
        {
            throw new ArgumentException("Weights cannot be empty.", nameof(weights));
        }

        var total = 0;
        for (var i = 0; i < weights.Count; i++)
        {
            if (weights[i] >= 0)
            {
                total += weights[i];
            }
        }

        if (total <= 0)
        {
            return random.Next(0, weights.Count);
        }

        var roll = random.NextDouble();
        var cumulative = 0.0;

        for (var i = 0; i < weights.Count; i++)
        {
            if (weights[i] <= 0)
            {
                continue;
            }

            cumulative += (double)weights[i] / total;
            if (cumulative >= roll)
            {
                return i;
            }
        }

        return random.Next(0, weights.Count);
    }
}
