# PRD: ModelSwitch

## Self-Hosted AI Gateway and Observability Platform

**Version:** 1.0  
**Status:** Ready for implementation  
**Product type:** Self-hosted developer infrastructure  
**Primary users:** Software developers, platform engineers, and teams building AI-powered applications

---

## 1. Product Overview

### 1.1 Summary

ModelSwitch is a self-hosted AI gateway that provides a unified API for interacting with multiple Large Language Model (LLM) providers.

It allows developers to route inference requests across providers, configure fallback policies, enforce usage limits, track costs, inspect request traces, and evaluate model quality through reproducible benchmarks.

The platform separates application code from provider-specific implementations and makes AI workloads observable, measurable, and more resilient.

### 1.2 Problem Statement

Applications integrating directly with LLM providers often accumulate provider-specific logic throughout their codebases. This makes switching models expensive and creates duplicated implementations of retry handling, cost tracking, rate limiting, and observability.

Teams also struggle to determine whether a model or prompt change improves quality enough to justify its cost.

ModelSwitch addresses these problems through a centralized inference API, configurable routing policies, reliable request execution, and evaluation tooling.

### 1.3 Product Goals

1. Provide a unified API for supported LLM providers.
2. Enable configurable, deterministic model routing.
3. Handle transient provider failures without uncontrolled retries.
4. Track request-level token usage, latency, failures, and estimated costs.
5. Enforce tenant-level authentication, rate limits, and usage budgets.
6. Compare models and routing policies using repeatable evaluation datasets.
7. Provide a reproducible self-hosted deployment.
8. Demonstrate production-oriented backend and applied AI engineering practices.

### 1.4 Non-Goals

The MVP will not:

- Train or fine-tune foundation models.
- Provide a general-purpose autonomous AI agent.
- Guarantee compatibility with every provider-specific feature.
- Provide a managed cloud-hosted SaaS offering.
- Implement enterprise SSO, SAML, or SCIM.
- Include semantic caching.
- Support image, audio, video, or multimodal inference.
- Guarantee exactly-once execution of external provider calls.
- Replace a provider's authoritative billing system.
- Introduce Kubernetes or a microservices architecture without a demonstrated need.

---

## 2. Target Users and Use Cases

### 2.1 Primary Persona: Application Developer

A developer building an AI-powered application needs one stable API for inference without embedding provider-specific logic into the application.

**Primary workflow:**

1. Create an API key.
2. Configure an available model alias.
3. Send inference requests to ModelSwitch.
4. Inspect request history and usage.
5. Change routing configuration without modifying the client application.

### 2.2 Secondary Persona: Platform Engineer

A platform engineer needs to manage provider reliability, tenant limits, operational performance, and model availability.

**Primary workflow:**

1. Configure providers and model capabilities.
2. Define routing and fallback policies.
3. Configure rate limits and budgets.
4. Inspect provider errors and latency.
5. Diagnose failures using request IDs and traces.

### 2.3 Secondary Persona: AI Engineer

An AI engineer needs to compare models and prompts against a known evaluation dataset.

**Primary workflow:**

1. Create or upload an evaluation dataset.
2. Select models and prompt versions.
3. Run the same test cases against each configuration.
4. Compare quality, cost, latency, and failure rates.
5. Use the results to improve routing policies.

---

## 3. MVP Scope

The MVP consists of six functional modules.

### 3.1 Inference API

- OpenAI-compatible chat-completion request format where supported.
- Non-streaming inference.
- Server-Sent Events (SSE) streaming.
- Model aliases independent of provider-specific model IDs.
- Structured output support where the provider supports the required capabilities.
- Request validation and normalized error responses.
- Request IDs and correlation IDs.
- Cancellation and timeout propagation.

### 3.2 Provider Management

- Register and configure supported providers.
- Store provider credentials securely.
- Configure model identifiers, capabilities, and pricing.
- Enable or disable models.
- Perform connectivity checks.
- Normalize provider responses and errors.
- Track provider health independently of application health.

Initial provider implementations:

- An OpenAI-compatible remote provider.
- An Ollama-compatible local provider.

Provider adapters must be replaceable and independently testable.

### 3.3 Routing and Reliability

- Static model routing.
- Rule-based routing by task type and model capability.
- Cost-aware selection among eligible models.
- Configurable fallback models.
- Bounded retries for eligible transient failures.
- Circuit breakers.
- Request deadlines.
- Per-tenant concurrency and request limits.
- Configuration changes without redeployment.

### 3.4 Usage and Observability

- Request history and request detail views.
- Provider-attempt history.
- Input and output token counts when available.
- Estimated cost and cost-estimation provenance.
- Latency, time to first token, and error metrics.
- Usage aggregation by tenant, model, provider, and time period.
- OpenTelemetry traces, metrics, and structured logs.
- Configurable retention for request metadata.

### 3.5 Tenant and Security Management

- Administrative login.
- Tenant creation and management.
- API-key creation, hashing, revocation, and expiration.
- Tenant-scoped model access.
- Rate limits and spending limits.
- Role-based administrative permissions.
- Audit records for sensitive administrative operations.

### 3.6 Evaluation Lab

- Versioned evaluation datasets.
- Structured expected outputs and task-specific scoring.
- Execution against selected models and prompt versions.
- Per-case result recording.
- Aggregate quality, latency, cost, and failure comparisons.
- Reproducible evaluation configurations.
- Exportable evaluation results.

---

## 4. Functional Requirements

Requirements marked **P0** are mandatory for the initial release. **P1** items may follow once the core release is stable.

### FR-01: Authentication and Authorization — P0

The system must authenticate administrative users and API clients independently.

- Administrative endpoints require an authenticated administrative session.
- Inference endpoints require a valid tenant API key.
- API keys must be stored as cryptographic hashes, not plaintext.
- API keys must be revocable and optionally expirable.
- Each API key belongs to one tenant and may have an associated permission scope.
- Tenant A must never access Tenant B's requests, usage, keys, or evaluations.
- Administrative actions must be auditable.

**Acceptance criteria:**

- Requests with invalid or revoked API keys return HTTP 401.
- Authenticated but unauthorized operations return HTTP 403.
- Cross-tenant access attempts are rejected.
- Raw API keys are shown only when created and cannot subsequently be retrieved.

### FR-02: Provider Abstraction — P0

The system must define a provider-neutral contract for supported inference operations.

The contract should cover:

- Model capabilities and metadata.
- Non-streaming completion.
- Streaming completion.
- Normalized usage information.
- Provider errors and retry classification.
- Cancellation and timeout behavior.
- Structured output capabilities where supported.

Provider-specific features must be represented explicitly. Unsupported features must produce clear errors rather than silently changing request semantics.

**Acceptance criteria:**

- At least two provider adapters are available.
- A client request does not require provider-specific application code.
- Each adapter passes a shared provider contract test suite.
- Provider-specific identifiers remain internal to routing and execution configuration.

### FR-03: Inference API — P0

Endpoint: `POST /v1/chat/completions`

The endpoint must accept:

- A model alias.
- Messages.
- Optional generation parameters.
- Optional structured-output configuration.
- Optional streaming configuration.

The endpoint must:

1. Authenticate the client.
2. Validate the request.
3. Resolve the model alias and routing policy.
4. Execute the selected provider request.
5. Return a normalized response.
6. Record the logical request and provider attempt.
7. Record usage and cost information when available.

The API must preserve cancellation signals and enforce configured request deadlines.

**Acceptance criteria:**

- Valid non-streaming requests return normalized responses.
- Valid streaming requests emit SSE events and a documented completion signal.
- Invalid requests return structured client errors.
- Provider failures return normalized error responses with a request ID.
- Request IDs correlate with persisted request records and operational traces.

### FR-04: Routing Engine — P0

Routing must be deterministic for a given routing configuration and set of eligible model candidates, except for explicitly configured dynamic health or load signals.

Supported strategies:

- Static routing.
- Rule-based routing.
- Cost-aware routing.

Routing constraints may include:

- Required capabilities.
- Allowed providers and models.
- Maximum estimated request cost.
- Tenant permissions.
- Provider health.
- Configured model preference.

The routing engine must separate eligibility filtering from candidate ranking.

**Acceptance criteria:**

- Ineligible models are never selected.
- Hard budget constraints cannot be overridden by ranking scores.
- Routing decisions record the policy version and selection reason.
- A configuration update affects new requests without restarting the service.
- If no eligible model exists, the API returns a clear error.

### FR-05: Retry and Fallback Handling — P0

The execution layer must implement bounded retries, timeouts, and provider fallback.

- Retry only failures classified as transient.
- Respect request deadlines and cancellation.
- Use configurable exponential backoff with jitter.
- Avoid retrying permanent authentication and validation failures.
- Limit total attempts and total execution time.
- Preserve a record of every provider attempt.
- Do not automatically replay partially delivered streaming responses.
- Treat ambiguous provider timeouts as potentially billable outcomes.

Fallback execution must respect the same tenant permissions, capability requirements, and budget constraints as the original request.

**Acceptance criteria:**

- A simulated transient failure can recover through a permitted retry or fallback.
- Permanent failures do not trigger retries.
- A circuit breaker prevents repeated calls to an unhealthy provider after its configured threshold is reached.
- The gateway does not continue retrying after cancellation.
- Every attempt is associated with the original logical request.

### FR-06: Usage Accounting — P0

The system must track:

- Input and output token counts.
- Provider-reported usage where available.
- Estimated usage where provider-reported values are unavailable.
- Applicable pricing configuration and version.
- Estimated input, output, and total cost.
- The source and confidence of the estimate.

Cost calculations must distinguish estimated cost from actual provider billing.

**Acceptance criteria:**

- Every completed request has a usage status, even when exact token usage is unavailable.
- Usage records identify whether values are reported, estimated, or unavailable.
- Pricing changes do not silently alter previously recorded cost calculations.
- Retries and fallbacks produce separate attempt records without losing logical request totals.

### FR-07: Rate Limits and Budgets — P0

The system must support:

- Per-tenant request rate limits.
- Per-tenant concurrency limits.
- Maximum request size and execution duration.
- Configurable spending budgets.
- Maximum estimated cost per request.

Rate limiting must work across multiple application instances using shared state.

Spending controls must account for pending and completed requests. Because provider usage and billing may be delayed, strict financial guarantees cannot be based solely on eventual usage records.

**Acceptance criteria:**

- Requests exceeding rate limits return HTTP 429.
- Excessive request sizes are rejected before provider execution.
- Requests exceeding the configured per-request cost limit are rejected or routed to an eligible alternative.
- Concurrent requests cannot bypass shared tenant limits through separate gateway instances.

### FR-08: Observability Dashboard — P0

The dashboard must display:

- Request volume.
- Error rates.
- p50 and p95 latency.
- Time to first token for streaming requests.
- Token consumption.
- Estimated cost.
- Model and provider usage.
- Fallback rates.
- Recent failed requests.

The request detail page must display:

- Logical request metadata.
- Routing decision.
- Provider attempts.
- Normalized error information.
- Usage and cost records.
- Correlation identifiers.

Raw prompts and model responses must not be retained by default.

**Acceptance criteria:**

- An administrator can filter requests by date, provider, model, and status.
- A failed request can be traced to its relevant provider attempt.
- Dashboard unavailability does not prevent otherwise healthy inference requests.
- Metrics avoid sensitive data and unbounded high-cardinality labels.

### FR-09: Evaluation Engine — P0

The evaluation engine must support datasets containing input cases, expected results, and scoring configuration.

Initial evaluation types:

- Exact or field-level structured extraction.
- Classification accuracy, precision, recall, and F1.
- Configurable answer-quality scoring for suitable tasks.

Evaluation runs must store:

- Dataset version.
- Model and provider configuration.
- Prompt version.
- Generation parameters.
- Evaluator version.
- Per-case outcomes.
- Aggregate results.
- Usage, cost, latency, and errors.

**Acceptance criteria:**

- The same dataset can be run against multiple model configurations.
- Results remain associated with the exact configuration used.
- Failed cases are recorded and included in aggregate failure statistics.
- Re-running an evaluation creates a new run rather than overwriting historical results.
- Aggregate metrics are calculated from recorded case-level results.

### FR-10: Deployment and Configuration — P0

The repository must provide:

- Docker Compose configuration.
- Database migrations.
- Environment-based configuration.
- Example environment configuration without real secrets.
- Health and readiness endpoints.
- A documented initial setup process.
- A repeatable database initialization procedure.

**Acceptance criteria:**

- A new developer can launch the application from a clean checkout by following the README.
- Database schema initialization is repeatable.
- Missing mandatory configuration produces an actionable startup error.
- No production credentials are committed to the repository.

### FR-11: Prompt and Policy Versioning — P1

The system should maintain immutable versions of prompts and routing policies.

Historical requests and evaluation runs must reference the version used during execution.

**Acceptance criteria:**

- Editing a prompt or routing policy creates a new version.
- Historical records retain their original version references.
- An administrator can compare evaluation results across versions.

### FR-12: Semantic Caching — Post-MVP

Semantic caching is explicitly excluded from the MVP.

Before implementation, define cache-key semantics, tenant isolation, model and prompt versioning, invalidation, similarity thresholds, and protections against returning stale or inappropriate responses.

---

## 5. Non-Functional Requirements

### NFR-01: Performance

The gateway must introduce minimal overhead relative to provider response latency.

Initial engineering targets, measured under a documented test environment:

- Gateway processing overhead: p95 below 100 ms for non-streaming requests, excluding provider execution.
- Administrative dashboard queries: p95 below 500 ms for ordinary filtered queries on the benchmark dataset.
- Streaming responses should begin forwarding provider output without waiting for the full completion.

These are initial targets to validate and adjust through benchmarking, not guarantees for every deployment.

### NFR-02: Reliability

- All external calls must have bounded execution time.
- Retries must be bounded and cancellation-aware.
- Database operations must use transactions where required.
- Background jobs must be recoverable after process restarts.
- Observability failures must not cascade into inference failures.
- The system must expose health and readiness separately.

### NFR-03: Security

- Hash API keys with an appropriate cryptographic hash construction.
- Protect stored provider credentials using an appropriate secret-management strategy.
- Enforce tenant isolation at the application and data-access layers.
- Apply least-privilege database permissions.
- Prevent sensitive credentials and prompts from entering ordinary logs.
- Protect administrative sessions against common web security threats.
- Validate request sizes and content types.
- Restrict outbound provider destinations to configured endpoints to reduce SSRF risk.
- Avoid logging authorization headers or secret configuration values.

### NFR-04: Maintainability

- Use clear module boundaries.
- Keep provider implementations independent of business rules.
- Use dependency injection and explicit interfaces.
- Include unit and integration tests.
- Use automated formatting, linting, and static analysis where applicable.
- Document important architectural decisions.

### NFR-05: Portability

- Support local development through Docker Compose.
- Support remote provider endpoints and an optional local inference service.
- Avoid dependencies on a single cloud vendor.
- Keep deployment configuration separate from application logic.

### NFR-06: Data Integrity

- Persist logical requests separately from provider attempts.
- Preserve historical pricing and configuration references.
- Use stable identifiers for correlation.
- Prevent duplicate usage-event processing where consumers may retry.
- Define retention and deletion behavior for operational data.

---

## 6. Architecture and Module Boundaries

Use a modular monolith with a separately executable background worker where needed.

Recommended logical modules:

### 6.1 Identity and Tenancy

Responsibilities:

- Administrative authentication.
- Tenant lifecycle.
- API keys and permissions.
- Authorization policies.
- Audit logging.

### 6.2 Provider Registry

Responsibilities:

- Provider configuration.
- Model metadata and capabilities.
- Pricing versions.
- Credential references.
- Provider health state.

### 6.3 Inference

Responsibilities:

- Request validation.
- Provider abstraction.
- Streaming.
- Response normalization.
- Cancellation and deadlines.

### 6.4 Routing

Responsibilities:

- Routing policies.
- Eligibility filtering.
- Candidate ranking.
- Fallback decisions.
- Policy versioning.

### 6.5 Resilience

Responsibilities:

- Retry classification.
- Timeouts.
- Circuit breakers.
- Concurrency controls.
- Failure normalization.

### 6.6 Usage and Observability

Responsibilities:

- Request records.
- Provider-attempt records.
- Usage events.
- Cost calculations.
- Metrics and trace correlation.

### 6.7 Evaluation

Responsibilities:

- Dataset management.
- Prompt versions.
- Evaluation scheduling.
- Scoring.
- Benchmark reports.

### 6.8 Administration

Responsibilities:

- Dashboard APIs.
- Provider and model management.
- Tenant management.
- Request inspection.
- Evaluation visualization.

### 6.9 Background Processing

Responsibilities:

- Usage aggregation.
- Evaluation execution.
- Retention jobs.
- Recoverable background work.

The inference request path must not synchronously wait for noncritical analytics aggregation.

For financial usage accounting, use a durable mechanism with explicit delivery and failure semantics. Do not claim that an in-memory queue guarantees durable delivery.

---

## 7. Data Model

Use PostgreSQL as the primary database.

### Core entities

**Tenant**

- Id
- Name
- Status
- CreatedAtUtc

**ApiKey**

- Id
- TenantId
- KeyHash
- KeyPrefix
- Scopes
- ExpiresAtUtc
- RevokedAtUtc
- CreatedAtUtc

**Provider**

- Id
- Name
- AdapterType
- Endpoint
- CredentialReference
- Enabled
- CreatedAtUtc
- UpdatedAtUtc

**Model**

- Id
- ProviderId
- ProviderModelId
- Alias
- Capabilities
- InputPricePerMillionTokens
- OutputPricePerMillionTokens
- PricingVersion
- Enabled

**RoutingPolicy**

- Id
- Name
- Version
- Configuration
- Enabled
- CreatedAtUtc

**InferenceRequest**

- Id
- TenantId
- ClientRequestId
- RequestedModelAlias
- SelectedModelId
- RoutingPolicyVersion
- Status
- StartedAtUtc
- CompletedAtUtc
- TimeToFirstTokenMs
- ErrorCode

**ProviderAttempt**

- Id
- InferenceRequestId
- ProviderId
- ModelId
- AttemptNumber
- Status
- StartedAtUtc
- CompletedAtUtc
- HttpStatusCode
- ErrorCode
- InputTokens
- OutputTokens
- EstimatedCost

**UsageRecord**

- Id
- InferenceRequestId
- ProviderAttemptId
- InputTokens
- OutputTokens
- CostAmount
- Currency
- UsageSource
- PricingVersion
- RecordedAtUtc

**EvaluationDataset**

- Id
- Name
- Version
- Schema
- CreatedAtUtc

**EvaluationCase**

- Id
- EvaluationDatasetId
- Input
- ExpectedOutput
- Metadata

**EvaluationRun**

- Id
- EvaluationDatasetId
- DatasetVersion
- ModelConfiguration
- PromptVersion
- EvaluatorVersion
- Status
- StartedAtUtc
- CompletedAtUtc

**EvaluationResult**

- Id
- EvaluationRunId
- EvaluationCaseId
- Status
- QualityScore
- InputTokens
- OutputTokens
- EstimatedCost
- LatencyMs
- ErrorCode

Additional tables may be introduced where needed for routing rules, prompt versions, administrative sessions, budget reservations, audit records, and durable event processing.

### Data modeling rules

- Use UTC for stored timestamps.
- Use database transactions for logically related state changes.
- Use decimal-compatible numeric types for monetary amounts.
- Use explicit units for durations and token counts.
- Store structured configuration as JSONB only where flexibility is useful; avoid making all business entities unqueryable JSON documents.
- Index tenant IDs, request timestamps, provider-attempt foreign keys, and frequently used filter columns.
- Define deletion and retention behavior before retaining user-provided content.

---

## 8. API Contracts

All administrative endpoints must require administrative authentication and enforce authorization.

### Inference

`POST /v1/chat/completions`

Request:

    {
      "model": "balanced",
      "messages": [
        {
          "role": "user",
          "content": "Extract the customer's name and email."
        }
      ],
      "stream": false
    }

The example demonstrates the contract shape only. Exact request fields must be validated against the supported API specification.

Response should include normalized completion data and usage when available. Provider-specific metadata may be exposed through an optional, documented metadata field.

### Model discovery

`GET /v1/models`

Returns the model aliases available to the authenticated tenant.

### Request inspection

`GET /admin/requests`

Supports filters such as:

- Time range.
- Model alias.
- Provider.
- Status.
- Tenant, when authorized.

`GET /admin/requests/{requestId}`

Returns request metadata, routing information, provider attempts, and usage records without exposing secrets.

### Usage analytics

`GET /admin/usage`

Supports grouping and filtering by:

- Time interval.
- Tenant.
- Provider.
- Model.

### Routing configuration

`GET /admin/routing-policies`

`POST /admin/routing-policies`

`PUT /admin/routing-policies/{policyId}`

Policy updates must be validated and versioned.

### Evaluations

`POST /admin/evaluations`

`GET /admin/evaluations`

`GET /admin/evaluations/{runId}`

Evaluation execution must support status inspection and failure reporting.

### Health

`GET /health`

Reports process health.

`GET /ready`

Reports whether the instance is ready to accept traffic based on required dependencies and startup initialization.

Provider outages must not necessarily make the entire gateway unready if other configured providers remain usable.

---

## 9. Frontend Requirements

Use Vue 3 with TypeScript.

The frontend is an administrative dashboard, not a customer-facing chat application.

### 9.1 Overview Dashboard

Display:

- Total requests.
- Success and failure rates.
- Estimated cost.
- Token usage.
- p50/p95 latency.
- Provider fallback rates.

Include configurable time ranges and clear empty states.

### 9.2 Request Explorer

Provide:

- Paginated request table.
- Filtering and sorting.
- Request detail view.
- Provider-attempt timeline.
- Usage and cost breakdown.
- Error inspection.

### 9.3 Provider Management

Provide:

- Provider configuration.
- Credential reference management.
- Model registration and capability editing.
- Connectivity testing.
- Enable/disable controls.
- Pricing configuration.

Secrets must never be returned to the browser after initial submission.

### 9.4 Routing Configuration

Provide:

- Routing strategy selection.
- Model preferences.
- Capability constraints.
- Cost limits.
- Fallback order.
- Policy validation and version history.

### 9.5 Tenant Management

Provide:

- Tenant creation and status.
- API-key issuance and revocation.
- Rate limits.
- Budget configuration.
- Usage summaries.

### 9.6 Evaluation Lab

Provide:

- Dataset management.
- Evaluation run configuration.
- Progress and failure status.
- Side-by-side model comparisons.
- Quality, latency, and cost charts.
- Exportable results.

### UX Requirements

- Support desktop and tablet layouts.
- Use accessible forms, labels, and keyboard navigation.
- Provide clear loading, error, and empty states.
- Use consistent units for time, tokens, and currency.
- Do not communicate estimated costs as actual provider charges.

---

## 10. Testing Strategy

### 10.1 Unit Tests

Test:

- Routing eligibility and ranking.
- Cost calculations.
- Retry classification.
- Circuit-breaker state transitions.
- Budget decisions.
- Usage normalization.
- Evaluation metrics.
- API-key verification.

### 10.2 Integration Tests

Test:

- Database persistence and migrations.
- Authentication and tenant isolation.
- Provider adapter contracts.
- Request and attempt recording.
- Rate limits across multiple application instances.
- Background job recovery.
- Streaming cancellation.
- Pricing version preservation.

Use mock HTTP servers for deterministic provider behavior and Testcontainers where appropriate for PostgreSQL and Redis.

### 10.3 Failure-Injection Tests

Simulate:

- Provider timeouts.
- HTTP 429 and 5xx responses.
- Invalid provider responses.
- Provider authentication errors.
- Database outages.
- Redis outages.
- Interrupted streams.
- Worker crashes.
- Ambiguous provider completion timeouts.

Verify that retries remain bounded, failures remain observable, and the application does not silently corrupt usage accounting.

### 10.4 Security Tests

Test:

- Invalid and revoked API keys.
- Cross-tenant resource access.
- Unauthorized administrative operations.
- Secret leakage in logs.
- Request-size enforcement.
- Malicious provider endpoint configuration.
- Prompt and response content handling.
- Abuse of rate limits and concurrency controls.

### 10.5 Load Tests

Use a controlled mock provider to isolate gateway overhead from model inference latency.

Measure:

- Throughput.
- p50, p95, and p99 gateway latency.
- Memory usage.
- Connection-pool saturation.
- Queue depth.
- Error rates.
- Behavior under sustained load.

Document hardware, dataset, concurrency, test duration, and limitations for all published results.

---

## 11. Definition of Done

The MVP is complete when all of the following conditions are satisfied:

1. A developer can deploy the entire stack using documented instructions.
2. At least two provider adapters pass the shared contract tests.
3. The gateway supports normalized non-streaming and streaming inference.
4. Routing, retries, fallbacks, and circuit breakers have automated tests.
5. Tenant isolation and API-key revocation are verified.
6. Request history and provider attempts are persisted and inspectable.
7. Token usage and cost estimates are recorded with explicit provenance.
8. The dashboard exposes request, cost, latency, and failure information.
9. Evaluation runs can compare models on the same versioned dataset.
10. A benchmark report contains actual, reproducible measurements.
11. Logs and traces do not expose API keys or sensitive content by default.
12. The README explains architecture, deployment, API usage, testing, and limitations.
13. CI runs formatting checks, automated tests, and static analysis.
14. Failure-injection tests demonstrate bounded retries and controlled degradation.

---

## 12. Delivery Plan

### Phase 1: Foundation

- Initialize repository and solution structure.
- Configure Docker Compose and PostgreSQL.
- Implement provider contracts and two adapters.
- Implement the basic inference endpoint.
- Add unit and integration testing infrastructure.

**Deliverable:** A working gateway that can invoke either provider.

### Phase 2: Routing and Resilience

- Implement model aliases and routing policies.
- Add retries, timeouts, circuit breakers, and fallbacks.
- Add streaming and cancellation handling.
- Record logical requests and provider attempts.

**Deliverable:** A resilient gateway with inspectable execution history.

### Phase 3: Identity and Usage

- Implement administrative authentication.
- Add tenants and API keys.
- Implement rate limits and budget enforcement.
- Record usage and versioned pricing.

**Deliverable:** A multi-tenant gateway with usage controls.

### Phase 4: Observability

- Integrate OpenTelemetry.
- Build the request explorer and analytics dashboard.
- Add failure diagnosis and provider health views.
- Test telemetry degradation.

**Deliverable:** An operationally observable product.

### Phase 5: Evaluation

- Implement dataset and evaluation-run management.
- Add task-specific scoring.
- Compare models and routing policies.
- Generate benchmark reports.

**Deliverable:** A repeatable AI evaluation workflow.

### Phase 6: Hardening and Release

- Perform load and failure-injection testing.
- Review security and data retention.
- Document architectural decisions.
- Publish a demonstration and benchmark results.

**Deliverable:** A portfolio-ready release.

---

## 13. Portfolio Success Metrics

The project should demonstrate measurable engineering outcomes rather than arbitrary adoption targets.

Track:

- Provider adapter contract-test coverage.
- Routing correctness on a known test matrix.
- Gateway overhead under documented load.
- Request success rate under controlled provider failures.
- Retry amplification during failure scenarios.
- Tenant-isolation test results.
- Usage-accounting completeness.
- Evaluation reproducibility.
- Quality versus cost across routing strategies.
- Setup time from a clean checkout.

Publish the results honestly, including failures and limitations.

---

## 14. Key Architectural Decisions

The initial implementation should follow these principles:

1. **Modular monolith first.** Separate logical modules and use a worker for asynchronous processing without introducing unnecessary service boundaries.
2. **Provider-neutral core.** Keep provider-specific behavior inside adapters.
3. **Deterministic routing first.** Optimize using measurements before introducing complex automated decision-making.
4. **Explicit failure semantics.** Model timeouts, retries, fallbacks, and cancellation as first-class behaviors.
5. **Durable usage accounting.** Distinguish operational telemetry from authoritative usage records.
6. **Versioned configuration.** Preserve the model, prompt, pricing, and routing configuration associated with each execution.
7. **Secure by default.** Avoid retaining sensitive content unnecessarily and enforce tenant isolation throughout the system.
8. **Measure before optimizing.** Benchmark performance and quality using repeatable tests.

---

## 15. Final Product Vision

ModelSwitch should become a practical demonstration of how to engineer reliable AI infrastructure.

Its central value is not simply the ability to call multiple models. It is the ability to make AI requests **controllable, observable, measurable, and resilient**.

A successful MVP will let another developer deploy the gateway, connect an application, trigger a provider failure, inspect the fallback behavior, review the associated usage records, and compare model quality and cost using a repeatable evaluation dataset.

That end-to-end demonstration is the project's primary measure of success.
