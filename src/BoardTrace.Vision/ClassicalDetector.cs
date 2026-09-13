using System.Diagnostics;
using OpenCvSharp;

namespace BoardTrace.Vision;

public sealed class ClassicalDetector(ClassicalSettings settings)
{
    public DetectionResult Detect(byte[] testedBytes, byte[] referenceBytes, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        using var tested = Cv2.ImDecode(testedBytes, ImreadModes.Grayscale);
        using var reference = Cv2.ImDecode(referenceBytes, ImreadModes.Grayscale);
        if (tested.Empty() || reference.Empty()) throw new InvalidDataException("图像损坏或无法解码。");
        var size = tested.Size();
        if (size != reference.Size() || size != new Size(640, 640))
            throw new InvalidDataException("当前方案要求参考图和待检图均为 640×640。");
        Cv2.MeanStdDev(tested, out _, out var deviation);
        if (deviation.Val0 < 5) throw new InvalidDataException("待检图缺少有效结构，不能判定质量。");
        using var testedFloat = new Mat();
        using var referenceFloat = new Mat();
        tested.ConvertTo(testedFloat, MatType.CV_32F);
        reference.ConvertTo(referenceFloat, MatType.CV_32F);
        // Preserve board structure at image edges; a Hann window can remove all shared
        // features in sparse PCB views and let an isolated defect dominate registration.
        using var noWindow = new Mat();
        var shift = Cv2.PhaseCorrelate(referenceFloat, testedFloat, noWindow, out var response);
        if (!double.IsFinite(response) || response < settings.MinimumAlignmentResponse ||
            Math.Abs(shift.X) > settings.MaximumTranslation || Math.Abs(shift.Y) > settings.MaximumTranslation)
            throw new InvalidDataException("参考图对齐失败，不能给出有效质量结论。");
        // Source data are binarized; integer translation avoids interpolated artificial edges.
        var dx = (int)Math.Round(shift.X);
        var dy = (int)Math.Round(shift.Y);
        using var transform = new Mat(2, 3, MatType.CV_64FC1, Scalar.Black);
        transform.Set(0, 0, 1.0);
        transform.Set(1, 1, 1.0);
        transform.Set(0, 2, (double)dx);
        transform.Set(1, 2, (double)dy);
        using var aligned = new Mat();
        Cv2.WarpAffine(reference, aligned, transform, size, InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.White);
        using var testedCopper = new Mat();
        using var referenceCopper = new Mat();
        Cv2.Threshold(tested, testedCopper, settings.BinarizationThreshold, 255, ThresholdTypes.BinaryInv);
        Cv2.Threshold(aligned, referenceCopper, settings.BinarizationThreshold, 255, ThresholdTypes.BinaryInv);
        using var tolerance = Cv2.GetStructuringElement(MorphShapes.Rect,
            new Size(settings.EdgeTolerance * 2 + 1, settings.EdgeTolerance * 2 + 1));
        using var expandedTested = new Mat();
        using var expandedReference = new Mat();
        Cv2.Dilate(testedCopper, expandedTested, tolerance);
        Cv2.Dilate(referenceCopper, expandedReference, tolerance);
        using var excess = new Mat();
        using var missing = new Mat();
        Cv2.Subtract(testedCopper, expandedReference, excess);
        Cv2.Subtract(referenceCopper, expandedTested, missing);
        using var difference = new Mat();
        Cv2.BitwiseOr(excess, missing, difference);
        // Pixels without a corresponding reference are outside the aligned inspection field.
        if (dx != 0 || dy != 0)
        {
            using var overlap = new Mat(size, MatType.CV_8UC1, Scalar.Black);
            Cv2.Rectangle(overlap, new Rect(Math.Max(0, dx), Math.Max(0, dy), size.Width - Math.Abs(dx), size.Height - Math.Abs(dy)), Scalar.White, -1);
            Cv2.BitwiseAnd(difference, overlap, difference);
        }
        var changedPixels = Cv2.CountNonZero(difference);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(settings.ClosingSize, settings.ClosingSize));
        Cv2.MorphologyEx(difference, difference, MorphTypes.Close, kernel);
        cancellationToken.ThrowIfCancellationRequested();
        using var labels = new Mat();
        using var statistics = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(difference, labels, statistics, centroids);
        var defects = new List<DetectedDefect>();
        for (var index = 1; index < count; index++)
        {
            var area = statistics.At<int>(index, (int)ConnectedComponentsTypes.Area);
            if (area < settings.MinimumArea) continue;
            var left = statistics.At<int>(index, (int)ConnectedComponentsTypes.Left);
            var top = statistics.At<int>(index, (int)ConnectedComponentsTypes.Top);
            var width = statistics.At<int>(index, (int)ConnectedComponentsTypes.Width);
            var height = statistics.At<int>(index, (int)ConnectedComponentsTypes.Height);
            double[] box = [Math.Max(0, left - settings.BoxPadding), Math.Max(0, top - settings.BoxPadding),
                Math.Min(size.Width, left + width + settings.BoxPadding), Math.Min(size.Height, top + height + settings.BoxPadding)];
            defects.Add(new DetectedDefect(box, null, 1, area));
        }
        return new DetectionResult(defects.Count == 0 ? "Pass" : "Fail", size.Width, size.Height, defects,
            timer.Elapsed.TotalMilliseconds, new Dictionary<string, double>
            {
                ["alignmentX"] = shift.X, ["alignmentY"] = shift.Y,
                ["alignmentResponse"] = response, ["changedPixels"] = changedPixels
            });
    }
}
