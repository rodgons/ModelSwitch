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
- Provider errors make a Case **errored** (no score). Aggregates report quality over scored Cases **and** the errored rate, as two separate numbers (FR-09: failed cases included in failure stats).
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

This creates one Comparison and one Evaluation Run per configuration. Each run's `run_configuration` is frozen at creation, with every reference resolved:
- `alias` targets also record the Policy Version that was current **at run creation**. The run uses that version for its whole duration, not later edits, so it can be reproduced.
- `providerModelId` targets bypass routing (`IInferenceService` "pinned" mode), but still apply the Tenant's Budget, per-request cost cap and allowed aliases.
- The Price ids in force are captured on each Case Result's usage.

**Spend.** Evaluation spend counts against the Tenant's **one** Budget: there is no separate evaluation Budget. To stop a large Comparison from draining the month and failing production traffic with `budget_exceeded`, each run takes an optional `maxEstimatedCost` (per run, not per Comparison). Before each Case starts, the worker checks the run's Estimated Cost so far (candidate + judge) plus that Case's worst case against the cap. Once it would be exceeded, no new Case starts, the remaining Cases are `skipped`, and the run ends `failed` with `error = run_cost_cap_reached`. The run builder shows a worst-case estimate (Cases × per-Case worst case) before the run is created.

## 5. Execution

- Creating a run enqueues one `evaluation.run` job. The worker claims it and processes Cases with `concurrency` parallelism (cap 16), calling `IInferenceService` with the Tenant's identity and `X-ModelSwitch-Task = dataset.task_kind`. There is no API Key: each Inference Request is recorded with Request Origin `evaluation`, `api_key_id = null` and the run's `evaluation_run_id`. The Budget, the per-request cost cap (`max_request_cost`) and `allowed_aliases` apply. The Tenant's Rate and Concurrency Limits do **not**, so a run can't starve production traffic of its slots; the run's own `concurrency` (cap 16) throttles it instead.
- Each Case Result is written as soon as it completes. `unique(run_id, case_id)` lets a run that was interrupted by a worker crash be resumed by re-claiming the job: completed Cases are skipped (NFR-02).
- Cancellation sets `status=cancelled`. In-flight Cases finish, and nothing new starts.
- Aggregates are computed **only from stored Case Results** at the end, and can be recomputed on demand (FR-09).
- Re-running always creates a new run (FR-09), through "Run again", which copies the configuration.

## 6. Reproducibility record

Each run stores enough to answer "what exactly produced this number?":

`dataset_id`, `dataset_version`, `content_sha256`, prompt id+version, target (alias + Policy Version, or provider model id), Provider Model id(s) actually used per Case (via `inference_request_id`), generation params, evaluator kind+version+config, `config_sha256` (hash of the canonical run configuration), the gateway build version, and the start and end times.

LLM outputs are not deterministic even at temperature 0. The UI says so and shows, per Comparison, the run-to-run variance whenever the same configuration has been run more than once.

## 7. Export

CSV and JSONL with one row per Case Result. The export includes the Run Configuration header and the aggregate summary (JSONL: first line `{"type":"run",…}`, then `{"type":"case",…}` lines). Exports stream from the DB and are never built in memory.
