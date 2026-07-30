using System.Threading.Channels;

namespace WinDayFlow.Application.Ai;

public sealed class AnalysisProviderSendGate
{
    private readonly Channel<byte> _gate = CreateGate();

    public async ValueTask<IDisposable> EnterAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = await _gate.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(_gate.Writer);
    }

    private static Channel<byte> CreateGate()
    {
        var gate = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
        if (!gate.Writer.TryWrite(0))
        {
            throw new InvalidOperationException(
                "The analysis provider send gate could not initialize.");
        }

        return gate;
    }

    private sealed class Releaser(ChannelWriter<byte> writer) : IDisposable
    {
        private ChannelWriter<byte>? _writer = writer;

        public void Dispose()
        {
            var writer = Interlocked.Exchange(ref _writer, null);
            if (writer is not null && !writer.TryWrite(0))
            {
                throw new InvalidOperationException(
                    "The analysis provider send gate was released out of order.");
            }
        }
    }
}
