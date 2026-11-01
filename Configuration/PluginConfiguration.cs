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
        MagicPlaylistName = "[{username}: Hide Content]";
        DefaultPauseDurationMinutes = 60;
        HideEmptyCollections = true;
    }

    /// <summary>
    /// Gets or sets the default display name template for newly provisioned per-user Magic Playlists.
    /// Supports <c>{username}</c> placeholder substitution.
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
}
