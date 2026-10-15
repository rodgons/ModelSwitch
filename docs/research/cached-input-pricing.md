# Research: cached-input token pricing

Ticket: [#5](https://github.com/rodgons/ModelSwitch/issues/5). Feeds open question OQ2 in [`docs/open-questions.md`](../open-questions.md) (decision: ticket #6).
All sources were retrieved on **2026-10-14**. No Provider API was called. Everything below comes from documentation, SDK type definitions generated from the vendors' OpenAPI specs, or open source code.

This note answers the factual question only: do the Providers report cached input tokens, under which usage fields, at what discount, and on streaming final usage chunks too? It also estimates the cost of ignoring them. It does not decide how ModelSwitch should model this.

## TL;DR

- **Every candidate Provider reports cached input tokens.** On the OpenAI-compatible APIs the field is `usage.prompt_tokens_details.cached_tokens`. Ollama native uses `prompt_eval_cached_count`. In every source that shows an example, the cached count is a **subset of** `prompt_tokens` / `prompt_eval_count`, not added on top.
- **Discounts are large and vary by model, not by Provider.** OpenAI charges 0.1x the input rate on the GPT-5 family, 0.25x on gpt-4.1, 0.5x on gpt-4o and 0.05x on one model. Groq charges 0.5x. Together sets a cached rate per model, and some models have none. OpenRouter passes through the upstream rate. Newer OpenAI models (GPT-5.6+) also **charge cache writes at 1.25x**.
- **If the gateway ignores caching, Estimated Cost overstates the Provider's charge.** For a multi-turn chat at 90% cache hit, a worked example gives about 3.4x on gpt-5.4 and about 1.7x on 50%-discount models. The input-cost ceiling is 1/(cached-rate ratio): 10x at 0.1x, 20x at 0.05x. Cache writes, where they apply, move the error the other way (underestimation).

## 1. Per Provider

| Provider | Reports cached input? | Field (non-streaming) | On streaming final usage chunk? | Discount on cached input | Cache-write charge |
|---|---|---|---|---|---|
| OpenAI (Chat Completions) | Yes, automatic on supported models | `usage.prompt_tokens_details.cached_tokens` (also `cache_write_tokens`) | Yes. The final chunk carries the same `CompletionUsage` object [S2] | Per model: 0.1x (gpt-5.x), 0.25x (gpt-4.1), 0.5x (gpt-4o), 0.05x (GPT-6.1 Sol) [S1][S3] | None before GPT-5.6. 1.25x on GPT-5.6+ [S1][S3] |
| Groq | Yes, but only on `openai/gpt-oss-20b`, `gpt-oss-120b`, `gpt-oss-safeguard-20b` [S4] | `usage.prompt_tokens_details.cached_tokens` [S4] | Probably. The chunk type has `usage` and `x_groq.usage` (`CompletionUsage`) [S5]. **Unverified at runtime** | 50% [S4] | None ("no additional fees") [S4] |
| Together (serverless) | Yes, automatic [S6] | **Depends on the model:** `usage.prompt_tokens_details.cached_tokens` *or* flat `usage.cached_tokens` [S6][S7] | **Unverified.** The docs do not show a streaming final usage chunk with cache fields | Per model, from the "Cached input pricing" column. For example Kimi K3 is $3.00 input and $0.30 cached (0.1x). Models with no cached price bill every token at the full rate [S6][S8] | None [S6] |
| OpenRouter | Yes, passed through from upstream [S9][S10] | `usage.prompt_tokens_details.cached_tokens`, `cache_write_tokens`. Also `cost` and `cost_details.upstream_inference_cost` [S10] | Yes. Usage is always included "in the last SSE message". `stream_options.include_usage` is deprecated and has no effect [S10] | That of the upstream Provider: OpenAI 0.25x/0.5x, Anthropic 0.1x, DeepSeek 0.1x, Gemini 0.25x, Groq 0.5x, Grok/Moonshot 0.25x… [S9] | Depends on upstream: Anthropic 1.25x (5 min) or 2x (1 h), OpenAI GPT-5.6+ 1.25x [S9] |
| Ollama (`/api/chat`) | Yes. Local KV-cache reuse | Final `done: true` object: `prompt_eval_cached_count` (pointer, omitted when absent) alongside `prompt_eval_count` [S11][S12] | Yes. It is part of the `Metrics` embedded in every `ChatResponse`, and metrics arrive on the final streamed object [S12] | Not applicable. There is no per-token charge (local) | Not applicable |

### Notes and caveats

- **Subset, not additive.** Groq's example has `prompt_tokens: 4641` and `cached_tokens: 4608` [S4]. Together's has `prompt_tokens=3417` and `cached_tokens=3327`, and it bills the "remaining 90" at the standard rate [S6]. OpenAI's cost snippet computes `inputTokens - cachedTokens - cacheWriteTokens` [S1]. Ollama's CLI computes `uncached = PromptEvalCount - cached` [S12]. So for all of them, `uncached = prompt_tokens − cached_tokens (− cache_write_tokens on OpenAI GPT-5.6+)`. For OpenAI this is **inferred from code, not stated as a rule**.
- **OpenAI granularity.** Before GPT-5.6, `cached_tokens` is rounded down to a multiple of 128. GPT-5.6+ reports the exact boundary. The minimum cacheable prompt is 1,024 tokens on GPT-5.6+ and varies by request settings on earlier models [S1]. Short single-turn prompts therefore usually show `cached_tokens: 0`.
- **OpenAI streaming caveat.** The final usage chunk "may not" arrive if the stream is interrupted or cancelled [S2]. That gap is the same one ModelSwitch already has for `usage_source = estimated`. It is not specific to caching.
- **Together's two shapes.** Together says a client that reads only one location "will return `0` … with no error message" [S7]. An Adapter has to check both `prompt_tokens_details.cached_tokens` and top-level `cached_tokens`.
- **Groq's two locations.** In the Groq SDK, `ChatCompletionChunk` has a top-level `usage` and an `x_groq.usage` [S5]. I could not confirm from the docs which one the final chunk populates, or whether `stream_options.include_usage` is honoured. The API reference collapses `stream_options` properties [S13]. Read both.
- **OpenRouter's `cost`** is "the total amount charged to your account" [S10]. That is a Provider-reported charge, which ModelSwitch's Estimated Cost explicitly is not. It is mentioned only for completeness.
- **OpenRouter multipliers** were rendered from template placeholders on the page [S9]. The resolved values above are what the fetch returned and may not be exact for every model.
- **Ollama `/api/chat` docs** show no cache field in the chat examples. The field is documented only under `/api/generate` [S11]. The Go source shows that `ChatResponse` embeds the same `Metrics` struct, which includes `PromptEvalCachedCount` [S12]. Ollama's OpenAI-compatible endpoint maps it to `prompt_tokens_details.cached_tokens` [S14].
- Responses API (OpenAI) uses `usage.input_tokens_details.cached_tokens` instead [S1]. That is out of scope here, because the gateway's Adapter speaks Chat Completions (`docs/provider-adapters.md`).

## 2. Size of the overestimation if ignored

Current design: `usage.usage_record` stores `input_tokens` and `output_tokens`, and `registry.model_price` has `input_per_million` and `output_per_million` (`docs/data-model.md`). `docs/usage-and-budgets.md` §7 already says cached-input pricing "is not modelled in the MVP". If `input_tokens = prompt_tokens` and everything is priced at the full input rate:

```
overestimate (absolute) = cached_tokens × (input_price − cached_price)
                        = cached_tokens × input_price × (1 − r)        r = cached_price / input_price
```

The ceiling on the input-cost ratio (all input cached) is `1/r`: 2x at r = 0.5, 4x at 0.25, 10x at 0.1, 20x at 0.05.

### Worked example

A multi-turn chat or agent request: 20,000 prompt tokens, 18,000 of them cached (90% hit, typical of a growing conversation with a stable system prompt), 500 output tokens. Prices per 1M are from [S3]. The "50% model" uses relative prices (input 1, output 4) because Groq's absolute prices were not retrieved.

| Model (input / cached / output per 1M) | Estimated Cost ignoring cache | Cache-aware cost | Ratio |
|---|---|---|---|
| gpt-5.4 ($2.50 / $0.25 / $15.00) | $0.0575 | $0.0170 | **3.4x** |
| gpt-5.4, 100 output tokens | $0.0515 | $0.0110 | **4.7x** |
| gpt-4o ($2.50 / $1.25 / $10.00) | $0.0550 | $0.0325 | **1.7x** |
| 50%-discount model (Groq gpt-oss, rel. 1 / 0.5 / 4) | 22,000 u | 13,000 u | **1.7x** |
| Any model, prompt < ~1,024 tokens or first turn | same | same | 1.0x |

The error is therefore workload-dependent. It is close to zero for one-shot short prompts. It becomes several-fold for long multi-turn or agent traffic with stable prefixes, and that is exactly the traffic that dominates spend.

### Direction of the error

- Ignoring **cache reads** always **overestimates**. For Budgets and Reservations this is the conservative direction: a Tenant hits its Budget early and never late.
- Ignoring **cache writes** (OpenAI GPT-5.6+ 1.25x, and Anthropic 1.25x/2x via OpenRouter) **underestimates** the write-heavy first request by `cache_write_tokens × input_price × 0.25` (or ×1.0 for 2x). That is the non-conservative direction. It is bounded, and later cache reads usually offset it.

## 3. Facts that bear on OQ2 (for ticket #6, not a decision)

- The information is available on every candidate Provider, including on streaming final chunks for OpenAI, OpenRouter and Ollama. Groq and Together streaming are unverified.
- The discount is **per ProviderModel**, not per Provider. One `cached_input_per_million` on `registry.model_price` (nullable, meaning no discount) would express OpenAI, Groq, Together and OpenRouter. Cache writes would need a fourth component, and only for a subset of models.
- Field-shape variance (Together flat vs nested, Groq `x_groq.usage`) is an Adapter concern either way, if the count is ever captured.
- Capturing `cached_tokens` on the Usage Record is separable from pricing it: the count could be stored even if the Price ignores it.

## Sources

All retrieved 2026-10-14.

- [S1] OpenAI, Prompt caching guide: https://developers.openai.com/api/docs/guides/prompt-caching (redirected from platform.openai.com/docs/guides/prompt-caching)
- [S2] OpenAI Python SDK, generated from the OpenAPI spec: [`completion_usage.py`](https://github.com/openai/openai-python/blob/main/src/openai/types/completion_usage.py), [`chat_completion_stream_options_param.py`](https://github.com/openai/openai-python/blob/main/src/openai/types/chat/chat_completion_stream_options_param.py), [`chat_completion_chunk.py`](https://github.com/openai/openai-python/blob/main/src/openai/types/chat/chat_completion_chunk.py)
- [S3] OpenAI, Pricing: https://developers.openai.com/api/docs/pricing
- [S4] Groq, Prompt caching: https://console.groq.com/docs/prompt-caching
- [S5] Groq TypeScript SDK, [`src/resources/chat/completions.ts`](https://github.com/groq/groq-typescript/blob/main/src/resources/chat/completions.ts) and [`src/resources/completions.ts`](https://github.com/groq/groq-typescript/blob/main/src/resources/completions.ts)
- [S6] Together, Prompt caching: https://docs.together.ai/docs/inference/chat/prompt-caching
- [S7] Together, OpenAI compatibility (response shape differences): https://docs.together.ai/docs/inference/openai-compatibility
- [S8] Together, Serverless chat models (pricing table): https://docs.together.ai/docs/serverless/models
- [S9] OpenRouter, Prompt caching: https://openrouter.ai/docs/features/prompt-caching
- [S10] OpenRouter, Usage accounting: https://openrouter.ai/docs/use-cases/usage-accounting
- [S11] Ollama, API docs: https://github.com/ollama/ollama/blob/main/docs/api.md (last commit efe43c5, 2026-10-06)
- [S12] Ollama source, [`api/types.go`](https://github.com/ollama/ollama/blob/main/api/types.go) (`Metrics.PromptEvalCachedCount`, embedded in `ChatResponse`)
- [S13] Groq, API reference: https://console.groq.com/docs/api-reference
- [S14] Ollama source, [`openai/openai.go`](https://github.com/ollama/ollama/blob/main/openai/openai.go) (maps the count to `prompt_tokens_details.cached_tokens`)

## Unverified / open

- Groq: which chunk field (`usage` vs `x_groq.usage`) carries the final streaming usage, and whether `stream_options.include_usage` is required.
- Together: whether the streaming final chunk includes cache fields, and which shape it uses.
- Groq absolute per-token prices for gpt-oss (the pricing page fetch returned no table).
- OpenAI: `prompt_tokens` including `cached_tokens` is inferred from the guide's code, not stated.
- OpenRouter's resolved multipliers (the page renders them from templates).
