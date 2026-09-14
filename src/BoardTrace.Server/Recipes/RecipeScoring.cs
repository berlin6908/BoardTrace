using BoardTrace.Contracts;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("BoardTrace.Server.Tests")]

namespace BoardTrace.Server.Recipes;

internal sealed record RecipeTruth(double[] Box, int ClassId);
internal sealed record RecipeScore(int Tp, int Fp, int Fn, IReadOnlyList<RecipeClassMetrics> Classes);

internal static class RecipeScoring
{
    public static RecipeScore Match(IReadOnlyList<RecipePrediction> predictions, IReadOnlyList<RecipeTruth> truth, bool classAware)
    {
        var used = new bool[truth.Count];
        var tp = 0;
        var fp = 0;
        var byClass = new int[6, 3];
        // Detector already applied the six inclusive thresholds. Do not filter again at .5.
        foreach (var prediction in predictions.OrderByDescending(x => x.Score))
        {
            var bestIndex = -1;
            var best = 0.5;
            for (var i = 0; i < truth.Count; i++)
            {
                if (used[i] || (classAware && prediction.ClassId != truth[i].ClassId)) continue;
                var overlap = IoU(prediction.Box, truth[i].Box);
                if (overlap >= 0.5 && (bestIndex < 0 || overlap > best)) { best = overlap; bestIndex = i; }
            }
            if (bestIndex >= 0)
            {
                used[bestIndex] = true;
                tp++;
                if (classAware) byClass[truth[bestIndex].ClassId - 1, 0]++;
            }
            else
            {
                fp++;
                if (classAware) byClass[prediction.ClassId!.Value - 1, 1]++;
            }
        }
        if (classAware)
            for (var i = 0; i < truth.Count; i++)
                if (!used[i]) byClass[truth[i].ClassId - 1, 2]++;
        return new(tp, fp, truth.Count - tp, classAware
            ? Enumerable.Range(0, 6).Select(i => Metrics(i + 1, byClass[i, 0], byClass[i, 1], byClass[i, 2])).ToArray() : []);
    }

    public static RecipeClassMetrics Metrics(int id, int tp, int fp, int fn)
    {
        var p = tp + fp == 0 ? 0 : (double)tp / (tp + fp);
        var r = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
        return new(id, tp, fp, fn, p, r, p + r == 0 ? 0 : 2 * p * r / (p + r));
    }

    private static double IoU(double[] a, double[] b)
    {
        var intersection = Math.Max(0, Math.Min(a[2], b[2]) - Math.Max(a[0], b[0])) *
            Math.Max(0, Math.Min(a[3], b[3]) - Math.Max(a[1], b[1]));
        var union = Math.Max(0, a[2] - a[0]) * Math.Max(0, a[3] - a[1]) +
            Math.Max(0, b[2] - b[0]) * Math.Max(0, b[3] - b[1]) - intersection;
        return union <= 0 ? 0 : intersection / union;
    }
}
