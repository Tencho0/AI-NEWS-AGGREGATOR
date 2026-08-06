using System.Net;
using Newsroom.Core.Ai;
using Newsroom.Core.Drafting;
using Newsroom.Core.Images;
using Newsroom.Core.Operations;
using Newsroom.Core.Publishing;
using Newsroom.Core.Review;
using Newsroom.Core.Scraping;
using Newsroom.Core.Trends;
using Newsroom.Infrastructure.Ai;
using Newsroom.Infrastructure.Database;
using Newsroom.Infrastructure.Images;
using Newsroom.Infrastructure.Operations;
using Newsroom.Infrastructure.Publishing;
using Newsroom.Infrastructure.Repositories;
using Newsroom.Infrastructure.Review;
using Newsroom.Infrastructure.Scraping;
using Newsroom.Worker.Jobs;
using Serilog;

// Bootstrap logger so startup failures (before configuration is read) are still captured.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    // Sandbox mode (ADR-0014). Host.CreateApplicationBuilder only auto-loads dotnet user-secrets
    // in the Development environment, so under Sandbox the LIVE store is never read; the
    // sandbox's own store is added explicitly and, being appended last, wins over every other
    // provider already registered — the JSON files, environment variables and the command line
    // alike — which inverts the framework's usual env-vars/command-line-override-config ordering.
    if (builder.Environment.IsEnvironment(SandboxOptions.EnvironmentName))
        builder.Configuration.AddUserSecrets(SandboxOptions.UserSecretsId);

    var sandbox = SandboxOptions.From(builder.Configuration);

    builder.Services.AddWindowsService(options => options.ServiceName = "PredelNewsroom");

    builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext());

    var connectionString = builder.Configuration.GetConnectionString("Newsroom")
        ?? throw new InvalidOperationException("ConnectionStrings:Newsroom is not configured.");

    // Fail closed: a sandbox still pointing at a live destination must not reach a job. All
    // violations are reported at once so the config is fixed in one pass.
    if (sandbox.Enabled)
    {
        var violations = SandboxOptions.Violations(
            connectionString,
            builder.Configuration.GetValue("Umbraco:BaseUrl", "")!,
            ImageStorageOptions.From(builder.Configuration).Root);
        if (violations.Count > 0)
            throw new InvalidOperationException(
                $"Sandbox mode refused to start:{Environment.NewLine}  - "
                + string.Join($"{Environment.NewLine}  - ", violations));
    }

    var connectionFactory = new SqlConnectionFactory(connectionString);
    builder.Services.AddSingleton(connectionFactory);
    builder.Services.AddSingleton<IDbConnectionFactory>(connectionFactory);
    builder.Services.AddSingleton<MigrationRunner>();

    // Scraping HTTP: honest user agent, decompression, bounded lifetime, standard
    // Polly-based resilience (retry + circuit breaker + timeouts).
    var userAgent = builder.Configuration.GetValue(
        "Scrape:UserAgent", "PredelNewsBot/1.0 (+https://predel.news/bot)");
    void ConfigureScrapeClient(HttpClient client)
    {
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.MaxResponseContentBufferSize = HtmlTextExtractor.MaxDownloadBytes;
    }
    HttpClientHandler CreateScrapeHandler() => new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        MaxAutomaticRedirections = 5,
    };

    builder.Services.AddHttpClient<IFeedReader, RssFeedReader>(ConfigureScrapeClient)
        .ConfigurePrimaryHttpMessageHandler(CreateScrapeHandler)
        .AddStandardResilienceHandler();
    builder.Services.AddHttpClient<IArticleTextExtractor, HtmlTextExtractor>(ConfigureScrapeClient)
        .ConfigurePrimaryHttpMessageHandler(CreateScrapeHandler)
        .AddStandardResilienceHandler();
    builder.Services.AddHttpClient(RobotsPolicy.HttpClientName, ConfigureScrapeClient)
        .ConfigurePrimaryHttpMessageHandler(CreateScrapeHandler)
        .AddStandardResilienceHandler();

    builder.Services.AddSingleton<IRobotsPolicy, RobotsPolicy>(); // holds the per-host cache
    builder.Services.AddSingleton<ISourceRepository, SourceRepository>();
    builder.Services.AddSingleton<ISourceArticleRepository, SourceArticleRepository>();

    // AI analysis (ADR-0010). The Gemini clients are Lazy so a missing API key degrades to a
    // skipped stage (the jobs guard on key presence) instead of failing host startup. One
    // AiRateLimiter is shared by all stages: the free-tier RPM cap is per key, not per stage.
    builder.Services.AddSingleton(_ => AiRateLimiter.From(builder.Configuration));
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<GeminiModelFallback>(); // daily-quota fallback state, per model
    builder.Services.AddSingleton<IAiBudget, AiBudget>();
    builder.Services.AddSingleton<IAnalysisRepository, AnalysisRepository>();
    builder.Services.AddSingleton(provider => new Lazy<IAiClient>(() => new GeminiAiClient(
        GeminiChatClientFactory.Create(builder.Configuration),
        GeminiAiOptions.From(builder.Configuration),
        provider.GetRequiredService<AiRateLimiter>(),
        provider.GetRequiredService<ILogger<GeminiAiClient>>())));

    // Trend detection (docs/02-functional-spec.md §3): clustering + scoring over nw_Topic.
    builder.Services.AddSingleton<ITopicRepository, TopicRepository>();
    builder.Services.AddSingleton(provider => new Lazy<IClusteringAi>(() => new GeminiClusteringAi(
        GeminiChatClientFactory.CreateWithDailyQuotaFallback(builder.Configuration, "Cluster",
            provider.GetRequiredService<GeminiModelFallback>()),
        GeminiClusteringOptions.From(builder.Configuration),
        provider.GetRequiredService<AiRateLimiter>(),
        provider.GetRequiredService<ILogger<GeminiClusteringAi>>())));

    // Draft generation (docs/02-functional-spec.md §4): drafting + self-check on the same
    // Lazy pattern, plus stock-image suggestions over a shared resilient HttpClient.
    builder.Services.AddSingleton<IDraftRepository, DraftRepository>();
    builder.Services.AddSingleton(provider => new Lazy<IDraftingAi>(() => new GeminiDraftingAi(
        GeminiChatClientFactory.CreateWithDailyQuotaFallback(builder.Configuration, "Draft",
            provider.GetRequiredService<GeminiModelFallback>()),
        GeminiChatClientFactory.CreateWithDailyQuotaFallback(builder.Configuration, "SelfCheck",
            provider.GetRequiredService<GeminiModelFallback>()),
        GeminiDraftingOptions.From(builder.Configuration),
        provider.GetRequiredService<AiRateLimiter>(),
        provider.GetRequiredService<PublicFigureDirectory>())));

    builder.Services.AddHttpClient(ImageSuggestionService.HttpClientName,
            client => client.Timeout = TimeSpan.FromSeconds(15))
        .AddStandardResilienceHandler();
    builder.Services.AddSingleton(ImagesOptions.From(builder.Configuration));
    // Persistent image storage (ADR-0013): every local image file lives under Images:StorageRoot,
    // outside this deployment directory, and nw_DraftImage.Url holds a relative key into it.
    builder.Services.AddSingleton(ImageStorageOptions.From(builder.Configuration));
    builder.Services.AddSingleton(provider =>
        provider.GetRequiredService<ImageStorageOptions>().CreateStorage());
    builder.Services.AddSingleton<ImageCompositor>();
    builder.Services.AddSingleton<IImageProvider, PixabayImageProvider>();
    builder.Services.AddSingleton<IImageProvider, PexelsImageProvider>();
    builder.Services.AddSingleton<ImageSuggestionService>();

    // Cover-image generation (ADR-0011, ADR-0012): FLUX.2 klein 4B on Cloudflare Workers AI,
    // tried before the stock providers. Its client gets a generation-friendly timeout and NO
    // resilience handler — a failed generation must fall back to stock, not burn quota on
    // retries. FeaturedImageService is a singleton because it remembers, for the rest of the UTC
    // day, that the free allocation ran out.
    builder.Services.AddHttpClient(CloudflareFluxImageGenerator.HttpClientName,
        client => client.Timeout = TimeSpan.FromSeconds(60));
    builder.Services.AddSingleton(CloudflareImagesOptions.From(builder.Configuration));
    builder.Services.AddSingleton(provider =>
        new PublicFigureDirectory(provider.GetRequiredService<CloudflareImagesOptions>().PublicFigures));
    builder.Services.AddSingleton<IAiImageGenerator, CloudflareFluxImageGenerator>();
    builder.Services.AddSingleton<IOperatorAlerts, TelegramOperatorAlerts>();
    builder.Services.AddSingleton<FeaturedImageService>();

    // Telegram editorial review (docs/02-functional-spec.md §5, ADR-0006: long polling). The
    // gateway is Lazy on the AI-client pattern: TelegramJob guards on configuration, so a
    // missing bot token degrades to a dormant review stage instead of failing host startup.
    builder.Services.AddSingleton<IReviewRepository, ReviewRepository>();
    builder.Services.AddSingleton(_ => new Lazy<ITelegramGateway>(() =>
    {
        var draftingOptions = GeminiDraftingOptions.From(builder.Configuration);
        // Offer a target button only when PublishJob would actually honour it, so the keyboard
        // can never promise a destination that publishes nowhere. Mirrors the publishing wiring
        // below: the sandbox forces FacebookOnly off (ADR-0014), so the site button is always
        // live there. ✅ and 📅 keep meaning "publish everywhere possible" in every case.
        var websiteEnabled = sandbox.Enabled
            || !PublishingOptions.From(builder.Configuration).FacebookOnly;
        var facebookEnabled = FacebookOptions.From(builder.Configuration).IsConfigured;
        ITelegramGateway gateway = new TelegramGateway(
            TelegramOptions.From(builder.Configuration).BotToken
                ?? throw new InvalidOperationException("Telegram:BotToken is not configured."),
            draftingOptions.Categories,
            draftingOptions.Regions,
            websiteEnabled,
            facebookEnabled);
        // Wrapping the gateway (not the renderer) also marks watchdog alerts and the daily digest.
        return sandbox.Enabled ? new SandboxTelegramGateway(gateway) : gateway;
    }));

    // Publishing (docs/02-functional-spec.md §6, ADR-0007/0008): Approved drafts go to the
    // Umbraco site's publishing endpoint, then a link post to the Facebook page via the Graph
    // API — both over resilient typed clients. PublishJob guards on configuration, so a
    // missing BaseUrl/secret degrades to a dormant publishing stage and a missing Facebook
    // page/token to a site-only one (Facebook:DryRun additionally defaults ON).
    builder.Services.AddSingleton(UmbracoOptions.From(builder.Configuration));

    // Sandbox overrides configuration rather than trusting it (ADR-0014): DryRun on means
    // FacebookPublisher takes its existing dry-run branch and never calls the Graph API, whatever
    // a stray token in configuration says; FacebookOnly off is required because it would
    // otherwise skip the Umbraco leg entirely — and publishing to the local site is the point.
    var facebookOptions = FacebookOptions.From(builder.Configuration);
    var publishingOptions = PublishingOptions.From(builder.Configuration);
    if (sandbox.Enabled)
    {
        facebookOptions = facebookOptions with { DryRun = true };
        publishingOptions = publishingOptions with { FacebookOnly = false };
    }
    builder.Services.AddSingleton(facebookOptions);
    builder.Services.AddSingleton(publishingOptions);
    builder.Services.AddSingleton<IPublishRepository, PublishRepository>();
    builder.Services.AddHttpClient<IUmbracoPublisher, UmbracoPublisher>(
            client => client.Timeout = TimeSpan.FromSeconds(30))
        .AddStandardResilienceHandler();
    builder.Services.AddHttpClient<IFacebookPublisher, FacebookPublisher>(
            client => client.Timeout = TimeSpan.FromSeconds(30))
        .AddStandardResilienceHandler();

    // Operations (docs/07-operations.md): per-job heartbeats + watchdog alerts, the daily
    // digest, data retention and the startup crash-recovery sweep. IJobHeartbeat is injected
    // into every job; the ops jobs share one repository over nw_Config + aggregate queries.
    builder.Services.AddSingleton<IJobHeartbeat, JobHeartbeat>();
    builder.Services.AddSingleton<IOperationsRepository, OperationsRepository>();

    // Order matters: migrations must complete before any job starts, and crash recovery must
    // reset stuck drafts before the jobs that would otherwise skip or re-pick them.
    builder.Services.AddHostedService<MigrationStartupService>();
    builder.Services.AddHostedService<StartupRecoveryService>();
    builder.Services.AddHostedService<HeartbeatService>();
    builder.Services.AddHostedService<ScrapeJob>();
    builder.Services.AddHostedService<AnalyseJob>();
    builder.Services.AddHostedService<TrendJob>();
    builder.Services.AddHostedService<DraftJob>();
    builder.Services.AddHostedService<TelegramJob>();
    builder.Services.AddHostedService<PublishJob>();
    builder.Services.AddHostedService<FacebookTestPostService>();
    builder.Services.AddHostedService<WatchdogJob>();
    builder.Services.AddHostedService<DailyDigestJob>();
    builder.Services.AddHostedService<RetentionJob>();

    var host = builder.Build();

    if (sandbox.Enabled)
    {
        // Warning level and first in the log: the fastest way to notice a sandbox pointed
        // somewhere unintended. Every destination is named explicitly.
        host.Services.GetRequiredService<ILogger<Program>>().LogWarning(
            "🧪 SANDBOX MODE — database {Database}, Telegram chat {ChatId}, site {Site}, "
            + "Facebook dry-run (forced), images {ImageRoot}",
            SandboxOptions.DatabaseName(connectionString) ?? "(unparseable)",
            TelegramOptions.From(builder.Configuration).ReviewChatId,
            builder.Configuration.GetValue("Umbraco:BaseUrl", ""),
            ImageStorageOptions.From(builder.Configuration).Root);
    }

    host.Run();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Worker terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}
