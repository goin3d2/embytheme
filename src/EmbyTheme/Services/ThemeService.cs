using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyTheme.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;

namespace EmbyTheme.Services
{
    /// <summary>Result of looking at one series.</summary>
    public sealed class SeriesOutcome
    {
        public ThemeStatus Status { get; set; }

        /// <summary>True if an outside request (Plex or TMDB) was made, which counts against the per-run limit.</summary>
        public bool UsedLookup { get; set; }

        public bool WriteFailed { get; set; }

        public string Detail { get; set; }
    }

    /// <summary>
    /// Everything that touches Emby: finding series, checking for existing themes, saving theme.mp3 and asking Emby
    /// to pick it up. Shared by the scheduled task and the settings page's per-series actions.
    /// </summary>
    public sealed class ThemeService
    {
        private readonly ILibraryManager libraryManager;
        private readonly ILibraryMonitor libraryMonitor;
        private readonly IProviderManager providerManager;
        private readonly IFileSystem fileSystem;
        private readonly ILogger logger;

        /// <summary>Write check results per library folder (a series folder's parent), for the life of this service.</summary>
        private readonly Dictionary<string, string> unwritableParents = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> writableParents = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Library folders found read-only so far.</summary>
        public IReadOnlyCollection<string> UnwritableFolders => this.unwritableParents.Keys;

        public ThemeService(ILibraryManager libraryManager, ILibraryMonitor libraryMonitor, IProviderManager providerManager, IFileSystem fileSystem, ILogger logger)
        {
            this.libraryManager = libraryManager;
            this.libraryMonitor = libraryMonitor;
            this.providerManager = providerManager;
            this.fileSystem = fileSystem;
            this.logger = logger;
        }

        /// <summary>Libraries that can hold TV series, for the settings page.</summary>
        public List<LibraryInfo> GetTvLibraries()
        {
            return this.libraryManager.GetVirtualFolders()
                .Where(f => string.IsNullOrEmpty(f.CollectionType)
                    || string.Equals(f.CollectionType, "tvshows", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f.CollectionType, "mixed", StringComparison.OrdinalIgnoreCase))
                .Select(f => new LibraryInfo { Id = f.ItemId, Name = f.Name, CollectionType = f.CollectionType })
                .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Series in the chosen libraries (all libraries when none are chosen), newest first.</summary>
        public Series[] GetSeriesInScope(PluginOptions options)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Series" },
                Recursive = true,
                IsVirtualItem = false,
            };

            if (options.LibraryIds.Length > 0)
            {
                var ids = options.LibraryIds
                    .Select(id => this.libraryManager.GetItemById(id))
                    .Where(item => item != null)
                    .Select(item => item.InternalId)
                    .ToArray();
                if (ids.Length == 0)
                {
                    this.logger.Warn("None of the chosen libraries exist any more; covering all libraries");
                }
                else
                {
                    query.AncestorIds = ids;
                }
            }

            return this.libraryManager.GetItemList(query)
                .OfType<Series>()
                .OrderByDescending(s => s.DateCreated)
                .ToArray();
        }

        public Series FindSeries(string itemId)
        {
            return string.IsNullOrWhiteSpace(itemId) ? null : this.libraryManager.GetItemById(itemId) as Series;
        }

        /// <summary>Copy the series' name and ids into its entry (ids can change when you re-identify a show).</summary>
        public static void Describe(Series series, SeriesThemeEntry entry)
        {
            entry.Name = series.Name;
            var tvdb = series.GetProviderId(MetadataProviders.Tvdb);
            var tmdb = series.GetProviderId(MetadataProviders.Tmdb);
            if (!string.Equals(entry.Tmdb, tmdb, StringComparison.Ordinal))
            {
                entry.ResolvedTvdb = null;
                entry.TmdbResolveTried = false;
            }

            entry.Tvdb = string.IsNullOrWhiteSpace(tvdb) ? null : tvdb.Trim();
            entry.Tmdb = string.IsNullOrWhiteSpace(tmdb) ? null : tmdb.Trim();
        }

        public static bool HasTheme(Series series)
        {
            if (series.ThemeSongIds != null && series.ThemeSongIds.Length > 0)
            {
                return true;
            }

            return series.IsFileProtocol && ThemeRules.FolderHasTheme(series.Path);
        }

        /// <summary>
        /// Look up and save a theme for one series, updating its entry. Never throws for ordinary failures; they're
        /// recorded on the entry.
        /// </summary>
        public async Task<SeriesOutcome> ProcessAsync(Series series, SeriesThemeEntry entry, TmdbClient tmdb, PluginOptions options, CancellationToken ct)
        {
            Describe(series, entry);
            var outcome = await this.LookupAndSaveAsync(series, entry, tmdb, options, ct).ConfigureAwait(false);
            entry.Status = outcome.Status;
            entry.CheckedUtc = DateTime.UtcNow;
            entry.Error = outcome.Status == ThemeStatus.Saved || outcome.Status == ThemeStatus.Existing ? null : outcome.Detail;
            entry.Attempts = outcome.Status == ThemeStatus.Failed ? entry.Attempts + 1 : 0;
            if (outcome.Status == ThemeStatus.Saved)
            {
                entry.Source = "plex";
            }

            return outcome;
        }

        /// <summary>Download an MP3 from any URL and save it as the series' theme, replacing what's there.</summary>
        public async Task<SeriesOutcome> SaveFromUrlAsync(Series series, SeriesThemeEntry entry, string url, PluginOptions options, CancellationToken ct)
        {
            Describe(series, entry);
            SeriesOutcome outcome;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                outcome = new SeriesOutcome { Status = ThemeStatus.Failed, Detail = "not an http(s) URL" };
            }
            else
            {
                var download = await Http.GetMp3Async(uri.AbsoluteUri, ct).ConfigureAwait(false);
                outcome = download.Outcome switch
                {
                    LookupOutcome.Found => this.Save(series, download.Data, overwrite: true, options),
                    LookupOutcome.NotFound => new SeriesOutcome { Status = ThemeStatus.Failed, Detail = "URL returned 404" },
                    _ => new SeriesOutcome { Status = ThemeStatus.Failed, Detail = download.Error },
                };
            }

            entry.CheckedUtc = DateTime.UtcNow;
            if (outcome.Status == ThemeStatus.Saved)
            {
                entry.Status = ThemeStatus.Saved;
                entry.Source = "url";
                entry.Error = null;
                entry.Attempts = 0;
            }
            else
            {
                // A bad manual URL shouldn't throw away what the series had before; just record the error.
                entry.Error = outcome.Detail;
            }

            return outcome;
        }

        private async Task<SeriesOutcome> LookupAndSaveAsync(Series series, SeriesThemeEntry entry, TmdbClient tmdb, PluginOptions options, CancellationToken ct)
        {
            if (HasTheme(series))
            {
                return new SeriesOutcome { Status = ThemeStatus.Existing, Detail = "already has a theme" };
            }

            if (!series.IsFileProtocol || string.IsNullOrEmpty(series.Path) || !Directory.Exists(series.Path))
            {
                return new SeriesOutcome { Status = ThemeStatus.Failed, Detail = "series folder not found: " + (series.Path ?? "(none)") };
            }

            if (!this.CanWriteTo(series.Path, out var writeError))
            {
                return new SeriesOutcome { Status = ThemeStatus.Failed, WriteFailed = true, Detail = "folder is not writable (" + writeError + ")" };
            }

            var usedLookup = false;
            if (string.IsNullOrEmpty(entry.EffectiveTvdb) && !string.IsNullOrEmpty(entry.Tmdb) && tmdb != null && tmdb.IsConfigured && !entry.TmdbResolveTried)
            {
                usedLookup = true;
                try
                {
                    entry.ResolvedTvdb = await tmdb.GetTvdbIdAsync(entry.Tmdb, ct).ConfigureAwait(false);
                    entry.TmdbResolveTried = true;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && !(ex is InvalidOperationException))
                {
                    return new SeriesOutcome { Status = ThemeStatus.Failed, UsedLookup = true, Detail = "TMDB lookup failed: " + ex.Message };
                }
            }

            var tvdb = entry.EffectiveTvdb;
            if (string.IsNullOrEmpty(tvdb))
            {
                var why = string.IsNullOrEmpty(entry.Tmdb) ? "no TVDB or TMDB id"
                    : tmdb == null || !tmdb.IsConfigured ? "no TVDB id (add a TMDB key to look one up)"
                    : "no TVDB id, and TMDB doesn't list one";
                return new SeriesOutcome { Status = ThemeStatus.NoId, UsedLookup = usedLookup, Detail = why };
            }

            var download = await PlexThemes.GetAsync(tvdb, ct).ConfigureAwait(false);
            switch (download.Outcome)
            {
                case LookupOutcome.NotFound:
                    return new SeriesOutcome { Status = ThemeStatus.NotFound, UsedLookup = true, Detail = "Plex has no theme for TVDB " + tvdb };
                case LookupOutcome.Error:
                    return new SeriesOutcome { Status = ThemeStatus.Failed, UsedLookup = true, Detail = "Plex: " + download.Error };
            }

            var saved = this.Save(series, download.Data, overwrite: false, options);
            saved.UsedLookup = true;
            return saved;
        }

        /// <summary>
        /// Probe the series folder once per parent folder: series in the same library location share a mount, so one
        /// read-only result covers them all without a probe (or a download) each.
        /// </summary>
        private bool CanWriteTo(string seriesFolder, out string error)
        {
            var parent = Path.GetDirectoryName(seriesFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? seriesFolder;
            if (this.writableParents.Contains(parent))
            {
                error = null;
                return true;
            }

            if (this.unwritableParents.TryGetValue(parent, out error))
            {
                return false;
            }

            if (ThemeRules.CanWrite(seriesFolder, out error))
            {
                this.writableParents.Add(parent);
                return true;
            }

            this.unwritableParents[parent] = error;
            this.logger.Warn("Can't write theme files under {0} ({1}); series there are skipped this run", parent, error);
            return false;
        }

        private SeriesOutcome Save(Series series, byte[] data, bool overwrite, PluginOptions options)
        {
            var target = Path.Combine(series.Path, ThemeRules.ThemeFileName);
            this.libraryMonitor.ReportFileSystemChangeBeginning(target);
            try
            {
                ThemeRules.WriteTheme(series.Path, data, overwrite);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                this.logger.Warn("Could not write {0}: {1}", target, ex.Message);
                return new SeriesOutcome { Status = ThemeStatus.Failed, WriteFailed = true, Detail = "could not write " + target + ": " + ex.Message };
            }
            finally
            {
                this.libraryMonitor.ReportFileSystemChangeComplete(target, false);
            }

            this.logger.Info("Saved theme for {0} ({1} KB) -> {2}", series.Name, data.Length / 1024, target);
            if (options.RefreshAfterSave)
            {
                this.QueueRefresh(series);
            }

            return new SeriesOutcome { Status = ThemeStatus.Saved, Detail = target };
        }

        /// <summary>
        /// A light refresh (no metadata or image downloads) so Emby notices the new theme.mp3 without a library scan.
        /// </summary>
        private void QueueRefresh(BaseItem series)
        {
            try
            {
                var refresh = new MetadataRefreshOptions(this.fileSystem)
                {
                    MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                    ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                    ReplaceAllMetadata = false,
                    ReplaceAllImages = false,
                };
                this.providerManager.QueueRefresh(series.InternalId, refresh, RefreshPriority.Low);
            }
            catch (Exception ex)
            {
                this.logger.Warn("Could not queue a refresh for {0}: {1}", series.Name, ex.Message);
            }
        }
    }

    public sealed class LibraryInfo
    {
        public string Id { get; set; }

        public string Name { get; set; }

        public string CollectionType { get; set; }
    }
}
