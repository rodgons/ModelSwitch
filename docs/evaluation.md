# Evaluation Lab

This document covers FR-09 and FR-11 (prompt versions). Terms: Dataset, Dataset Version, Case, Prompt Version, Evaluator, Evaluation Run, Run Configuration, Comparison, Case Result (see [`CONTEXT.md`](../CONTEXT.md)).

## 1. Dataset format

Datasets are uploaded as JSONL, one Case per line. Each upload creates a new immutable Dataset Version. Datasets are never edited in place.

```jsonl
{"id":"c-001","input":{"text":"Hi, I'm Ana Silva (ana@example.com)…"},"expected":{"name":"Ana Silva","email":"ana@example.com"},"metadata":{"lang":"en"}}
{"id":"c-002","input":{"text":"…"},"expected":{"name":null,"email":"bob@example.org"}}
```

- `id`: required, unique within the version. It lets Case Results be matched across versions.
- `input`: an object whose keys are template variables for the Prompt Version.
- `expected`: an object (extraction), a string label (classification) or a reference answer (free text).
- The upload is validated as a whole: either every line is valid, or the version is rejected with `400 validation_failed` and line-numbered errors in the Problem Details `errors` extension. Limits are ≤ 5 000 Cases and ≤ 10 MiB.
- `content_sha256` of the canonicalized cases is stored, so two runs on "the same dataset" can be proven identical.

## 2. Prompt Versions

```json
{ "messages": [
    { "role": "system", "content": "Extract the customer's name and email. Reply with JSON." },
    { "role": "user",   "content": "{{text}}" } ],
  "responseFormat": { "type": "json_schema", "schema": { … } } }
```

`{{var}}` substitution is plain text with no logic. A missing variable is a Case error (`template_variable_missing`), not an empty string.

## 3. Evaluators

Each Evaluator has a kind, a semantic **version** (bumped whenever its scoring changes) and a config. The version is stored in the Run Configuration, so old runs stay interpretable.

| Kind | Task | Per-case score | Aggregate |
|---|---|---|---|
| `exact_match` v1 | any | 1 if normalized output == expected (trim, optional case-fold) | accuracy |
| `json_fields` v1 | extraction | fraction of expected fields matched. Field comparators: `exact`, `casefold`, `number(tolerance)`, `set` | mean score, per-field accuracy, `parse_failure_rate` |
| `classification` v1 | classification | 1 if the predicted label == expected. Output is mapped to labels by exact match after trimming, otherwise `unparseable` | accuracy, per-class precision/recall/F1, macro-F1, confusion matrix |
| `llm_judge` v1 | free text | 0–1 from a rubric scored by a judge model alias (itself pinned in the config) | mean, distribution |
| `reference_similarity` v1 | free text | token-level F1 against the reference (SQuAD-style) | mean |

Rules:
- An unparseable output is a **failed** Case (score 0, `status=failed`) and is counted in aggregates. It is never dropped.
- Provider errors make a Case **errored** (no score). A judge reply that does not follow the required `{"score": 0..1, "reason"?: string}` format counts as a Provider error (`provider_error`): the judge failed to answer usably. Aggregates report quality over scored Cases **and** the errored rate, as two separate numbers (FR-09: failed cases included in failure stats).
- `llm_judge` results are labelled as model-judged in the UI. Judge calls are made through the same `IInferenceService`. Each one is its own Inference Request (Request Origin `evaluation`, same `evaluation_run_id`), so it has ordinary Usage Records and counts toward the Budget and the run's cost cap. Its tokens and cost are stored on the Case Result in separate `judge_*` columns. Run aggregates report `cost.candidate`, `cost.judge` and `cost.total`. The Comparison view lines configurations up on **candidate** cost, the comparable number, and shows judge cost as a separate labelled column. The request detail page shows "judge for Case X of run Y" by following the Case Result's link.
- All metric code lives in the Evaluation module's `Domain/Metrics` as pure functions with golden-value unit tests (e.g. scikit-learn-computed fixtures for P/R/F1).

## 4. Runs and Comparisons

`POST /admin/evaluations`:

```json
{ "tenantId": "…", "datasetId": "…", "datasetVersion": 3, "name": "extraction: mini vs local",
  "configurations": [
    { "target": { "alias": "balanced" },                       "prompt": {"id": "…", "version": 2},
      "params": { "temperature": 0, "seed": 7, "maxOutputTokens": 256 },
      "evaluator": { "kind": "json_fields", "version": "1", "config": { … } } },
    { "target": { "providerModelId": "…" }, … } ],
  "concurrency": 4, "maxEstimatedCost": 5.00 }
```

This creates one Comparison and one Evaluation Run per configuration (`POST /admin/evaluations/estimate` takes the same body and returns the worst-case estimate without writing anything). Each run's `run_configuration` is frozen at creation, with every reference resolved:
- `alias` targets also record the Policy Version that was current **at run creation** (`IPolicyStore.GetActiveForAliasAsync`). The run uses that version for its whole duration, not later edits, so it can be reproduced (`InferenceTarget.For(alias, policyId, policyVersion)`). The alias itself must still exist and be allowed when each Case runs.
- `providerModelId` targets are checked at creation (`IProviderCatalog.FindEnabledByIdAsync`) and bypass routing (`InferenceTarget.Pinned(providerModelId)`, `IInferenceService` "pinned" mode: one Candidate, the default execution settings), but still apply the Tenant's Budget, per-request cost cap and allowed aliases. "Allowed aliases" for a pinned model means: a Tenant allowed `*` may pin any Provider Model; otherwise the model must be a Candidate of at least one alias the Tenant is allowed, else `model_not_allowed` ([routing-and-resilience.md §1](routing-and-resilience.md#pinned-targets)). Such requests are recorded under `requested_alias = <providerName>/<providerModelId>`.
- The Prompt Version's template is copied into the configuration; the Evaluator kind, version and config are kept as written. An `llm_judge` judge alias is frozen like a target alias (its Policy Version at creation, `evaluator.judge`).
- Creation checks the Evaluator fits the Dataset Version: the Task column above (`exact_match` fits any task), `classification` labels must include every label the version expects, and `json_fields` may only configure fields the version's expected results have. Template variables are not checked against the Cases: a missing one is a Case error.
- `config_sha256` hashes the canonical configuration without `concurrency` and the gateway build version, so a re-run with another parallelism or on a newer build still counts as the same configuration for run-to-run variance.
- The Price ids in force are captured on each Case Result's usage.

**Estimated Cost cap.** The Estimated Cost of evaluation requests counts against the Tenant's **one** Budget: there is no separate evaluation Budget. To stop a large Comparison from draining the month and failing production traffic with `budget_exceeded`, each run takes an optional `maxEstimatedCost` (per run, not per Comparison). Before each Case starts, the worker checks the run's Estimated Cost so far (candidate + judge) plus that Case's worst case against the cap. Once it would be exceeded, no new Case starts, the remaining Cases are `skipped`, and the run ends `failed` with `error = run_cost_cap_reached`. The run builder shows a worst-case estimate (Cases × per-Case worst case) before the run is created.

A Case's worst case is the Budget Reservation its candidate call would hold (`IInferenceService.EstimateWorstCaseAsync`: the most expensive routed Candidate's worst case × `min(maxAttempts, 2)`), plus, for `llm_judge`, the judge call's, whose input is costed with the candidate's output ceiling added. "Recorded so far" is the run's recorded Estimated Cost (`IEvaluationRunCost.RunEstimatedCostAsync` when the worker claims the run, then each finished Case's candidate and judge cost), and the check also counts the worst case of the Cases still in flight, so the cap holds with `concurrency` above 1. Without a `maxEstimatedCost` nothing is estimated.

## 5. Execution

- Creating a run enqueues one `evaluation.run` job. The worker claims it and processes Cases with `concurrency` parallelism (cap 16), calling `IInferenceService` with the Tenant's identity and `X-ModelSwitch-Task = dataset.task_kind`. There is no API Key: each Inference Request is recorded with Request Origin `evaluation`, `api_key_id = null` and the run's `evaluation_run_id`. The Budget, the per-request cost cap (`max_request_cost`) and `allowed_aliases` apply. The Tenant's Rate and Concurrency Limits do **not**, so a run can't starve production traffic of its slots; the run's own `concurrency` (cap 16) throttles it instead.
- Each Case Result is written as soon as it completes. `unique(run_id, case_id)` lets a run that was interrupted by a worker crash be resumed by re-claiming the job: completed Cases are skipped (NFR-02). The outcome follows the stored Case Results, not only what the resumed pass saw: a worker that hit the cost cap writes the `skipped` Case Results before it finishes the run, so if it dies in between, the resumed pass finds a stored `skipped` result, starts no new Case, skips the Cases still without a result and ends the run `failed` with `run_cost_cap_reached`.
- Before each Case starts the worker re-reads the run's status. Cancellation (`POST …/cancel`, 409 once the run has finished) sets `status=cancelled`. In-flight Cases finish and are recorded, nothing new starts, and the Cases that never started get **no** Case Result (they are not `skipped`, which means "stopped by the cost cap"). The worker then stores the aggregates over what was recorded and `completed_at`. A run cancelled while still queued never starts; it gets empty aggregates at once.
- `cases_done` counts stored Case Results of any status; `cases_failed` counts the failed and errored ones. Each Case Result and both counters are written in one statement, so a resumed run's counters stay exact.
- If the job's last attempt throws, the run is marked `failed` with `error = internal_error` so it does not stay `running`. A worker killed during the last attempt never reaches that code: the claim loop dead-letters the job once its lease expires. The recurring worker job `evaluation.sweep_orphaned_runs` (every minute) catches this. It reads the `queued` and `running` runs, looks up each one's `evaluation.run` job by dedupe key (`IJobStatusReader`), and fails every run whose job is `dead`, `succeeded` or gone (removed by retention) the same way: `error = internal_error`, aggregates from the stored Case Results, `completed_at`. A run whose job is still `pending` or `running` is left alone, and the update is guarded on `status in (queued, running)`, so a run that finishes meanwhile keeps its outcome. The same pass also reads the `cancelled` runs that have no `completed_at` (cancelled while running, so the worker owes them their aggregates): if that worker died first, the re-claimed job skips the already-terminal run and nothing else would finish it. When such a run's job is `dead`, `succeeded` or gone, the sweep stores its aggregates from the stored Case Results and `completed_at` (guarded on `status = cancelled and completed_at is null`), and the run stays `cancelled`.
- Aggregates are computed **only from stored Case Results** at the end, and can be recomputed on demand (FR-09, `POST …/aggregates/recompute`, finished runs only). While a run is queued or running, its detail and the Comparison compute them on the fly from what is stored (`aggregatesLive`).
- Re-running always creates a new run (FR-09), through "Run again", which copies the frozen configuration as it is (the same Policy Versions, Prompt Version, parameters and Evaluator, so the same `config_sha256`) into a new Comparison; only the gateway build version is the current one. A disabled Provider Model or a revoked alias makes the new run's Cases error rather than refusing the re-run.

## 6. Reproducibility record

Each run stores enough to answer "what exactly produced this number?":

`dataset_id`, `dataset_version`, `content_sha256`, prompt id+version, target (alias + Policy Version, or provider model id), Provider Model id(s) actually used per Case (via `inference_request_id`), generation params, evaluator kind+version+config, `config_sha256` (hash of the canonical run configuration), the gateway build version, and the start and end times.

LLM outputs are not deterministic even at temperature 0. The UI says so and shows, per Comparison, the run-to-run variance whenever the same configuration has been run more than once.

## 7. Export

CSV and JSONL with one row per Case Result, in upload order. The export includes the Run Configuration header and the aggregate summary (JSONL: first line `{"type":"run",…}`, then `{"type":"case",…}` lines). Exports stream from the DB and are never built in memory.

- JSONL run line: `{type, runId, comparisonId, tenantId, status, error, configSha256, configuration, maxEstimatedCost, casesTotal, createdAt, startedAt, completedAt, aggregates}`. Case line: `{type, caseId, externalId, ordinal, status, score, predicted, outputText, expected, scoreDetails, errorCode, inputTokens, outputTokens, estimatedCost, judgeInputTokens, judgeOutputTokens, judgeEstimatedCost, latencyMs, inferenceRequestId, judgeInferenceRequestId, createdAt}`.
- CSV (RFC 4180, UTF-8, `\n`): a header block of `# key: value` lines (`modelswitch_evaluation_export: 1`, `run_id`, `comparison_id`, `tenant_id`, `status`, `error`, `config_sha256`, `max_estimated_cost`, `started_at`, `completed_at`, and `run_configuration` and `aggregates` as one-line canonical JSON), then the column row `case_id,external_id,ordinal,status,score,predicted,output_text,expected,score_details,error_code,input_tokens,output_tokens,estimated_cost,judge_input_tokens,judge_output_tokens,judge_estimated_cost,latency_ms,inference_request_id,judge_inference_request_id,created_at`, then one row per Case Result (JSON cells as compact JSON). A CSV reader must skip the `#` lines.
- The aggregates in the header are the stored ones, or, for an unfinished run, computed when the export starts.
