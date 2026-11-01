using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.ContentHider.Filters;
using Jellyfin.Plugin.ContentHider.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ContentHider;

/// <summary>
/// Registers Content Hider services, MVC action filters, and user lifecycle event consumers
/// with Jellyfin's dependency injection container at server startup.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IContentBlockService, ContentBlockService>();
        serviceCollection.AddSingleton<IMagicPlaylistService, MagicPlaylistService>();

        serviceCollection.AddScoped<IEventConsumer<UserCreatedEventArgs>, UserLifecycleEventConsumer>();
        serviceCollection.AddScoped<IEventConsumer<UserDeletedEventArgs>, UserLifecycleEventConsumer>();

        serviceCollection.AddHostedService<MagicPlaylistHostedService>();

        serviceCollection.AddSingleton<MagicPlaylistActionFilter>();
        serviceCollection.AddSingleton<ContentHiderActionFilter>();

        serviceCollection.PostConfigure<MvcOptions>(options =>
        {
            options.Filters.AddService<MagicPlaylistActionFilter>(order: -10);
            options.Filters.AddService<ContentHiderActionFilter>(order: 10);
        });
    }
}
