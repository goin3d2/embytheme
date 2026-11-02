using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbyTheme.Core;

namespace EmbyTheme.Tests
{
    public static class Program
    {
        private static int failures;
        private static int passed;

        public static async Task<int> Main()
        {
            Rules();
            Mp3Checks();
            Files();
            StoreRoundTrip();
            await HttpSources().ConfigureAwait(false);

            Console.WriteLine($"{passed} passed, {failures} failed");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(bool ok, string name)
        {
            if (ok)
            {
                passed++;
            }
            else
            {
                failures++;
                Console.WriteLine("FAIL: " + name);
            }
        }

        private static byte[] FakeMp3(int size, bool id3 = true)
        {
            var data = new byte[size];
            if (id3)
            {
                data[0] = (byte)'I'; data[1] = (byte)'D'; data[2] = (byte)'3';
            }
            else
            {
                data[0] = 0xFF; data[1] = 0xFB; // MPEG-1 Layer III
            }

            return data;
        }

        private static void Rules()
        {
            var now = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
            Check(ThemeRules.IsDue(null, now, 30, 12), "new series is due");
            Check(ThemeRules.IsDue(new SeriesThemeEntry(), now, 30, 12), "pending is due");
            Check(!ThemeRules.IsDue(new SeriesThemeEntry { Status = ThemeStatus.Saved, CheckedUtc = now.AddYears(-1) }, now, 30, 12), "saved never due");
            Check(!ThemeRules.IsDue(new SeriesThemeEntry { Status = ThemeStatus.Existing }, now, 30, 12), "existing never due");
            Check(!ThemeRules.IsDue(new SeriesThemeEntry { Status = ThemeStatus.Skipped }, now, 30, 12), "skipped never due");
            Check(!ThemeRules.IsDue(new SeriesThemeEntry { Status = ThemeStatus.NotFound, CheckedUtc = now.AddDays(-29) }, now, 30, 12), "not found waits 30 days");
            Check(ThemeRules.IsDue(new SeriesThemeEntry { Status = ThemeStatus.NotFound, CheckedUtc = now.AddDays(-30) }, now, 30, 12), "not found due after 30 days");
            Check(ThemeRules.IsDue(new SeriesThemeEntry { Status = ThemeStatus.NoId, CheckedUtc = now.AddDays(-31) }, now, 30, 12), "no id due after 30 days");
            Check(ThemeRules.IsDue(new SeriesThemeEntry { Status = ThemeStatus.Failed, Attempts = 1, CheckedUtc = now.AddHours(-12) }, now, 30, 12), "failed once: 12h");
            Check(!ThemeRules.IsDue(new SeriesThemeEntry { Status = ThemeStatus.Failed, Attempts = 2, CheckedUtc = now.AddHours(-12) }, now, 30, 12), "failed twice: waits 24h");
            Check(ThemeRules.IsDue(new SeriesThemeEntry { Status = ThemeStatus.Failed, Attempts = 2, CheckedUtc = now.AddHours(-24) }, now, 30, 12), "failed twice: due at 24h");
            Check(!ThemeRules.IsDue(new SeriesThemeEntry { Status = ThemeStatus.Failed, Attempts = 20, CheckedUtc = now.AddDays(-6) }, now, 30, 12), "failure backoff capped at 7 days (not yet)");
            Check(ThemeRules.IsDue(new SeriesThemeEntry { Status = ThemeStatus.Failed, Attempts = 20, CheckedUtc = now.AddDays(-7) }, now, 30, 12), "failure backoff capped at 7 days");
            Check(ThemeRules.IsDue(new SeriesThemeEntry { Status = ThemeStatus.NotFound }, now, 30, 12), "missing check time is due");
        }

        private static void Mp3Checks()
        {
            Check(ThemeRules.LooksLikeMp3(FakeMp3(100_000), out _), "ID3 mp3 accepted");
            Check(ThemeRules.LooksLikeMp3(FakeMp3(100_000, id3: false), out _), "frame-sync mp3 accepted");
            Check(!ThemeRules.LooksLikeMp3(FakeMp3(1000), out var r1) && r1.StartsWith("too small"), "tiny file rejected");
            Check(!ThemeRules.LooksLikeMp3(Encoding.UTF8.GetBytes("<html>" + new string(' ', 50_000) + "</html>"), out var r2) && r2 == "not an MP3", "html rejected");
            Check(!ThemeRules.LooksLikeMp3(null, out _), "null rejected");
            var bad = new byte[50_000];
            bad[0] = 0xFF; bad[1] = 0xE8; // version bits 01 = reserved
            Check(!ThemeRules.LooksLikeMp3(bad, out _), "reserved MPEG version rejected");
        }

        private static void Files()
        {
            var dir = Path.Combine(Path.GetTempPath(), "embytheme-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                Check(!ThemeRules.FolderHasTheme(dir), "empty folder has no theme");
                Check(!ThemeRules.FolderHasTheme(Path.Combine(dir, "missing")), "missing folder has no theme");
                File.WriteAllText(Path.Combine(dir, "theme.nfo"), "x");
                Check(!ThemeRules.FolderHasTheme(dir), "theme.nfo is not a theme");

                var path = ThemeRules.WriteTheme(dir, FakeMp3(20_000), overwrite: false);
                Check(File.Exists(path) && new FileInfo(path).Length == 20_000, "theme written");
                Check(ThemeRules.FolderHasTheme(dir), "theme.mp3 detected");
                Check(Directory.GetFiles(dir, "*.tmp").Length == 0, "no temp file left");

                var threw = false;
                try
                {
                    ThemeRules.WriteTheme(dir, FakeMp3(30_000), overwrite: false);
                }
                catch (IOException)
                {
                    threw = true;
                }

                Check(threw && new FileInfo(path).Length == 20_000, "existing theme not overwritten");
                ThemeRules.WriteTheme(dir, FakeMp3(30_000), overwrite: true);
                Check(new FileInfo(path).Length == 30_000, "overwrite replaces");

                var dir2 = Path.Combine(dir, "show2");
                Directory.CreateDirectory(Path.Combine(dir2, "theme-music"));
                Check(!ThemeRules.FolderHasTheme(dir2), "empty theme-music folder is not a theme");
                File.WriteAllText(Path.Combine(dir2, "theme-music", "song.FLAC"), "x");
                Check(ThemeRules.FolderHasTheme(dir2), "theme-music with audio detected");

                var dir3 = Path.Combine(dir, "show3");
                Directory.CreateDirectory(dir3);
                File.WriteAllText(Path.Combine(dir3, "Theme.M4A"), "x");
                Check(ThemeRules.FolderHasTheme(dir3), "theme.m4a detected (case-insensitive)");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static void StoreRoundTrip()
        {
            var path = Path.Combine(Path.GetTempPath(), "embytheme-store-" + Guid.NewGuid().ToString("N"), "theme-state.json");
            var store = ThemeStore.Load(path);
            store.GetOrAdd(42).Status = ThemeStatus.NotFound;
            store.GetOrAdd(42).Name = "Show";
            store.GetOrAdd(42).CheckedUtc = DateTime.UtcNow;
            store.GetOrAdd(7).Status = ThemeStatus.Saved;
            store.Data.LastRun = new RunSummary { Saved = 3 };
            store.Save();

            var again = ThemeStore.Load(path);
            Check(again.Get(42)?.Status == ThemeStatus.NotFound && again.Get(42).Name == "Show", "store round-trips entries");
            Check(again.Data.LastRun?.Saved == 3, "store round-trips last run");
            Check(again.CountByStatus()["Saved"] == 1 && again.CountByStatus()["NotFound"] == 1, "counts by status");
            Check(again.Reset(e => e.Status == ThemeStatus.NotFound) == 1 && again.Get(42).Status == ThemeStatus.Pending && again.Get(42).CheckedUtc == null, "reset");
            Check(File.ReadAllText(path).Contains("\"NotFound\""), "statuses saved as names");

            File.WriteAllText(path, "{ not json");
            string error = null;
            var broken = ThemeStore.Load(path, (p, ex) => error = ex.Message);
            Check(broken.Data.Series.Count == 0 && error != null, "corrupt file starts fresh and reports");
            Directory.Delete(Path.GetDirectoryName(path), true);
        }

        private static async Task HttpSources()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            var http = new HttpListener();
            var prefix = $"http://127.0.0.1:{port}/";
            http.Prefixes.Add(prefix);
            http.Start();
            var requests = new List<string>();
            var serve = Task.Run(async () =>
            {
                while (http.IsListening)
                {
                    HttpListenerContext ctx;
                    try
                    {
                        ctx = await http.GetContextAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        return;
                    }

                    var p = ctx.Request.Url.PathAndQuery;
                    lock (requests)
                    {
                        requests.Add(p + " auth=" + ctx.Request.Headers["Authorization"]);
                    }

                    byte[] body = Array.Empty<byte>();
                    var status = 200;
                    var type = "application/json";
                    if (p.StartsWith("/plex/81189.mp3"))
                    {
                        body = FakeMp3(80_000);
                        type = "audio/mpeg";
                    }
                    else if (p.StartsWith("/plex/500.mp3"))
                    {
                        status = 500;
                    }
                    else if (p.StartsWith("/plex/html.mp3"))
                    {
                        body = Encoding.UTF8.GetBytes("<html>" + new string(' ', 40_000));
                        type = "text/html";
                    }
                    else if (p.StartsWith("/plex/"))
                    {
                        status = 404;
                    }
                    else if (p.StartsWith("/tmdb/tv/1396/external_ids"))
                    {
                        body = Encoding.UTF8.GetBytes("{\"imdb_id\":\"tt0903747\",\"tvdb_id\":81189}");
                    }
                    else if (p.StartsWith("/tmdb/tv/2/external_ids"))
                    {
                        body = Encoding.UTF8.GetBytes("{\"imdb_id\":null,\"tvdb_id\":null}");
                    }
                    else if (p.StartsWith("/tmdb/tv/401/"))
                    {
                        status = 401;
                    }
                    else
                    {
                        status = 404;
                    }

                    ctx.Response.StatusCode = status;
                    ctx.Response.ContentType = type;
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
                    ctx.Response.Close();
                }
            });

            PlexThemes.BaseUrl = prefix + "plex/";
            TmdbClient.BaseUrl = prefix + "tmdb/";
            var ct = CancellationToken.None;

            var found = await PlexThemes.GetAsync("81189", ct).ConfigureAwait(false);
            Check(found.Outcome == LookupOutcome.Found && found.Data.Length == 80_000, "plex found");
            Check((await PlexThemes.GetAsync("12345", ct).ConfigureAwait(false)).Outcome == LookupOutcome.NotFound, "plex 404 = not found");
            var err = await PlexThemes.GetAsync("500", ct).ConfigureAwait(false);
            Check(err.Outcome == LookupOutcome.Error && err.Error == "HTTP 500", "plex 500 = error");
            var html = await PlexThemes.GetAsync("html", ct).ConfigureAwait(false);
            Check(html.Outcome == LookupOutcome.Error && html.Error.Contains("not an MP3") && html.Error.Contains("text/html"), "html body = error");

            var v3 = new TmdbClient("0123456789abcdef0123456789abcdef");
            Check(await v3.GetTvdbIdAsync("1396", ct).ConfigureAwait(false) == "81189", "tmdb -> tvdb");
            Check(await v3.GetTvdbIdAsync("2", ct).ConfigureAwait(false) == null, "tmdb null tvdb");
            Check(await v3.GetTvdbIdAsync("999", ct).ConfigureAwait(false) == null, "tmdb 404 -> null");
            var threw = false;
            try
            {
                await v3.GetTvdbIdAsync("401", ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            Check(threw, "tmdb 401 throws InvalidOperationException");
            var bearer = new TmdbClient(new string('x', 200));
            await bearer.GetTvdbIdAsync("1396", ct).ConfigureAwait(false);
            lock (requests)
            {
                Check(requests.Any(r => r.Contains("api_key=0123456789abcdef0123456789abcdef")), "v3 key sent as query");
                Check(requests.Any(r => r.EndsWith("auth=Bearer " + new string('x', 200)) && !r.Contains("api_key")), "v4 token sent as bearer");
            }

            http.Stop();
            await serve.ConfigureAwait(false);
        }
    }
}
