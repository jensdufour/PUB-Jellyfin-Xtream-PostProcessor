using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.XtreamPostProcessor.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.XtreamPostProcessor;

/// <summary>
/// Xtream post-processing plugin.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Gets the stable plugin identifier.
    /// </summary>
    public static readonly Guid PluginId = Guid.Parse("d9f0f3d1-2bb9-4eed-a96e-5344f4e683a9");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <param name="xmlSerializer">Jellyfin XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the active plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Xtream Post Processor";

    /// <inheritdoc />
    public override string Description => "Enriches and normalizes Xtream-backed Jellyfin libraries after synchronization.";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                GetType().Namespace)
        };
    }
}
