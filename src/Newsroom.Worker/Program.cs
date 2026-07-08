using System.Net;
using Newsroom.Core.Ai;
using Newsroom.Core.Drafting;
using Newsroom.Core.Operations;
using Newsroom.Core.Publishing;
using Newsroom.Core.Review;
using Newsroom.Core.Scraping;
using Newsroom.Core.Trends;
using Newsroom.Infrastructure.Ai;
using Newsroom.Infrastructure.Database;
using Newsroom.Infrastructure.Images;
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

    builder.Services.AddWindowsService(options => options.ServiceName = "PredelNewsroom");

    builder.Services.AddSerilog((services, loggerConfiguration) => loggerConfiguration
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext());

    var connectionString = builder.Configuration.GetConnectionString("Newsroom")
        ?? throw new InvalidOperationException("ConnectionStrings:Newsroom is not configured.");

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
        GeminiChatClientFactory.Create(builder.Configuration, "Cluster"),
        GeminiClusteringOptions.From(builder.Configuration),
        provider.GetRequiredService<AiRateLimiter>(),
        provider.GetRequiredService<ILogger<GeminiClusteringAi>>())));

    // Draft generation (docs/02-functional-spec.md §4): drafting + self-check on the same
    // Lazy pattern, plus stock-image suggestions over a shared resilient HttpClient.
    builder.Services.AddSingleton<IDraftRepository, DraftRepository>();
    builder.Services.AddSingleton(provider => new Lazy<IDraftingAi>(() => new GeminiDraftingAi(
        GeminiChatClientFactory.Create(builder.Configuration, "Draft"),
        GeminiChatClientFactory.Create(builder.Configuration, "SelfCheck"),
        GeminiDraftingOptions.From(builder.Configuration),
        provider.GetRequiredService<AiRateLimiter>())));

    builder.Services.AddHttpClient(ImageSuggestionService.HttpClientName,
            client => client.Timeout = TimeSpan.FromSeconds(15))
        .AddStandardResilienceHandler();
    builder.Services.AddSingleton(ImagesOptions.From(builder.Configuration));
    builder.Services.AddSingleton<IImageProvider, PixabayImageProvider>();
    builder.Services.AddSingleton<IImageProvider, PexelsImageProvider>();
    builder.Services.AddSingleton<ImageSuggestionService>();

    // Telegram editorial review (docs/02-functional-spec.md §5, ADR-0006: long polling). The
    // gateway is Lazy on the AI-client pattern: TelegramJob guards on configuration, so a
    // missing bot token degrades to a dormant review stage instead of failing host startup.
    builder.Services.AddSingleton<IReviewRepository, ReviewRepository>();
    builder.Services.AddSingleton(_ => new Lazy<ITelegramGateway>(() =>
        new TelegramGateway(TelegramOptions.From(builder.Configuration).BotToken
            ?? throw new InvalidOperationException("Telegram:BotToken is not configured."))));

    // Publishing (docs/02-functional-spec.md §6, ADR-0007/0008): Approved drafts go to the
    // Umbraco site's publishing endpoint, then a link post to the Facebook page via the Graph
    // API — both over resilient typed clients. PublishJob guards on configuration, so a
    // missing BaseUrl/secret degrades to a dormant publishing stage and a missing Facebook
    // page/token to a site-only one (Facebook:DryRun additionally defaults ON).
    builder.Services.AddSingleton(UmbracoOptions.From(builder.Configuration));
    builder.Services.AddSingleton(FacebookOptions.From(builder.Configuration));
    builder.Services.AddSingleton(PublishingOptions.From(builder.Configuration));
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
