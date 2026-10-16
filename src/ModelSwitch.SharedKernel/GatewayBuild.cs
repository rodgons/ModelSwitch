using System.Reflection;

namespace ModelSwitch.SharedKernel;

/// <summary>
/// The gateway build that is running: the assembly informational version the SDK stamps at build time (the package version plus,
/// in a git checkout, <c>+&lt;commit sha&gt;</c>). Every ModelSwitch assembly is built from the same commit, so the shared kernel's
/// stamp stands for the whole build. Evaluation Runs record it in their reproducibility record (evaluation.md section 6).
/// </summary>
public static class GatewayBuild
{
    public static string Version { get; } =
        typeof(GatewayBuild).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(GatewayBuild).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
