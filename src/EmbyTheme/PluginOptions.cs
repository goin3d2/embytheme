using System;
using System.Linq;

namespace EmbyTheme
{
    /// <summary>All settings. Stored as settings.json in the plugin's data folder and edited on the plugin's settings page.</summary>
    public class PluginOptions
    {
        /// <summary>Library ids (Emby internal ids) to cover. Empty means every library with TV series.</summary>
        public string[] LibraryIds { get; set; } = Array.Empty<string>();

        /// <summary>How many series to look up per run. Keeps a 28k-series library from hitting Plex all at once.</summary>
        public int MaxLookupsPerRun { get; set; } = 1000;

        /// <summary>Pause between theme-server requests, in milliseconds.</summary>
        public int RequestDelayMs { get; set; } = 250;

        /// <summary>Days before a series with no theme found (or no usable id) is tried again.</summary>
        public int RetryMissingDays { get; set; } = 30;

        /// <summary>Hours before a failed download or write is tried again (doubles on repeat failures, max 7 days).</summary>
        public int RetryFailedHours { get; set; } = 12;

        /// <summary>Optional. Lets series that only have a TMDB id be looked up on Plex by their TVDB id.</summary>
        public string TmdbApiKey { get; set; } = string.Empty;

        /// <summary>Ask Emby to refresh each series after saving its theme, so the theme plays without a library scan.</summary>
        public bool RefreshAfterSave { get; set; } = true;

        /// <summary>Stop a run after this many write failures in a row (usually a read-only folder or mount). 0 = never.</summary>
        public int StopAfterWriteFailures { get; set; } = 25;

        public PluginOptions Normalize()
        {
            this.LibraryIds = (this.LibraryIds ?? Array.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct()
                .ToArray();
            this.MaxLookupsPerRun = Clamp(this.MaxLookupsPerRun, 1, 100000);
            this.RequestDelayMs = Clamp(this.RequestDelayMs, 0, 10000);
            this.RetryMissingDays = Clamp(this.RetryMissingDays, 1, 365);
            this.RetryFailedHours = Clamp(this.RetryFailedHours, 1, 24 * 7);
            this.StopAfterWriteFailures = Clamp(this.StopAfterWriteFailures, 0, 100000);
            this.TmdbApiKey = (this.TmdbApiKey ?? string.Empty).Trim();
            return this;
        }

        private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
    }
}
