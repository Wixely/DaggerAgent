namespace Daggeragent.Configuration;

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 5090;
    public string Path { get; set; } = "/agent";
}
