---
status: accepted
---

# The inference API is OpenAI Chat Completions-compatible, with ModelSwitch extensions in headers

`POST /v1/chat/completions` accepts and returns the OpenAI Chat Completions shape, so existing OpenAI SDKs work by changing only `base_url` and the API key. Gateway-specific inputs go in `X-ModelSwitch-*` request headers rather than extra body fields: Task Type, a tighter Deadline, and a client correlation ID. Every OpenAI SDK supports default headers, while body extensions need SDK-specific escape hatches and could collide with future OpenAI fields.

## Consequences

- Fields the gateway does not support (tools, `n > 1`, audio, images) are rejected with `400 unsupported_parameter`, not silently dropped (FR-02).
- ~~Errors on `/v1/*` use the OpenAI error envelope. Admin endpoints use RFC 9457 Problem Details.~~ Superseded by [ADR-0012](0012-inference-errors-are-problem-details-with-openai-error-member.md): every error is Problem Details, and `/v1/*` errors also carry the OpenAI `error` member.
