using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyTheme.Core;
using EmbyTheme.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace EmbyTheme.Tasks
{
    /// <summary>
    /// Looks up themes for series that don't have one, up to the per-run limit. Series never checked go first (newest
    /// added first), then misses and failures whose retry interval has passed (oldest check first).
    /// </summary>
    public class DownloadThemesTask : IScheduledTask
    {
        private readonly ILibraryManager libraryManager;
        private readonly ILibraryMonitor libraryMonitor;
        private readonly IProviderManager providerManager;
        private readonly IFileSystem fileSystem;
        private readonly ILogger logger;

        public DownloadThemesTask(ILibraryManager libraryManager, ILibraryMonitor libraryMonitor, IProviderManager providerManager, IFileSystem fileSystem, ILogManager logManager)
        {
            this.libraryManager = libraryManager;
            this.libraryMonitor = libraryMonitor;
            this.providerManager = providerManager;
            this.fileSystem = fileSystem;
            this.logger = logManager.GetLogger(Plugin.PluginName);
        }

        public string Name => "Download TV theme songs";

        public string Key => "EmbyThemeDownload";

        public string Description => "Finds series without a theme song and saves theme.mp3 from Plex's theme server.";

        public string Category => Plugin.PluginName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerInterval, IntervalTicks = TimeSpan.FromHours(6).Ticks },
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var plugin = Plugin.Instance ?? throw new InvalidOperationException(Plugin.PluginName + " is not loaded");
            if (plugin.IsRunning)
            {
                this.logger.Info("A theme download is already running; skipping this one");
                return;
            }

            plugin.IsRunning = true;
            try
            {
                await this.RunAsync(plugin, cancellationToken, progress).ConfigureAwait(false);
            }
            finally
            {
                plugin.IsRunning = false;
            }
        }

        private async Task RunAsync(Plugin plugin, CancellationToken ct, IProgress<double> progress)
        {
            var options = plugin.Options;
            var store = plugin.Store;
            var service = new ThemeService(this.libraryManager, this.libraryMonitor, this.providerManager, this.fileSystem, this.logger);
            var tmdb = new TmdbClient(options.TmdbApiKey);
            var summary = new RunSummary { StartedUtc = DateTime.UtcNow };
            var now = DateTime.UtcNow;

            var series = service.GetSeriesInScope(options);
            summary.SeriesInScope = series.Length;

            // When covering every library, forget series that are gone so the counts match the library. (With a
            // narrower choice, other libraries' history is kept in case they're switched back on.)
            if (options.LibraryIds.Length == 0)
            {
                var inScope = new HashSet<long>(series.Select(s => s.InternalId));
                foreach (var gone in store.Data.Series.Keys.Where(k => !inScope.Contains(k)).ToList())
                {
                    store.Data.Series.TryRemove(gone, out _);
                }
            }

            // Cheap pass with no I/O: register new series, and mark ones Emby already knows have a theme.
            foreach (var show in series)
            {
                var entry = store.GetOrAdd(show.InternalId);
                ThemeService.Describe(show, entry);
                if (entry.Status == ThemeStatus.Pending && show.ThemeSongIds != null && show.ThemeSongIds.Length > 0)
                {
                    entry.Status = ThemeStatus.Existing;
                    entry.CheckedUtc = now;
                }
            }

            var due = series
                .Select(s => (Series: s, Entry: store.Get(s.InternalId)))
                .Where(x => ThemeRules.IsDue(x.Entry, now, options.RetryMissingDays, options.RetryFailedHours))
                .OrderBy(x => x.Entry.CheckedUtc.HasValue ? 1 : 0)
                .ThenBy(x => x.Entry.CheckedUtc ?? DateTime.MinValue)
                .ToList();

            this.logger.Info(
                "{0} series in scope, {1} due for a lookup; this run looks up at most {2}{3}",
                series.Length,
                due.Count,
                options.MaxLookupsPerRun,
                tmdb.IsConfigured ? string.Empty : " (no TMDB key: series with only a TMDB id are skipped)");

            var lookups = 0;
            var processed = 0;
            foreach (var (show, entry) in due)
            {
                ct.ThrowIfCancellationRequested();
                if (lookups >= options.MaxLookupsPerRun)
                {
                    break;
                }

                SeriesOutcome outcome;
                try
                {
                    outcome = await service.ProcessAsync(show, entry, tmdb, options, ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    // TMDB rejected the key: stop using it this run, and retry this series without it.
                    this.logger.Error("{0}; TMDB lookups are off for the rest of this run", ex.Message);
                    tmdb = new TmdbClient(null);
                    outcome = await service.ProcessAsync(show, entry, tmdb, options, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    this.logger.ErrorException("Theme lookup failed for {0}", ex, show.Name);
                    entry.Status = ThemeStatus.Failed;
                    entry.CheckedUtc = DateTime.UtcNow;
                    entry.Error = ex.Message;
                    entry.Attempts++;
                    outcome = new SeriesOutcome { Status = ThemeStatus.Failed, Detail = ex.Message };
                }

                processed++;
                summary.Checked++;
                switch (outcome.Status)
                {
                    case ThemeStatus.Saved: summary.Saved++; break;
                    case ThemeStatus.NotFound: summary.NotFound++; break;
                    case ThemeStatus.NoId: summary.NoId++; break;
                    case ThemeStatus.Existing: summary.AlreadyHadTheme++; break;
                    case ThemeStatus.Failed:
                        summary.Failed++;
                        if (outcome.WriteFailed)
                        {
                            // Read-only folders are reported once each in the summary, not once per series.
                            this.logger.Debug("{0}: {1}", show.Name, outcome.Detail);
                        }
                        else
                        {
                            this.logger.Warn("{0}: {1}", show.Name, outcome.Detail);
                        }

                        break;
                }

                if (outcome.UsedLookup)
                {
                    lookups++;
                    if (options.RequestDelayMs > 0)
                    {
                        await Task.Delay(options.RequestDelayMs, ct).ConfigureAwait(false);
                    }
                }

                progress.Report(100.0 * Math.Min(lookups, options.MaxLookupsPerRun) / Math.Max(1, Math.Min(due.Count, options.MaxLookupsPerRun)));
                if (processed % 100 == 0)
                {
                    store.Save();
                }
            }

            summary.Waiting = Math.Max(0, due.Count - processed);
            if (service.UnwritableFolders.Count > 0)
            {
                summary.Note = "Emby can't write to: " + string.Join(", ", service.UnwritableFolders) + ". Series there are retried later.";
            }

            summary.FinishedUtc = DateTime.UtcNow;
            store.Data.LastRun = summary;
            store.Save();
            progress.Report(100);

            this.logger.Info(
                "Theme run done: {0} checked, {1} saved, {2} not on Plex, {3} without a usable id, {4} already had one, {5} failed; {6} still waiting for a later run",
                summary.Checked,
                summary.Saved,
                summary.NotFound,
                summary.NoId,
                summary.AlreadyHadTheme,
                summary.Failed,
                summary.Waiting);
        }
    }
}
