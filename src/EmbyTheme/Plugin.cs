using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbyTheme.Core;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;

namespace EmbyTheme
{
    public class Plugin : BasePlugin, IHasWebPages, IHasThumbImage
    {
        public const string PluginName = "EmbyTheme";

        public static readonly JsonSerializerOptions SettingsJson = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private static readonly Guid PluginId = new Guid("29b19a83-f6f6-4b87-ba15-8f11cef45040");

        private readonly ILogger logger;
        private readonly object sync = new object();
        private PluginOptions options;
        private ThemeStore store;

        public Plugin(ILogManager logManager)
        {
            Instance = this;
            this.logger = logManager.GetLogger(PluginName);
            this.logger.Info("{0} loaded", PluginName);
        }

        public static Plugin Instance { get; private set; }

        public override string Description => "Downloads TV theme songs (theme.mp3) into series folders so they play while you browse.";

        public override Guid Id => PluginId;

        public override string Name => PluginName;

        public ILogger Logger => this.logger;

        public string DataDirectory
        {
            get
            {
                var dir = this.DataFolderPath;
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public PluginOptions Options
        {
            get
            {
                var current = this.options;
                if (current != null)
                {
                    return current;
                }

                lock (this.sync)
                {
                    return this.options ??= this.LoadOptions();
                }
            }
        }

        public ThemeStore Store
        {
            get
            {
                lock (this.sync)
                {
                    return this.store ??= ThemeStore.Load(
                        Path.Combine(this.DataDirectory, "theme-state.json"),
                        (path, ex) => this.logger.ErrorException("Could not read {0}; starting fresh", ex, path));
                }
            }
        }

        /// <summary>Set while the download task runs, so the settings page can show it and manual fetches can wait.</summary>
        public bool IsRunning { get; set; }

        private string SettingsPath => Path.Combine(this.DataDirectory, "settings.json");

        public void SaveOptions(PluginOptions newOptions)
        {
            newOptions.Normalize();
            lock (this.sync)
            {
                var tmp = this.SettingsPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(newOptions, SettingsJson));
                File.Move(tmp, this.SettingsPath, true);
                this.options = newOptions;
            }

            this.logger.Info("{0} settings saved", PluginName);
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "embytheme",
                    DisplayName = PluginName,
                    EmbeddedResourcePath = this.GetType().Namespace + ".Configuration.configPage.html",
                    IsMainConfigPage = true,
                },
                new PluginPageInfo
                {
                    Name = "embythemejs",
                    EmbeddedResourcePath = this.GetType().Namespace + ".Configuration.configPage.js",
                },
            };
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public Stream GetThumbImage()
        {
            return this.GetType().Assembly.GetManifestResourceStream("EmbyTheme.ThumbImage.png");
        }

        private PluginOptions LoadOptions()
        {
            try
            {
                if (File.Exists(this.SettingsPath))
                {
                    var loaded = JsonSerializer.Deserialize<PluginOptions>(File.ReadAllText(this.SettingsPath), SettingsJson);
                    if (loaded != null)
                    {
                        return loaded.Normalize();
                    }
                }
            }
            catch (Exception ex)
            {
                this.logger.ErrorException("Could not read {0}; using defaults", ex, this.SettingsPath);
            }

            return new PluginOptions();
        }
    }
}
