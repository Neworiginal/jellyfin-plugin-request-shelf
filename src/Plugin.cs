using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace RequestShelf;

public sealed class PlexAccountLink
{
    public Guid JellyfinUserId { get; set; }
    public int SeerrUserId { get; set; }
    public long PlexId { get; set; }
}

public sealed class Configuration : BasePluginConfiguration
{
    public string SeerrUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string QBitUrl { get; set; } = "";
    public string QBitUsername { get; set; } = "";
    public string QBitPassword { get; set; } = "";
    public string QBitCategories { get; set; } = "";
    public bool ShowDownloadsLibrary { get; set; } = true;
    public bool DirectArrRequests { get; set; } = false;
    public string RadarrUrl { get; set; } = "";
    public string RadarrApiKey { get; set; } = "";
    public string SonarrUrl { get; set; } = "";
    public string SonarrApiKey { get; set; } = "";
    public PlexAccountLink[] PlexLinks { get; set; } = [];
    public Guid[] EnabledUsers { get; set; } = [];
}

public sealed class Plugin : BasePlugin<Configuration>, IHasWebPages
{
    public static Plugin Instance { get; private set; } = null!;
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;
    public override Guid Id => new("2cb52794-23e3-4181-80e3-d43d18fde3a9");
    public override string Name => "Request Shelf";
    public override string Description => "Search for a title, request missing movies and seasons through Seerr, and see live download progress.";
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new() {
            Name = "RequestShelfDownloads",
            DisplayName = "Downloads",
            EmbeddedResourcePath = "RequestShelf.downloads.html",
            EnableInMainMenu = true,
            MenuIcon = "download"
        },
        new() { Name = Name, EmbeddedResourcePath = "RequestShelf.settings.html" }
    ];
}

public sealed class Services : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<Seerr>();
        services.AddSingleton<QBit>();
        services.AddSingleton<ArrClient>();
        services.AddSingleton<CardStore>();
        services.AddScoped<RequestFilter>();
        services.Configure<MvcOptions>(o => o.Filters.AddService<RequestFilter>());
    }
}
