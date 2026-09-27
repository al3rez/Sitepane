# Sitepane

Sitepane turns a website into a Windows app. The page fills the window, the site's own icon sits on the taskbar, and there is no address bar. Hold Alt to show the window buttons.

## Run

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and the [WebView2 runtime](https://go.microsoft.com/fwlink/p/?LinkId=2124703), which current Windows 10 and 11 already have.

```bat
dotnet run --project Sitepane.csproj -- example.com
dotnet run --project Sitepane.csproj -- --install example.com
dotnet run --project Sitepane.csproj -- --install --name "My App" https://example.com
```

A URL without a scheme opens as https. localhost opens as http. Leave off `--install` to open a site without adding it to the Start menu.

Each installed URL gets its own taskbar identity, icon cache, cookies, local storage and WebView2 profile. Pinning its taskbar button relaunches that URL.
