namespace ModelSwitch.SharedKernel.Persistence;

/// <summary>
/// Applies one module's EF Core migrations to its own schema. Only the Migrator host resolves these:
/// api and worker never migrate on startup (architecture.md section 2).
/// </summary>
public interface IModuleMigrator
{
    /// <summary>The PostgreSQL schema the module owns, e.g. <c>identity</c>.</summary>
    string Schema { get; }

    /// <summary>Creates the schema if missing and applies pending migrations. Returns the migration ids applied.</summary>
    Task<IReadOnlyList<string>> MigrateAsync(CancellationToken cancellationToken);
}
