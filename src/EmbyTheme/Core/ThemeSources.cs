using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyTheme.Core
{
    public enum LookupOutcome
    {
        Found,
        NotFound,
        Error,
    }

    public sealed class DownloadResult
    {
        public LookupOutcome Outcome { get; set; }

        public byte[] Data { get; set; }

        public string Error { get; set; }

        public static DownloadResult NotFound() => new DownloadResult { Outcome = LookupOutcome.NotFound };

        public static DownloadResult Fail(string error) => new DownloadResult { Outcome = LookupOutcome.Error, Error = error };
    }

    public static class Http
    {
        /// <summary>Shared client. Settable so tests can point the sources at a fake server.</summary>
        public static HttpClient Client { get; set; } = Create();

        private static HttpClient Create()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("EmbyTheme/" + typeof(Http).Assembly.GetName().Version);
            return client;
        }

        /// <summary>GET a URL and return the body if it's a plausible MP3. 404/410 count as "not found", anything else as an error.</summary>
        public static async Task<DownloadResult> GetMp3Async(string url, CancellationToken ct)
        {
            try
            {
                using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Gone)
                {
                    return DownloadResult.NotFound();
                }

                if (!response.IsSuccessStatusCode)
                {
                    return DownloadResult.Fail("HTTP " + (int)response.StatusCode);
                }

                var length = response.Content.Headers.ContentLength;
                if (length > ThemeRules.MaxThemeBytes)
                {
                    return DownloadResult.Fail($"file too large ({length / 1024 / 1024} MB)");
                }

                var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (!ThemeRules.LooksLikeMp3(data, out var reason))
                {
                    var type = response.Content.Headers.ContentType?.MediaType ?? "unknown type";
                    return DownloadResult.Fail($"download was {reason} ({type})");
                }

                return new DownloadResult { Outcome = LookupOutcome.Found, Data = data };
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                return DownloadResult.Fail("timed out");
            }
            catch (HttpRequestException ex)
            {
                return DownloadResult.Fail(ex.Message);
            }
        }
    }

    /// <summary>Plex's TV theme server: one MP3 per TVDB series id, or 404.</summary>
    public static class PlexThemes
    {
        public static string BaseUrl { get; set; } = "http://tvthemes.plexapp.com/";

        public static string UrlFor(string tvdbId) => BaseUrl + Uri.EscapeDataString(tvdbId) + ".mp3";

        public static Task<DownloadResult> GetAsync(string tvdbId, CancellationToken ct) => Http.GetMp3Async(UrlFor(tvdbId), ct);
    }

    /// <summary>Just enough TMDB to turn a TMDB tv id into a TVDB id.</summary>
    public sealed class TmdbClient
    {
        public static string BaseUrl { get; set; } = "https://api.themoviedb.org/3/";

        private readonly string key;

        public TmdbClient(string key)
        {
            this.key = (key ?? string.Empty).Trim();
        }

        public bool IsConfigured => this.key.Length > 0;

        // A v4 read access token is a long JWT; a v3 API key is 32 hex characters.
        private bool UsesBearer => this.key.Length > 40;

        /// <summary>The series' TVDB id, or null if TMDB doesn't have one. Throws on errors other than 404.</summary>
        public async Task<string> GetTvdbIdAsync(string tmdbId, CancellationToken ct)
        {
            var url = BaseUrl + "tv/" + Uri.EscapeDataString(tmdbId) + "/external_ids";
            if (!this.UsesBearer)
            {
                url += "?api_key=" + Uri.EscapeDataString(this.key);
            }

            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (this.UsesBearer)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", this.key);
                }

                using var response = await Http.Client.SendAsync(request, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                if (response.StatusCode == (HttpStatusCode)429 && attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct).ConfigureAwait(false);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new InvalidOperationException("TMDB rejected the API key (HTTP 401)");
                }

                response.EnsureSuccessStatusCode();
                using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("tvdb_id", out var id))
                {
                    if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var n) && n > 0)
                    {
                        return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }

                    if (id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()))
                    {
                        return id.GetString().Trim();
                    }
                }

                return null;
            }
        }
    }
}
