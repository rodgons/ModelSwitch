---
status: accepted
supersedes: ADR-0009 (the error-envelope consequence only)
---

# Inference API errors are Problem Details that also carry the OpenAI `error` member

The project constitution requires every validation error to return 400 with Problem Details (RFC 9457). ADR-0009 had given `/v1/*` errors the plain OpenAI error envelope, which breaks that rule. Instead, every `/v1/*` error body is now an `application/problem+json` document (`type`, `title`, `status`, `detail`, `instance`, plus the `errorCode` and `requestId` extensions shared with the admin API). It also carries OpenAI's `error` object as an extension member. OpenAI SDKs parse `error` from any JSON error body, whatever its `Content-Type`, so drop-in compatibility (FR-02) is kept. Clients that understand Problem Details read the standard members.

We rejected plain Problem Details because OpenAI SDKs would lose the error code and message. We also rejected an exemption in the constitution: one error format across `/v1` and `/admin` is simpler to implement and test.

## Consequences

- `/v1/*` and `/admin/*` share one error writer in `SharedKernel`. The only difference is that `/v1/*` adds the `error` member.
- `error.code` and the top-level `errorCode` always hold the same value from the closed set in [api.md §2](../api.md#2-error-codes).
- Mid-stream SSE error events (`stream_interrupted`) are not HTTP responses and keep the bare `{"error":{…}}` event shape.
- Everything else in ADR-0009 (request shape, `X-ModelSwitch-*` headers, rejecting unsupported fields) still applies.
