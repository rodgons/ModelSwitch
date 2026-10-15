# Research: hosted OpenAI-compatible Providers

Research ticket [#3](https://github.com/rodgons/ModelSwitch/issues/3), feeding open question 6 in [open-questions.md](../open-questions.md) (which hosted Provider is the reference for the demo and the benchmark). The pick itself is decided in [#4](https://github.com/rodgons/ModelSwitch/issues/4), not here.

- **Retrieved:** 2026-10-14, from official docs and pricing pages only. No account was created and no Provider API was called.
- **Scope:** OpenAI, Groq, Together, OpenRouter. For each one: Chat Completions compatibility for the fields the gateway uses ([api.md §1](../api.md#1-inference-api), [provider-adapters.md §2](../provider-adapters.md#2-openai-compatible-adapter)), the Price of small and medium Provider Models, free-tier and rate limits, and what an account needs for demo and benchmark use.
- **Volatility warning:** model line-ups and Prices change every few months. Treat every number below as a snapshot and re-check it before seeding `model_price`.

## 1. Compatibility matrix

| Feature | OpenAI | Groq | Together | OpenRouter |
|---|---|---|---|---|
| Base URL | `https://api.openai.com/v1` | `https://api.groq.com/openai/v1` | `https://api.together.ai/v1` | `https://openrouter.ai/api/v1` |
| `stream_options.include_usage` | Yes (defines the spec). Final chunk has `choices: []` and the usage [O2] | Yes. Final chunk before `[DONE]` carries usage, `choices` empty, `usage: null` on the others [G4] | **Not documented.** Example chunks carry `usage: null` and extra non-OpenAI fields (`token`, `stats`, `generated_text`) [T4] | Usage is **always** sent, once, in the last chunk before `[DONE]`. `include_usage` is accepted but deprecated. The last chunk has a **non-empty** `choices` array (empty delta + `finish_reason`) [R5] |
| `response_format: json_object` | Yes [O2] | Yes [G3] | Yes in the API reference [T3]. Not mentioned in the structured-outputs guide [T5] | Yes [R1] |
| `response_format: json_schema` | Yes, `strict` supported [O2] | Yes. `strict: true` only on `openai/gpt-oss-20b`, `gpt-oss-120b`, `qwen/qwen3.8-27b`. **Not supported together with streaming** [G3] | Yes (also `regex`). Supported models not listed; `strict` and streaming not addressed [T5] | Yes, but support is per upstream endpoint. Unsupported endpoint → error, unless `provider.require_parameters: true` routes around it. Streaming supported [R6] |
| `max_completion_tokens` | Yes. `max_tokens` deprecated and not compatible with o-series [O2] | Yes. `max_tokens` deprecated [G4] | **Not in the API reference.** Only `max_tokens` is documented [T3] | Yes, same semantics as `max_tokens` [R1] |
| `seed` | Yes, best effort (watch `system_fingerprint`) [O2] | Yes, best effort [G4] | Yes, best effort across replicas [T2] | Yes, "not guaranteed for some models" [R1] |
| Error body | `{error:{message,type,param,code}}` [O3] | `{error:{message,type}}` (no `code` documented) [G5] | Not documented [T6] | `{error:{code (numeric), message, metadata?}}` [R3] |
| Errors after a 200 | Not documented for Chat Completions | Not documented | Not documented | **Yes, both streaming and non-streaming.** Stream: SSE event with top-level `error` and `finish_reason: "error"`. Non-streaming: a 200 body with `error` and no `choices` [R3] |
| `Retry-After` on 429 | "May be present" on temporary rate limits, also on 503 overload. Not guaranteed [O1] | Always set on 429, seconds [G2] | Not documented; docs say "use exponential backoff" [T7] | "May" be sent on 429, 503, and 402 with `limit_source = openrouter_in_flight_budget` [R3] |
| Rate-limit headers | `x-ratelimit-{limit,remaining,reset}-{requests,tokens}` plus project-token variants [O1] | Same names. `-requests` = per **day**, `-tokens` = per minute [G2] | Not documented [T7] | Not documented |
| Non-standard statuses | 429 is also used for **non-retryable** billing errors (`credit_balance_exhausted`, `organization_spend_limit_exceeded`) [O3] | 498 (flex capacity), 499 (caller cancelled), 424 [G5] | 402 (spend limit), **403 for context length exceeded**, 524 (Cloudflare timeout), 529 [T6] | 402 (credits), 403 (guardrail/moderation), 502 (model down), 503 (no provider meets routing requirements) [R3] |
| Unsupported fields | — | `logprobs`, `logit_bias`, `top_logprobs`, `messages[].name` → 400. `n` must be 1. `temperature: 0` becomes `1e-8` [G1] | `logit_bias` on most models. `service_tier`, `store`, `metadata`, `prediction` accepted and ignored [T2] | Depends on upstream. Without `require_parameters`, an upstream may ignore params it does not support [R7] |

## 2. Prices of small and medium Provider Models (USD per 1M tokens, standard tier)

| Provider | Provider Model | Input | Cached input | Output | Source |
|---|---|---|---|---|---|
| OpenAI | `gpt-5-nano` | 0.05 | 0.005 | 0.40 | [O4] |
| OpenAI | `gpt-6-luna` | 0.10 | 0.01 | 0.50 | [O4] |
| OpenAI | `gpt-4.1-nano` | 0.10 | 0.025 | 0.40 | [O4] |
| OpenAI | `gpt-4o-mini` | 0.15 | 0.075 | 0.60 | [O4] |
| OpenAI | `gpt-5.4-nano` | 0.20 | 0.02 | 1.25 | [O4] |
| OpenAI | `gpt-5-mini` | 0.25 | 0.025 | 2.00 | [O4] |
| OpenAI | `gpt-4.1-mini` | 0.40 | 0.10 | 1.60 | [O4] |
| OpenAI | `gpt-5.4-mini` | 0.75 | 0.075 | 4.50 | [O4] |
| Groq | `openai/gpt-oss-20b` | 0.075 | — | 0.30 | [G6] |
| Groq | `openai/gpt-oss-120b` | 0.15 | — | 0.60 | [G6] |
| Groq | `qwen/qwen3.8-27b` (preview) | 0.80 | — | 4.00 | [G6] |
| Groq | `llama-3.1-8b-instant`, `llama-3.3-70b-versatile` | "Contact sales" on Groq's page; OpenRouter lists Groq at 0.05/0.08 and 0.59/0.79 | — | | [G6], [R8] |
| Together | Llama 3 8B Instruct Lite | 0.14 | — | 0.14 | [T1] |
| Together | Qwen3.5 9B | 0.17 | — | 0.25 | [T1] |
| Together | `openai/gpt-oss-120b` | 0.15 | — | 0.60 | [T1] |
| Together | Llama 3.3 70B | 1.04 | — | 1.04 | [T1] |
| OpenRouter | `openai/gpt-oss-20b` (10 upstreams) | 0.018–0.075 | | 0.09–0.30 | [R8] |
| OpenRouter | `openai/gpt-oss-120b` (21 upstreams) | 0.03–0.35 | | 0.15–0.95 | [R8] |
| OpenRouter | `meta-llama/llama-3.1-8b-instruct` (5 upstreams) | 0.02–0.22 | | 0.04–0.29 | [R8] |
| OpenRouter | `meta-llama/llama-3.3-70b-instruct` (10 upstreams) | 0.10–1.04 | | 0.32–2.25 | [R8] |
| OpenRouter | `openai/gpt-5-mini` (OpenAI, Azure) | 0.25–0.275 | | 2.00–2.20 | [R8] |

OpenRouter passes through upstream Prices with no inference markup, but charges 5.5% (min $0.80) on card credit purchases [R4]. The Price of one OpenRouter model id depends on which upstream serves the Attempt [R7].

## 3. Free tier, rate limits and account requirements

| | OpenAI | Groq | Together | OpenRouter |
|---|---|---|---|---|
| Free use | "Free" usage tier in allowed geographies, $100/month usage limit. Its rate limits are not published on the page [O1] | Free plan. Example free limits: `gpt-oss-20b`/`120b` and `qwen3.8-27b` at 30 RPM, 1K RPD, 8K TPM, 200K TPD [G2] | **None.** "Together AI does not currently offer free trials" [T8] | `:free` model variants: 20 RPM, 50 requests/day (1,000/day after ≥ $10 of credits bought). A "small free allowance" on signup, amount not stated [R2], [R4] |
| Paid entry | Build tier after $5 in credit purchases: $500/month, e.g. 5,000 RPM / 1M–2M TPM [O1] | Developer plan: payment method on file, pay-as-you-go, spend limits. `gpt-oss-*` at 1K RPM, 500K RPD, 250K TPM [G2], [G7] | Prepaid: **$15 first purchase**, $5 after. API suspended at zero balance. Dynamic rate limits, no published numbers [T8], [T7], [T9] | Prepaid credits. Paid variants have "no platform-level request cap" [R2] |
| Limit scope | Organization and project | Organization [G2] | Account | Account / key |

## 4. What this means for ModelSwitch

These are findings that affect the design whichever Provider is picked.

1. **429 is not always transient on OpenAI.** `credit_balance_exhausted` and `organization_spend_limit_exceeded` come back as 429 and must not be retried [O3]. The OpenAI-compatible Adapter must classify a 429 by `error.code`, not by status alone. See [routing-and-resilience.md §3](../routing-and-resilience.md#3-failure-classification).
2. **A 200 non-streaming body can be an error** on OpenRouter [R3]. [provider-adapters.md §2](../provider-adapters.md#2-openai-compatible-adapter) covers only "an error object inside a 200 stream". The contract test suite would need a non-streaming case too.
3. **Usage may arrive on a chunk that also has `choices`** (OpenRouter [R5]), or on every chunk (Together's examples [T4]). The SSE parser must not assume usage only appears on an empty-`choices` chunk.
4. **Streaming and JSON-schema output are separate Capabilities on Groq** [G3]. A Provider Model can support `json_schema` without supporting it while streaming, so a single "JSON-schema" Capability is too coarse for Groq.
5. **`max_completion_tokens` needs the per-Provider fallback flag** already in the Adapter design: Together documents only `max_tokens` [T3].
6. **Status codes outside the contract suite's list** appear: 402 (Together, OpenRouter), 403 meaning context length (Together), 424/498/499 (Groq), 524/529 (Together). Each needs a failure class.
7. **OpenRouter routes and falls back on its own** across upstreams by default [R7]. That hides failures from Provider Health and the circuit breakers, and makes the Price of an Attempt depend on an upstream the gateway cannot see. To keep Estimated Cost honest the Provider Model would have to pin one upstream (`provider.only` / `order` with `allow_fallbacks: false`). OpenRouter also reports `usage.cost` per request, which is its actual charge. ModelSwitch would still compute Estimated Cost from the Price, but `usage.cost` is a useful cross-check for the benchmark.
8. **Cached-input Prices** are a third Price component at OpenAI, Together (DeepSeek) and OpenRouter. This is evidence for open question 2. Together returns cached tokens in two different `usage` shapes depending on the model [T2].

### Which options look strongest (input to #4, not a decision)

- **Groq** has the only free plan with documented per-model limits, standard rate-limit headers, and an always-present `Retry-After` on 429. Its cheap `gpt-oss-20b` makes a large benchmark affordable. Weak points: no JSON schema while streaming, 30 RPM / 8K TPM on the free plan is too low for a load benchmark, and its Llama Prices are not public on its own page.
- **OpenAI** is the reference implementation of the wire format, so its fixtures double as the canonical contract. A $5 purchase unlocks the Build tier with ample limits. Weak point: the 429-for-billing behaviour, and its newest models have the highest Prices of the four.
- **OpenRouter** gives the widest model choice and `:free` models for a no-cost demo, but its own routing and fallbacks overlap with the gateway's job and blur Price per Attempt. It fits better as a second Provider than as the reference.
- **Together** needs a $15 prepayment, documents neither `include_usage` nor `max_completion_tokens` nor its rate limits, and has the most non-standard status codes. It looks weakest as the reference.

## 5. Could not verify

- OpenAI's Free-tier rate limits (only the $100/month cap is published) [O1].
- Groq's free-plan numbers come from the data embedded in the page source; the rendered text did not label the tables clearly [G2]. Groq does not say whether the Free plan needs a card.
- Whether Together honours `stream_options.include_usage`, accepts `max_completion_tokens`, sends `Retry-After` or rate-limit headers, and what its error body looks like.
- OpenRouter Prices were read from the data embedded in each model page, not from the rendered page or the `/models` API. The size of the signup allowance is not stated.
- Groq and Together pricing pages are partly rendered by JavaScript; figures above are what the fetched pages contained.
- Nothing was tested against a live endpoint. Each behaviour above should be confirmed when the wire fixtures for the Adapter contract tests are recorded.

## Sources

All retrieved 2026-10-14.

- **[O1]** OpenAI, Rate limits: https://developers.openai.com/api/docs/guides/rate-limits
- **[O2]** OpenAI, Create chat completion (API reference): https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create (read via search snippets; direct fetch returned 404) and Chat Completions streaming events: https://developers.openai.com/api/reference/resources/chat/subresources/completions/streaming-events
- **[O3]** OpenAI, Error codes: https://developers.openai.com/api/docs/guides/error-codes
- **[O4]** OpenAI, Pricing: https://developers.openai.com/api/docs/pricing
- **[G1]** Groq, OpenAI compatibility: https://console.groq.com/docs/openai
- **[G2]** Groq, Rate limits: https://console.groq.com/docs/rate-limits
- **[G3]** Groq, Structured outputs: https://console.groq.com/docs/structured-outputs
- **[G4]** Groq, API reference: https://console.groq.com/docs/api-reference
- **[G5]** Groq, Errors: https://console.groq.com/docs/errors
- **[G6]** Groq, Models: https://console.groq.com/docs/models
- **[G7]** Groq, Billing FAQs: https://console.groq.com/docs/billing-faqs
- **[T1]** Together, Pricing: https://www.together.ai/pricing
- **[T2]** Together, OpenAI API compatibility: https://docs.together.ai/docs/openai-api-compatibility
- **[T3]** Together, Chat completions API reference: https://docs.together.ai/reference/chat-completions-1
- **[T4]** Together, Chat overview (streaming example): https://docs.together.ai/docs/inference/chat/overview
- **[T5]** Together, Structured outputs: https://docs.together.ai/docs/inference/chat/structured-outputs
- **[T6]** Together, Error codes: https://docs.together.ai/docs/error-codes
- **[T7]** Together, Rate limits: https://docs.together.ai/docs/rate-limits
- **[T8]** Together, Credits: https://docs.together.ai/docs/billing-credits
- **[T9]** Together, Changelog (tiers retired, dynamic rate limits): https://docs.together.ai/docs/changelog
- **[R1]** OpenRouter, Parameters: https://openrouter.ai/docs/api/reference/parameters
- **[R2]** OpenRouter, Limits: https://openrouter.ai/docs/api/reference/limits
- **[R3]** OpenRouter, Errors and debugging: https://openrouter.ai/docs/api/reference/errors-and-debugging
- **[R4]** OpenRouter, FAQ: https://openrouter.ai/docs/faq
- **[R5]** OpenRouter, Usage accounting: https://openrouter.ai/docs/cookbook/administration/usage-accounting and API overview: https://openrouter.ai/docs/api/reference/overview
- **[R6]** OpenRouter, Structured outputs: https://openrouter.ai/docs/guides/features/structured-outputs
- **[R7]** OpenRouter, Provider selection: https://openrouter.ai/docs/guides/routing/provider-selection
- **[R8]** OpenRouter model pages: https://openrouter.ai/openai/gpt-oss-20b, https://openrouter.ai/openai/gpt-oss-120b, https://openrouter.ai/meta-llama/llama-3.1-8b-instruct, https://openrouter.ai/meta-llama/llama-3.3-70b-instruct, https://openrouter.ai/openai/gpt-5-mini
