namespace ModelSwitch.SharedKernel;

/// <summary>Source of new identifiers. Always UUIDv7, so ids sort by creation time. Never call <c>Guid.NewGuid()</c>.</summary>
public interface IGuidGenerator
{
    Guid NewGuid();
}

internal sealed class GuidGenerator(TimeProvider time) : IGuidGenerator
{
    public Guid NewGuid() => Guid.CreateVersion7(time.GetUtcNow());
}
