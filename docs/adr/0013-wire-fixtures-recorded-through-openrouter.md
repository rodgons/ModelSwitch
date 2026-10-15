---
status: accepted
---

# Wire fixtures are recorded through OpenRouter, pinned to the OpenAI upstream

The OpenAI-compatible Adapter's contract fixtures were to be recorded from the real OpenAI API ([provider-adapters.md §4](../provider-adapters.md#wire-fixtures)). The project has no OpenAI API key, only an OpenRouter key. OpenAI therefore stays the wire-format reference, but `scripts/record-fixtures` captures it through OpenRouter (`https://openrouter.ai/api/v1`) with every request pinned to the OpenAI upstream (`provider: { only: ["openai"], allow_fallbacks: false }`), so the model's behaviour is OpenAI's and OpenRouter's own routing cannot substitute another upstream. The script reads `OPENROUTER_API_KEY`, defaults to a cheap model (`openai/gpt-4.1-nano`, overridable with `--model`), sends synthetic prompts only, and stamps `"_source": "recorded-openrouter"`.

Until the script is run, the "recorded" fixtures are hand-written from the documented formats and stamped `"_source": "provisional"`, so nobody mistakes them for captures. The script overwrites them. Synthetic fixtures keep `"_source": "synthetic"`.

## Consequences

- Recorded bodies may carry OpenRouter additions that OpenAI does not send (`provider`, `native_finish_reason`, `usage.cost`, `: OPENROUTER PROCESSING` keep-alive comments). The Adapter ignores unknown fields, so this is harmless, and it exercises that rule.
- Error bodies differ. OpenRouter wraps errors as `{"error":{"message","code":<number>}}`, while OpenAI sends a string `code` and a `type`. The recorded 400, 401 and 404 fixtures are therefore OpenRouter's. Hand-written OpenAI-shaped 400, 401, 404 and context-length bodies are kept beside them (`synthetic-openai-*`) so the OpenAI shape stays covered. The Adapter classifies by status first and refines with either shape.
- OpenRouter's context-length error has no OpenAI `code`, so the Adapter also recognises the sentence "maximum context length" in a 400. The wording is confirmed when the context-length fixture is recorded.
- Whether OpenRouter honours `max_completion_tokens` (it documents `max_tokens`) is confirmed by the recording. If it does not, OpenRouter Providers set `UseLegacyMaxTokens`.
- A capture shows OpenAI's behaviour only as OpenRouter relays it. Wire drift that OpenRouter normalises away is not detected. If an OpenAI key appears, re-recording directly against OpenAI supersedes this decision.
- The script costs a fraction of a cent per run, is manual, and is never run in CI.
