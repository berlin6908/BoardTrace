using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace BoardTrace.Vision;

/// <summary>
/// Runs the exported BoardTrace detector. Owns one CPU session; callers must serialize
/// Detect and Dispose, as a station executes one inspection at a time.
/// Input planes are tested grayscale, reference grayscale and their absolute difference,
/// each divided by 255. The model contains normalization (mean/std 0.5) and NMS.
/// </summary>
public sealed class OnnxDetector : IDisposable
{
    private const int Size = 640;
    private static readonly string[] InputNames = ["images"];
    private static readonly string[] OutputNames = ["boxes", "labels", "scores"];
    private readonly InferenceSession session;
    private bool disposed;

    public string ModelSha256 { get; }

    /// <summary>Native session construction only; excludes file reading and hash verification.</summary>
    public double SessionInitializationMs { get; }

    public OnnxDetector(string modelPath, string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        var model = File.ReadAllBytes(modelPath);
        ModelSha256 = Convert.ToHexStringLower(SHA256.HashData(model));
        if (!string.Equals(ModelSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ONNX 模型 SHA-256 与指定版本不一致。");

        var watch = Stopwatch.StartNew();
        try
        {
            session = new InferenceSession(model);
        }
        catch (OnnxRuntimeException exception)
        {
            throw new InvalidDataException("无法加载 ONNX 检测模型。", exception);
        }
        SessionInitializationMs = watch.Elapsed.TotalMilliseconds;
        try
        {
            ValidateModelContract();
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public DetectionResult Detect(byte[] testedBytes, byte[] referenceBytes, OnnxScoreThresholds scoreThresholds,
        CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(testedBytes);
        ArgumentNullException.ThrowIfNull(referenceBytes);
        ArgumentNullException.ThrowIfNull(scoreThresholds);
        scoreThresholds.Validate();

        using var tested = Decode(testedBytes, "待检");
        using var reference = Decode(referenceBytes, "参考");

        tested.GetArray(out byte[] testedPixels);
        reference.GetArray(out byte[] referencePixels);
        var plane = Size * Size;
        var input = new float[3 * plane];
        for (var i = 0; i < plane; i++)
        {
            input[i] = testedPixels[i] / 255f;
            input[plane + i] = referencePixels[i] / 255f;
            input[2 * plane + i] = Math.Abs(testedPixels[i] - referencePixels[i]) / 255f;
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var tensor = OrtValue.CreateTensorValueFromMemory(input, [1, 3, Size, Size]);
        using var options = new RunOptions();
        using var registration = cancellationToken.Register(() => options.Terminate = true);
        try
        {
            using var output = session.Run(options, InputNames, [tensor], OutputNames);
            cancellationToken.ThrowIfCancellationRequested();
            var boxes = output[0].GetTensorDataAsSpan<float>();
            var labels = output[1].GetTensorDataAsSpan<long>();
            var scores = output[2].GetTensorDataAsSpan<float>();
            if (boxes.Length != 4 * scores.Length || labels.Length != scores.Length)
                throw new InvalidDataException("ONNX 检测输出数量不一致。");

            var defects = new List<DetectedDefect>();
            for (var i = 0; i < scores.Length; i++)
            {
                var box = boxes.Slice(i * 4, 4);
                if (labels[i] is < 1 or > 6 || !float.IsFinite(scores[i]) || scores[i] is < 0 or > 1
                    || !float.IsFinite(box[0]) || !float.IsFinite(box[1])
                    || !float.IsFinite(box[2]) || !float.IsFinite(box[3])
                    || box[0] < 0 || box[1] < 0 || box[2] > Size || box[3] > Size
                    || box[2] < box[0] || box[3] < box[1])
                    throw new InvalidDataException("ONNX 检测输出包含无效类别、分数或边界框。");
                if (scores[i] >= scoreThresholds.ForClass((int)labels[i]))
                {
                    // ONNX provides boxes, not segmentation masks: Area is bounding-box area.
                    var area = (int)Math.Round((double)(box[2] - box[0]) * (box[3] - box[1]));
                    defects.Add(new DetectedDefect([box[0], box[1], box[2], box[3]], (int)labels[i], scores[i], area));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostics = new Dictionary<string, double> { ["candidateCount"] = scores.Length };
            for (var classId = 1; classId <= 6; classId++) diagnostics[$"scoreThreshold{classId}"] = scoreThresholds.ForClass(classId);
            return new DetectionResult(defects.Count == 0 ? "Pass" : "Fail", Size, Size, defects, watch.Elapsed.TotalMilliseconds, diagnostics);
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OnnxRuntimeException exception)
        {
            throw new InvalidDataException("ONNX 检测执行失败。", exception);
        }
    }

    private static Mat Decode(byte[] bytes, string kind)
    {
        if (bytes.Length == 0) throw new InvalidDataException($"{kind}图像为空。");
        Mat image;
        try
        {
            image = Cv2.ImDecode(bytes, ImreadModes.Grayscale);
        }
        catch (OpenCVException exception)
        {
            throw new InvalidDataException($"无法解码{kind}图像。", exception);
        }
        try
        {
            if (image.Empty()) throw new InvalidDataException($"无法解码{kind}图像。");
            if (image.Width != Size || image.Height != Size)
                throw new InvalidDataException($"ONNX 检测要求 640 × 640 {kind}图像。");
            Cv2.MeanStdDev(image, out _, out Scalar deviation);
            if (deviation.Val0 < 5) throw new InvalidDataException($"{kind}图像缺少有效对比度。");
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private void ValidateModelContract()
    {
        if (session.InputMetadata.Count != 1
            || !session.InputMetadata.TryGetValue(InputNames[0], out var input)
            || input.ElementType != typeof(float) || !input.Dimensions.SequenceEqual(new[] { 1, 3, Size, Size }))
            throw new InvalidDataException("ONNX 模型输入必须为 images: float32 [1,3,640,640]。");
        var types = new[] { typeof(float), typeof(long), typeof(float) };
        for (var i = 0; i < OutputNames.Length; i++)
        {
            if (!session.OutputMetadata.TryGetValue(OutputNames[i], out var output)
                || output.ElementType != types[i] || output.Dimensions.Length != (i == 0 ? 2 : 1)
                || (i == 0 && output.Dimensions[1] != 4))
                throw new InvalidDataException("ONNX 模型必须输出 boxes: float32 Nx4、labels: int64 N、scores: float32 N。");
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        session.Dispose();
    }
}
