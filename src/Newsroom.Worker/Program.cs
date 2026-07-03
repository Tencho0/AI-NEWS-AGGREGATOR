using System.Net;
using Newsroom.Core.Ai;
using Newsroom.Core.Scraping;
using Newsroom.Core.Trends;
using Newsroom.Infrastructure.Ai;
using Newsroom.Infrastructure.Database;
using Newsroom.Infrastructure.Repositories;
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

    // Order matters: migrations must complete before any job starts.
    builder.Services.AddHostedService<MigrationStartupService>();
    builder.Services.AddHostedService<HeartbeatService>();
    builder.Services.AddHostedService<ScrapeJob>();
    builder.Services.AddHostedService<AnalyseJob>();
    builder.Services.AddHostedService<TrendJob>();

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
