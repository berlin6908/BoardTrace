using System.Buffers.Binary;
using BoardTrace.Contracts;
using BoardTrace.Station.Core;

namespace BoardTrace.Station.Tests;

public sealed class PlcProtocolTests
{
    [Fact]
    public void DecodesWireInputAtFixedZeroBasedOffsets()
    {
        var bytes = new byte[PlcProtocol.InputRegisterCount * 2];
        var session = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        WriteWord(bytes, 0, 1);
        session.TryWriteBytes(bytes.AsSpan(2, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(18, 4), 0x01020304);
        WriteWord(bytes, 11, 7);
        "BOARD-1"u8.CopyTo(bytes.AsSpan(24));
        WriteWord(bytes, 28, 6);
        "S-0001"u8.CopyTo(bytes.AsSpan(58));
        WriteWord(bytes, 37, 1);

        Assert.True(PlcProtocol.TryDecodeInput(bytes, out var input, out var error), error);
        Assert.True(input.Trigger);
        Assert.Equal(session, input.Identity.ControllerSessionId);
        Assert.Equal(0x01020304u, input.Identity.TriggerSequence);
        Assert.Equal("BOARD-1", input.ProductId);
        Assert.Equal("S-0001", input.SampleId);
        Assert.True(input.ResultsAck);
    }

    [Fact]
    public void MalformedTriggerRetainsIdentityForInvalidResponse()
    {
        var bytes = new byte[PlcProtocol.InputRegisterCount * 2];
        var session = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        WriteWord(bytes, 0, 1);
        session.TryWriteBytes(bytes.AsSpan(2, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(18, 4), 8);
        WriteWord(bytes, 11, 33); // Too long for ProductId.

        Assert.False(PlcProtocol.TryDecodeInput(bytes, out var input, out var error));
        Assert.NotNull(error);
        Assert.Equal(new InspectionIdentity(session, 8), input.Identity);
        Assert.True(input.Trigger);
    }

    [Fact]
    public void EncodesOneCompleteOutputSnapshotInNetworkOrder()
    {
        var session = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var inspection = Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f");
        var bytes = PlcProtocol.EncodeOutput(new PlcOutput(
            TriggerReady: false,
            TriggerAck: true,
            Busy: true,
            ResultsValid: true,
            Fault: false,
            TriggerDisposition: PlcTriggerDisposition.Duplicate,
            TriggerIdentity: new InspectionIdentity(session, 0x01020304),
            ResultIdentity: new InspectionIdentity(session, 0x01020304),
            ResultCode: PlcResultCode.Fail,
            InspectionId: inspection,
            ProductionSequence: 0x05060708,
            Heartbeat: 0x1234));

        Assert.Equal(68, bytes.Length);
        Assert.Equal(14, ReadWord(bytes, 0));
        Assert.Equal(2, ReadWord(bytes, 1));
        Assert.Equal(Convert.FromHexString("00112233445566778899AABBCCDDEEFF"), bytes[4..20]);
        Assert.Equal(0x01020304u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)));
        Assert.Equal(Convert.FromHexString("00112233445566778899AABBCCDDEEFF"), bytes[24..40]);
        Assert.Equal(2, ReadWord(bytes, 22));
        Assert.Equal(Convert.FromHexString("102132435465768798A9BACBDCEDFE0F"), bytes[46..62]);
        Assert.Equal(0x05060708u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(62, 4)));
        Assert.Equal(0x1234, ReadWord(bytes, 33));
    }

    private static void WriteWord(Span<byte> bytes, int register, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(bytes.Slice(register * 2, 2), value);

    private static ushort ReadWord(ReadOnlySpan<byte> bytes, int register) =>
        BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(register * 2, 2));
}
