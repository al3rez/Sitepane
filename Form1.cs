using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Sitepane;

internal partial class Form1 : Form
{
    // Window-controls overlay in CSS px: 3 buttons × 46 (measured; WebView2 exposes no geometry to pages).
    private const int OverlayHeight = 36;
    private const int OverlayButtonsWidth = 3 * 46;

    private static readonly Dictionary<string, int> EdgeHitTests = new()
    {
        ["n"] = NativeChrome.HTTOP,
        ["s"] = NativeChrome.HTBOTTOM,
        ["w"] = NativeChrome.HTLEFT,
        ["e"] = NativeChrome.HTRIGHT,
        ["nw"] = NativeChrome.HTTOPLEFT,
        ["ne"] = NativeChrome.HTTOPRIGHT,
        ["sw"] = NativeChrome.HTBOTTOMLEFT,
        ["se"] = NativeChrome.HTBOTTOMRIGHT,
    };

    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
    private readonly LaunchOptions _options;
    private readonly Uri _startUrl;
    private readonly string _appId;
    private readonly AppPaths _paths;
    private readonly Dictionary<string, TaskCompletionSource<IReadOnlyDictionary<int, byte[]>?>> _svgJobs = new();
    private CoreWebView2WindowControlsOverlay? _overlay;
    private bool _chromeVisible;
    private bool _resizable;
    private bool _iconResolved;
    private bool _installed;
    private string? _appName;
    private Icon? _siteIcon;
    private Icon? _bigIcon;
    private Icon? _smallIcon;

    public Form1(LaunchOptions options)
    {
        _options = options;
        _startUrl = options.Url;
        _appId = AppIdentity.AppId(_startUrl);
        _paths = AppPaths.For(_startUrl);
        _appName = options.Name;
        InitializeComponent();

        Text = "Sitepane";
        FormBorderStyle = FormBorderStyle.None; // CreateParams adds the thick frame back (border, shadow, snap)
        BackColor = Color.Black;
        Controls.Add(_webView);

        if (SiteIcon.ReadCache(_paths.IconFile) is { } cached)
            SetSiteIcon(cached);

        // Keys pressed inside the page are raised on the WebView2 control (not the form, not KeyPreview).
        _webView.KeyDown += OnWebViewKeyDown;
        _webView.KeyUp += (_, e) =>
        {
            if (e.KeyCode == Keys.Menu)
                SetChromeVisible(false);
        };
        Deactivate += (_, _) => SetChromeVisible(false); // Alt+Tab: key-up goes to the other window
        Load += async (_, _) => await InitWebViewAsync();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // No caption bar; the thick frame keeps the DWM border/shadow, snapping and maximize/minimize.
            cp.Style = (cp.Style & ~(NativeChrome.WS_CAPTION | NativeChrome.WS_SYSMENU))
                | NativeChrome.WS_THICKFRAME | NativeChrome.WS_MINIMIZEBOX | NativeChrome.WS_MAXIMIZEBOX;
            return cp;
        }
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            // Every installed URL owns its browser profile, cookies and local storage.
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _paths.WebViewData);
            await _webView.EnsureCoreWebView2Async(environment);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this,
                "Microsoft Edge WebView2 Runtime is not installed.\nhttps://go.microsoft.com/fwlink/p/?LinkId=2124703",
                "Sitepane", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        var core = _webView.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsNonClientRegionSupportEnabled = true; // CSS app-region: drag for the Alt strip

        try
        {
            _overlay = core.WindowControlsOverlay;
            _overlay.Height = OverlayHeight;
            // Alpha 0 keeps the buttons see-through (the page's wash sits under them); WebView2 still
            // derives glyph/hover colors from the RGB, so it is set to the page color on each Alt ("tint").
            // Black until then: white glyphs. (Color.Transparent is transparent *white* → black glyphs.)
            _overlay.BackgroundColor = Color.FromArgb(0, 0, 0, 0);
            _overlay.IsEnabled = false; // shown only while Alt is held
        }
        catch (Exception e) when (e is NotImplementedException or InvalidCastException)
        {
            _overlay = null; // runtime without the experimental overlay: Alt still shows the drag strip
        }

        await core.AddScriptToExecuteOnDocumentCreatedAsync(PageScript.Bootstrap);
        core.WebMessageReceived += OnWebMessage;
        core.DOMContentLoaded += (_, _) => PushPageState();
        core.NavigationCompleted += async (_, e) =>
        {
            if (e.IsSuccess && Uri.TryCreate(core.Source, UriKind.Absolute, out var page) && page.Scheme is ("http" or "https"))
            {
                await ResolveAppNameAsync(page); // first: the letter-icon fallback uses the name
                await RefreshSiteIconAsync(page);
                InstallOnce();
            }
        };
        core.DocumentTitleChanged += (_, _) =>
            Text = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "Sitepane" : core.DocumentTitle;

        core.Navigate(_startUrl.AbsoluteUri);
    }

    private void SetChromeVisible(bool visible)
    {
        if (_chromeVisible == visible)
            return;
        _chromeVisible = visible;
        if (_overlay is not null)
            _overlay.IsEnabled = visible;
        PushPageState();
    }

    private void PushPageState()
    {
        if (_webView.CoreWebView2 is null)
            return;
        string Js(bool b) => b ? "true" : "false";
        int controlsWidth = _overlay is null ? 0 : OverlayButtonsWidth;
        _ = _webView.ExecuteScriptAsync(
            $"window.__pbSet && window.__pbSet({{ chrome: {Js(_chromeVisible)}, resizable: {Js(_resizable)}, " +
            $"controlsWidth: {controlsWidth}, controlsHeight: {OverlayHeight} }})");
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        bool resizable = IsHandleCreated && !NativeChrome.IsZoomed(Handle) && !NativeChrome.IsIconic(Handle);
        if (resizable == _resizable)
            return;
        _resizable = resizable;
        PushPageState();
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var msg = doc.RootElement;
        if (msg.ValueKind != JsonValueKind.Object)
            return;

        switch (SiteIcon.JsonString(msg, "pb"))
        {
            case "resize":
                if (_resizable && EdgeHitTests.TryGetValue(SiteIcon.JsonString(msg, "edge") ?? "", out int hitTest))
                    NativeChrome.BeginResize(Handle, hitTest);
                break;
            case "icon":
                if (_svgJobs.Remove(SiteIcon.JsonString(msg, "id") ?? "", out var job))
                    job.TrySetResult(ReadPngs(msg));
                break;
            case "tint":
                if (_overlay is not null && Channel(msg, "r") is { } r && Channel(msg, "g") is { } g && Channel(msg, "b") is { } b)
                    _overlay.BackgroundColor = Color.FromArgb(0, r, g, b);
                break;
        }
    }

    private static int? Channel(JsonElement msg, string name) =>
        msg.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int c) && c is >= 0 and <= 255
            ? c
            : null;

    private static IReadOnlyDictionary<int, byte[]>? ReadPngs(JsonElement msg)
    {
        if (!msg.TryGetProperty("pngs", out var pngs) || pngs.ValueKind != JsonValueKind.Object)
            return null;
        var result = new Dictionary<int, byte[]>();
        foreach (var p in pngs.EnumerateObject())
        {
            if (!int.TryParse(p.Name, out int size) || p.Value.ValueKind != JsonValueKind.String)
                return null;
            try
            {
                result[size] = Convert.FromBase64String(p.Value.GetString()!);
            }
            catch (FormatException)
            {
                return null;
            }
        }
        return result;
    }

    /// <summary>
    /// Resolved once per run: the app keeps its own icon when it navigates elsewhere. A known-good
    /// icon is not replaced by another host's (e.g. the first page is a sign-in provider). Sites
    /// without a usable icon get a generated letter icon.
    /// </summary>
    private async Task RefreshSiteIconAsync(Uri page)
    {
        bool ownApp = string.Equals(page.Authority, _startUrl.Authority, StringComparison.OrdinalIgnoreCase);
        if (_iconResolved || !ownApp)
            return;

        byte[]? ico = null;
        try
        {
            var candidates = await _webView.ExecuteScriptAsync(SiteIcon.CandidatesScript);
            if (!CandidateDocumentMatchesApp(candidates))
                return; // navigation raced ExecuteScriptAsync; wait for the matching document
            _iconResolved = true;
            ico = await SiteIcon.LoadAsync(candidates, RasterizeSvgAsync);
        }
        catch (Exception ex)
        {
            // Best effort: a missing icon must never take the browser down.
            Debug.WriteLine($"Site icon for {page.Host} failed: {ex}");
        }

        if (ico is null)
        {
            if (_siteIcon is not null)
                return; // keep the cached icon
            ico = SiteIcon.LetterIcon(_appName ?? _startUrl.Host, _startUrl.Host);
        }
        SetSiteIcon(ico);
        try
        {
            SiteIcon.WriteCache(_paths.IconFile, ico);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Icon cache write failed: {ex}");
        }
        ApplyShellIdentity(); // pins and shortcuts reference the cached .ico
    }

    private bool CandidateDocumentMatchesApp(string candidates)
    {
        using var doc = JsonDocument.Parse(candidates);
        return doc.RootElement.TryGetProperty("page", out var page)
            && page.ValueKind == JsonValueKind.String
            && Uri.TryCreate(page.GetString(), UriKind.Absolute, out var uri)
            && string.Equals(uri.Authority, _startUrl.Authority, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>App name for pins and the Start menu shortcut, from the app's own first page (not a sign-in provider).</summary>
    private async Task ResolveAppNameAsync(Uri page)
    {
        if (_appName is not null || !string.Equals(page.Host, _startUrl.Host, StringComparison.OrdinalIgnoreCase))
            return;
        var pageName = JsonSerializer.Deserialize<string?>(await _webView.ExecuteScriptAsync(WindowsShell.PageNameScript));
        _appName = WindowsShell.AppName(pageName, _startUrl);
        ApplyShellIdentity();
    }

    /// <summary>--install: Start menu shortcut, after the name and icon are known.</summary>
    private void InstallOnce()
    {
        if (!_options.Install || _installed)
            return;
        _installed = true;
        try
        {
            WindowsShell.CreateStartMenuShortcut(_appId, _startUrl, _appName ?? _startUrl.Host, IconFile() ?? Environment.ProcessPath!);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Could not create the Start menu shortcut:\n{ex.Message}", "Sitepane",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string? IconFile() => File.Exists(_paths.IconFile) ? _paths.IconFile : null;

    /// <summary>Own taskbar button per site; pinning relaunches this URL with the site icon.</summary>
    private void ApplyShellIdentity()
    {
        if (!IsHandleCreated)
            return;
        var relaunch = IconFile() is { } icon ? (_startUrl, _appName ?? _startUrl.Host, icon) : ((Uri, string, string)?)null;
        WindowsShell.SetWindowIdentity(Handle, _appId, relaunch);
    }

    private async Task<IReadOnlyDictionary<int, byte[]>?> RasterizeSvgAsync(byte[] svg, int[] sizes)
    {
        var id = Guid.NewGuid().ToString("N");
        var job = new TaskCompletionSource<IReadOnlyDictionary<int, byte[]>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _svgJobs[id] = job;
        await _webView.ExecuteScriptAsync(SiteIcon.RasterizeScript(svg, sizes, id));
        var finished = await Task.WhenAny(job.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        _svgJobs.Remove(id);
        return finished == job.Task ? await job.Task : null;
    }

    private void SetSiteIcon(byte[] ico)
    {
        var previous = _siteIcon;
        _siteIcon = new Icon(new MemoryStream(ico));
        Icon = _siteIcon;
        ApplyDpiIcons();
        previous?.Dispose();
    }

    /// <summary>
    /// Form.Icon hands the shell a system-DPI-sized big icon; the taskbar then upscales it on
    /// high-DPI monitors. Pick exact frames for this window's DPI instead.
    /// </summary>
    private void ApplyDpiIcons()
    {
        if (_siteIcon is null || !IsHandleCreated)
            return;
        var (big, small) = NativeChrome.IconSizes(Handle);
        var bigIcon = new Icon(_siteIcon, big, big);
        var smallIcon = new Icon(_siteIcon, small, small);
        NativeChrome.SetWindowIcons(Handle, bigIcon, smallIcon);
        _bigIcon?.Dispose();
        _smallIcon?.Dispose();
        _bigIcon = bigIcon;
        _smallIcon = smallIcon;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeChrome.ApplyWindowAttributes(Handle);
        ApplyDpiIcons();
        ApplyShellIdentity(); // before the window is shown, so it never groups under Sitepane.exe
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyDpiIcons();
    }

    private void OnWebViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Menu)
        {
            SetChromeVisible(true);
            return;
        }
        switch (e.KeyData)
        {
            case Keys.F11:
                WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                e.Handled = true;
                break;
            case Keys.Shift | Keys.Escape:
            case Keys.Alt | Keys.F4:
                Close();
                e.Handled = true;
                break;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeChrome.WM_NCCALCSIZE)
        {
            NativeChrome.CalcClientArea(ref m);
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _bigIcon?.Dispose();
        _smallIcon?.Dispose();
        base.OnFormClosed(e);
    }
}
