using OpenCvSharp;

namespace BoardTrace.Station.Core;

public sealed class CameraImageSource : IImageSource
{
    private readonly int deviceIndex;

    public CameraImageSource(string sampleId, int deviceIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sampleId);
        ArgumentOutOfRangeException.ThrowIfNegative(deviceIndex);
        SampleId = sampleId;
        this.deviceIndex = deviceIndex;
    }

    public string SampleId { get; }
    public string SourceKind => "Camera";

    public Task<CapturedPair> CaptureAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var camera = new VideoCapture(deviceIndex);
        if (!camera.IsOpened())
            throw new IOException($"无法打开相机设备 {deviceIndex}。");

        camera.Set(VideoCaptureProperties.FrameWidth, 640);
        camera.Set(VideoCaptureProperties.FrameHeight, 640);
        using var frame = new Mat();
        if (!camera.Read(frame) || frame.Empty())
            throw new IOException($"相机设备 {deviceIndex} 未返回图像帧。");
        cancellationToken.ThrowIfCancellationRequested();
        return new CapturedPair(frame.ImEncode(".png"), null);
    }, cancellationToken);
}
