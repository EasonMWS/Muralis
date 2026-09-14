# Wallpaper sources

Muralis can pull wallpapers from several sources at once. Each source is a separate
`IWallpaperProvider` implementation, so adding one never touches the user interface or
the other sources.

## Shipped sources

| Source | Id | Search | API key | Notes |
| --- | --- | --- | --- | --- |
| Bing daily images | `bing` | – | not needed | Microsoft's daily photo; eight most recent days |
| Wallhaven | `wallhaven` | yes | optional | Community library; **SFW content only** (`purity=100`) |
| NASA APOD | `nasa` | – | **required** | Astronomy Picture of the Day; videos are skipped |
| Sample wallpapers | `mock` | yes | not needed | Images that ship with Windows; works offline |

Users can turn sources on or off and pick the default source in
**Settings → Wallpaper sources**. The default source fills the Home page; Browse can
search one source or **All sources** at once. If one source fails, the others still
return results and the failing source is named in a warning above the grid.

## API keys

Keys are **never** compiled into the application or committed to the repository. A source
that needs one is marked *Needs an API key* in Settings and is skipped until a key is
configured.

Two ways to provide a key — the environment variable wins:

1. **Configuration file** `%LOCALAPPDATA%\Muralis\providers.json`:

   ```json
   {
     "providers": {
       "nasa":     { "apiKey": "YOUR_NASA_API_KEY" },
       "wallhaven": { "apiKey": "OPTIONAL_WALLHAVEN_API_KEY" }
     }
   }
   ```

   The file is read on first use and re-read whenever it changes, so keys can be added,
   rotated or removed while Muralis is running. It lives in the per-user data folder and
   is never part of the repository.

2. **Environment variable** `MURALIS_PROVIDER_<ID>_API_KEY`, for example:

   ```powershell
   setx MURALIS_PROVIDER_NASA_API_KEY "YOUR_NASA_API_KEY"
   ```

### Getting the keys

- **NASA APOD** — free and instant: <https://api.nasa.gov> (the sign-up form emails a
  key; `DEMO_KEY` also works for a quick try but is heavily rate-limited).
- **Wallhaven** — optional; only needed to lift anonymous rate limits.
  Account settings → API key: <https://wallhaven.cc/settings/account>

Keys are never written to the log. Requests that carry a key are logged by host and
path only.

## Network behaviour

- **One shared `HttpClient`** for all providers, downloads and thumbnails.
- **Retries**: idempotent (GET) requests are retried up to three times with exponential
  backoff and jitter on connection errors, timeouts, 408, 429 and 5xx responses; a
  server `Retry-After` header is honoured. The caller's cancellation always wins.
- **Timeouts**: provider metadata calls are bounded (20 s); downloads follow the
  download service settings.
- **Caching** (see `cache/metadata` under `%LOCALAPPDATA%\Muralis`):
  - provider responses (search results, feeds) are cached on disk with a freshness
    window (Bing 1 h, Wallhaven search 30 min / featured 3 h, APOD today 6 h / random
    set 5 min), so revisiting a page or restarting the app does not re-hit the API;
  - thumbnails are cached on disk separately and rendered from there;
  - **Settings → Storage** shows the combined cache size and can clear it.
- **Catalog merge**: fresh search results are merged with the local catalog, so hearts
  and already-downloaded files show up immediately in Browse and no wallpaper is
  downloaded twice.

## Adding a source

1. Implement `IWallpaperProvider` in `src/Muralis.Core/Providers/`:
   - `Id` (stable, lowercase) and `DisplayName` (English fallback);
   - `GetWallpapersAsync` for search/browse; set `SupportsSearch` if a search box makes
     sense;
   - `GetFeaturedAsync` for the Home feed (optional — a sensible default is provided);
   - `GetWallpaperAsync` for details by id;
   - `RequiresApiKey` if the source cannot work without a key, and read it through the
     injected `IProviderConfiguration` (never from code);
   - use `RemoteJson.GetAsync` so retries, timeouts and caching apply, and pick a cache
     lifetime that respects the API's rate limits.
2. Register it in `AppHost.BuildProvider` (`AddSingleton<IWallpaperProvider>`), in the
   order it should appear in Settings.
3. Add `Provider_<id>` and `Provider_Description_<id>` entries to both resource files
   (`Strings/Resources.resx`, `Strings/Resources.zh-CN.resx`) — the UI localizes names
   through these keys.
4. Add mapping tests with a recorded response fixture (`FakeHttpMessageHandler`).

Notes for compliance-sensitive sources:

- Keep the default request set conservative (Muralis requests SFW content only from
  Wallhaven and never asks for NSFW media).
- If a source must be told about downloads (for example Unsplash's `download_location`
  endpoint), override `GetDownloadUrlAsync` and return the final file URL — the
  `DownloadService` then fetches exactly that URL.

## Sources we deliberately do not ship

**Unsplash** was evaluated and left out. Its API guidelines require the app to:
hotlink images (Muralis' offline-friendly thumbnail cache stores copies on disk), show
attribution links with UTM parameters for both the photographer and Unsplash (the
wallpaper model has no author/link fields yet), be told about every download through a
tracking endpoint, and live with a 50 requests/hour demo quota — with access keys
issued per developer app. None of that is impossible, but it does not fit the
"works out of the box" model of this app, and shipping it half-compliant would be worse
than not shipping it. The provider recipe above (plus an attribution field on
`Wallpaper` and a hotlink-only thumbnail path) is the way to add it properly.

**Pexels** was ruled out for the same reasons: keys are per-developer, and its terms
expect hotlinked images rather than a local thumbnail cache.

Both can be added by anyone who wants them — the provider manager treats every
`IWallpaperProvider` the same.
