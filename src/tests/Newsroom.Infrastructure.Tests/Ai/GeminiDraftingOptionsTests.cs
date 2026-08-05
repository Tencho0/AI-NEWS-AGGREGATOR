using Newsroom.Infrastructure.Ai;

namespace Newsroom.Infrastructure.Tests.Ai;

public class GeminiDraftingOptionsTests
{
    [Fact]
    public void Default_regions_are_the_sites_five_provinces_not_the_old_municipality_list()
    {
        // Pre-2026-08-04 this was the fourteen municipalities of Blagoevgrad Province — wrong,
        // the site's actual region taxonomy is five provinces (tools/2026-08-04-repair-stale-taxonomy.sql).
        Assert.Equal(
            ["Благоевград", "Кюстендил", "Перник", "София", "България"],
            GeminiDraftingOptions.DefaultRegions);
    }

    [Fact]
    public void Default_categories_delegate_to_GeminiAiOptions()
    {
        Assert.Equal(GeminiAiOptions.DefaultCategories, GeminiDraftingOptions.From(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).Categories);
    }
}
