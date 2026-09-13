using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BoardTrace.Contracts;

namespace BoardTrace.Station.Core;

public enum UploadConnection { Idle, Connected, Unavailable, Rejected }

public sealed record UploadBatchResult(int Uploaded, int Pending, UploadConnection Connection, string? Error = null);

public sealed class InspectionUploader(LocalInspectionStore store, HttpClient client)
{
    public async Task<UploadBatchResult> UploadPendingAsync(int limit = 10, CancellationToken cancellationToken = default)
    {
        var records = await Task.Run(() => store.ReadPending(limit), cancellationToken);
        var uploaded = 0;
        var connection = UploadConnection.Idle;
        string? error = null;
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await client.PutAsJsonAsync($"api/inspections/{record.Id:D}", record, cancellationToken);
                if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created))
                {
                    connection = (int)response.StatusCode >= 500 ? UploadConnection.Unavailable : UploadConnection.Rejected;
                    error = response.StatusCode == HttpStatusCode.Conflict
                        ? $"检测 {record.Id} 与中央同编号内容冲突，原件保留待处理。"
                        : $"中央返回 HTTP {(int)response.StatusCode}，检测 {record.Id} 仍待上传。";
                    break;
                }
                var receipt = await response.Content.ReadFromJsonAsync<InspectionReceipt>(cancellationToken);
                if (receipt is null || receipt.InspectionId != record.Id ||
                    !string.Equals(receipt.ContentHash, InspectionTransfer.Hash(record), StringComparison.Ordinal))
                {
                    connection = UploadConnection.Rejected;
                    error = $"检测 {record.Id} 的中央回执编号或内容不匹配，原件保留待处理。";
                    break;
                }
                await Task.Run(() => store.ConfirmUploaded(receipt), cancellationToken);
                uploaded++;
                connection = UploadConnection.Connected;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                connection = UploadConnection.Unavailable;
                error = "中央请求超时，原件保留；下次继续上传同一检测记录。";
                break;
            }
            catch (HttpRequestException)
            {
                connection = UploadConnection.Unavailable;
                error = "中央暂不可达，原件保留；连接恢复后自动补传。";
                break;
            }
            catch (JsonException)
            {
                connection = UploadConnection.Rejected;
                error = $"检测 {record.Id} 的中央回执格式不正确，原件保留待处理。";
                break;
            }
        }
        return new UploadBatchResult(uploaded, await Task.Run(store.PendingCount, cancellationToken), connection, error);
    }
}
