namespace Daggeragent.Persistence;

public sealed class JobRecord
{
    public string Id { get; set; } = "";
    public string? ParentId { get; set; }
    public string Status { get; set; } = "";
    public string Model { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string StateJson { get; set; } = "";
}
