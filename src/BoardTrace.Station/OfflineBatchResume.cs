using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BoardTrace.Contracts;
using BoardTrace.Station.Core;

namespace BoardTrace.Station;

internal sealed record ResumeCookie(string Name, string Value, string Path, bool Secure, bool HttpOnly, DateTime Expires);
internal sealed record ResumeEnvelope(CurrentUser Operator, BatchExecutionSession Session, ResumeCookie[] Cookies);
internal sealed record ResumeCandidate(CachedBatchState Batch, ResumeEnvelope Envelope);

// Windows owns key protection; the existing batch session owns authority and expiry.
internal static class OfflineBatchResume
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static byte[] Purpose(StationOptions options) =>
        Encoding.UTF8.GetBytes($"BoardTrace.BatchResume\n{options.ServerUrl.AbsoluteUri}\n{options.StationId}");

    public static byte[] Protect(StationOptions options, CurrentUser actor, StationPersonnelSession personnel, BatchExecutionSession session)
    {
        var cookies = personnel.Cookies.GetCookies(options.ServerUrl).Cast<Cookie>().Where(cookie => !cookie.Expired)
            .Select(cookie => new ResumeCookie(cookie.Name, cookie.Value, cookie.Path, cookie.Secure, cookie.HttpOnly, cookie.Expires)).ToArray();
        if (cookies.Length == 0) throw new InvalidOperationException("人员登录票据不可用，请重新登录后启动批次。");
        return ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(new ResumeEnvelope(actor, session, cookies), Json),
            Purpose(options), DataProtectionScope.CurrentUser);
    }

    public static ResumeCandidate? Read(StationOptions options)
    {
        if (!File.Exists(options.DatabasePath)) return null;
        var saved = new LocalInspectionStore(options.DatabasePath).ReadBatchResume();
        if (saved is null) return null;
        var plaintext = ProtectedData.Unprotect(saved.ProtectedPayload, Purpose(options), DataProtectionScope.CurrentUser);
        ResumeEnvelope envelope;
        try { envelope = JsonSerializer.Deserialize<ResumeEnvelope>(plaintext, Json) ?? throw new InvalidDataException("本班恢复包为空。"); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        var batch = saved.Batch;
        var session = envelope.Session;
        StationAuthentication.RequireOperator(envelope.Operator);
        if (batch.Session != session || session.ArchiveId != batch.ArchiveId ||
            session.OperatorId != envelope.Operator.Id || session.StationId != options.StationId ||
            batch.Status != BatchStatus.InProgress || batch.Approval is null ||
            session.IssuedAt > DateTimeOffset.UtcNow || session.ExpiresAt <= DateTimeOffset.UtcNow ||
            batch.AcceptedProductionCount >= batch.Batch.PlannedQuantity)
            throw new InvalidOperationException("原批次人员授权已结束或当前批次不能继续，请在线登录。");
        return new ResumeCandidate(batch, envelope);
    }

    public static StationPersonnelSession RestorePersonnel(StationOptions options, ResumeCandidate candidate)
    {
        var personnel = StationAuthentication.CreatePersonnelSession(options.ServerUrl);
        try
        {
            foreach (var cookie in candidate.Envelope.Cookies)
                personnel.Cookies.Add(options.ServerUrl, new Cookie(cookie.Name, cookie.Value, cookie.Path)
                    { Secure = cookie.Secure, HttpOnly = cookie.HttpOnly, Expires = cookie.Expires });
            return personnel;
        }
        catch { personnel.Dispose(); throw; }
    }

    public static bool IsUnavailable(Exception error) => error is HttpRequestException { StatusCode: null }
        or HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError } or OperationCanceledException;
}
