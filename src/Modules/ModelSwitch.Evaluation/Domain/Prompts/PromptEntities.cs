namespace ModelSwitch.Evaluation.Domain.Prompts;

/// <summary>A named prompt of one Tenant whose content lives in immutable Prompt Versions.</summary>
internal sealed class Prompt
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public required string Name { get; init; }

    /// <summary>The newest Prompt Version, or 0 before the first one. Moved only by an atomic increment.</summary>
    public int LatestVersion { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>An immutable template that turns a Case's input into messages (FR-11). Rows are only ever inserted.</summary>
internal sealed class PromptVersion
{
    public Guid PromptId { get; init; }

    public int Version { get; init; }

    /// <summary><c>{"messages":[{"role","content"}],"responseFormat"?}</c>, as <see cref="PromptTemplateRules"/> normalized it.</summary>
    public required string TemplateJson { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public Guid? CreatedByAdminId { get; init; }
}
