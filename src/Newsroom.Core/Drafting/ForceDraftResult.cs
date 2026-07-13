namespace Newsroom.Core.Drafting;

/// <summary>Outcome of an editor's /draft &lt;topicId&gt; request (docs/05-integrations/telegram.md).</summary>
public enum ForceDraftResult
{
    /// <summary>No topic with that id.</summary>
    TopicNotFound,

    /// <summary>The topic has fallen out of the window (Done); v1 refuses forcing it.</summary>
    TopicDone,

    /// <summary>A draft for this topic already exists in a non-inactive status.</summary>
    AlreadyActive,

    /// <summary>ForceDraftAtUtc set (and DraftAttempts reset); DraftJob will pick it up.</summary>
    Queued,
}
