// Adapted from zivillian/ism7mqtt commit 6a0aeca339 (GPL-3.0).
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using ism7mqtt;
using ism7mqtt.ISM7.Protocol;
using ism7ssl;

namespace ism7mqtt.Tests;

/// <summary>
/// Minimal ISM7 device stand-in: speaks the same TLS handshake (reusing the embedded client/server
/// certificate, exactly like <c>ism7proxy</c>'s accept side) and the same 4-byte-length + 2-byte-type +
/// XML wire framing as the real device, so a real <see cref="Ism7Client"/> can be pointed at it.
/// </summary>
public sealed class FakeIsm7Server : IAsyncDisposable
{
    public delegate List<InfonumberReadResp> PullBundleResponder(TelegramBundleReq request);

    private readonly TcpListener _listener;
    private readonly LoginState _loginState;
    private readonly PullBundleResponder _pullResponder;
    private readonly int? _dropPullBundleOrdinal;
    private readonly int? _dropPushBundleOrdinal;
    private readonly TimeSpan? _pullResponseDelay;
    private readonly TimeSpan? _pushResponseDelay;
    private readonly object _pushGate = new object();
    private readonly HashSet<int> _pendingPushResponses = new();
    private readonly List<int> _prematurePushSubscribeOrdinals = new List<int>();
    private readonly List<TelegramBundleReq> _pullRequests = new();
    private readonly List<TelegramBundleReq> _pushRequests = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _pullArrivals = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _pushArrivals = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly List<Task> _pendingResponses = new();
    private readonly Task _acceptTask;
    private TcpClient? _client;
    private SslStream? _sslStream;

    public FakeIsm7Server(PullBundleResponder pullResponder, LoginState loginState = LoginState.ok,
        int? dropPullBundleOrdinal = null, TimeSpan? pushResponseDelay = null,
        int? dropPushBundleOrdinal = null, TimeSpan? pullResponseDelay = null)
    {
        _pullResponder = pullResponder;
        _loginState = loginState;
        _dropPullBundleOrdinal = dropPullBundleOrdinal;
        _dropPushBundleOrdinal = dropPushBundleOrdinal;
        _pullResponseDelay = pullResponseDelay;
        _pushResponseDelay = pushResponseDelay;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = AcceptLoopAsync();
    }

    public int Port { get; }

    /// <summary>
    /// Ordinals (1-based, among push-subscribe requests on this connection) of push-subscribe bundles that
    /// arrived while an earlier push-subscribe's response was still outstanding. Delayed or dropped
    /// responses expose burst behavior; a serialized client must not send the next request in that window.
    /// </summary>
    public IReadOnlyList<int> PrematurePushSubscribeOrdinals
    {
        get { lock (_pushGate) return _prematurePushSubscribeOrdinals.ToArray(); }
    }

    public IReadOnlyList<TelegramBundleReq> PullRequests
    {
        get { lock (_pushGate) return _pullRequests.ToArray(); }
    }

    public IReadOnlyList<TelegramBundleReq> PushRequests
    {
        get { lock (_pushGate) return _pushRequests.ToArray(); }
    }

    public Task ConnectionClosed => _acceptTask;

    public Task WaitForPullAsync(int ordinal) => GetArrival(_pullArrivals, ordinal).Task.WaitAsync(TimeSpan.FromSeconds(10));

    public Task WaitForPushAsync(int ordinal) => GetArrival(_pushArrivals, ordinal).Task.WaitAsync(TimeSpan.FromSeconds(10));

    private static TaskCompletionSource<bool> GetArrival(ConcurrentDictionary<int, TaskCompletionSource<bool>> arrivals, int ordinal)
        => arrivals.GetOrAdd(ordinal, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

    private async Task AcceptLoopAsync()
    {
        try
        {
            _client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
            _sslStream = new SslStream(_client.GetStream(), false, (_, _, _, _) => true);
            await _sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = Ism7SslStream.Certificate,
                ClientCertificateRequired = true,
            }, _lifetime.Token);
            await HandleConnectionAsync(_sslStream);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (IOException ex) when (IsExpectedDisconnect(ex))
        {
        }
    }

    private async Task HandleConnectionAsync(Stream stream)
    {
        var pullOrdinal = 0;
        var pushOrdinal = 0;
        var header = new byte[6];
        while (true)
        {
            if (!await ReadExactAsync(stream, header, 6)) return;
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            var type = (PayloadType)BinaryPrimitives.ReadInt16BigEndian(header.AsSpan(4));
            var body = length > 0 ? new byte[length] : Array.Empty<byte>();
            if (length > 0 && !await ReadExactAsync(stream, body, length)) return;

            switch (type)
            {
                case PayloadType.DirectLogonReq:
                    await SendAsync(stream, PayloadType.DirectLogonResp, BuildLoginResp());
                    break;
                case PayloadType.TgrBundleReq:
                    var request = DeserializeXml<TelegramBundleReq>(body);
                    if (request.TelegramBundleType == TelegramBundleType.pull)
                    {
                        pullOrdinal++;
                        lock (_pushGate) _pullRequests.Add(request);
                        GetArrival(_pullArrivals, pullOrdinal).TrySetResult(true);
                        if (pullOrdinal != _dropPullBundleOrdinal)
                        {
                            if (_pullResponseDelay is { } pullDelay)
                                await Task.Delay(pullDelay, _lifetime.Token);
                            await SendAsync(stream, PayloadType.TgrBundleResp, BuildBundleResp(request, _pullResponder(request)));
                        }
                    }
                    else if (request.TelegramBundleType == TelegramBundleType.push)
                    {
                        pushOrdinal++;
                        lock (_pushGate) _pushRequests.Add(request);
                        RegisterPushSubscribeArrival(pushOrdinal);
                        GetArrival(_pushArrivals, pushOrdinal).TrySetResult(true);
                        if (pushOrdinal == _dropPushBundleOrdinal)
                            break;
                        if (_pushResponseDelay is { } delay)
                        {
                            // decouple sending the response from this read loop so that, if the client fires
                            // push-subscribes in a burst instead of waiting for each response, request N+1's
                            // bytes are already readable here well before N's (deliberately delayed) response
                            // has been sent - see RegisterPushSubscribeArrival.
                            _pendingResponses.Add(RespondToPushSubscribeAfterDelayAsync(stream, request, pushOrdinal, delay));
                        }
                        else
                        {
                            await RespondToPushSubscribeAfterDelayAsync(stream, request, pushOrdinal, TimeSpan.Zero);
                        }
                    }
                    // write bundles and keep-alives are not needed by the current test cases.
                    break;
            }
        }
    }

    private void RegisterPushSubscribeArrival(int ordinal)
    {
        lock (_pushGate)
        {
            if (_pendingPushResponses.Count > 0)
            {
                _prematurePushSubscribeOrdinals.Add(ordinal);
            }
            _pendingPushResponses.Add(ordinal);
        }
    }

    private async Task RespondToPushSubscribeAfterDelayAsync(Stream stream, TelegramBundleReq request, int ordinal, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _lifetime.Token);
            // Mark the response before writing so a fast client cannot outrun the server's bookkeeping.
            lock (_pushGate) _pendingPushResponses.Remove(ordinal);
            await SendAsync(stream, PayloadType.TgrBundleResp, BuildBundleResp(request, new List<InfonumberReadResp>()));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (IOException ex) when (IsExpectedDisconnect(ex))
        {
        }
    }

    private bool IsExpectedDisconnect(IOException exception)
        => _lifetime.IsCancellationRequested ||
           exception.InnerException is SocketException
           {
               SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown
           };

    private LoginResp BuildLoginResp() => new LoginResp
    {
        State = _loginState,
        Sid = "test-session",
        InstallationName = "FakeIsm7Server",
        Serialnumber = "0",
        DateTime = DateTime.UtcNow.ToString("O"),
        IsmSoftwareVersion = "1.0",
        IsmHardwareVersion = "1.0",
        IsWLanConnected = false,
        Type = "ISM7i",
    };

    private static TelegramBundleResp BuildBundleResp(TelegramBundleReq request, List<InfonumberReadResp> telegrams) => new TelegramBundleResp
    {
        Timestamp = DateTime.UtcNow.ToString("O"),
        GatewayId = request.GatewayId,
        BundleId = request.BundleId,
        State = TelegrResponseState.OK,
        Telegrams = telegrams,
    };

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset));
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    private async Task SendAsync<T>(Stream stream, PayloadType type, T payload)
    {
        var body = SerializeXml(payload);
        var frame = new byte[body.Length + 6];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        BinaryPrimitives.WriteInt16BigEndian(frame.AsSpan(4), (short)type);
        Buffer.BlockCopy(body, 0, frame, 6, body.Length);
        await _sendGate.WaitAsync(_lifetime.Token);
        try
        {
            await stream.WriteAsync(frame, _lifetime.Token);
            await stream.FlushAsync(_lifetime.Token);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private static byte[] SerializeXml<T>(T obj)
    {
        var namespaces = new XmlSerializerNamespaces();
        namespaces.Add("", "");
        using var sw = new Utf8StringWriter();
        using var xmlWriter = XmlWriter.Create(sw, new XmlWriterSettings { Indent = false });
        new XmlSerializer(typeof(T)).Serialize(xmlWriter, obj, namespaces);
        xmlWriter.Flush();
        return Encoding.UTF8.GetBytes(sw.ToString());
    }

    private static T DeserializeXml<T>(byte[] body)
    {
        using var ms = new MemoryStream(body);
        return (T)new XmlSerializer(typeof(T)).Deserialize(ms)!;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _listener.Stop();
        _sslStream?.Dispose();
        _client?.Dispose();
        await _acceptTask;
        await Task.WhenAll(_pendingResponses);
        _sendGate.Dispose();
        _lifetime.Dispose();
    }
}
