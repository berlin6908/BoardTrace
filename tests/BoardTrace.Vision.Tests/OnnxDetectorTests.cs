using System.Security.Cryptography;
using BoardTrace.Vision;
using OpenCvSharp;

namespace BoardTrace.Vision.Tests;

public sealed class OnnxDetectorTests
{
    private static readonly string ModelPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pixel-detector.onnx");
    private static string ModelHash => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(ModelPath)));

    private static byte[] Image()
    {
        using var image = new Mat(640, 640, MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(image, new Rect(320, 0, 320, 640), Scalar.White, -1);
        image.Set(0, 0, new Vec3b(64, 128, 192));
        return image.ToBytes(".png");
    }

    [Fact]
    public void ConvertsPixelsToRgbNchwAndRetainsModelCoordinatesAndClasses()
    {
        using var detector = new OnnxDetector(ModelPath, ModelHash);
        var result = detector.Detect(Image(), 0);

        Assert.Equal("Fail", result.Decision);
        Assert.Equal(640, result.Width);
        Assert.Equal(640, result.Height);
        Assert.Equal(Enumerable.Range(1, 6), result.Defects.Select(x => x.ClassId!.Value));
        Assert.Equal(new double[] { 192f / 255, 128f / 255, 64f / 255, 192f / 255, 128f / 255, 64f / 255 },
            result.Defects.Select(x => x.Score));
        Assert.All(result.Defects, defect =>
        {
            Assert.Equal(new double[] { 10.25, 20.5, 30.25, 50.5 }, defect.Box);
            Assert.Equal(600, defect.Area);
        });
        Assert.Equal(ModelHash, detector.ModelSha256);
        Assert.True(detector.SessionInitializationMs > 0);
        Assert.True(result.ElapsedMs > 0);
    }

    [Fact]
    public void SameSessionUsesInclusiveThresholdAndCanRunAgainAfterARejectedImage()
    {
        using var detector = new OnnxDetector(ModelPath, ModelHash);
        var bytes = Image();
        var boundary = (double)(128f / 255);
        Assert.Equal(new int?[] { 1, 2, 4, 5 }, detector.Detect(bytes, boundary).Defects.Select(x => x.ClassId));
        Assert.Equal(new int?[] { 1, 4 }, detector.Detect(bytes, Math.BitIncrement(boundary)).Defects.Select(x => x.ClassId));
        Assert.Throws<InvalidDataException>(() => detector.Detect([1, 2, 3], 0.5));
        var pass = detector.Detect(bytes, 1);
        Assert.Equal("Pass", pass.Decision);
        Assert.Empty(pass.Defects);
        Assert.Equal(6, detector.Detect(bytes, 0).Defects.Count);
    }

    [Fact]
    public void MissingOrDifferentModelFailsBeforeDetection()
    {
        Assert.Throws<FileNotFoundException>(() => new OnnxDetector(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".onnx"), ModelHash));
        Assert.Throws<InvalidDataException>(() => new OnnxDetector(ModelPath, new string('0', 64)));
    }

    [Fact]
    public void BadDimensionsAndFlatCaptureCannotBecomeAQualityPass()
    {
        using var detector = new OnnxDetector(ModelPath, ModelHash);
        using var small = new Mat(32, 32, MatType.CV_8UC3, Scalar.Black);
        using var blank = new Mat(640, 640, MatType.CV_8UC3, Scalar.White);
        Assert.Throws<InvalidDataException>(() => detector.Detect([], 0.5));
        Assert.Throws<InvalidDataException>(() => detector.Detect(small.ToBytes(".png"), 0.5));
        Assert.Throws<InvalidDataException>(() => detector.Detect(blank.ToBytes(".png"), 0.5));
    }

    [Fact]
    public void CancellationAndDisposedSessionCannotProduceAResult()
    {
        var detector = new OnnxDetector(ModelPath, ModelHash);
        Assert.Throws<OperationCanceledException>(() => detector.Detect([], 0.5, new CancellationToken(true)));
        detector.Dispose();
        detector.Dispose();
        Assert.Throws<ObjectDisposedException>(() => detector.Detect(Image(), 0.5));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void RejectsInvalidConfidenceThreshold(double threshold)
    {
        using var detector = new OnnxDetector(ModelPath, ModelHash);
        Assert.Throws<ArgumentOutOfRangeException>(() => detector.Detect(Image(), threshold));
    }
}
