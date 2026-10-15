using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ModelSwitch.AdapterContractTests.Support;

/// <summary>What the raw server does after it has sent its frames.</summary>
public enum StreamEnding
{
    /// <summary>Close the socket without the terminating chunk: the response is cut mid-way.</summary>
    Cut,

    /// <summary>Keep the connection open and silent until the client goes away.</summary>
    Hold,
}

/// <summary>
/// A tiny single-purpose HTTP/1.1 server for the two things WireMock cannot do: close a socket in the middle of a streamed response,
/// and keep a stream open (after some frames) so cancellation can be observed. It answers every request with the same script.
/// </summary>
public sealed class RawStreamServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _clientDisconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _contentType;
    private readonly IReadOnlyList<string> _frames;
    private readonly StreamEnding _ending;
    private readonly Task _acceptLoop;

    public RawStreamServer(string contentType, IReadOnlyList<string> frames, StreamEnding ending)
    {
        _contentType = contentType;
        _frames = frames;
        _ending = ending;
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptAsync(_stop.Token));
    }

    public Uri BaseAddress => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");

    /// <summary>Completes when the client closes its end of the connection (after receiving the script, for <see cref="StreamEnding.Hold"/>).</summary>
    public Task ClientDisconnected => _clientDisconnected.Task;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => ServeAsync(client, ct), ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        try
        {
            await ReadRequestAsync(stream, ct);

            var head = $"HTTP/1.1 200 OK\r\nContent-Type: {_contentType}\r\nTransfer-Encoding: chunked\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
            foreach (var frame in _frames)
            {
                var payload = Encoding.UTF8.GetBytes(frame);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"{payload.Length:x}\r\n"), ct);
                await stream.WriteAsync(payload, ct);
                await stream.WriteAsync("\r\n"u8.ToArray(), ct);
            }

            await stream.FlushAsync(ct);

            if (_ending == StreamEnding.Hold)
            {
                var buffer = new byte[256];
                while (await stream.ReadAsync(buffer, ct) > 0)
                {
                }

                _clientDisconnected.TrySetResult();
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            _clientDisconnected.TrySetResult();
        }
    }

    private static async Task ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var received = new List<byte>();
        var buffer = new byte[4096];
        int headerEnd;
        while ((headerEnd = IndexOfHeaderEnd(received)) < 0)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
            {
                throw new IOException("The client closed the connection before sending a request.");
            }

            received.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        var headers = Encoding.ASCII.GetString([.. received.Take(headerEnd)]);
        var contentLength = headers.Split("\r\n")
            .Where(l => l.StartsWith("content-length:", StringComparison.OrdinalIgnoreCase))
            .Select(l => int.Parse(l[15..].Trim(), System.Globalization.CultureInfo.InvariantCulture))
            .FirstOrDefault();
        var bodyRead = received.Count - (headerEnd + 4);
        while (bodyRead < contentLength)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
            {
                throw new IOException("The client closed the connection mid-request.");
            }

            bodyRead += read;
        }
    }

    private static int IndexOfHeaderEnd(List<byte> bytes)
    {
        for (var i = 0; i + 3 < bytes.Count; i++)
        {
            if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }
}
