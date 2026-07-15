namespace RubyDownloader.Config;

internal sealed class AppSettings
{
    public bool PlaywrightHeadless { get; init; } = true;

    public float PlaywrightSlowMoMs { get; init; }

    public int NavigationTimeoutMs { get; init; } = 60_000;

    public int ResourceTimeoutMs { get; init; } = 30_000;

    public int DownloadTimeoutMs { get; init; } = 120_000;

    public string DownloadPath { get; init; } = "./downloads";

    public string UserAgent { get; init; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/150.0.0.0 Safari/537.36";

    public static AppSettings Load()
    {
        return new AppSettings
        {
            PlaywrightHeadless = GetBoolean(
                "PLAYWRIGHT_HEADLESS",
                defaultValue: true),

            PlaywrightSlowMoMs = GetFloat(
                "PLAYWRIGHT_SLOW_MO_MS",
                defaultValue: 0),

            NavigationTimeoutMs = GetInteger(
                "NAVIGATION_TIMEOUT_MS",
                defaultValue: 60_000,
                minimumValue: 5_000,
                maximumValue: 300_000),

            ResourceTimeoutMs = GetInteger(
                "RESOURCE_TIMEOUT_MS",
                defaultValue: 30_000,
                minimumValue: 5_000,
                maximumValue: 300_000),

            DownloadTimeoutMs = GetInteger(
                "DOWNLOAD_TIMEOUT_MS",
                defaultValue: 120_000,
                minimumValue: 10_000,
                maximumValue: 600_000),

            DownloadPath =
                Environment.GetEnvironmentVariable("DOWNLOAD_PATH")
                ?? "./downloads",

            UserAgent =
                Environment.GetEnvironmentVariable("USER_AGENT")
                ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                   "AppleWebKit/537.36 (KHTML, like Gecko) " +
                   "Chrome/150.0.0.0 Safari/537.36"
        };
    }

    private static bool GetBoolean(
        string variableName,
        bool defaultValue)
    {
        string? value =
            Environment.GetEnvironmentVariable(variableName);

        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return value.Equals(
                   "true",
                   StringComparison.OrdinalIgnoreCase) ||
               value.Equals(
                   "1",
                   StringComparison.OrdinalIgnoreCase) ||
               value.Equals(
                   "yes",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static int GetInteger(
        string variableName,
        int defaultValue,
        int minimumValue,
        int maximumValue)
    {
        string? value =
            Environment.GetEnvironmentVariable(variableName);

        if (!int.TryParse(value, out int result))
        {
            return defaultValue;
        }

        return Math.Clamp(
            result,
            minimumValue,
            maximumValue);
    }

    private static float GetFloat(
        string variableName,
        float defaultValue)
    {
        string? value =
            Environment.GetEnvironmentVariable(variableName);

        if (!float.TryParse(value, out float result))
        {
            return defaultValue;
        }

        return Math.Max(0, result);
    }
}
