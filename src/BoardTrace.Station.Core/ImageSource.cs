namespace BoardTrace.Station.Core;

public sealed record ReplaySample(string SampleId, string Image, string Reference)
{
    public override string ToString() => SampleId;
}

public sealed record CapturedPair(byte[] Tested, byte[]? Reference);

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

// Published execution obtains its reference from the frozen recipe, never this data directory.
public sealed class PublishedReplayImageSource(string dataRoot, ReplaySample sample, byte[]? constructedNormal = null) : IImageSource
{
    private readonly byte[]? normal = constructedNormal?.ToArray();
    public string SampleId => sample.SampleId;
    public string SourceKind => normal is null ? "Replay" : "ConstructedNormal";
    public async Task<CapturedPair> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tested = normal?.ToArray() ?? await File.ReadAllBytesAsync(Path.Combine(dataRoot, sample.Image), cancellationToken);
        return new CapturedPair(tested, null);
    }
}
