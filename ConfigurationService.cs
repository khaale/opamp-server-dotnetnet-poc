using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Opamp.Proto; // Namespace from generated code

namespace OpampServerDemo.Services;

public interface IConfigurationService
{
    AgentRemoteConfig GetCurrentConfig();
}

public class ConfigurationService : IConfigurationService
{
    private AgentRemoteConfig _currentConfig;
    private readonly ILogger<ConfigurationService> _logger;

    // --- Configuration Definition ---
    // In a real app, load this from a file, database, or config management system.
    // We'll define a simple YAML-like config for an OTel Collector.
    private const string ConfigFileName = "otel-collector-config.yaml";
    private const string ConfigFileContent = @"
receivers:
  otlp:
    protocols:
      grpc:
      http:

processors:
  batch:

exporters:
  logging:
    loglevel: debug

service:
  pipelines:
    traces:
      receivers: [otlp]
      processors: [batch]
      exporters: [logging]
";
    // -----------------------------

    public ConfigurationService(ILogger<ConfigurationService> logger)
    {
        _logger = logger;
        _currentConfig = LoadAndPrepareConfig();
        _logger.LogInformation("Initial configuration loaded. Hash: {ConfigHash}",
             ByteString.CopyFrom(_currentConfig.ConfigHash.ToByteArray()).ToBase64()); // Log hash for debugging
    }

    private AgentRemoteConfig LoadAndPrepareConfig()
    {
        // Create the structure OpAMP expects
        var configFile = new AgentConfigFile
        {
            Body = ByteString.CopyFromUtf8(ConfigFileContent),
            ContentType = "text/yaml" // Or application/x-yaml
        };

        var configMap = new AgentConfigMap();
        configMap.ConfigMap.Add(ConfigFileName, configFile); // Use the const filename as key

        // Calculate the hash (SHA256 is a reasonable choice)
        byte[] configBytes = configMap.ToByteArray(); // Serialize the map part
        byte[] hashBytes;
        using (var sha256 = SHA256.Create())
        {
            hashBytes = sha256.ComputeHash(configBytes);
        }

        return new AgentRemoteConfig
        {
            Config = configMap,
            ConfigHash = ByteString.CopyFrom(hashBytes)
        };
    }

    public AgentRemoteConfig GetCurrentConfig()
    {
        // In a real app, you might add logic here to reload the config if it changed.
        // For this demo, we return the initially loaded config.
        return _currentConfig;
    }

    // TODO: Add a method to reload the configuration and update _currentConfig
    // public void ReloadConfig() { ... update _currentConfig ... }
}