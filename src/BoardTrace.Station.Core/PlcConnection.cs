using System.Net.Sockets;
using FluentModbus;

namespace BoardTrace.Station.Core;

public sealed class PlcConnection : IDisposable
{
    private const byte UnitId = 1;
    private readonly TcpClient socket;
    private readonly ModbusTcpClient client;
    private readonly TimeSpan timeout;

    private PlcConnection(TcpClient socket, ModbusTcpClient client, TimeSpan timeout)
    {
        this.socket = socket;
        this.client = client;
        this.timeout = timeout;
    }

    public static async Task<PlcConnection> ConnectAsync(
        string host, int port, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        var socket = new TcpClient();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            await socket.ConnectAsync(host, port, deadline.Token);
            var timeoutMs = checked((int)Math.Ceiling(timeout.TotalMilliseconds));
            var client = new ModbusTcpClient
            {
                ReadTimeout = timeoutMs,
                WriteTimeout = timeoutMs
            };
            client.Initialize(socket, ModbusEndianness.BigEndian);
            return new PlcConnection(socket, client, timeout);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task<(PlcInput Input, string? Error)> ReadInputAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var bytes = await client.ReadHoldingRegistersAsync(
            UnitId, PlcProtocol.InputAddress, PlcProtocol.InputRegisterCount, deadline.Token);
        PlcProtocol.TryDecodeInput(bytes.Span, out var input, out var error);
        return (input, error);
    }

    public async Task WriteOutputAsync(PlcOutput output, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        await client.WriteMultipleRegistersAsync(
            UnitId, PlcProtocol.OutputAddress, PlcProtocol.EncodeOutput(output), deadline.Token);
    }

    public void Dispose()
    {
        client.Dispose();
        socket.Dispose();
    }
}
