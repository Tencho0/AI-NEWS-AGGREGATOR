namespace Newsroom.Core.Publishing;

/// <summary>
/// The publishing endpoint refused the article (HTTP 400 problem details) — a permanent
/// failure: the same payload can never succeed, so it must not be retried, and the
/// human-readable reason goes straight to the editor's failure alert. Transient
/// transport/server failures stay ordinary exceptions and do retry.
/// </summary>
public sealed class PublishRejectedException(string reason) : Exception(reason);
