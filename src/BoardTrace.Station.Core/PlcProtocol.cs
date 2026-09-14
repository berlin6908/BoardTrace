using System.Buffers.Binary;
using System.Text;
using BoardTrace.Contracts;

namespace BoardTrace.Station.Core;

public readonly record struct PlcInput(
    bool Trigger,
    InspectionIdentity Identity,
    string ProductId,
    string SampleId,
    bool ResultsAck);

public enum PlcTriggerDisposition : ushort
{
    None = 0,
    Accepted = 1,
    Duplicate = 2,
    BusyRejected = 3,
    InvalidRejected = 4
}

public enum PlcResultCode : ushort
{
    None = 0,
    Pass = 1,
    Fail = 2,
    NotEvaluated = 3
}

public readonly record struct PlcOutput(
    bool TriggerReady,
    bool TriggerAck,
    bool Busy,
    bool ResultsValid,
    bool Fault,
    PlcTriggerDisposition TriggerDisposition,
    InspectionIdentity? TriggerIdentity,
    InspectionIdentity? ResultIdentity,
    PlcResultCode ResultCode,
    Guid? InspectionId,
    uint ProductionSequence,
    ushort Heartbeat);

public static class PlcProtocol
{
    public const ushort InputAddress = 0;
    public const ushort InputRegisterCount = 38;
    public const ushort OutputAddress = 100;
    public const ushort OutputRegisterCount = 34;

    public static bool TryDecodeInput(ReadOnlySpan<byte> bytes, out PlcInput input, out string? error)
    {
        if (bytes.Length != InputRegisterCount * 2)
        {
            input = default;
            error = "PLC input block must contain exactly 38 registers.";
            return false;
        }

        try
        {
            input = DecodeInput(bytes);
            error = null;
            return true;
        }
        catch (ArgumentException exception)
        {
            input = new PlcInput(
                ReadUInt16(bytes, 0) != 0,
                new InspectionIdentity(ReadGuid(bytes, 1), ReadUInt32(bytes, 9)),
                "", "", ReadUInt16(bytes, 37) != 0);
            error = exception.Message;
            return false;
        }
    }

    public static PlcInput DecodeInput(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != InputRegisterCount * 2)
            throw new ArgumentException("PLC input block must contain exactly 38 registers.", nameof(bytes));

        var trigger = ReadFlag(bytes, 0);
        var ack = ReadFlag(bytes, 37);
        var session = ReadGuid(bytes, 1);
        var sequence = ReadUInt32(bytes, 9);
        var product = ReadText(bytes, 11, 12, 32);
        var sample = ReadText(bytes, 28, 29, 16);
        if (trigger && (session == Guid.Empty || sequence == 0 || product.Length == 0 || sample.Length == 0))
            throw new ArgumentException("Triggered PLC input has an invalid identity or missing product/sample.", nameof(bytes));

        return new PlcInput(trigger, new InspectionIdentity(session, sequence), product, sample, ack);
    }

    public static byte[] EncodeOutput(PlcOutput output)
    {
        var bytes = new byte[OutputRegisterCount * 2];
        ushort flags = 0;
        if (output.TriggerReady) flags |= 1;
        if (output.TriggerAck) flags |= 2;
        if (output.Busy) flags |= 4;
        if (output.ResultsValid) flags |= 8;
        if (output.Fault) flags |= 16;
        WriteUInt16(bytes, 0, flags);
        WriteUInt16(bytes, 1, (ushort)output.TriggerDisposition);
        WriteIdentity(bytes, 2, output.TriggerIdentity);
        WriteIdentity(bytes, 12, output.ResultIdentity);
        WriteUInt16(bytes, 22, (ushort)output.ResultCode);
        WriteGuid(bytes, 23, output.InspectionId ?? Guid.Empty);
        WriteUInt32(bytes, 31, output.ProductionSequence);
        WriteUInt16(bytes, 33, output.Heartbeat);
        return bytes;
    }

    private static bool ReadFlag(ReadOnlySpan<byte> bytes, int register)
    {
        var value = ReadUInt16(bytes, register);
        if (value > 1) throw new ArgumentException("PLC flag must be zero or one.", nameof(bytes));
        return value == 1;
    }

    private static string ReadText(ReadOnlySpan<byte> bytes, int lengthRegister, int firstRegister, int capacity)
    {
        var length = ReadUInt16(bytes, lengthRegister);
        if (length > capacity) throw new ArgumentException("PLC text length exceeds register capacity.", nameof(bytes));
        var text = bytes.Slice(firstRegister * 2, length);
        foreach (var value in text)
            if (value == 0 || value > 0x7f) throw new ArgumentException("PLC text must be nonzero ASCII.", nameof(bytes));
        return Encoding.ASCII.GetString(text);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int register) =>
        BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(register * 2, 2));

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int register) =>
        BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(register * 2, 4));

    private static Guid ReadGuid(ReadOnlySpan<byte> bytes, int register) =>
        new(bytes.Slice(register * 2, 16), bigEndian: true);

    private static void WriteUInt16(Span<byte> bytes, int register, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(bytes.Slice(register * 2, 2), value);

    private static void WriteUInt32(Span<byte> bytes, int register, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(bytes.Slice(register * 2, 4), value);

    private static void WriteGuid(Span<byte> bytes, int register, Guid value) =>
        value.TryWriteBytes(bytes.Slice(register * 2, 16), bigEndian: true, out _);

    private static void WriteIdentity(Span<byte> bytes, int firstRegister, InspectionIdentity? identity)
    {
        WriteGuid(bytes, firstRegister, identity?.ControllerSessionId ?? Guid.Empty);
        WriteUInt32(bytes, firstRegister + 8, identity?.TriggerSequence ?? 0);
    }
}
