using System;
using System.IO;
using System.Linq;

namespace EmbyTheme.Core
{
    /// <summary>Pure decisions and file checks, kept free of Emby types so they can be tested on their own.</summary>
    public static class ThemeRules
    {
        public const string ThemeFileName = "theme.mp3";
        public const string ThemeFolderName = "theme-music";

        /// <summary>Audio extensions Emby accepts for a theme file.</summary>
        public static readonly string[] AudioExtensions = { ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma" };

        private static readonly EnumerationOptions CaseInsensitive = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive };

        public const int MinThemeBytes = 16 * 1024;
        public const int MaxThemeBytes = 25 * 1024 * 1024;

        /// <summary>
        /// Whether a series is due for a lookup this run. New and Pending entries always are; misses and failures wait
        /// out their retry interval; Saved, Existing and Skipped never are (use Reset to change that).
        /// </summary>
        public static bool IsDue(SeriesThemeEntry entry, DateTime nowUtc, int retryMissingDays, int retryFailedHours)
        {
            if (entry == null || entry.Status == ThemeStatus.Pending)
            {
                return true;
            }

            if (entry.Status == ThemeStatus.Saved || entry.Status == ThemeStatus.Existing || entry.Status == ThemeStatus.Skipped)
            {
                return false;
            }

            if (entry.CheckedUtc == null)
            {
                return true;
            }

            var age = nowUtc - entry.CheckedUtc.Value;
            switch (entry.Status)
            {
                case ThemeStatus.NotFound:
                case ThemeStatus.NoId:
                case ThemeStatus.ReadOnly:
                    return age >= TimeSpan.FromDays(Math.Max(1, retryMissingDays));
                case ThemeStatus.Failed:
                    // Back off on repeated failures: 1x, 2x, 4x... the interval, capped at 7 days.
                    var hours = Math.Max(1, retryFailedHours) * Math.Pow(2, Math.Min(6, Math.Max(0, entry.Attempts - 1)));
                    return age >= TimeSpan.FromHours(Math.Min(hours, 24 * 7));
                default:
                    return false;
            }
        }

        /// <summary>True if the path is one of the excluded folders or inside one (case-insensitive, either slash).</summary>
        public static bool IsExcluded(string path, string[] excludedPaths)
        {
            if (string.IsNullOrEmpty(path) || excludedPaths == null || excludedPaths.Length == 0)
            {
                return false;
            }

            var p = NormalizePath(path);
            foreach (var raw in excludedPaths)
            {
                var prefix = NormalizePath(raw);
                if (prefix.Length > 0 && (p.Equals(prefix, StringComparison.OrdinalIgnoreCase) || p.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        public static string NormalizePath(string path) => (path ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');

        /// <summary>True if the folder already has a theme song Emby would pick up (theme.* audio, or a theme-music folder with audio).</summary>
        public static bool FolderHasTheme(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return false;
            }

            if (Directory.EnumerateFiles(folder, "theme.*", CaseInsensitive).Any(IsAudioFile))
            {
                return true;
            }

            var themeDir = Path.Combine(folder, ThemeFolderName);
            return Directory.Exists(themeDir) && Directory.EnumerateFiles(themeDir).Any(IsAudioFile);
        }

        public static bool IsAudioFile(string path)
        {
            var ext = Path.GetExtension(path);
            return AudioExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Sanity check on downloaded bytes before they land in a media folder: right size, and an MP3 (ID3 tag or an
        /// MPEG audio frame header). Stops an HTML error page or an empty body being saved as theme.mp3.
        /// </summary>
        public static bool LooksLikeMp3(byte[] data, out string reason)
        {
            if (data == null || data.Length < MinThemeBytes)
            {
                reason = $"too small ({data?.Length ?? 0} bytes)";
                return false;
            }

            if (data.Length > MaxThemeBytes)
            {
                reason = $"too large ({data.Length / 1024 / 1024} MB)";
                return false;
            }

            if (data[0] == (byte)'I' && data[1] == (byte)'D' && data[2] == (byte)'3')
            {
                reason = null;
                return true;
            }

            // Frame sync: 11 set bits, then a valid MPEG version and layer.
            if (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0 && (data[1] & 0x18) != 0x08 && (data[1] & 0x06) != 0)
            {
                reason = null;
                return true;
            }

            reason = "not an MP3";
            return false;
        }

        /// <summary>
        /// Whether files can be created in a folder: creates and deletes a tiny hidden probe file. Used once per
        /// library folder per run, so a read-only mount is found before anything is downloaded for it.
        /// </summary>
        public static bool CanWrite(string folder, out string error)
        {
            var probe = Path.Combine(folder, ".embytheme-probe-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                File.WriteAllBytes(probe, Array.Empty<byte>());
                File.Delete(probe);
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Write the theme next to the series' files. Written to a hidden temp name first and then renamed, so Emby
        /// never sees a half-written theme.mp3.
        /// </summary>
        public static string WriteTheme(string folder, byte[] data, bool overwrite)
        {
            var target = Path.Combine(folder, ThemeFileName);
            if (File.Exists(target) && !overwrite)
            {
                throw new IOException(target + " already exists");
            }

            var tmp = Path.Combine(folder, ".embytheme-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
            try
            {
                File.WriteAllBytes(tmp, data);
                File.Move(tmp, target, overwrite);
            }
            finally
            {
                if (File.Exists(tmp))
                {
                    try
                    {
                        File.Delete(tmp);
                    }
                    catch (IOException)
                    {
                    }
                }
            }

            return target;
        }
    }
}
