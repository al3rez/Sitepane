using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sitepane;

/// <summary>
/// Picks the highest-quality icon a site publishes (&lt;link rel=icon|apple-touch-icon&gt;, manifest
/// icons, /favicon.ico) by actual decoded size, not declared size, and turns it into a
/// multi-resolution .ico. SVG wins outright and is rasterized per size (crisp at every size).
/// </summary>
internal static class SiteIcon
{
    public delegate Task<IReadOnlyDictionary<int, byte[]>?> SvgRasterizer(byte[] svg, int[] sizes);

    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
    private const int MaxBytes = 4 * 1024 * 1024;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly JsonDocumentOptions LenientJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public const string CandidatesScript = """
        (() => {
          const icons = [];
          for (const l of document.querySelectorAll('link[rel][href]')) {
            const rel = l.rel.toLowerCase().split(/\s+/);
            if (rel.includes('icon') || rel.includes('apple-touch-icon') || rel.includes('apple-touch-icon-precomposed'))
              icons.push(l.href);
          }
          const manifest = document.querySelector('link[rel~="manifest"][href]');
          return { page: location.href, icons, manifest: manifest ? manifest.href : null, favicon: new URL('/favicon.ico', location.href).href };
        })()
        """;

    private enum Kind { Svg, Ico, Raster }

    private sealed record Source(string Url, bool Maskable);

    private sealed record Candidate(Kind Kind, byte[] Bytes, int Pixels, bool Maskable);

    /// <returns>.ico bytes, or null when the site has no usable icon.</returns>
    public static async Task<byte[]?> LoadAsync(string candidatesJson, SvgRasterizer rasterizeSvg)
    {
        var sources = await CollectSourcesAsync(candidatesJson);
        var candidates = await Task.WhenAll(sources.Select(ClassifyAsync));
        var ranked = candidates.OfType<Candidate>()
            .OrderBy(c => c.Maskable)              // maskable art is padded for masking; last resort
            .ThenByDescending(c => c.Pixels)
            .ThenBy(c => c.Kind);                  // ties: SVG, then ICO (keeps hand-tuned small frames)

        foreach (var c in ranked)
        {
            var ico = c.Kind switch
            {
                Kind.Ico => FromIco(c.Bytes),
                Kind.Svg => await FromSvgAsync(c.Bytes, rasterizeSvg),
                _ => FromRaster(c.Bytes),
            };
            if (ico is not null && IsLoadable(ico))
                return ico;
        }
        return null;
    }

    public static string RasterizeScript(byte[] svg, int[] sizes, string id) => $$"""
        (() => {
          const id = {{JsonSerializer.Serialize(id)}};
          const sizes = {{JsonSerializer.Serialize(sizes)}};
          const done = (result) => chrome.webview.postMessage({ pb: 'icon', id, ...result });
          const img = new Image();
          img.onload = () => {
            try {
              const pngs = {};
              for (const s of sizes) {
                const canvas = document.createElement('canvas');
                canvas.width = canvas.height = s;
                const w = img.naturalWidth || s, h = img.naturalHeight || s, k = Math.min(s / w, s / h);
                canvas.getContext('2d').drawImage(img, (s - w * k) / 2, (s - h * k) / 2, w * k, h * k);
                pngs[s] = canvas.toDataURL('image/png').split(',')[1];
              }
              done({ pngs });
            } catch (e) {
              done({ error: String(e) });
            }
          };
          img.onerror = () => done({ error: 'decode failed' });
          img.src = {{JsonSerializer.Serialize("data:image/svg+xml;base64," + Convert.ToBase64String(svg))}};
        })();
        """;

    public static byte[]? ReadCache(Uri app)
    {
        try
        {
            var path = CachePath(app);
            return path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static void WriteCache(Uri app, byte[] ico)
    {
        if (CachePath(app) is not { } path)
            return;
        Directory.CreateDirectory(AppPaths.Icons);
        File.WriteAllBytes(path, ico);
    }

    /// <summary>
    /// One icon per app, keyed by host[:port] like the app identity, so http→https redirects
    /// and scheme-less launches share it. Also the icon file used by shortcuts and taskbar pins.
    /// </summary>
    public static string? CachePath(Uri app)
    {
        if (app.Scheme is not ("http" or "https"))
            return null;
        var key = app.Authority.ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return Path.Combine(AppPaths.Icons, hash + ".ico");
    }

    private static async Task<List<Source>> CollectSourcesAsync(string json)
    {
        var sources = new List<Source>();
        using (var doc = JsonDocument.Parse(json))
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return sources;

            if (root.TryGetProperty("icons", out var icons) && icons.ValueKind == JsonValueKind.Array)
                foreach (var icon in icons.EnumerateArray())
                    if (icon.ValueKind == JsonValueKind.String)
                        sources.Add(new Source(icon.GetString()!, Maskable: false));

            if (JsonString(root, "manifest") is { } manifest)
                sources.AddRange(await ReadManifestAsync(manifest));

            if (JsonString(root, "favicon") is { } favicon)
                sources.Add(new Source(favicon, Maskable: false));
        }

        return sources
            .GroupBy(s => s.Url)
            .Select(g => g.OrderBy(s => s.Maskable).First())
            .ToList();
    }

    private static async Task<List<Source>> ReadManifestAsync(string manifestUrl)
    {
        var result = new List<Source>();
        var bytes = await DownloadAsync(manifestUrl);
        if (bytes is null || !Uri.TryCreate(manifestUrl, UriKind.Absolute, out var baseUri))
            return result;

        try
        {
            using var doc = JsonDocument.Parse(new MemoryStream(bytes), LenientJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("icons", out var icons)
                || icons.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var icon in icons.EnumerateArray())
            {
                if (icon.ValueKind != JsonValueKind.Object || JsonString(icon, "src") is not { } src)
                    continue;
                var purposes = (JsonString(icon, "purpose") ?? "any").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                bool any = purposes.Contains("any");
                if (!any && !purposes.Contains("maskable"))
                    continue; // monochrome-only
                if (Uri.TryCreate(baseUri, src, out var abs))
                    result.Add(new Source(abs.AbsoluteUri, Maskable: !any));
            }
        }
        catch (JsonException)
        {
            // Not a manifest; ignore.
        }
        return result;
    }

    private static async Task<Candidate?> ClassifyAsync(Source source)
    {
        var bytes = await DownloadAsync(source.Url);
        if (bytes is null)
            return null;
        if (TryReadIcoSize(bytes, out int icoPixels))
            return new Candidate(Kind.Ico, bytes, icoPixels, source.Maskable);
        if (IsSvg(bytes))
            return new Candidate(Kind.Svg, bytes, int.MaxValue, source.Maskable);

        try
        {
            using var stream = new MemoryStream(bytes);
            using var image = Image.FromStream(stream);
            return new Candidate(Kind.Raster, bytes, Math.Min(image.Width, image.Height), source.Maskable);
        }
        catch (Exception e) when (e is ArgumentException or ExternalException or OutOfMemoryException)
        {
            return null; // e.g. HTML fallback page or an unsupported format (WebP)
        }
    }

    private static async Task<byte[]?> DownloadAsync(string url)
    {
        try
        {
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return DecodeDataUrl(url);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return null;

            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes)
                return null;

            await using var body = await response.Content.ReadAsStreamAsync();
            var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                    return null;
                buffer.Write(chunk, 0, read);
            }
            return buffer.Length > 0 ? buffer.ToArray() : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or FormatException or IOException)
        {
            return null;
        }
    }

    private static byte[]? DecodeDataUrl(string url)
    {
        int comma = url.IndexOf(',');
        if (comma < 0)
            return null;
        var payload = Uri.UnescapeDataString(url[(comma + 1)..]);
        return url[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
            ? Convert.FromBase64String(payload)
            : Encoding.UTF8.GetBytes(payload);
    }

    private static bool TryReadIcoSize(byte[] b, out int pixels)
    {
        pixels = 0;
        if (b.Length < 6 || b[0] != 0 || b[1] != 0 || b[2] != 1 || b[3] != 0)
            return false;
        int count = BitConverter.ToUInt16(b, 4);
        if (count == 0 || b.Length < 6 + 16 * count)
            return false;
        for (int i = 0; i < count; i++)
        {
            int w = b[6 + 16 * i] == 0 ? 256 : b[6 + 16 * i];
            int h = b[7 + 16 * i] == 0 ? 256 : b[7 + 16 * i];
            pixels = Math.Max(pixels, Math.Min(w, h));
        }
        return true;
    }

    private static bool IsSvg(byte[] b)
    {
        var head = Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 2048)).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
            return true;
        // SPA servers answer unknown paths with index.html, which may contain inline <svg>.
        bool xmlPrologue = head.StartsWith("<?xml", StringComparison.Ordinal)
            || head.StartsWith("<!--", StringComparison.Ordinal)
            || head.StartsWith("<!DOCTYPE svg", StringComparison.OrdinalIgnoreCase);
        return xmlPrologue && head.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>ICO frames are used as-is (they may be hand-tuned per size) unless the artwork is padded.</summary>
    private static byte[] FromIco(byte[] ico)
    {
        try
        {
            using var icon = new Icon(new MemoryStream(ico), 256, 256);
            using var largest = icon.ToBitmap();
            return IsPadded(largest) ? FromImage(largest) : ico;
        }
        catch (ArgumentException)
        {
            return ico; // IsLoadable decides
        }
    }

    /// <summary>Per-size vector renders unless the artwork is padded; then trimmed from the 256 px render.</summary>
    private static async Task<byte[]?> FromSvgAsync(byte[] svg, SvgRasterizer rasterize)
    {
        var pngs = await rasterize(svg, Sizes);
        if (pngs is null)
            return null;
        if (pngs.TryGetValue(Sizes[^1], out var largestPng))
        {
            using var largest = Image.FromStream(new MemoryStream(largestPng));
            if (IsPadded(largest))
                return FromImage(largest);
        }
        var frames = Sizes.Where(pngs.ContainsKey).Select(s => (s, pngs[s])).ToList();
        return frames.Count > 0 ? BuildIco(frames) : null;
    }

    private static byte[] FromRaster(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var image = Image.FromStream(stream);
        return FromImage(image);
    }

    private static byte[] FromImage(Image source)
    {
        using var art = TrimToSquare(source);
        // No upscaling beyond the source, except always providing the small shell sizes.
        int limit = Math.Max(art.Width, 32);
        return BuildIco(Sizes.Where(s => s <= limit).Select(s => (s, RenderPng(art, s))).ToList());
    }

    /// <summary>
    /// Site icons often sit inside transparent margins, which makes them look small on the taskbar.
    /// Crop to the visible pixels and center on a square.
    /// </summary>
    private static Bitmap TrimToSquare(Image source)
    {
        var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
            g.DrawImage(source, 0, 0, source.Width, source.Height);

        var bounds = OpaqueBounds(bitmap);
        if (bounds.IsEmpty || !IsPadded(bounds, bitmap.Size))
            return bitmap;

        int side = Math.Max(bounds.Width, bounds.Height);
        var square = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(square))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            var dest = new Rectangle((side - bounds.Width) / 2, (side - bounds.Height) / 2, bounds.Width, bounds.Height);
            g.DrawImage(bitmap, dest, bounds, GraphicsUnit.Pixel);
        }
        bitmap.Dispose();
        return square;
    }

    private static bool IsPadded(Image image)
    {
        using var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
            g.DrawImage(image, 0, 0, image.Width, image.Height);
        var bounds = OpaqueBounds(bitmap);
        return !bounds.IsEmpty && IsPadded(bounds, bitmap.Size);
    }

    /// <summary>Visible art fills less than 90% of the canvas in either direction.</summary>
    private static bool IsPadded(Rectangle bounds, Size canvas) =>
        bounds.Width < canvas.Width * 0.9 || bounds.Height < canvas.Height * 0.9;

    /// <summary>Bounding box of pixels with alpha above a faint-shadow threshold.</summary>
    private static Rectangle OpaqueBounds(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var pixels = new int[bitmap.Width * bitmap.Height];
        try
        {
            for (int y = 0; y < bitmap.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * bitmap.Width, bitmap.Width);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if ((uint)pixels[y * bitmap.Width + x] >> 24 <= 16)
                    continue;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }
        return maxX < 0 ? Rectangle.Empty : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    private static byte[] RenderPng(Image source, int size)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        using (var attributes = new ImageAttributes())
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            attributes.SetWrapMode(WrapMode.TileFlipXY); // no dark fringe at the edges

            float scale = Math.Min((float)size / source.Width, (float)size / source.Height);
            var dest = Rectangle.Round(new RectangleF(
                (size - source.Width * scale) / 2, (size - source.Height * scale) / 2,
                source.Width * scale, source.Height * scale));
            g.DrawImage(source, dest, 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        }

        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        return png.ToArray();
    }

    /// <summary>Chrome-style fallback for sites without a usable icon: first letter on a per-host color.</summary>
    public static byte[] LetterIcon(string name, string host)
    {
        char first = name.FirstOrDefault(char.IsLetterOrDigit);
        var letter = first == default ? "?" : char.ToUpperInvariant(first).ToString();
        int hue = BitConverter.ToUInt16(SHA256.HashData(Encoding.UTF8.GetBytes(host.ToLowerInvariant())), 0) % 360;
        var background = FromHsl(hue, 0.55, 0.42);
        return BuildIco(Sizes.Select(s => (s, RenderLetter(letter, background, s))).ToList());
    }

    private static byte[] RenderLetter(string letter, Color background, int size)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        using (var shape = new GraphicsPath())
        using (var fill = new SolidBrush(background))
        using (var font = new Font("Segoe UI Semibold", size * 0.56f, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            float d = size * 0.44f; // corner diameter (Win11-style rounded square)
            float max = size - 1;
            shape.AddArc(0, 0, d, d, 180, 90);
            shape.AddArc(max - d, 0, d, d, 270, 90);
            shape.AddArc(max - d, max - d, d, d, 0, 90);
            shape.AddArc(0, max - d, d, d, 90, 90);
            shape.CloseFigure();
            g.FillPath(fill, shape);
            g.DrawString(letter, font, Brushes.White, new RectangleF(0, size * 0.02f, size, size), centered);
        }

        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        return png.ToArray();
    }

    private static Color FromHsl(int hue, double saturation, double lightness)
    {
        double c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double x = c * (1 - Math.Abs(hue / 60.0 % 2 - 1));
        double m = lightness - c / 2;
        var (r, g, b) = (hue / 60) switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }

    /// <summary>ICO container with PNG-compressed frames (supported by Windows Vista+ at every size).</summary>
    private static byte[] BuildIco(IReadOnlyList<(int Size, byte[] Png)> frames)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)frames.Count);

        int offset = 6 + 16 * frames.Count;
        foreach (var (size, png) in frames)
        {
            byte dim = size >= 256 ? (byte)0 : (byte)size;
            w.Write(dim);
            w.Write(dim);
            w.Write((byte)0);   // palette colors
            w.Write((byte)0);   // reserved
            w.Write((ushort)1); // planes
            w.Write((ushort)32);
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in frames)
            w.Write(png);

        w.Flush();
        return ms.ToArray();
    }

    private static bool IsLoadable(byte[] ico)
    {
        try
        {
            using var icon = new Icon(new MemoryStream(ico));
            return true;
        }
        catch (Exception e) when (e is ArgumentException or ExternalException)
        {
            return false;
        }
    }

    public static string? JsonString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
