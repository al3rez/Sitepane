using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Sitepane;

/// <summary>One deterministic identity for an installed URL, shared by shell and storage.</summary>
internal static class AppIdentity
{
    public static string Key(Uri url)
    {
        var canonical = CanonicalUrl(url);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
    }

    public static string AppId(Uri url)
    {
        var host = Regex.Replace(url.IdnHost.ToLowerInvariant(), "[^a-z0-9.-]", "-").Trim('-');
        var id = $"Sitepane.{(host.Length > 0 ? host : "site")}.{Key(url)}";
        return id.Length <= 128 ? id : id[..128];
    }

    public static string FolderName(Uri url)
    {
        var host = Regex.Replace(url.IdnHost.ToLowerInvariant(), "[^a-z0-9.-]", "-").Trim('-');
        return $"{(host.Length > 0 ? host : "site")}-{Key(url)}";
    }

    private static string CanonicalUrl(Uri url)
    {
        var builder = new UriBuilder(url) { Fragment = string.Empty };
        return builder.Uri.AbsoluteUri;
    }
}
