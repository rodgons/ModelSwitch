using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ModelSwitch.SharedKernel.Hosting;

/// <summary>Startup validation of one deployment setting. A failure names the environment variable (FR-10).</summary>
public static class RequiredSetting
{
    public const string EnvironmentPrefix = "MODELSWITCH__";

    /// <summary>The environment variable that sets a configuration key, e.g. <c>Security:ApiKeyPepper</c> to <c>MODELSWITCH__Security__ApiKeyPepper</c>.</summary>
    public static string EnvironmentVariableFor(string configurationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationKey);
        return EnvironmentPrefix + configurationKey.Replace(":", "__", StringComparison.Ordinal);
    }

    /// <summary>
    /// Requires <paramref name="configurationKey"/> to be set and, when given, to satisfy <paramref name="validate"/>
    /// (which returns an error description, or null when the value is valid). Checked by <c>ValidateOnStart</c>.
    /// </summary>
    public static IServiceCollection RequireSetting(
        this IServiceCollection services,
        string configurationKey,
        Func<string, string?>? validate = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationKey);

        // Several modules may need the same setting (e.g. the connection string): validate it once.
        if (services.Any(d => d.ServiceType == typeof(SettingMarker) && d.ImplementationInstance is SettingMarker marker && marker.Key == configurationKey))
        {
            return services;
        }

        services.AddSingleton(new SettingMarker(configurationKey));
        services.AddOptions<SettingValue>(configurationKey)
            .Configure<IConfiguration>((setting, configuration) => setting.Value = configuration[configurationKey])
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SettingValue>>(new SettingValidator(configurationKey, validate));
        return services;
    }

    internal sealed record SettingMarker(string Key);

    internal sealed class SettingValue
    {
        public string? Value { get; set; }
    }

    private sealed class SettingValidator(string key, Func<string, string?>? validate) : IValidateOptions<SettingValue>
    {
        public ValidateOptionsResult Validate(string? name, SettingValue options)
        {
            if (!string.Equals(name, key, StringComparison.Ordinal))
            {
                return ValidateOptionsResult.Skip;
            }

            var variable = EnvironmentVariableFor(key);
            if (string.IsNullOrWhiteSpace(options.Value))
            {
                return ValidateOptionsResult.Fail($"{variable} is required but not set.");
            }

            var problem = validate?.Invoke(options.Value);
            return problem is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail($"{variable} is invalid: {problem}");
        }
    }
}
