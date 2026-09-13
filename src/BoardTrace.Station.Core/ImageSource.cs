namespace BoardTrace.Station.Core;

public sealed record ReplaySample(string SampleId, string Image, string Reference)
{
    public override string ToString() => SampleId;
}

public sealed record CapturedPair(byte[] Tested, byte[] Reference);

public interface IImageSource
{
    string SampleId { get; }
    string SourceKind { get; }
    Task<CapturedPair> CaptureAsync(CancellationToken cancellationToken);
}

public sealed class ReplayImageSource(string dataRoot, ReplaySample sample, bool referenceSelfMatch = false) : IImageSource
{
    public string DataRoot { get; } = dataRoot;
    public string SampleId => sample.SampleId;
    public string SourceKind => referenceSelfMatch ? "ConstructedNormal" : "Replay";
    public async Task<CapturedPair> CaptureAsync(CancellationToken cancellationToken)
    {
        var reference = await File.ReadAllBytesAsync(Path.Combine(DataRoot, sample.Reference), cancellationToken);
        var tested = referenceSelfMatch
            ? reference
            : await File.ReadAllBytesAsync(Path.Combine(DataRoot, sample.Image), cancellationToken);
        return new CapturedPair(tested, reference);
    }
}
