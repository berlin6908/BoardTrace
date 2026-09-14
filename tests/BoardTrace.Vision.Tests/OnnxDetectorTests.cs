using System.Security.Cryptography;
using BoardTrace.Vision;
using OpenCvSharp;

namespace BoardTrace.Vision.Tests;

public sealed class OnnxDetectorTests
{
    private static readonly string ModelPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pixel-detector.onnx");
    private static readonly OnnxScoreThresholds AllCandidates = new(0, 0, 0, 0, 0, 0);
    private static readonly OnnxScoreThresholds None = new(1, 1, 1, 1, 1, 1);
    private static string ModelHash => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(ModelPath)));

    private static byte[] Image(byte first = 64, byte second = 200)
    {
        using var image = new Mat(640, 640, MatType.CV_8UC1, Scalar.Black);
        Cv2.Rectangle(image, new Rect(320, 0, 320, 640), Scalar.White, -1);
        image.Set(0, 0, first); image.Set(0, 1, second);
        return image.ToBytes(".png");
    }

    [Fact]
    public void UsesTestedReferenceAndAbsoluteDifferencePlanesWithoutRepeatingNormalization()
    {
        using var detector = new OnnxDetector(ModelPath, ModelHash);
        var tested = Image(); var reference = Image(192, 32);
        var result = detector.Detect(tested, reference, AllCandidates);
        Assert.Equal("Fail", result.Decision);
        Assert.Equal(640, result.Width); Assert.Equal(640, result.Height);
        Assert.Equal(Enumerable.Range(1, 6), result.Defects.Select(x => x.ClassId!.Value));
        Assert.Equal(new double[] { 64f / 255, 192f / 255, 128f / 255, 200f / 255, 32f / 255, 168f / 255 },
            result.Defects.Select(x => x.Score));
        Assert.Equal(new double[] { 192f / 255, 64f / 255, 128f / 255, 32f / 255, 200f / 255, 168f / 255 },
            detector.Detect(reference, tested, AllCandidates).Defects.Select(x => x.Score));
        Assert.All(result.Defects, defect =>
        {
            Assert.Equal(new double[] { 10.25, 20.5, 30.25, 50.5 }, defect.Box);
            Assert.Equal(600, defect.Area);
        });
        Assert.Equal(ModelHash, detector.ModelSha256);
        Assert.True(detector.SessionInitializationMs > 0); Assert.True(result.ElapsedMs > 0);
    }

    [Fact]
    public void InclusiveThresholdForEachClassDoesNotSuppressAnyOtherClass()
    {
        using var detector = new OnnxDetector(ModelPath, ModelHash);
        var tested = Image(); var reference = Image(192, 32);
        var boundaries = detector.Detect(tested, reference, AllCandidates).Defects.Select(defect => defect.Score).ToArray();
        Assert.Equal(6, detector.Detect(tested, reference, OnnxScoreThresholds.FromClassOrder(boundaries)).Defects.Count);
        for (var classId = 1; classId <= 6; classId++)
        {
            var thresholds = (double[])boundaries.Clone();
            thresholds[classId - 1] = Math.BitIncrement(thresholds[classId - 1]);
            Assert.Equal(Enumerable.Range(1, 6).Where(value => value != classId),
                detector.Detect(tested, reference, OnnxScoreThresholds.FromClassOrder(thresholds)).Defects.Select(defect => defect.ClassId!.Value));
        }
        Assert.Equal(new[] { 2, 3, 4, 6 }, detector.Detect(tested, reference, new OnnxScoreThresholds()).Defects.Select(defect => defect.ClassId!.Value));
        Assert.Equal(new[] { 2, 4, 6 }, detector.Detect(tested, reference, new OnnxScoreThresholds(Mousebite: 0.99)).Defects.Select(defect => defect.ClassId!.Value));
    }

    [Fact]
    public void SameSessionRunsAgainAfterEitherImageIsRejected()
    {
        using var detector = new OnnxDetector(ModelPath, ModelHash);
        var tested = Image(); var reference = Image(192, 32);
        Assert.Throws<InvalidDataException>(() => detector.Detect([1, 2, 3], reference, AllCandidates));
        Assert.Throws<InvalidDataException>(() => detector.Detect(tested, [1, 2, 3], AllCandidates));
        var pass = detector.Detect(tested, reference, None);
        Assert.Equal("Pass", pass.Decision); Assert.Empty(pass.Defects);
        Assert.Equal(6, detector.Detect(tested, reference, AllCandidates).Defects.Count);
    }

    [Fact]
    public void MissingOrDifferentModelFailsBeforeDetection()
    {
        Assert.Throws<FileNotFoundException>(() => new OnnxDetector(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".onnx"), ModelHash));
        Assert.Throws<InvalidDataException>(() => new OnnxDetector(ModelPath, new string('0', 64)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothImagesRequireValidDimensionsAndContrast(bool invalidReference)
    {
        using var detector = new OnnxDetector(ModelPath, ModelHash);
        using var small = new Mat(32, 32, MatType.CV_8UC1, Scalar.Black);
        using var blank = new Mat(640, 640, MatType.CV_8UC1, Scalar.White);
        var valid = Image();
        foreach (var invalid in new byte[][] { [], small.ToBytes(".png"), blank.ToBytes(".png") })
            Assert.Throws<InvalidDataException>(() => detector.Detect(invalidReference ? valid : invalid, invalidReference ? invalid : valid, AllCandidates));
        Assert.Throws<ArgumentNullException>(() => detector.Detect(valid, null!, AllCandidates));
    }

    [Fact]
    public void CancellationAndDisposedSessionCannotProduceAResult()
    {
        var detector = new OnnxDetector(ModelPath, ModelHash);
        Assert.Throws<OperationCanceledException>(() => detector.Detect([], [], AllCandidates, new CancellationToken(true)));
        detector.Dispose(); detector.Dispose();
        Assert.Throws<ObjectDisposedException>(() => detector.Detect(Image(), Image(), AllCandidates));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void RejectsInvalidClassThresholdBeforeInference(double threshold)
    {
        using var detector = new OnnxDetector(ModelPath, ModelHash);
        Assert.Throws<ArgumentOutOfRangeException>(() => detector.Detect(Image(), Image(), new OnnxScoreThresholds(Mousebite: threshold)));
        Assert.Throws<ArgumentException>(() => OnnxScoreThresholds.FromClassOrder([0.5, 0.5]));
    }
}
