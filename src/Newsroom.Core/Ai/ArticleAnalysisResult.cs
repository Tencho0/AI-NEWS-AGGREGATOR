namespace Newsroom.Core.Ai;

/// <summary>The AI verdict for one article (row in nw_ArticleAnalysis).</summary>
/// <param name="Summary">2-3 sentences, Bulgarian.</param>
/// <param name="Category">One of the configured site categories (Ai:Categories).</param>
/// <param name="RegionScore">0..1 relevance to Southwest Bulgaria / Blagoevgrad.</param>
/// <param name="Entities">Up to 8 names of people, organisations or places.</param>
/// <param name="Language">ISO 639-1 code of the source article.</param>
/// <param name="Relevant">false for sports score tables, ads, horoscopes and other non-news.</param>
public sealed record ArticleAnalysisResult(
    long ArticleId,
    string Summary,
    string Category,
    double RegionScore,
    IReadOnlyList<string> Entities,
    string Language,
    bool Relevant);
