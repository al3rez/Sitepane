# Sitepane

Sitepane turns a website into a Windows app. The page fills the window, the site's own icon sits on the taskbar, and there is no address bar. Hold Alt to show the window buttons.

## Run

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and the [WebView2 runtime](https://go.microsoft.com/fwlink/p/?LinkId=2124703), which current Windows 10 and 11 already have.

```bat
dotnet run --project Sitepane.csproj
dotnet run --project Sitepane.csproj -- --install daypuff.vercel.app
dotnet run --project Sitepane.csproj -- --install --name "My App" https://example.com
```

A URL without a scheme opens as https. localhost opens as http. Leave off `--install` to open a site without adding it to the Start menu.

Each site gets its own taskbar button. Pinning that button relaunches the same site. Logins are shared across Sitepane apps, and not with Edge.
