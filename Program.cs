namespace Sitepane;

internal sealed class AppPaths
{
    private static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sitepane", "apps");

    private AppPaths(Uri url)
    {
        Root = Path.Combine(DataRoot, AppIdentity.FolderName(url));
        WebViewData = Path.Combine(Root, "WebView2");
        IconFile = Path.Combine(Root, "icon.ico");
    }

    public string Root { get; }
    public string WebViewData { get; }
    public string IconFile { get; }

    public static AppPaths For(Uri url) => new(url);
}

/// <param name="Install">Create/refresh a Start menu shortcut once the site's name and icon are known.</param>
/// <param name="Name">Overrides the name derived from the page (shortcut and taskbar pin).</param>
internal sealed record LaunchOptions(Uri Url, bool Install, string? Name);

static class Program
{
    private const string Usage = "Usage: Sitepane [--install] [--name \"App name\"] <url>";

    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (Parse(args) is not { } options)
        {
            MessageBox.Show($"Invalid arguments: {string.Join(' ', args)}\n\n{Usage}", "Sitepane",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Application.Run(new Form1(options));
    }

    private static LaunchOptions? Parse(string[] args)
    {
        bool install = false;
        string? name = null;
        string? raw = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--install":
                    install = true;
                    break;
                case "--name" when i + 1 < args.Length:
                    name = args[++i];
                    break;
                default:
                    if (raw is not null || args[i].StartsWith("--", StringComparison.Ordinal))
                        return null;
                    raw = args[i].Trim();
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var url = ParseUrl(raw);
        return url is null ? null : new LaunchOptions(url, install, string.IsNullOrWhiteSpace(name) ? null : name.Trim());
    }

    /// <summary>"example.com" → https://…; "localhost:5173" → http://… (like a browser's address bar).</summary>
    private static Uri? ParseUrl(string raw)
    {
        if (!raw.Contains("://", StringComparison.Ordinal))
        {
            var host = raw.Split('/')[0].ToLowerInvariant();
            bool loopback = host.StartsWith("[::1]", StringComparison.Ordinal)
                || host.Split(':')[0] is "localhost" or "127.0.0.1"
                || host.Split(':')[0].EndsWith(".localhost", StringComparison.Ordinal);
            raw = (loopback ? "http://" : "https://") + raw;
        }
        return Uri.TryCreate(raw, UriKind.Absolute, out var url) && url.Scheme is ("http" or "https") ? url : null;
    }
}
