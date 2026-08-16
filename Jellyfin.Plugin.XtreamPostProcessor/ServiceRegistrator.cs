using Jellyfin.Plugin.XtreamPostProcessor.Services;
using Jellyfin.Plugin.XtreamPostProcessor.State;
using Jellyfin.Plugin.XtreamPostProcessor.Sync;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.XtreamPostProcessor;

/// <summary>
/// Registers plugin services with Jellyfin.
/// </summary>
public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<SyncHistoryReader>();
        serviceCollection.AddSingleton<EnrichmentStateReader>();
        serviceCollection.AddSingleton<LibraryAuditService>();
        serviceCollection.AddSingleton<LibraryWriteService>();
        serviceCollection.AddSingleton<AuditReportWriter>();
        serviceCollection.AddSingleton<DeferredTaskScheduler>();
        serviceCollection.AddHostedService<XtreamSyncWatcher>();
    }
}
