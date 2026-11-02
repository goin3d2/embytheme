using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbyTheme.Core;
using EmbyTheme.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Services;

namespace EmbyTheme.Api
{
    [Route("/EmbyTheme/Settings", "GET", Summary = "Gets EmbyTheme settings")]
    [Authenticated(Roles = "Admin")]
    public class GetSettings : IReturnVoid
    {
    }

    [Route("/EmbyTheme/Settings", "POST", Summary = "Saves EmbyTheme settings")]
    [Authenticated(Roles = "Admin")]
    public class SaveSettings : IRequiresRequestStream, IReturnVoid
    {
        public Stream RequestStream { get; set; }
    }

    [Route("/EmbyTheme/Status", "GET", Summary = "Counts, last run and libraries for the settings page")]
    [Authenticated(Roles = "Admin")]
    public class GetStatus : IReturnVoid
    {
    }

    [Route("/EmbyTheme/Series", "GET", Summary = "Series and their theme status")]
    [Authenticated(Roles = "Admin")]
    public class GetSeriesList : IReturnVoid
    {
        /// <summary>Optional status filter (Saved, NotFound, NoId, Failed, Existing, Skipped, Pending).</summary>
        public string Status { get; set; }

        /// <summary>Optional text matched against the series name.</summary>
        public string Search { get; set; }

        public int? StartIndex { get; set; }

        public int? Limit { get; set; }
    }

    [Route("/EmbyTheme/Reset", "POST", Summary = "Sets series with the given statuses back to Pending")]
    [Authenticated(Roles = "Admin")]
    public class ResetStatuses : IReturnVoid
    {
        /// <summary>Comma-separated statuses, e.g. "NotFound,NoId".</summary>
        public string Statuses { get; set; }
    }

    [Route("/EmbyTheme/Series/{Id}/Fetch", "POST", Summary = "Looks up and saves a theme for one series now")]
    [Authenticated(Roles = "Admin")]
    public class FetchOne : IReturnVoid
    {
        public string Id { get; set; }
    }

    [Route("/EmbyTheme/Series/{Id}/Url", "POST", Summary = "Saves an MP3 from a URL as the series' theme")]
    [Authenticated(Roles = "Admin")]
    public class SetFromUrl : IReturnVoid
    {
        public string Id { get; set; }

        public string Url { get; set; }
    }

    [Route("/EmbyTheme/Series/{Id}/Skip", "POST", Summary = "Tells the plugin to leave a series alone (or not)")]
    [Authenticated(Roles = "Admin")]
    public class SkipOne : IReturnVoid
    {
        public string Id { get; set; }

        public bool Skip { get; set; } = true;
    }

    public class EmbyThemeApi : IService, IRequiresRequest
    {
        private const int MaxListLimit = 500;

        private readonly IHttpResultFactory resultFactory;
        private readonly ILibraryManager libraryManager;
        private readonly ILibraryMonitor libraryMonitor;
        private readonly IProviderManager providerManager;
        private readonly IFileSystem fileSystem;

        public EmbyThemeApi(IHttpResultFactory resultFactory, ILibraryManager libraryManager, ILibraryMonitor libraryMonitor, IProviderManager providerManager, IFileSystem fileSystem)
        {
            this.resultFactory = resultFactory;
            this.libraryManager = libraryManager;
            this.libraryMonitor = libraryMonitor;
            this.providerManager = providerManager;
            this.fileSystem = fileSystem;
        }

        public IRequest Request { get; set; }

        private static Plugin Current => Plugin.Instance ?? throw new InvalidOperationException(Plugin.PluginName + " is not loaded");

        private ThemeService Service => new ThemeService(this.libraryManager, this.libraryMonitor, this.providerManager, this.fileSystem, Current.Logger);

        public object Get(GetSettings request) => this.Json(Current.Options);

        public async Task<object> Post(SaveSettings request)
        {
            using var reader = new StreamReader(request.RequestStream);
            var text = await reader.ReadToEndAsync().ConfigureAwait(false);
            var options = string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<PluginOptions>(text, Plugin.SettingsJson);
            Current.SaveOptions(options ?? throw new ArgumentException("No settings in the request"));
            return this.Json(Current.Options);
        }

        public object Get(GetStatus request)
        {
            var plugin = Current;
            return this.Json(new
            {
                Counts = plugin.Store.CountByStatus(),
                Tracked = plugin.Store.Data.Series.Count,
                plugin.Store.Data.LastRun,
                plugin.IsRunning,
                Libraries = this.Service.GetTvLibraries(),
                TmdbConfigured = !string.IsNullOrWhiteSpace(plugin.Options.TmdbApiKey),
                StateFile = plugin.Store.FilePath,
                Version = plugin.Version?.ToString(),
            });
        }

        public object Get(GetSeriesList request)
        {
            IEnumerable<KeyValuePair<long, SeriesThemeEntry>> rows = Current.Store.Data.Series;
            if (!string.IsNullOrWhiteSpace(request.Status) && Enum.TryParse<ThemeStatus>(request.Status, true, out var status))
            {
                rows = rows.Where(r => r.Value.Status == status);
            }

            if (!string.IsNullOrWhiteSpace(request.Search))
            {
                var term = request.Search.Trim();
                rows = rows.Where(r => (r.Value.Name ?? string.Empty).IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            var all = rows.OrderBy(r => r.Value.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase).ToList();
            var start = Math.Max(0, request.StartIndex ?? 0);
            var limit = Math.Max(1, Math.Min(MaxListLimit, request.Limit ?? 100));
            return this.Json(new
            {
                TotalRecordCount = all.Count,
                Items = all.Skip(start).Take(limit).Select(r => ToRow(r.Key, r.Value)),
            });
        }

        public object Post(ResetStatuses request)
        {
            var wanted = (request.Statuses ?? string.Empty)
                .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Enum.TryParse<ThemeStatus>(s.Trim(), true, out var st) ? st : (ThemeStatus?)null)
                .Where(s => s.HasValue && s.Value != ThemeStatus.Pending)
                .Select(s => s.Value)
                .ToHashSet();
            var count = wanted.Count == 0 ? 0 : Current.Store.Reset(e => wanted.Contains(e.Status));
            Current.Store.Save();
            Current.Logger.Info("Reset {0} series ({1}) to be looked up again", count, string.Join(", ", wanted));
            return this.Json(new { Reset = count });
        }

        public async Task<object> Post(FetchOne request)
        {
            var (series, entry) = this.Resolve(request.Id);
            var plugin = Current;
            var outcome = await this.Service.ProcessAsync(series, entry, new TmdbClient(plugin.Options.TmdbApiKey), plugin.Options, CancellationToken.None).ConfigureAwait(false);
            plugin.Store.Save();
            return this.Json(new { outcome.Status, outcome.Detail, Entry = ToRow(series.InternalId, entry) });
        }

        public async Task<object> Post(SetFromUrl request)
        {
            var (series, entry) = this.Resolve(request.Id);
            var plugin = Current;
            var outcome = await this.Service.SaveFromUrlAsync(series, entry, request.Url, plugin.Options, CancellationToken.None).ConfigureAwait(false);
            plugin.Store.Save();
            return this.Json(new { outcome.Status, outcome.Detail, Entry = ToRow(series.InternalId, entry) });
        }

        public object Post(SkipOne request)
        {
            var (series, entry) = this.Resolve(request.Id);
            entry.Status = request.Skip ? ThemeStatus.Skipped : ThemeStatus.Pending;
            entry.CheckedUtc = request.Skip ? DateTime.UtcNow : (DateTime?)null;
            entry.Error = null;
            Current.Store.Save();
            return this.Json(new { Entry = ToRow(series.InternalId, entry) });
        }

        private static object ToRow(long id, SeriesThemeEntry e) => new
        {
            Id = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            e.Name,
            e.Tvdb,
            e.Tmdb,
            e.ResolvedTvdb,
            Status = e.Status.ToString(),
            e.Source,
            e.CheckedUtc,
            e.Error,
        };

        private (MediaBrowser.Controller.Entities.TV.Series Series, SeriesThemeEntry Entry) Resolve(string id)
        {
            var series = this.Service.FindSeries(id) ?? throw new ArgumentException("No series with id " + id);
            return (series, Current.Store.GetOrAdd(series.InternalId));
        }

        private object Json(object value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Plugin.SettingsJson);
            return this.resultFactory.GetResult(this.Request, new ReadOnlyMemory<byte>(bytes), "application/json", new Dictionary<string, string> { ["Cache-Control"] = "no-store" });
        }
    }
}
