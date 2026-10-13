using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using ism7mqtt.HomeAssistant;
using ism7mqtt.ISM7.Protocol;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Xunit;

namespace ism7mqtt.Tests;

public class LocalBehaviorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task InvalidStartupTimeout_IsRejectedBeforeConnecting(int timeout)
    {
        var parameterPath = TestFixtures.WriteParameterFile("happy-path.json", 0);
        try
        {
            var client = new Ism7Client((_, _) => Task.CompletedTask,
                parameterPath, "127.0.0.1", new Ism7Localizer("DEU"))
            {
                StartupTimeout = timeout
            };

            var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => client.RunAsync("test-password", CancellationToken.None));
            Assert.Equal(nameof(Ism7Client.StartupTimeout), exception.ParamName);
        }
        finally
        {
            File.Delete(parameterPath);
        }
    }

    [Theory]
    [InlineData("0xD0", "0x07", 2000, "Geschlossen")]
    [InlineData("0x40", "0xFE", 65088, "Geöffnet")]
    public void E1Contact_UsesUnsignedContactValuesWithoutTemperatureMetadata(
        string low, string high, int rawValue, string label)
    {
        var parameterPath = TestFixtures.WriteParameterFile("e1-contact.json", 0);
        try
        {
            var localizer = new Ism7Localizer("DEU");
            var config = new Ism7Config(parameterPath, localizer);
            config.AddAllDevices("127.0.0.1").ToArray();
            var (_, reads) = Assert.Single(config.GetBundlesForDevice("0x25"));
            Assert.Equal(10006, Assert.Single(reads).InfoNumber);

            Assert.True(config.ProcessData(new[]
            {
                new InfonumberReadResp
                {
                    BusAddress = "0x25",
                    InfoNumber = 10006,
                    State = TelegrResponseState.OK,
                    DBLow = low,
                    DBHigh = high,
                }
            }));
            var message = Assert.Single(config.JsonMessages);
            var contact = message.Content["Eingang E1"]!;
            Assert.Equal(rawValue.ToString(), contact["value"]!.ToJsonString());
            Assert.Equal(label, contact["text"]!.GetValue<string>());

            var discovery = new HaDiscovery(config, null!, "Wolf", localizer);
            var entity = Assert.Single(discovery.GetDiscoveryInfo());
            Assert.StartsWith("homeassistant/sensor/", entity.Path);
            Assert.Equal("enum", entity.Content["device_class"]!.GetValue<string>());
            Assert.Equal(new[] { "Geöffnet", "Geschlossen" },
                entity.Content["options"]!.AsArray().Select(option => option!.GetValue<string>()));
            Assert.False(entity.Content.ContainsKey("unit_of_measurement"));
            Assert.False(entity.Content.ContainsKey("command_topic"));
        }
        finally
        {
            File.Delete(parameterPath);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Wolf/127.0.0.1/status")]
    public void DeviceDiscovery_PreservesConfiguredAvailability(string? stateTopic)
    {
        var parameterPath = TestFixtures.WriteParameterFile("happy-path.json", 0);
        try
        {
            var localizer = new Ism7Localizer("DEU");
            var config = new Ism7Config(parameterPath, localizer);
            config.AddAllDevices("127.0.0.1").ToArray();
            var discovery = new HaDiscovery(config, null!, "Wolf", localizer)
            {
                BridgeStateTopic = stateTopic
            };

            var entities = discovery.GetDiscoveryInfo().ToArray();
            Assert.Equal(5, entities.Length);
            Assert.DoesNotContain(entities, entity => entity.Content["name"]!.GetValue<string>() == "Reglertyp");
            Assert.All(entities, entity =>
            {
                if (stateTopic is null)
                {
                    Assert.False(entity.Content.ContainsKey("availability_topic"));
                    Assert.False(entity.Content.ContainsKey("payload_available"));
                    Assert.False(entity.Content.ContainsKey("payload_not_available"));
                }
                else
                {
                    Assert.Equal(stateTopic, entity.Content["availability_topic"]!.GetValue<string>());
                    Assert.Equal("online", entity.Content["payload_available"]!.GetValue<string>());
                    Assert.Equal("offline", entity.Content["payload_not_available"]!.GetValue<string>());
                }
            });
        }
        finally
        {
            File.Delete(parameterPath);
        }
    }

    [Fact]
    public async Task PublishDiscovery_RepublishesBridgeConnectivityAlongsideDevices()
    {
        var parameterPath = TestFixtures.WriteParameterFile("happy-path.json", 0);
        try
        {
            var localizer = new Ism7Localizer("DEU");
            var config = new Ism7Config(parameterPath, localizer);
            config.AddAllDevices("127.0.0.1").ToArray();
            var mqttClient = DispatchProxy.Create<IMqttClient, RecordingMqttClient>();
            var recording = (RecordingMqttClient)mqttClient;
            var discovery = new HaDiscovery(config, mqttClient, "Wolf", localizer)
            {
                Ip = "127.0.0.1",
                BridgeStateTopic = "Wolf/127.0.0.1/status",
                QosLevel = MqttQualityOfServiceLevel.AtLeastOnce,
            };

            for (var publication = 0; publication < 2; publication++)
            {
                recording.Messages.Clear();
                await discovery.PublishDiscoveryInfo(CancellationToken.None);

                Assert.Equal(6, recording.Messages.Count);
                var bridge = Assert.Single(recording.Messages,
                    message => message.Topic == "homeassistant/binary_sensor/Wolf_bridge_connectivity/config");
                var payload = JsonNode.Parse(Encoding.UTF8.GetString(
                    bridge.PayloadSegment.Array!, bridge.PayloadSegment.Offset, bridge.PayloadSegment.Count))!;
                Assert.Equal("Wolf/127.0.0.1/status", payload["state_topic"]!.GetValue<string>());
                Assert.Equal("online", payload["payload_on"]!.GetValue<string>());
                Assert.Equal("offline", payload["payload_off"]!.GetValue<string>());
                Assert.Equal("connectivity", payload["device_class"]!.GetValue<string>());
                Assert.Equal("diagnostic", payload["entity_category"]!.GetValue<string>());
                Assert.Equal("http://127.0.0.1/", payload["device"]!["configuration_url"]!.GetValue<string>());
                Assert.All(recording.Messages,
                    message => Assert.Equal(MqttQualityOfServiceLevel.AtLeastOnce, message.QualityOfServiceLevel));
            }
        }
        finally
        {
            File.Delete(parameterPath);
        }
    }

    public class RecordingMqttClient : DispatchProxy
    {
        public List<MqttApplicationMessage> Messages { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IMqttClient.PublishAsync))
            {
                Messages.Add((MqttApplicationMessage)args![0]!);
                return Task.FromResult<MqttClientPublishResult>(null!);
            }
            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
