using System.Text.Json;
using System.Text.Json.Serialization;
using RubyDownloader.Config;
using RubyDownloader.Services;

EnvLoader.Load();

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz ";
    options.IncludeScopes = true;
});
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

builder.Services.AddSingleton(AppSettings.Load());
builder.Services.AddSingleton<DownloadProcessor>();
builder.Services.AddSingleton(provider => new DownloadJobQueue(
    provider.GetRequiredService<DownloadProcessor>(),
    provider.GetRequiredService<AppSettings>(),
    provider.GetRequiredService<ILogger<DownloadJobQueue>>()));
builder.Services.AddHostedService(provider => provider.GetRequiredService<DownloadJobQueue>());
builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    });

WebApplication app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapControllers();

await app.RunAsync();
