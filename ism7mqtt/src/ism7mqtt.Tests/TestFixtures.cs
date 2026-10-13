// Adapted from zivillian/ism7mqtt commit 6a0aeca339 (GPL-3.0).
namespace ism7mqtt.Tests;

internal static class TestFixtures
{
    /// <summary>
    /// Materializes a parameter.json fixture (see Fixtures/*.json) to a file in the test output with the
    /// fake server's ephemeral TcpPort substituted in, since <see cref="Ism7Client"/> takes a file path.
    /// </summary>
    public static string WriteParameterFile(string fixtureName, int tcpPort)
    {
        var templatePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);
        var json = File.ReadAllText(templatePath).Replace("{{TCP_PORT}}", tcpPort.ToString());
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", $"parameters-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }
}
