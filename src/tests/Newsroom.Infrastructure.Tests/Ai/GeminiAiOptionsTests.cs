using Newsroom.Infrastructure.Ai;

namespace Newsroom.Infrastructure.Tests.Ai;

public class GeminiAiOptionsTests
{
    [Fact]
    public void Default_categories_match_the_sites_real_taxonomy()
    {
        // Predel-News src/Web/PredelNews.Web/Setup/TaxonomySeedSetup.cs is the source of truth —
        // mirrored in appsettings.json's Ai:Categories and here as the code-level fallback used
        // when that config key is absent. Both must agree; "Икономика" is NOT a real node, the
        // site's node is "Икономика / Бизнес" (tools/2026-08-04-repair-stale-taxonomy.sql).
        Assert.Equal(
            ["Общество", "Политика", "Криминално", "Икономика / Бизнес", "Спорт", "Култура", "Любопитно", "Хайлайф"],
            GeminiAiOptions.DefaultCategories);
    }
}
