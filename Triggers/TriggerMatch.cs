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
    /// Stable per-source key for dedup. Pattern: <c>{providerPrefix}-{naturalId}[-{commentId}]@{UpdatedAt:O}</c>.
    /// Provider is identified by which natural-id field is populated — Number (GitHub),
    /// Iid (GitLab), Id (AzureDevOps). When a comment id is present the key includes it,
    /// scoping dedup to the specific mention; otherwise the key is item-level so any
    /// timestamp bump on the parent item re-fires (intentional for edited-then-re-mentioned
    /// tickets, harmless for first-time matches).
    /// </summary>
    public string MatchKey()
    {
        string natural;
        if (Number is not null)
            natural = CommentId is not null ? $"gh-comment-{Number}-{CommentId}" : $"gh-{Kind}-{Number}";
        else if (Iid is not null)
            natural = NoteId is not null ? $"gl-note-{Iid}-{NoteId}" : $"gl-{Kind}-{Iid}";
        else if (Id is not null)
            natural = CommentId is not null ? $"az-comment-{Id}-{CommentId}" : $"az-{Kind}-{Id}";
        else
            natural = Url;
        return $"{natural}@{UpdatedAt:O}";
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
