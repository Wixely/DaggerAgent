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
    /// <summary>
    /// True when the job's <see cref="Agent.ConversationState.Interrupted"/> flag is set —
    /// projected directly from state_json by the SQLite store so listing doesn't have to
    /// deserialise every row. Surfaced to the UI so it can show a "Resume" affordance.
    /// </summary>
    public bool Interrupted { get; set; }
    /// <summary>
    /// Trigger source id stamped onto trigger-originated jobs, projected from state_json.
    /// Null for user-initiated jobs.
    /// </summary>
    public string? TriggerSourceId { get; set; }
}
