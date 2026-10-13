// Adapted from zivillian/ism7mqtt commit 6a0aeca339 (GPL-3.0); XML overrides are intentionally excluded.
using ism7mqtt.ISM7.Protocol;
using Xunit;

// Startup errors are logged to Console.Out, which is process-wide.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ism7mqtt.Tests;

public class Ism7ClientStartupTests
{
    private static readonly Dictionary<ushort, (string Low, string High)> HappyPathTelegrams = new()
    {
        [12] = ("0x55", "0x00"),    // Außentemperatur: 8.5°C
        [367] = ("0x2A", "0x00"),   // Leistungsvorgabe Verdichter: 42%
        [12409] = ("0x01", "0x00"), // Heizkreispumpe: Ein
        [10164] = ("0xD5", "0x00"), // Raumtemperatur: 21.3°C
        [10100] = ("0x01", "0x00"), // Programmwahl: Auto
        [5266] = ("0x02", "0x00"),  // Reglertyp: 2
    };

    private static List<InfonumberReadResp> RespondFromTable(
        TelegramBundleReq request, IReadOnlyDictionary<ushort, (string Low, string High)> table)
        => request.InfoReadTelegrams.Select(ir =>
        {
            var (low, high) = table[(ushort)ir.InfoNumber];
            return new InfonumberReadResp
            {
                BusAddress = ir.BusAddress,
                InfoNumber = (ushort)ir.InfoNumber,
                ServiceNumberAsText = ir.ServiceNumber >= 0 ? ir.ServiceNumber.ToString() : null,
                State = TelegrResponseState.OK,
                DBLow = low,
                DBHigh = high,
            };
        }).ToList();

    private static List<InfonumberReadResp> RespondWithZeros(TelegramBundleReq request)
        => RespondFromTable(request, request.InfoReadTelegrams
            .Select(ir => (ushort)ir.InfoNumber).Distinct()
            .ToDictionary(number => number, _ => ("0x00", "0x00")));

    [Fact]
    public async Task HappyPath_PublishesBothDevicesWithExpectedValues()
    {
        await using var server = new FakeIsm7Server(request => RespondFromTable(request, HappyPathTelegrams));
        var captured = new List<JsonMessage>();
        await using var session = new StartupSession(server, "happy-path.json", (config, _) =>
        {
            captured.AddRange(config.JsonMessages);
            return Task.CompletedTask;
        });

        Assert.Equal(90, session.Client.StartupTimeout);
        Assert.False(session.Client.SerializePushSubscribes);
        session.Start();
        await session.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.StopAsync();

        Assert.Equal(2, captured.Count);
        var cha = Assert.Single(captured, m => m.Path == "Wolf/127.0.0.1/CHA_0x29");
        Assert.Equal("8.5", cha.Content["Außentemperatur"]!.ToJsonString());
        Assert.Equal("42", cha.Content["Aktuelle Leistungsvorgabe Verdichter"]!.ToJsonString());
        Assert.Equal("Ein", cha.Content["Heizkreispumpe"]!["text"]!.GetValue<string>());
        Assert.Equal("1", cha.Content["Heizkreispumpe"]!["value"]!.ToJsonString());

        var mkBm2 = Assert.Single(captured, m => m.Path == "Wolf/127.0.0.1/MK_BM-2_0x85");
        Assert.Equal("21.3", mkBm2.Content["Raumtemperatur"]!.ToJsonString());
        Assert.Equal("Auto", mkBm2.Content["Programmwahl"]!["text"]!.GetValue<string>());
        Assert.Equal("1", mkBm2.Content["Programmwahl"]!["value"]!.ToJsonString());
        Assert.Equal("2", mkBm2.Content["Reglertyp"]!.ToJsonString());
    }

    [Fact]
    public async Task HappyPath_ChunksLargeDeviceIntoTwentyTelegramBundles()
    {
        await using var server = new FakeIsm7Server(RespondWithZeros);
        var handlerInvocationCount = 0;
        await using var session = new StartupSession(server, "chunked-pull.json", (_, _) =>
        {
            handlerInvocationCount++;
            return Task.CompletedTask;
        });
        session.Client.StartupTimeout = 5;
        session.Client.SerializePushSubscribes = true;

        session.Start();
        await session.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.StopAsync();

        Assert.Equal(new[] { 20, 20, 2 }, server.PullRequests.Select(r => r.InfoReadTelegrams.Count));
        Assert.All(server.PullRequests, r => Assert.InRange(r.InfoReadTelegrams.Count, 1, 20));
        Assert.Equal(3, server.PushRequests.Count);
        foreach (var (pull, push) in server.PullRequests.Zip(server.PushRequests))
            Assert.Equal(pull.InfoReadTelegrams.Select(r => (r.InfoNumber, r.ServiceNumber)),
                push.InfoReadTelegrams.Select(r => (r.InfoNumber, r.ServiceNumber)));
        Assert.Equal(6, handlerInvocationCount);
    }

    [Fact]
    public async Task DefaultPushSubscribes_AreBurstSentWithoutWaitingForResponses()
    {
        await using var server = new FakeIsm7Server(RespondWithZeros,
            pushResponseDelay: TimeSpan.FromSeconds(30));
        await using var session = new StartupSession(server, "chunked-pull.json");
        session.Client.StartupTimeout = 1;

        session.Start();
        await server.WaitForPushAsync(3);
        await session.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.StopAsync();

        Assert.False(session.Client.SerializePushSubscribes);
        Assert.Equal(3, server.PushRequests.Count);
        Assert.Equal(new[] { 2, 3 }, server.PrematurePushSubscribeOrdinals);
    }

    [Fact]
    public async Task SerializedPushSubscribes_WaitForEveryFirstResponse()
    {
        await using var server = new FakeIsm7Server(RespondWithZeros,
            pushResponseDelay: TimeSpan.FromMilliseconds(200));
        var handlerInvocationCount = 0;
        await using var session = new StartupSession(server, "chunked-pull.json", (_, _) =>
        {
            handlerInvocationCount++;
            return Task.CompletedTask;
        });
        session.Client.StartupTimeout = 5;
        session.Client.SerializePushSubscribes = true;

        session.Start();
        await session.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.StopAsync();

        Assert.Equal(3, server.PushRequests.Count);
        Assert.Empty(server.PrematurePushSubscribeOrdinals);
        // All three pull and all three first push responses were processed before initialization.
        Assert.Equal(6, handlerInvocationCount);
    }

    [Fact]
    public async Task StartupTimeout_IsPerBundleNotAnOverallStartupDeadline()
    {
        await using var server = new FakeIsm7Server(RespondWithZeros,
            pullResponseDelay: TimeSpan.FromMilliseconds(300));
        using var console = new ConsoleCapture();
        await using var session = new StartupSession(server, "pull-timeout.json");
        session.Client.StartupTimeout = 1;

        session.Start();
        // Six responses take at least 1.8 seconds overall, but each remains below one second.
        await session.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.StopAsync();

        Assert.Equal(6, server.PullRequests.Count);
        Assert.DoesNotContain("TimeoutException", console.Output);
    }

    [Fact]
    public async Task InvalidLoginState_LogsErrorAndReturnsWithoutStartingUp()
    {
        await using var server = new FakeIsm7Server(RespondWithZeros, loginState: LoginState.busy);
        using var console = new ConsoleCapture();
        await using var session = new StartupSession(server, "happy-path.json");

        session.Start();
        await session.RunTask!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("invalid login state", console.Output);
        Assert.Empty(server.PullRequests);
        Assert.False(session.Initialized.Task.IsCompleted);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public async Task DroppedPullResponse_LogsTimeoutAndNeverFinishesStartup(int droppedOrdinal)
    {
        await using var server = new FakeIsm7Server(RespondWithZeros, dropPullBundleOrdinal: droppedOrdinal);
        using var console = new ConsoleCapture();
        var publishedBundleCount = 0;
        await using var session = new StartupSession(server, "pull-timeout.json", (_, _) =>
        {
            publishedBundleCount++;
            return Task.CompletedTask;
        });
        session.Client.StartupTimeout = 1;

        session.Start();
        await session.RunTask!.WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Contains("TimeoutException", console.Output);
        Assert.Contains("Shutdown ism7mqtt due to timeout during startup.", console.Output);
        Assert.Equal(droppedOrdinal - 1, publishedBundleCount);
        Assert.Equal(droppedOrdinal, server.PullRequests.Count);
        Assert.Empty(server.PushRequests);
        Assert.False(session.Initialized.Task.IsCompleted);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task DroppedSerializedPushResponse_LogsTimeoutAndNeverFinishesStartup(int droppedOrdinal)
    {
        await using var server = new FakeIsm7Server(RespondWithZeros, dropPushBundleOrdinal: droppedOrdinal);
        using var console = new ConsoleCapture();
        await using var session = new StartupSession(server, "chunked-pull.json");
        session.Client.StartupTimeout = 1;
        session.Client.SerializePushSubscribes = true;

        session.Start();
        await session.RunTask!.WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Contains("TimeoutException", console.Output);
        Assert.Contains("Shutdown ism7mqtt due to timeout during startup.", console.Output);
        Assert.Equal(3, server.PullRequests.Count);
        Assert.Equal(droppedOrdinal, server.PushRequests.Count);
        Assert.False(session.Initialized.Task.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellation_StopsStalledStartupPromptlyWithoutReportingTimeout(bool duringPush)
    {
        await using var server = new FakeIsm7Server(RespondWithZeros,
            dropPullBundleOrdinal: duringPush ? null : 1,
            dropPushBundleOrdinal: duringPush ? 1 : null);
        using var console = new ConsoleCapture();
        await using var session = new StartupSession(server, "happy-path.json");
        session.Client.SerializePushSubscribes = duringPush;

        session.Start();
        if (duringPush)
            await server.WaitForPushAsync(1);
        else
            await server.WaitForPullAsync(1);
        await session.StopAsync();

        Assert.True(session.Cancellation.IsCancellationRequested);
        Assert.True(session.RunTask!.IsCompleted);
        Assert.DoesNotContain("TimeoutException", console.Output);
        Assert.DoesNotContain("timeout during startup", console.Output);
        Assert.False(session.Initialized.Task.IsCompleted);
    }

    [Fact]
    public async Task AlreadyCanceledCallerToken_ThrowsCancellationNotConnectionTimeout()
    {
        await using var server = new FakeIsm7Server(RespondWithZeros);
        await using var session = new StartupSession(server, "happy-path.json");
        session.Cancellation.Cancel();

        session.Start();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RunTask!);
        Assert.False(session.Initialized.Task.IsCompleted);
    }

    private sealed class StartupSession : IAsyncDisposable
    {
        private readonly FakeIsm7Server _server;
        private readonly string _parameterPath;

        public StartupSession(FakeIsm7Server server, string fixture,
            Func<Ism7Config, CancellationToken, Task>? messageHandler = null)
        {
            _server = server;
            _parameterPath = TestFixtures.WriteParameterFile(fixture, server.Port);
            Client = new Ism7Client(messageHandler ?? ((_, _) => Task.CompletedTask),
                _parameterPath, "127.0.0.1", new Ism7Localizer("DEU"))
            {
                OnInitializationFinishedAsync = (_, _) =>
                {
                    Initialized.TrySetResult(true);
                    return Task.CompletedTask;
                }
            };
        }

        public Ism7Client Client { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<bool> Initialized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? RunTask { get; private set; }

        public void Start() => RunTask = Client.RunAsync("test-password", Cancellation.Token);

        public async Task StopAsync()
        {
            Cancellation.Cancel();
            if (RunTask is null) return;
            try
            {
                await RunTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException) when (Cancellation.IsCancellationRequested)
            {
                // RunAsync propagates connection cancellation, but logs cancellation after connection.
            }
            if (!RunTask.IsCanceled)
                await _server.ConnectionClosed.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopAsync();
            }
            finally
            {
                Cancellation.Dispose();
                File.Delete(_parameterPath);
            }
        }
    }

    private sealed class ConsoleCapture : IDisposable
    {
        private readonly TextWriter _original = Console.Out;
        private readonly StringWriter _writer = new();

        public ConsoleCapture() => Console.SetOut(TextWriter.Synchronized(_writer));
        public string Output => _writer.ToString();
        public void Dispose() => Console.SetOut(_original);
    }
}
