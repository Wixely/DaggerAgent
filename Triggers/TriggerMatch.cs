using System.Text.Json.Serialization;

namespace Daggeragent.Triggers;

/// <summary>
/// One row of <c>matches[]</c> returned by an MCP server's list_mentions_since tool.
/// All three providers (GitHub, GitLab, AzureDevOps) populate the common fields
/// (<see cref="Kind"/>, <see cref="Author"/>, <see cref="Body"/>, <see cref="Url"/>,
/// <see cref="UpdatedAt"/>); provider-specific identifiers live on the nullable fields.
/// </summary>
public sealed class TriggerMatch
{
    [JsonPropertyName("kind")]       public string Kind { get; set; } = "";
    [JsonPropertyName("author")]     public string Author { get; set; } = "";
    [JsonPropertyName("body")]       public string Body { get; set; } = "";
    [JsonPropertyName("url")]        public string Url { get; set; } = "";
    [JsonPropertyName("createdAt")]  public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")]  public DateTimeOffset UpdatedAt { get; set; }

    // GitHub
    [JsonPropertyName("repo")]       public string? Repo { get; set; }
    [JsonPropertyName("number")]     public int? Number { get; set; }

    /// <summary>
    /// Provider-specific id of the comment / note / discussion that contained the mention.
    /// Shared between GitHub (issue comment id) and AzureDevOps (work-item comment id);
    /// disambiguated by which natural-id field is set (<see cref="Number"/> vs <see cref="Id"/>).
    /// When the MCP server populates this, dedup is scoped to the specific mention so an
    /// unrelated edit on the same item won't re-fire — see <see cref="MatchKey"/>.
    /// </summary>
    [JsonPropertyName("commentId")]  public long? CommentId { get; set; }

    // GitLab
    [JsonPropertyName("project")]    public string? Project { get; set; }
    [JsonPropertyName("iid")]        public int? Iid { get; set; }
    [JsonPropertyName("noteId")]     public long? NoteId { get; set; }

    // AzureDevOps
    [JsonPropertyName("id")]            public int? Id { get; set; }
    [JsonPropertyName("workItemType")]  public string? WorkItemType { get; set; }
    [JsonPropertyName("title")]         public string? Title { get; set; }

    /// <summary>
    /// Stable per-source key for dedup. Pattern: <c>{providerPrefix}-{naturalId}[-{commentId}]</c> —
    /// deliberately NOT time-stamped. This is a last-seen-id high-water mark: an item-level
    /// mention (in the ticket body/description) fires exactly once per item, and each distinct
    /// comment/note carrying the phrase fires exactly once (keyed by its own id). Provider is
    /// identified by which natural-id field is populated — Number (GitHub), Iid (GitLab),
    /// Id (AzureDevOps).
    ///
    /// The earlier design appended <c>@{UpdatedAt:O}</c> so any timestamp bump re-fired. That
    /// self-triggers: a job whose preamble says "update the ticket on start/end" bumps the item's
    /// changed-date, which re-matched the still-present body mention and spawned a duplicate job
    /// every poll — a runaway loop. An author-based "ignore self" guard can't fix it here either,
    /// because the agent writes back through a PAT/ambient identity indistinguishable from the
    /// human's. Tracking the comment id instead means the agent's own edits (which add no new
    /// phrase-bearing comment) never re-fire, while a genuine follow-up "@phrase" comment — the
    /// intended way to reopen work on a long-lived ticket — always does.
    /// </summary>
    public string MatchKey()
    {
        if (Number is not null)
            return CommentId is not null ? $"gh-comment-{Number}-{CommentId}" : $"gh-{Kind}-{Number}";
        if (Iid is not null)
            return NoteId is not null ? $"gl-note-{Iid}-{NoteId}" : $"gl-{Kind}-{Iid}";
        if (Id is not null)
            return CommentId is not null ? $"az-comment-{Id}-{CommentId}" : $"az-{Kind}-{Id}";
        return Url;
    }

    /// <summary> Short human description for logs / job preambles. </summary>
    public string ShortRef()
    {
        if (Number is not null) return $"{Repo} #{Number}{(CommentId is not null ? $" comment {CommentId}" : "")}";
        if (Iid is not null) return $"{Project} !{Iid}{(NoteId is not null ? $" note {NoteId}" : "")}";
        if (Id is not null) return $"{Project} #{Id}{(CommentId is not null ? $" comment {CommentId}" : "")}";
        return Url;
    }
}

public sealed class TriggerMatchEnvelope
{
    [JsonPropertyName("matches")]  public List<TriggerMatch> Matches { get; set; } = new();
    [JsonPropertyName("polledAt")] public DateTimeOffset PolledAt { get; set; }
    [JsonPropertyName("since")]    public DateTimeOffset? Since { get; set; }
    [JsonPropertyName("mention")]  public string Mention { get; set; } = "";
    [JsonPropertyName("error")]    public string? Error { get; set; }
}
