# Engineering Conventions

These are the coding standards the code-review process checks against (NFR-04, DoD #13). The [constitution](constitution.md) overrides them: if this file, a design doc or the roadmap contradicts it, the doc is changed, not the constitution.

## Repository

- Trunk-based on `main`, with short-lived branches `feat/…`, `fix/…`, `docs/…`.
- Commits use the format `type(scope): description [REQ]`. `type` is a Conventional Commits type (`feat`, `fix`, `docs`, `test`, `refactor`, `chore`, `ci`, `perf`), `scope` is a module name (`identity`, `registry`, `routing`, `inference`, `usage`, `evaluation`, `admin`, `configuration`, `telemetry`) or one of `api`, `worker`, `migrator`, `adapters`, `web`, `deploy`, `load`, `ci`, `docs`, `tests`, and `REQ` is at least one requirement identifier from the PRD (`FR-04`, `NFR-01`, `DoD-10`). Example: `feat(routing): rank candidates by worst-case cost [FR-04]`. Repository-wide work with no specific requirement references NFR-04 (maintainability).
- Changes to `docs/constitution.md` go in a commit of their own, containing nothing else.
- `.githooks/pre-commit` runs `dotnet format --verify-no-changes` and `prettier --check` on the staged files. Enable it once per clone with `git config core.hooksPath .githooks`.
- Every PR states which FR/NFR it advances, and updates docs in the same PR when it changes behaviour described here. A design change that meets the ADR criteria adds an ADR.
- Every bug fix includes a regression test that fails without the fix. The test name or a comment references the bug.

## .NET

- `Directory.Build.props`: `net10.0`, `<Nullable>enable`, `<TreatWarningsAsErrors>true`, `<AnalysisLevel>latest-recommended</AnalysisLevel>`, `<EnforceCodeStyleInBuild>true`, `<InvariantGlobalization>true`.
- Central package management (`Directory.Packages.props`). A new dependency (NuGet or npm) is added only after its written justification is in the plan: the [dependency table](architecture.md#dependency-justifications) or the roadmap task that introduces it. A reason in the PR description alone is not enough.
- Endpoints are thin: they call an application service and never a `DbContext`, and hold no business logic ([architecture.md §3](architecture.md#module-structure)).
- Formatting: `.editorconfig` + `dotnet format --verify-no-changes` in CI.
- Use the vocabulary in `CONTEXT.md` for type and member names: `Attempt` not `ProviderCall`, `ProviderModel` not `Model`, `EstimatedCost` not `Cost`.
- Types are `sealed` by default, `record` for immutable data, and everything outside `Contracts/` is `internal`.
- Async all the way, with a `CancellationToken` as the last parameter on every async method. No `.Result` and no `.Wait()`.
- Expected failures return `Result<T>` with an error code from the closed set. Exceptions are for bugs and infrastructure faults.
- Use `TimeProvider` instead of `DateTime.UtcNow`, and `IGuidGenerator` (UUIDv7) instead of `Guid.NewGuid()`, so tests are deterministic.
- Logging uses `[LoggerMessage]` source-generated methods only. String interpolation in log calls is never allowed.
- EF Core: one `DbContext` per module in `Infrastructure/`, explicit snake_case table and column names (no naming-convention package), migrations in `Infrastructure/Migrations` with the history table `<schema>.__ef_migrations_history`. EF scaffolds migration classes as `public`; they are exempt from the "only Contracts is public" architecture rule and from analyzers (`generated_code` in `.editorconfig`). `AsNoTracking()` for reads, explicit `Include` (no lazy loading), migrations reviewed as SQL (`dotnet ef migrations script`) in the PR.
- No `HttpClient` resilience handlers on Adapter clients (ADR-0004).

## TypeScript / Vue

- `strict: true`, `noUncheckedIndexedAccess: true`. ESLint (`@vue/eslint-config-typescript`) + Prettier. `vue-tsc --noEmit` in CI.
- Server state lives in TanStack Query only, never copied into Pinia.
- The generated API types are the only source of response shapes. No hand-written DTO interfaces.

## CI (GitHub Actions)

`ci.yml` on push/PR:
1. `dotnet format --verify-no-changes`
2. `dotnet build -c Release` (warnings = errors, analyzers)
3. Unit + architecture + adapter contract tests
4. Integration + failure-injection tests (Docker available on the runner)
5. Web: `pnpm lint`, `vue-tsc`, `vitest`, OpenAPI client drift check
6. `gitleaks` (full history) on every push and PR. CodeQL (`codeql.yml`: C# with a manual `dotnet build` of the `.slnx`, JS/TS with no build, `security-extended` queries) on PRs to main, pushes to main and weekly
7. Docker image builds (no push on PRs), `docker compose config`, and `shellcheck` over every script in `scripts/`, `load/` and `.githooks/`

`nightly.yml` (nightly, on pushes to main and on demand): Playwright smoke E2E and axe against compose through `scripts/e2e.sh`, with the report, traces and Compose logs uploaded on failure. `load.yml` (nightly and on demand) runs a short k6 smoke load and uploads its results. Not added yet: the dependency vulnerability scan (`dotnet list package --vulnerable`, `pnpm audit`).

## Definition of done for a task

The code compiles with no warnings and tests are added at the right level ([testing.md](testing.md)): a unit test for every business rule, a happy-path integration test for every new endpoint, and a regression test for every bug fix. Validation errors return 400 Problem Details. Any docs, error-code tables and the OpenAPI document that the change affects are updated. No secrets or content in logs. If the change touches the request path, the gateway-overhead metric has not regressed.

## Review checklist

The reviewer checks a diff against these, in addition to the rules above. Each line names its source of truth.

- **Constitution** ([constitution.md](constitution.md)): a unit test per business rule, a `[HappyPath]` integration test per endpoint, 400 Problem Details for validation errors, a regression test per bug fix, a written justification for each new dependency ([architecture.md](architecture.md#dependency-justifications)).
- **Vocabulary** ([CONTEXT.md](../CONTEXT.md)): domain terms used exactly; a word under _Avoid_ is a finding, in code, tests and docs alike.
- **ADRs** ([adr/](adr/)): a diff that reverses a decision needs a superseding ADR.
- **Errors**: every new error code is in the closed set of [api.md §2](api.md#2-error-codes) and has a `[TriggersError]` test.
- **Providers**: tests reach Providers only through the Mock Provider or WireMock fixtures, never a real external Provider.
- **Waiting in tests** ([testing.md §1](testing.md#1-projects-and-what-goes-where)): a test waits for a state with a deadline, never for a fixed time.
- **Docs describe tested behaviour**: a sentence the diff adds to a doc that describes runtime behaviour (a header, a limit, a rule) names or is covered by a test that proves it. A promise with no test is a finding.
