using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.ContentHider.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ContentHider;

/// <summary>
/// The main plugin.
/// </summary>
[SuppressMessage("Naming", "CA1724:Type names should not match namespaces", Justification = "Standard Jellyfin plugin entry point name.")]
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Content Hider";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("3867d4a9-959b-476d-b1a6-a912a4741924");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the resolved on-disk file path for the isolated SQLite database.
    /// </summary>
    public string DatabaseFilePath
    {
        get
        {
            var custom = Configuration.CustomDatabasePath;
            if (!string.IsNullOrWhiteSpace(custom))
            {
                return custom.Trim();
            }

            return Path.Combine(DataFolderPath, "content_blocks.sqlite");
        }
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }
}
