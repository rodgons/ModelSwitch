namespace ModelSwitch.Routing.Contracts;

/// <summary>Every Model Alias with the latest Policy Version of the policy it points to.</summary>
/// <param name="AliasToPolicy">Alias name to policy id.</param>
/// <param name="Policies">Policy id to its latest Policy Version.</param>
public sealed record PolicyCatalog(
    IReadOnlyDictionary<string, Guid> AliasToPolicy,
    IReadOnlyDictionary<Guid, PolicySnapshot> Policies);

/// <summary>Bulk read of all aliases and policies for the runtime configuration snapshot (architecture.md section 6).</summary>
public interface IPolicyCatalogSource
{
    Task<PolicyCatalog> LoadAsync(CancellationToken cancellationToken);
}
