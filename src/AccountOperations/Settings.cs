using Azure.Core;
using Azure.Identity;
using Azure.Messaging.EventHubs;
using Microsoft.Extensions.Configuration;

namespace AccountOperations;

public static class Settings
{
    private static IConfiguration? configuration;

    public static TokenCredential Credential { get; } =
        Environment.GetEnvironmentVariable("APPSETTINGS_FILE") is null
            ? new DefaultAzureCredential() : new AzureCliCredential();

    public static void Configure(IConfigurationManager manager)
    {
        var localFile = Environment.GetEnvironmentVariable("APPSETTINGS_FILE");
        manager.AddJsonFile(localFile ?? "/mnt/keyvault/appsettings.json",
            optional: localFile is null, reloadOnChange: true);
        manager.AddEnvironmentVariables();
        configuration = manager;
    }

    public static string? Get(string name) => configuration?[name] ?? Environment.GetEnvironmentVariable(name);

    public static string Required(string name) => !string.IsNullOrWhiteSpace(Get(name))
        ? Get(name)! : throw new InvalidOperationException($"Missing setting {name}");

    public static EventHubConnectionOptions ConnectionOptions()
    {
        var configured = Get("EVENTHUB_TRANSPORT_TYPE");
        if (string.IsNullOrWhiteSpace(configured)) return new EventHubConnectionOptions();
        if (!Enum.TryParse<EventHubsTransportType>(configured, true, out var transport) ||
            !Enum.IsDefined(transport))
            throw new InvalidOperationException("EVENTHUB_TRANSPORT_TYPE must be AmqpTcp or AmqpWebSockets");
        return new EventHubConnectionOptions { TransportType = transport };
    }
}
