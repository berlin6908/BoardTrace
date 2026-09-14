namespace BoardTrace.Vision;

// Class IDs are fixed by the exported detector: open, short, mousebite, spur, copper, pin-hole.
public sealed record OnnxScoreThresholds(double Open = 0.5, double Short = 0.5, double Mousebite = 0.5,
    double Spur = 0.5, double Copper = 0.5, double PinHole = 0.5)
{
    public double[] ToClassOrder() => [Open, Short, Mousebite, Spur, Copper, PinHole];

    public static OnnxScoreThresholds FromClassOrder(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count != 6) throw new ArgumentException("必须按类别 1 到 6 提供六个置信度阈值。", nameof(values));
        var result = new OnnxScoreThresholds(values[0], values[1], values[2], values[3], values[4], values[5]);
        result.Validate();
        return result;
    }

    internal void Validate()
    {
        if (ToClassOrder().Any(value => !double.IsFinite(value) || value is < 0 or > 1))
            throw new ArgumentOutOfRangeException(nameof(OnnxScoreThresholds), "六个置信度阈值都必须在 0 到 1 之间。");
    }

    internal double ForClass(int classId) => classId switch
    {
        1 => Open, 2 => Short, 3 => Mousebite, 4 => Spur, 5 => Copper, 6 => PinHole,
        _ => throw new ArgumentOutOfRangeException(nameof(classId))
    };
}
