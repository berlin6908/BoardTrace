using BoardTrace.Vision;
using OpenCvSharp;

namespace BoardTrace.Vision.Tests;

public sealed class ClassicalDetectorTests
{
    private readonly ClassicalDetector detector = new(new ClassicalSettings());

    private static Mat Reference()
    {
        var image = new Mat(640, 640, MatType.CV_8UC1, Scalar.White);
        Cv2.Rectangle(image, new Rect(83, 65, 300, 35), Scalar.Black, -1);
        Cv2.Rectangle(image, new Rect(348, 65, 35, 440), Scalar.Black, -1);
        Cv2.Circle(image, new Point(200, 450), 60, Scalar.Black, -1);
        return image;
    }

    [Fact]
    public void IdenticalReferenceIsControlledPass()
    {
        using var reference = Reference();
        var bytes = reference.ToBytes(".png");
        var result = detector.Detect(bytes, bytes);
        Assert.Equal("Pass", result.Decision);
        Assert.Empty(result.Defects);
    }

    [Fact]
    public void ComputesLocationOfAnAddedCopperIsland()
    {
        using var reference = Reference();
        using var tested = reference.Clone();
        Cv2.Rectangle(tested, new Rect(470, 280, 15, 18), Scalar.Black, -1);
        var result = detector.Detect(tested.ToBytes(".png"), reference.ToBytes(".png"));
        Assert.Equal("Fail", result.Decision);
        var box = Assert.Single(result.Defects).Box;
        Assert.True(box[0] <= 470 && box[1] <= 280 && box[2] >= 485 && box[3] >= 298);
        Assert.Null(result.Defects[0].ClassId);
    }

    [Fact]
    public void RegistrationRemovesControlledIntegerTranslation()
    {
        using var reference = Reference();
        using var shifted = new Mat();
        using var transform = Mat.Eye(2, 3, MatType.CV_64FC1).ToMat();
        transform.Set(0, 2, 4.0);
        transform.Set(1, 2, -3.0);
        Cv2.WarpAffine(reference, shifted, transform, reference.Size(), InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.White);
        var result = detector.Detect(shifted.ToBytes(".png"), reference.ToBytes(".png"));
        Assert.Equal("Pass", result.Decision);
        Assert.InRange(result.Diagnostics["alignmentX"], 3.5, 4.5);
        Assert.InRange(result.Diagnostics["alignmentY"], -3.5, -2.5);
    }

    [Fact]
    public void CorruptImageCannotBecomeAQualityPass()
    {
        using var reference = Reference();
        Assert.Throws<InvalidDataException>(() => detector.Detect([1, 2, 3], reference.ToBytes(".png")));
    }

    [Fact]
    public void CancellationStopsBeforeImageProcessing()
    {
        Assert.Throws<OperationCanceledException>(() => detector.Detect([], [], new CancellationToken(true)));
    }
}
