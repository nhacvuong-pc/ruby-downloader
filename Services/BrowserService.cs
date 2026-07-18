using Microsoft.Playwright;
using RubyDownloader.Config;

namespace RubyDownloader.Services;

internal sealed class BrowserService : IAsyncDisposable
{
    private readonly AppSettings _settings;

    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public BrowserService(AppSettings settings)
    {
        _settings = settings;
    }

    public async Task<BrowserSession> CreateSessionAsync()
    {
        _playwright = await Playwright.CreateAsync();

        _browser = await _playwright.Chromium.LaunchAsync(
            new BrowserTypeLaunchOptions
            {
                Headless = _settings.PlaywrightHeadless,
                SlowMo = _settings.PlaywrightSlowMoMs,
                Args =
                [
                    "--disable-blink-features=AutomationControlled",
                    "--autoplay-policy=no-user-gesture-required",
                    "--disable-background-networking",
                    "--disable-component-update",
                    "--disable-default-apps",
                    "--disable-domain-reliability",
                    "--disable-sync",
                    "--metrics-recording-only",
                    "--no-default-browser-check",
                    "--no-first-run",
                    "--disable-dev-shm-usage",
                    "--no-sandbox",
                    "--disable-gpu"
                ]
            });

        IBrowserContext context = await _browser.NewContextAsync(
            new BrowserNewContextOptions
            {
                UserAgent = _settings.UserAgent,
                Locale = "en-US",
                ViewportSize = new ViewportSize
                {
                    Width = 1365,
                    Height = 768
                },
                ExtraHTTPHeaders = new Dictionary<string, string>
                {
                    ["Accept-Language"] = "en-US,en;q=0.9"
                }
            });

        IPage page = await context.NewPageAsync();

        return new BrowserSession(context, page);
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
    }
}

internal sealed record BrowserSession(
    IBrowserContext Context,
    IPage Page) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
    }
}
