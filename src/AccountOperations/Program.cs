using AccountOperations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
Settings.Configure(builder.Configuration);
builder.Services.AddSingleton<IAccountStore, AccountStore>();
builder.Services.AddHttpClient<IGraphClient, GraphClient>();
builder.Services.AddHttpClient<ISharePointClient, SharePointClient>();
builder.Services.AddHttpClient<IFunctionClient, FunctionClient>(client =>
    client.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddHttpClient<ISharePointSiteStarterClient, SharePointSiteStarterClient>(client =>
    client.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddHttpClient<IResourceManagerClient, ResourceManagerClient>(client =>
    client.Timeout = TimeSpan.FromHours(2));
builder.Services.AddSingleton<IAccountEventPublisher, AccountPublishers>();
builder.Services.AddSingleton<IAccountEventHandler, AccountWorkflowHandler>();
builder.Services.AddHostedService<EventWorker>();
await builder.Build().RunAsync();
return Environment.ExitCode;
