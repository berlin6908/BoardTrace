using BoardTrace.Contracts;
using System.Text.Json;

namespace BoardTrace.Server.Inspections;

public static class InspectionValidation
{
    public static string? Validate(Guid id, InspectionRecord record)
    {
        if (id == Guid.Empty || record.Id != id) return "路径编号必须与检测档案编号一致。";
        if (!Enum.IsDefined(record.Purpose)) return "检测用途无效。";
        if ((record.ControllerSessionId is null) != (record.TriggerSequence is null) ||
            record.ControllerSessionId == Guid.Empty || record.TriggerSequence == 0)
            return "PLC 会话与触发序号必须同时提供，且均非零。";
        foreach (var (value, limit, name) in new[]
        {
            (record.StationId, 128, "工位"), (record.ProductId, 128, "产品"),
            (record.SampleId, 128, "输入样本"), (record.SourceKind, 32, "采集来源"), (record.RecipeId, 128, "方案版本"),
            (record.OperatorId, 450, "操作员编号"), (record.OperatorName, 128, "操作员姓名")
        })
            if (string.IsNullOrWhiteSpace(value) || value.Length > limit) return $"{name}不能为空且不能超过 {limit} 字符。";
        if (record.CompletedAt is null || !Enum.IsDefined(record.ExecutionStatus) || record.ExecutionStatus == InspectionExecution.Started)
            return "中央只接收已经结束的检测尝试。";
        if (record.Defects is null || record.Diagnostics is null) return "缺陷与诊断数据不能为空。";
        if (string.IsNullOrWhiteSpace(record.RecipeJson) || record.RecipeJson.Length > 65536) return "缺少有效的方案参数快照。";
        try { using var recipe = JsonDocument.Parse(record.RecipeJson); }
        catch (JsonException) { return "方案参数快照不是有效 JSON。"; }

        if (record.ExecutionStatus == InspectionExecution.Completed)
        {
            if (record.Decision is not (QualityDecision.Pass or QualityDecision.Fail) ||
                (record.Decision == QualityDecision.Pass) != (record.Defects.Count == 0))
                return "有效质量判定必须与缺陷结果一致。";
            if (record.TestedImage is not { Length: > 0 } || record.ReferenceImage is not { Length: > 0 } || record.Width <= 0 || record.Height <= 0)
                return "完成检测必须携带两幅原始图像及有效尺寸。";
        }
        else if (record.Decision != QualityDecision.NotEvaluated || record.Defects.Count != 0)
            return "执行失败或中断不能携带有效 Pass/Fail 判定。";
        foreach (var defect in record.Defects)
        {
            if (defect is null || defect.Box is not { Length: 4 } || defect.Box.Any(value => !double.IsFinite(value)) ||
                defect.Box[0] < 0 || defect.Box[1] < 0 || defect.Box[0] >= defect.Box[2] || defect.Box[1] >= defect.Box[3] ||
                defect.Box[2] > record.Width || defect.Box[3] > record.Height ||
                defect.ClassId is < 1 or > 6 || !double.IsFinite(defect.Score) || defect.Score is < 0 or > 1)
                return "缺陷坐标、类别或置信度无效。";
        }
        return null;
    }
}
