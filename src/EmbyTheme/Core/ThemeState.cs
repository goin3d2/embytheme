using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmbyTheme.Core
{
    public enum ThemeStatus
    {
        /// <summary>Not looked at yet.</summary>
        Pending,

        /// <summary>We downloaded and saved a theme.</summary>
        Saved,

        /// <summary>The series already had a theme (from you or another tool); left alone.</summary>
        Existing,

        /// <summary>No source had a theme. Retried after the "missing" interval.</summary>
        NotFound,

        /// <summary>No TVDB id, and none could be resolved from TMDB. Retried after the "missing" interval.</summary>
        NoId,

        /// <summary>Download or write failed (network error, read-only folder...). Retried after the "failed" interval.</summary>
        Failed,

        /// <summary>You told the plugin to leave this series alone.</summary>
        Skipped,
    }

    /// <summary>What the plugin knows about one series. Keyed by Emby's internal item id.</summary>
    public sealed class SeriesThemeEntry
    {
        public string Name { get; set; }

        public string Tvdb { get; set; }

        public string Tmdb { get; set; }

        /// <summary>TVDB id found through TMDB, cached so TMDB is asked once.</summary>
        public string ResolvedTvdb { get; set; }

        public bool TmdbResolveTried { get; set; }

        public ThemeStatus Status { get; set; } = ThemeStatus.Pending;

        /// <summary>"plex", "url" (set by hand) or empty.</summary>
        public string Source { get; set; }

        public DateTime? CheckedUtc { get; set; }

        public string Error { get; set; }

        public int Attempts { get; set; }

        [JsonIgnore]
        public string EffectiveTvdb => !string.IsNullOrEmpty(this.Tvdb) ? this.Tvdb : this.ResolvedTvdb;
    }

    public sealed class RunSummary
    {
        public DateTime StartedUtc { get; set; }

        public DateTime FinishedUtc { get; set; }

        public int SeriesInScope { get; set; }

        public int Checked { get; set; }

        public int Saved { get; set; }

        public int NotFound { get; set; }

        public int NoId { get; set; }

        public int Failed { get; set; }

        public int AlreadyHadTheme { get; set; }

        public int Waiting { get; set; }

        public bool StoppedEarly { get; set; }

        public string Note { get; set; }
    }

    public sealed class ThemeStateData
    {
        public ConcurrentDictionary<long, SeriesThemeEntry> Series { get; set; } = new ConcurrentDictionary<long, SeriesThemeEntry>();

        public RunSummary LastRun { get; set; }
    }

    /// <summary>theme-state.json in the plugin's data folder: per-series results and the last run's summary.</summary>
    public sealed class ThemeStore
    {
        public static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
        };

        private readonly object sync = new object();

        private ThemeStore(string path, ThemeStateData data)
        {
            this.FilePath = path;
            this.Data = data;
        }

        public string FilePath { get; }

        public ThemeStateData Data { get; }

        public static ThemeStore Load(string path, Action<string, Exception> onError = null)
        {
            try
            {
                if (File.Exists(path))
                {
                    var data = JsonSerializer.Deserialize<ThemeStateData>(File.ReadAllText(path), Json);
                    if (data != null)
                    {
                        data.Series ??= new ConcurrentDictionary<long, SeriesThemeEntry>();
                        return new ThemeStore(path, data);
                    }
                }
            }
            catch (Exception ex)
            {
                onError?.Invoke(path, ex);
            }

            return new ThemeStore(path, new ThemeStateData());
        }

        public SeriesThemeEntry GetOrAdd(long id) => this.Data.Series.GetOrAdd(id, _ => new SeriesThemeEntry());

        public SeriesThemeEntry Get(long id) => this.Data.Series.TryGetValue(id, out var e) ? e : null;

        public Dictionary<string, int> CountByStatus()
        {
            var counts = Enum.GetNames(typeof(ThemeStatus)).ToDictionary(n => n, _ => 0);
            foreach (var entry in this.Data.Series.Values)
            {
                counts[entry.Status.ToString()]++;
            }

            return counts;
        }

        /// <summary>Set matching entries back to Pending so the next run looks at them again. Returns how many.</summary>
        public int Reset(Func<SeriesThemeEntry, bool> match)
        {
            var n = 0;
            foreach (var entry in this.Data.Series.Values.Where(match))
            {
                entry.Status = ThemeStatus.Pending;
                entry.CheckedUtc = null;
                entry.Error = null;
                entry.Attempts = 0;
                entry.TmdbResolveTried = false;
                n++;
            }

            return n;
        }

        public void Save()
        {
            lock (this.sync)
            {
                var dir = Path.GetDirectoryName(this.FilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var tmp = this.FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this.Data, Json));
                File.Move(tmp, this.FilePath, true);
            }
        }
    }
}
