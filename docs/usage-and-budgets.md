# Usage Accounting and Budgets

This document implements FR-06 and FR-07 (spending) and PRD §14.5 ("durable usage accounting").

## 1. Principles

1. **The Usage Record is authoritative. Telemetry is not.** Metrics and traces may be sampled, dropped or delayed. Usage Records are written transactionally and never sampled.
2. **Every Attempt gets exactly one Usage Record**, including failed, cancelled and ambiguous Attempts. Every Inference Request gets a `usage_status`.
3. **Estimated Cost is never presented as a charge.** The API field, the UI label and the export header all say "estimated".
4. **History is immutable.** A Usage Record keeps the `model_price_id` it was costed with. Changing a Price creates a new row and leaves recorded costs unchanged.

## 2. Usage Source

| `usage_source` | When | `estimate_method` |
|---|---|---|
| `reported` | The Provider returned token counts | null |
| `estimated` | No reported counts, but the gateway knows the input and can count the output it forwarded | `tokenizer:<name>` or `bytes_div_3` |
| `unavailable` | The gateway cannot know, e.g. an ambiguous outcome or an abandoned request | `reservation_upper_bound` (cost) and NULL tokens |

The request-level `usage_status` is the worst of its Attempts' values (`reported` < `estimated` < `unavailable`). It is `partial` when reported and non-reported Attempts are mixed.

## 3. Budget enforcement (Reservation model)

Budgets are monthly (calendar month, UTC) and checked against **reserved + spent** (FR-07: "account for pending and completed requests").

**Reserve** (request step 8, one transaction):

```sql
INSERT INTO usage.budget_period (tenant_id, period_start, limit_amount, …)
  VALUES (…) ON CONFLICT DO NOTHING;                -- lazily create the period row

UPDATE usage.budget_period
   SET reserved_amount = reserved_amount + @hold
 WHERE tenant_id = @t AND period_start = @p
   AND (limit_amount IS NULL OR spent_amount + reserved_amount + @hold <= limit_amount)
RETURNING …;                                        -- 0 rows → 402 budget_exceeded

INSERT INTO usage.reservation (…, amount = @hold, status = 'held', expires_at = deadline + grace);
INSERT INTO usage.inference_request (…, status = 'running');
```

- `@hold` is the worst-case cost of the **most expensive** Candidate in the Routing Decision, multiplied by `min(maxAttempts, 2)`. A Fallback can then normally proceed without a second reservation round-trip. If an Attempt sequence would exceed the hold, for example many ambiguous Attempts, the executor tops up the Reservation with the same conditional `UPDATE` before the next Attempt. If the top-up fails, the request stops with `budget_exceeded`.
- A Tenant without a Budget still gets a Reservation row of the same amount, so the logic has one path and the sweeper can recover abandoned requests.

**Settle** (request step 11, one transaction):

```
UPDATE inference_request SET status, totals, routing_decision, …
INSERT provider_attempt × n, usage_record × n
UPDATE budget_period SET reserved_amount -= @hold, spent_amount += @actualTotal
UPDATE reservation SET status = 'settled'
INSERT outbox_event('usage.request_completed', {requestId, tenantId, …})
```

If settlement fails (e.g. the database is briefly unavailable), it is retried in-process with backoff for up to 10 s, then handed to the sweeper. The client response is **not** delayed past that. For non-streaming responses settlement happens after the response is written, and for streaming responses after `[DONE]`.

**Sweep** (worker, every 30 s): Reservations with `status='held' AND expires_at < now()` mean the instance died or settlement failed for good. For each one, in its own transaction, the sweeper:
- marks the request `abandoned`, `error_code = internal_error`, `usage_status = unavailable`
- writes one `usage_record` with `usage_source = unavailable` and cost = the held amount (pessimistic: treated as possibly billed)
- moves the held amount from `reserved_amount` to `spent_amount`
- marks the Reservation `expired`

**Spend precision.** Because Reservations use the worst case, a Tenant can be refused a little *before* reaching the limit. They are never allowed meaningfully past it. The overshoot is bounded by the difference between estimated and Provider-reported tokens, and the integration tests measure it. This is the strict-financial-guarantee caveat from FR-07.

## 4. Ambiguous outcomes

An ambiguous Attempt (a timeout or reset after the request was sent) gets:
- `status = ambiguous`, `usage_source = unavailable`
- `input_tokens` = the estimate, `output_tokens` = NULL
- cost = that Attempt's worst-case cost (`reservation_upper_bound`)

This follows FR-05: "treat ambiguous provider timeouts as potentially billable outcomes". The dashboard shows ambiguous cost as a separate figure so operators can reconcile it against Provider invoices.

## 5. Cost calculation

```
input_cost  = round_half_even(input_tokens  × price.input_per_million  / 1_000_000, 10)
output_cost = round_half_even(output_tokens × price.output_per_million / 1_000_000, 10)
total_cost  = input_cost + output_cost
```

- The price used is the one in force at **Attempt start**.
- `decimal` is used throughout. `double` is never used for money.
- A pure `CostCalculator` in the Usage module's `Domain/` is the only implementation, used by routing (worst case), settlement and evaluation.

## 6. Aggregation

The worker relays `outbox_event` rows to in-process consumers. The `UsageRollupConsumer` upserts `usage_hourly` and records the event in `processed_event` **in the same transaction**, so redelivery is a no-op (NFR-06).

Dashboard totals for tokens and cost read `usage_hourly` for whole hours and the raw tables for the current partial hour. Latency percentiles use the histogram buckets for ranges longer than 24 h, and exact `percentile_cont` over `inference_request` for shorter ranges.

## 7. What is *not* guaranteed (documented limitations)

- Estimated Cost is not the Provider's bill. Provider-side discounts, cached-input pricing and rounding are not modelled in the MVP.
- Reserving with the worst case can refuse requests slightly before the budget is fully used.
- A request in flight when its Tenant's budget is lowered completes under the old reservation.
