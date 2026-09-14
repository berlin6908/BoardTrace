namespace BoardTrace.Contracts;

public sealed record StationRuntimeUpdate(Guid? BatchId, Guid? ArchiveId, int FirstArticleCount,
    int ProductionCount, int ReinspectionCount, int PendingUploads, bool HasStartedInspection,
    bool HasUnacknowledgedPlc, string State, string? Alarm, DateTimeOffset ObservedAt);

public sealed record StationRuntimeReceipt(string StationId, DateTimeOffset ReceivedAt);

public sealed record StationRuntimeView(string StationId, string DisplayName, StationRuntimeUpdate? Runtime,
    DateTimeOffset? ReceivedAt, bool IsOnline, string? BatchNumber);
