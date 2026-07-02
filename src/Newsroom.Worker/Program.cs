using Newsroom.Infrastructure.Database;
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

    // Order matters: migrations must complete before any job starts.
    builder.Services.AddHostedService<MigrationStartupService>();
    builder.Services.AddHostedService<HeartbeatService>();

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
