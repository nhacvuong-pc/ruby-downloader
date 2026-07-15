namespace RubyDownloader.Config;

internal static class EnvLoader
{
    public static void Load(string fileName = ".env")
    {
        string? path = FindFile(fileName);
        if (path is null)
        {
            return;
        }

        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key = line[..separator].Trim();
            string value = RemoveOuterQuotes(line[(separator + 1)..].Trim());
            if (key.Length == 0)
            {
                continue;
            }

            // Biến môi trường của hệ điều hành được ưu tiên hơn file .env.
            if (Environment.GetEnvironmentVariable(key) is null)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    private static string? FindFile(string fileName)
    {
        string current = Path.Combine(Directory.GetCurrentDirectory(), fileName);
        if (File.Exists(current)) return current;

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return null;
    }

    private static string RemoveOuterQuotes(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }
        return value;
    }
}
