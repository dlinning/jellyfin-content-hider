using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ContentHider.Configuration;

/// <summary>
/// Plugin configuration for Content Hider.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        EnableWebUiInjection = true;
        EnableMagicPlaylist = true;
        MagicPlaylistName = "[Hide Content]";
        DefaultPauseDurationMinutes = 60;
        HideEmptyCollections = true;
        NormalizeEpisodesToSeries = true;
        CustomDatabasePath = string.Empty;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the Jellyfin Web UI client script is injected in memory.
    /// </summary>
    public bool EnableWebUiInjection { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether per-user Magic Playlists are provisioned for TV and mobile apps.
    /// </summary>
    public bool EnableMagicPlaylist { get; set; }

    /// <summary>
    /// Gets or sets the display name of the per-user Magic Playlist.
    /// </summary>
    public string MagicPlaylistName { get; set; }

    /// <summary>
    /// Gets or sets the default duration in minutes when pausing blocking (0 = manual re-enable only).
    /// </summary>
    public int DefaultPauseDurationMinutes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether collections (BoxSets) with 100% blocked items should be hidden automatically.
    /// </summary>
    public bool HideEmptyCollections { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether blocking an episode or season blocks its parent TV Series by default.
    /// </summary>
    public bool NormalizeEpisodesToSeries { get; set; }

    /// <summary>
    /// Gets or sets an optional custom file path on disk for the standalone SQLite database.
    /// When empty, defaults to &lt;DataFolderPath&gt;/content_blocks.sqlite.
    /// </summary>
    public string CustomDatabasePath { get; set; }
}
