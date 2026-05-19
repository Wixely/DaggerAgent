namespace Daggeragent.Configuration;

public sealed class JobsOptions
{
    public const string SectionName = "Jobs";

    public string Provider { get; set; } = "Sqlite";
    public string ConnectionString { get; set; } = "Data Source=data/jobs.db";
}
