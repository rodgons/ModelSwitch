# ModelSwitch

A self-hosted gateway that lets tenants' applications call LLMs through one stable API, while operators control which concrete models serve each call and observe what happened and what it cost.

## Language

### Tenancy and access

**Tenant**:
An isolated customer of the gateway whose requests, keys, usage, budgets and evaluations are invisible to every other tenant.
_Avoid_: Customer, account, organization, project

**API Key**:
A secret credential belonging to exactly one Tenant, used by applications to call the inference API.
_Avoid_: Token, client secret

**Key Prefix**:
The non-secret, displayable leading portion of an API Key used to identify it in lists and logs.

**Scope**:
A named permission attached to an API Key that limits which inference operations it may perform.

**Administrator**:
A human user who signs into the dashboard to manage the platform; never authenticates with an API Key.
_Avoid_: Admin user, operator (as a noun for the person), user

**Role**:
The set of administrative permissions granted to an Administrator: Owner, Operator or Viewer.

**Tenant Grant**:
The set of Tenants an Administrator may see and act on — either all Tenants or an explicit list.

**Audit Record**:
An immutable entry describing a sensitive administrative action, who performed it, and when.
_Avoid_: Audit log entry, activity

### Providers and models

**Provider**:
A configured upstream inference service the gateway can call, such as an OpenAI-compatible endpoint or an Ollama server.
_Avoid_: Vendor, backend, upstream (as a noun)

**Adapter**:
The provider-specific translation layer between the gateway's neutral contract and one Provider's wire protocol.
_Avoid_: Driver, connector, client

**Provider Model**:
A concrete model offered by a Provider, identified internally by the Provider's own model identifier, with declared Capabilities and a Price.
_Avoid_: Model (unqualified), deployment, engine

**Capability**:
A declared feature of a Provider Model that a request may require, such as streaming, JSON-schema output, or a context window size.

**Price**:
The immutable, versioned per-token rates (input, cached input, output) for a Provider Model from a given point in time onward.
_Avoid_: Pricing (as a mutable field), rate card

**Credential Reference**:
A pointer to a Provider's secret, never the secret itself, as seen by anything outside the secret store.

**Provider Health**:
The gateway's current belief about whether a Provider is usable, derived from recent Attempt outcomes.

### Inference and routing

**Model Alias**:
The client-facing name a request asks for (for example `balanced`), resolved by a Routing Policy into Provider Models.
_Avoid_: Model name, virtual model

**Routing Policy**:
The named rules that decide which Provider Models may serve a Model Alias and in what order.
_Avoid_: Route, routing config

**Policy Version**:
An immutable snapshot of a Routing Policy; editing a policy creates a new version and never changes an old one.

**Task Type**:
A client-declared label describing the kind of work a request performs, used by rule-based routing.

**Candidate**:
A Provider Model under consideration for a request before routing has decided whether and where it will be used.

**Eligibility**:
Whether a Candidate satisfies every hard constraint for a request; ineligible Candidates are never used.

**Routing Decision**:
The recorded outcome of routing: the Policy Version used, the ordered eligible Candidates, the excluded Candidates with reasons, and why the first was chosen.

**Fallback**:
Using the next eligible Candidate in a Routing Decision after the previous one failed.
_Avoid_: Failover

**Inference Request**:
One logical call received from a client, regardless of how many Attempts were needed to serve it.
_Avoid_: Call, job, transaction

**Attempt**:
One call from the gateway to one Provider Model on behalf of an Inference Request.
_Avoid_: Try, provider call, sub-request

**Retry**:
A further Attempt against the same Provider Model after a transient failure.

**Deadline**:
The absolute moment after which no further work is done for an Inference Request.
_Avoid_: Timeout (for the whole request)

**Ambiguous Outcome**:
An Attempt whose result is unknown because the connection failed after the Provider may have started work; treated as possibly billed.

**First Token**:
The moment the first piece of generated content is forwarded to a streaming client.

### Usage and money

**Request Origin**:
Whether an Inference Request came from a client's API Key or from an Evaluation Run.
_Avoid_: Source (that's Usage Source), caller type

**Usage Record**:
The authoritative record of tokens consumed and cost estimated for one Attempt.
_Avoid_: Usage event, billing record

**Cached Input Tokens**:
The part of an Attempt's input tokens that the Provider served from its prompt cache; always a subset of the input tokens.
_Avoid_: Cache hits, cached prompt

**Usage Source**:
Where a Usage Record's token counts came from: reported by the Provider, estimated by the gateway, or unavailable.

**Estimated Cost**:
The gateway's calculation of an Attempt's cost from token counts and the Price in force; never a Provider's actual charge.
_Avoid_: Cost (unqualified), charge, bill, spend

**Budget**:
A Tenant's spending limit of Estimated Cost over a calendar period.

**Reservation**:
A temporary hold of Estimated Cost against a Budget, taken before an Inference Request runs and settled when it finishes.
_Avoid_: Hold, pre-authorization

**Rate Limit**:
A maximum number of Inference Requests a Tenant may start per time window.

**Concurrency Limit**:
A maximum number of a Tenant's Inference Requests that may be in flight at once.

### Evaluation

**Dataset**:
A named collection of evaluation cases maintained over time.

**Dataset Version**:
An immutable snapshot of a Dataset's cases; runs always reference a version, never the Dataset itself.

**Case**:
One input together with its expected result inside a Dataset Version.
_Avoid_: Example, sample, test case

**Prompt Version**:
An immutable template that turns a Case's input into messages for a model.

**Evaluator**:
A versioned scoring method that compares a model's output with a Case's expected result.
_Avoid_: Grader, judge (except for the LLM-judge evaluator kind), metric

**Evaluation Run**:
The execution of one Dataset Version against exactly one Run Configuration.

**Run Configuration**:
The full, frozen set of choices for an Evaluation Run: target model, Prompt Version, generation parameters and Evaluator version.

**Comparison**:
A set of Evaluation Runs over the same Dataset Version, created together so their results can be placed side by side.
_Avoid_: Experiment, benchmark

**Case Result**:
The outcome, score, usage and latency of one Case within one Evaluation Run.
