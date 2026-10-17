# Usage Accounting and Budgets

This document implements FR-06 and FR-07 (spending) and PRD §14.5 ("durable usage accounting").

## 1. Principles

1. **The Usage Record is authoritative. Telemetry is not.** Metrics and traces may be sampled, dropped or delayed. Usage Records are written transactionally and never sampled.
2. **Every Attempt gets exactly one Usage Record**, including failed, cancelled and ambiguous Attempts. Every Inference Request gets a `usage_status`.
3. **Estimated Cost is never presented as a charge.** The API field, the UI label and the export header all say "estimated".
4. **History is immutable.** A Usage Record keeps the `model_price_id` it was costed with. Changing a Price creates a new row and leaves recorded costs unchanged. The admin API (`POST /admin/models/{id}/prices`) only appends. A Price dated in the future bumps the config version when it is saved, but the snapshot only reads Prices already in force, so it takes effect at the first snapshot rebuild after its `effective_from` (the next config change); date a Price now unless a later switch-over is wanted.

## 2. Usage Source

| `usage_source` | When | `estimate_method` |
|---|---|---|
| `reported` | The Provider returned token counts | null |
| `estimated` | No reported counts, but the gateway knows the input and can count the output it forwarded | `tokenizer:<name>` or `bytes_div_3` |
| `unavailable` | The gateway cannot know, e.g. an ambiguous outcome or an abandoned request | `reservation_upper_bound` (cost) and NULL tokens |

The request-level `usage_status` is the worst of its Attempts' values (`reported` < `estimated` < `unavailable`). It is `partial` when reported and non-reported Attempts are mixed.

## 3. Budget enforcement (Reservation model)

Budgets are monthly (calendar month, UTC; daily and weekly periods are post-MVP) and checked against **reserved + spent** (FR-07: "account for pending and completed requests"). Evaluation Runs reserve and settle against the same Budget as API traffic. Their protection against draining it is a per-run cost cap ([evaluation.md §4](evaluation.md#4-runs-and-comparisons)), not a separate Budget.

**Only a Tenant with a Budget has `budget_period` rows.** A Tenant without one takes no lock on a shared row anywhere on the request path: its Reservation row is still written (the sweeper needs it), but nothing else. The 2026-10-17 load run ([benchmarks/2026-10-17-266c696.md](benchmarks/2026-10-17-266c696.md)) showed why: every request of a Tenant updated one row twice, so all of them queued on its lock.

The **invariant**, for every `budget_period` row (Tenant, month):

- `reserved_amount` = the sum of the Tenant's `held` Reservations with that `period_start`;
- `spent_amount` = the sum of `usage_record.total_cost` of the Tenant's requests that **started** in that month.

A row exists for the current month exactly while the Tenant has a Budget: setting a Budget creates it, removing the Budget deletes it, and the first Reservation of a new month creates that month's row if the Tenant still has a Budget. A past month keeps its row. Without a row, the month's figures (`IBudgets.GetCurrentAsync`, the Tenant detail page) are summed from the Reservations and Usage Records in the same way.

**Creating a row** (`BudgetSql.SeedPeriodSql`) sums the held Reservations and Usage Records that already exist, so it must not interleave with a request that is writing either. Each Tenant has a PostgreSQL advisory lock (`pg_advisory_xact_lock(0x4D534250, <key from the tenant id>)`):

- every transaction that moves a period row (reserve, top-up, settle, sweep) first takes it in **shared** mode, in a statement of its own, so the statement that reads the row takes its snapshot after the lock. Shared holders never wait for each other.
- setting or removing a Budget, and creating a month's row, take it in **exclusive** mode. They wait for the requests in flight to commit and hold new ones back until they commit, so the row they create counts every Reservation and Usage Record exactly once.

**Reserve** (request step 8, one transaction, three round trips):

```sql
INSERT INTO usage.inference_request (…, status = 'running');
INSERT INTO usage.reservation (…, amount = @hold, status = 'held', expires_at = deadline + grace);   -- one round trip (EF)

SELECT pg_advisory_xact_lock_shared(…);                                   -- one batch, one round trip:
WITH hold AS (
  UPDATE usage.budget_period SET reserved_amount = reserved_amount + @hold
   WHERE tenant_id = @t AND period_start = @p AND @hold > 0
     AND (limit_amount IS NULL OR spent_amount + reserved_amount + @hold <= limit_amount)
  RETURNING 1)
SELECT EXISTS (SELECT 1 FROM hold), <period row exists>, <Tenant has a Budget>;

COMMIT;
```

- The conditional `UPDATE` is the last statement before `COMMIT`, so a Tenant with a Budget holds its period row lock for one round trip. Concurrent requests at the boundary still serialize on that row, so the check is never stale.
- Held → commit. The row exists but the hold does not fit → roll back, `402 budget_exceeded`. No row and no Budget → commit: nothing to check. No row but a Budget (the first request of a month) → roll back, create the row under the exclusive lock, and reserve again once.
- `@hold` is the worst-case cost of the **most expensive** Candidate in the Routing Decision, multiplied by `min(maxAttempts, 2)`. A Fallback can then normally proceed without a second reservation round-trip. If an Attempt sequence would exceed the hold, for example many ambiguous Attempts, the executor tops up the Reservation with the same conditional `UPDATE` before the next Attempt. If the top-up fails, the request stops with `budget_exceeded`.
- The limit comes from `usage.budget` (set by `IBudgets.SetMonthlyLimitAsync`) when the period row is created, and setting it also updates the current period's `limit_amount`. A refused reservation rolls the transaction back, and the request is then recorded on its own as `failed` with `error_code = budget_exceeded` and no Reservation, so refusals stay visible.
- `expires_at` is the Deadline plus 1 minute. A stream is bounded only by the Provider once it has started, so its Reservation expires 15 minutes after the Deadline.
- Top-up: each Attempt commits its Candidate's worst case before it starts. While the commitments stay within the hold nothing happens. Beyond it, `IUsageLedger.TopUpAsync` raises the Reservation's amount by the excess, then runs the same shared lock and conditional `UPDATE` (refused only if a row exists and the excess does not fit).
- A Tenant without a Budget still gets a Reservation row of the same amount, so the logic has one path and the sweeper can recover abandoned requests.
- A Budget set while requests are in flight: the row it creates counts their held Reservations, and they are released from it when they settle. A Budget removed while requests are in flight: their settlement finds no row and moves nothing.

**Settle** (request step 11, one transaction):

```
UPDATE inference_request SET status, totals, routing_decision, …
INSERT provider_attempt × n, usage_record × n
INSERT outbox_event('usage.request_completed', {requestId, tenantId, …})
SELECT pg_advisory_xact_lock_shared(…)                                          -- one batch:
WITH released AS (UPDATE reservation SET status = 'settled' … RETURNING amount)
UPDATE budget_period SET reserved_amount -= released.amount, spent_amount += @actualTotal   -- no row: no-op
COMMIT
```

The `budget_period` statement is again the last before `COMMIT`.

If settlement fails (e.g. the database is briefly unavailable), it is retried in-process with backoff for up to 10 s, then handed to the sweeper. The client response is **not** delayed past that. For non-streaming responses settlement happens after the response is written, and for streaming responses after `[DONE]`.

**Sweep** (worker, every 30 s; the core is `IReservationSweeper.SweepAbandonedAsync`, run by the recurring `usage.sweep_reservations` job): Reservations with `status='held' AND expires_at < now()` mean the instance died or settlement failed for good. For each one, in its own transaction, the sweeper:
- marks the request `abandoned`, `error_code = internal_error`, `usage_status = unavailable`
- writes one `usage_record` with `usage_source = unavailable` and cost = the held amount (pessimistic: treated as possibly billed), and the `usage.request_completed` outbox event
- locks the request row before the Reservation, as settlement does, and does nothing if the request is no longer `running`
- marks the Reservation `expired`
- last, under the shared Budget lock, moves the held amount from `reserved_amount` to `spent_amount` (no row: nothing to move)

**Spend precision.** Because Reservations use the worst case, a Tenant can be refused a little *before* reaching the limit. They are never allowed meaningfully past it. The overshoot is bounded by the difference between estimated and Provider-reported tokens, and the integration tests measure it. This is the strict-financial-guarantee caveat from FR-07.

## 4. Ambiguous outcomes

An ambiguous Attempt (a timeout or reset after the request was sent) gets:
- `status = ambiguous`, `usage_source = unavailable`
- `input_tokens` = the estimate, `output_tokens` = NULL
- cost = that Attempt's worst-case cost (`reservation_upper_bound`)

This follows FR-05: "treat ambiguous provider timeouts as potentially billable outcomes". The dashboard shows ambiguous cost as a separate figure so operators can reconcile it against Provider invoices.

## 5. Cost calculation

```
cached      = cached_input_tokens ?? 0
input_cost  = round_half_even(((input_tokens − cached) × price.input_per_million
                             + cached × (price.cached_input_per_million ?? price.input_per_million))
                            / 1_000_000, 10)
output_cost = round_half_even(output_tokens × price.output_per_million / 1_000_000, 10)
total_cost  = input_cost + output_cost
```

- The price used is the one in force at **Attempt start**.
- Cached Input Tokens are only ever *reported*. Estimated and unavailable usage has `cached_input_tokens = null`, so all input is priced at the full rate.
- The **worst case** used for Reservations assumes nothing is cached, because the cache hit isn't known until the Provider answers. Settlement releases the difference.
- `decimal` is used throughout. `double` is never used for money.
- A pure `CostCalculator` in the Usage module's `Domain/` is the only implementation, used by routing (worst case), settlement and evaluation. Other modules reach it through `ICostCalculator` in `Usage/Contracts`, with `IInputTokenEstimator` for input tokens. Each of `input_cost` and `output_cost` is rounded half-to-even on its own before they are added. A cached count above the input count is clamped to it. If a request has no `max_tokens`, the output ceiling is the policy's `defaultMaxOutputTokens`, else 1024.
- **Input estimate.** The tokenizer named by the Provider Model's `capabilities.tokenizer` (`o200k_base` or `cl100k_base`) counts each message, plus 4 tokens per message. With no (or an unknown) tokenizer the estimate is `ceil(totalUtf8Bytes / 3) + 4 per message`, recorded as `bytes_div_3`.

**Phase 3 recording.** `OpenAsync` reserves, `SettleAsync` costs every Attempt through `CostCalculator` (price = the `model_price` row in the configuration snapshot the request was routed on, so the Price in force when the request began), inserts one `usage_record` per Attempt that reached a Provider, fills `provider_attempt.estimated_cost` and the request totals from them, releases the Reservation, adds the sum to `spent_amount` and writes the outbox event, all in one transaction. A non-streaming success without reported usage is `estimated` (input from the request, output from the text received). An ambiguous Attempt is `unavailable` and costed at its worst case, with the request's output ceiling.

**Phase 2 recording (superseded where Phase 3 says so).** `IUsageLedger.OpenAsync` (step 8) inserts the `inference_request` with `status = running` and `usage_status = unavailable`; `SettleAsync` (step 11) updates it and inserts the `provider_attempt` rows in one transaction, and does nothing if the request is no longer `running`. Totals, `attempt_count`, `had_fallback`, `selected_provider_model_id` and `usage_status` are derived from the Attempts (an Attempt skipped for an open circuit is excluded). Reservations, `usage_record` rows and budget movement are added behind the same two methods in Phase 3. A request that fails before any Attempt (unknown alias, no eligible model) is recorded as `failed` with zero Attempts. Settlement runs on its own 10 s token, retried with backoff, whatever the client did.

## 6. Aggregation

The worker relays `outbox_event` rows to in-process consumers. The `UsageRollupConsumer` upserts `usage_hourly` (one row per Tenant, Provider Model, alias, Request Origin and hour) and records the event in `processed_event` **in the same transaction**, so redelivery is a no-op (NFR-06).

Dashboard reads (`IUsageQueries`, `Usage.Domain.UsageRange`): a range of 24 h or less is read entirely from `inference_request`, so its percentiles are exact (`percentile_cont`). A longer range reads `usage_hourly` for its whole hours and `inference_request` for the partial hours at either edge, which also covers the current hour the rollup may not have caught up with; its percentiles are estimated from the merged histogram buckets (`exact: false`). Both paths attribute a request the same way, so they agree on totals. `running` requests are not counted. Because the rollup keeps the Request Origin, the Overview and usage reports can separate evaluation traffic from API traffic over any range; filters the rollup does not keep (status, error code, had-fallback, Provider) read the raw rows only.

## 7. What is *not* guaranteed (documented limitations)

- Estimated Cost is not the Provider's bill. Cache *reads* are priced (§5), but other Provider-side discounts and rounding are not modelled in the MVP.
- **Cache writes are not priced.** Some Provider Models (OpenAI GPT-5.6+, Anthropic via OpenRouter) charge 1.25×–2× the input rate for tokens written to the prompt cache. The MVP prices them at the input rate, so the first request of a cached conversation is *under*estimated. The error is bounded by the write surcharge, and later cache reads usually offset it.
- Reserving with the worst case can refuse requests slightly before the budget is fully used.
- A request in flight when its Tenant's budget is lowered completes under the old reservation. A request opened before its Tenant had a Budget was never checked against it, though the new period row counts its hold and its cost.
