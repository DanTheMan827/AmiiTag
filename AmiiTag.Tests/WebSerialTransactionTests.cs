using AmiiTag.Nfc;
using AmiiTag.Nfc.Mfrc522;
using AmiiTag.Tags;
using AmiiTag.Tests.Simulation;
using AmiiTag.WebAssembly.Services;
using Microsoft.JSInterop;

namespace AmiiTag.Tests;

public class WebSerialTransactionTests
{
    [Fact]
    public async Task WebBatchUsesOneInteropCallAndNoManagedReadCallbacks()
    {
        var js = new SerialRuntime { Data = [0x91, 0x92] };
        await using var port = new WebSerialPortStream(js, "serial-1");
        var result = await port.TransactAsync([0x80, 0x81], 2, TimeSpan.FromMilliseconds(250));
        Assert.Equal(new byte[] { 0x91, 0x92 }, result);
        Assert.Single(js.Calls);
        Assert.Equal("amiitag.serial.transact", js.Calls[0].Name);
        Assert.Equal("serial-1", js.Calls[0].Args[0]);
        Assert.Equal(2, js.Calls[0].Args[2]);
        Assert.Equal(250d, js.Calls[0].Args[3]);
        Assert.False(js.Calls[0].Token.CanBeCanceled);
    }

    [Fact]
    public async Task BrowserTimeoutAndDisconnectAreDistinctFromInteropFailures()
    {
        var js = new SerialRuntime { Error = "timeout", Received = 1 };
        await using var port = new WebSerialPortStream(js, "serial-1");
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => port.TransactAsync([0x80], 2, TimeSpan.FromMilliseconds(250)));
        Assert.Contains("1 of 2", timeout.Message);
        js.Error = "closed";
        await Assert.ThrowsAsync<IOException>(() => port.TransactAsync([0x80], 2, TimeSpan.FromMilliseconds(250)));
        js.Failure = new JSException("USB disconnected");
        var failure = await Assert.ThrowsAsync<IOException>(() => port.TransactAsync([0x80], 2, TimeSpan.FromMilliseconds(250)));
        Assert.IsType<JSException>(failure.InnerException);
    }

    [Fact]
    public async Task LegacyReceiveChannelStillSupportsPartialReadsAndDrainCancellation()
    {
        await using var port = new WebSerialPortStream(new SerialRuntime(), "serial-1");
        port.OnData([1, 2, 3]);
        var buffer = new byte[3];
        Assert.Equal(2, await port.ReadAsync(buffer, 0, 2));
        Assert.Equal(1, await port.ReadAsync(buffer, 2, 1));
        Assert.Equal(new byte[] { 1, 2, 3 }, buffer);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => port.ReadAsync(buffer, 0, 3, canceled.Token));
    }

    [Fact]
    public async Task UartDelegatesWholeBatchesAndKeepsItsBatchSizeLimits()
    {
        await using var port = new TransactionPort((request, count, _) => Task.FromResult(request.Take(count).ToArray()));
        var uart = new Mfrc522Uart(port, 115200);
        var reads = await uart.ReadManyAsync(Enumerable.Repeat((byte)1, 70).ToArray());
        Assert.Equal(70, reads.Length);
        Assert.Equal(new[] { 64, 6 }, port.ResponseLengths);
        Assert.Equal(new[] { 64, 6 }, port.RequestLengths);
        Assert.All(port.Timeouts, timeout => Assert.Equal(TimeSpan.FromMilliseconds(250), timeout));
    }

    [Fact]
    public async Task CancellationFinishesTheInFlightBatchBeforeStoppingTheNextOne()
    {
        var pending = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var port = new TransactionPort((_, _, _) => pending.Task);
        var uart = new Mfrc522Uart(port, 115200);
        using var cancellation = new CancellationTokenSource();
        var reading = uart.ReadManyAsync(Enumerable.Repeat((byte)1, 70).ToArray(), cancellation.Token);
        cancellation.Cancel();
        Assert.False(reading.IsCompleted);
        pending.SetResult(new byte[64]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.Single(port.ResponseLengths);
    }

    [Fact]
    public async Task Mfrc522ReadsTheLockOnThroughFragmentedBatchedSerialTransactions()
    {
        var figure = AmiiboV3Dump.Create(AmiiboV3Tests.V3Id, TestData.Keys).WithLockOn(new byte[] { 1, 2, 3 });
        var bridge = new SimulatedMfrc522Bridge(NtagI2cPlusSimulator.FromImage(figure));
        var ports = new BatchedFactory(new SimulatedSerialPortFactory(bridge));
        var provider = new Mfrc522NfcProvider(ports);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var connection = await provider.ConnectAsync(new NfcConnectOptions(), timeout.Token);
        var read = Assert.IsType<AmiiboV3Dump>(await AmiiboV3NfcOperations.ReadAnyAsync(connection, cancellationToken: timeout.Token));
        Assert.Equal(figure.LockOn, read.LockOn);
        Assert.True(ports.Transactions > 0);
    }

    private sealed class BatchedFactory(ISerialPortFactory inner) : ISerialPortFactory
    {
        public int Transactions;
        public bool IsSupported => true;
        public Task<IReadOnlyList<NfcReaderInfo>> GetPortsAsync(CancellationToken cancellationToken = default) => inner.GetPortsAsync(cancellationToken);
        public async Task<ISerialPortStream> OpenAsync(string portId, int baudRate, CancellationToken cancellationToken = default) =>
            new BatchedStream(await inner.OpenAsync(portId, baudRate, cancellationToken), () => Transactions++);
    }

    // Transport-only fragmentation adapter; the RF/UART simulator implementation remains unchanged.
    private sealed class BatchedStream(ISerialPortStream inner, Action record) : ISerialPortTransactionStream
    {
        public async Task<byte[]> TransactAsync(byte[] request, int responseLength, TimeSpan timeout)
        {
            record();
            await inner.WriteAsync(request);
            var output = new byte[responseLength];
            using var deadline = new CancellationTokenSource(timeout);
            try
            {
                for (var offset = 0; offset < responseLength;)
                {
                    await Task.Yield();
                    offset += await inner.ReadAsync(output, offset, Math.Min(3, responseLength - offset), deadline.Token);
                }
            }
            catch (OperationCanceledException) { throw new TimeoutException(); }
            return output;
        }
        public Task WriteAsync(byte[] data, CancellationToken cancellationToken = default) => inner.WriteAsync(data, cancellationToken);
        public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, offset, count, cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class TransactionPort(Func<byte[], int, TimeSpan, Task<byte[]>> transact) : ISerialPortTransactionStream
    {
        public List<int> ResponseLengths { get; } = [];
        public List<int> RequestLengths { get; } = [];
        public List<TimeSpan> Timeouts { get; } = [];
        public Task<byte[]> TransactAsync(byte[] request, int responseLength, TimeSpan timeout)
        {
            ResponseLengths.Add(responseLength);
            RequestLengths.Add(request.Length);
            Timeouts.Add(timeout);
            return transact(request, responseLength, timeout);
        }
        public Task WriteAsync(byte[] data, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Batch fell back to separate write");
        public Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Batch fell back to managed read");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SerialRuntime : IJSRuntime
    {
        public byte[]? Data { get; set; }
        public string? Error { get; set; }
        public int Received { get; set; }
        public Exception? Failure { get; set; }
        public List<(string Name, object?[] Args, CancellationToken Token)> Calls { get; } = [];
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Calls.Add((identifier, args ?? [], cancellationToken));
            if (identifier == "amiitag.serial.close") return ValueTask.FromResult(default(TValue)!);
            if (Failure != null) return ValueTask.FromException<TValue>(Failure);
            var reply = Activator.CreateInstance(typeof(TValue), nonPublic: true)!;
            typeof(TValue).GetProperty("Data")!.SetValue(reply, Data);
            typeof(TValue).GetProperty("Error")!.SetValue(reply, Error);
            typeof(TValue).GetProperty("Received")!.SetValue(reply, Received);
            return ValueTask.FromResult((TValue)reply);
        }
    }
}
