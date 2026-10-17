# Demo script

The end-to-end demonstration of PRD §15: deploy the gateway, connect an application, inject a Provider failure, watch the Fallback, inspect the usage, and run a Comparison. It needs Docker and about 15 minutes, and no paid account: the Providers are the Mock Provider and a small local Ollama model ([deployment.md §2](deployment.md#2-quick-start-dod-1)).

Commands run from the repository root. The dashboard is at <http://localhost:8080>.

## 1. Deploy

```bash
cp .env.example .env
./scripts/generate-secrets.sh
# In .env, set MODELSWITCH__Bootstrap__OwnerEmail and MODELSWITCH__Bootstrap__OwnerPassword (12+ characters).
docker compose --profile demo --profile ollama up -d --build
docker compose ps        # migrator exited 0; api, web and mock-provider healthy
KEY=$(./scripts/seed-demo.sh)
```

`seed-demo.sh` pulls the local model (about 1 GB) and seeds the `mock` and `ollama` Providers, their Prices, the alias `balanced` (`mock/mock-echo` first, then the Ollama model) and the Tenant `demo`. It prints a new API Key, alone on stdout.

**What to show:** sign in as the Owner. The Providers screen lists both Providers and their models; the Routing screen shows `balanced` and its Candidates in order.

## 2. Connect an application

Any OpenAI-compatible client works. Point it at `http://localhost:8080/v1` with the key:

```bash
curl -s http://localhost:8080/v1/chat/completions \
  -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' \
  -d '{"model":"balanced","messages":[{"role":"user","content":"Say hi"}]}'
```

With the OpenAI Python SDK, the same call is `OpenAI(base_url="http://localhost:8080/v1", api_key=KEY).chat.completions.create(model="balanced", messages=[…])`. Add `"stream": true` to see SSE chunks end with `data: [DONE]`.

**What to show:** the response's `X-Request-Id` header. The request appears on the Requests screen with one Attempt on `mock-echo`.

## 3. Inject a Provider failure

The mock's control endpoint is reachable only on the Compose network, so call it from a throwaway container. This makes `mock-echo` answer 503 to its next two calls:

```bash
docker run --rm --network modelswitch_default curlimages/curl:8.11.1 -s http://mock-provider:8080/__scenarios \
  -H 'Content-Type: application/json' -d '{"model":"mock-echo","fault":"status=503;times=2"}'
```

## 4. Watch the Fallback

Send the same request again (step 2, with `curl -i` to see the headers). It still succeeds, `X-ModelSwitch-Attempts` is `3`, and the answer now comes from the Ollama model (the first call to it can take about 30 s on CPU).

**What to show:** open the request on the Requests screen. The Route Strip shows three Attempts: `primary` and `retry` on `mock-echo`, both `failed` with `transient` / HTTP 503, then `fallback` on the Ollama model, `succeeded`. The request carries the "Fallback used" badge. With `OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-lgtm:4317` in `.env` and the `observability` profile up, the trace link opens the same story in Grafana.

To show the circuit breaker, inject `{"model":"mock-echo","fault":"status=503;times=50"}` and send ten requests. The first five take three Attempts each; their ten failures open the circuit, and from then on routing excludes `mock-echo` (reason `circuit_open`), so each request takes one Attempt on the Ollama model and the mock is not called. The Routing screen's simulate panel shows the open circuit.

## 5. Inspect usage

**What to show:**
- Overview: request count, error rate, latency percentiles and Estimated Cost for the last hour, with the Fallback rate.
- The request detail: each Attempt has its own Usage Record. Failed Attempts carry 0 tokens; the successful one shows input and output tokens with their provenance (`reported` by the Provider, or `estimated` with the method), the Price used, and the Estimated Cost. The Ollama model's Prices are 0, so its cost is $0, which is correct for a local model.
- Tenants → `demo`: usage for the Tenant and, if a Budget is set, spent and reserved against the limit.

## 6. Run a Comparison

Use the classification sample ([samples/evaluations](../samples/evaluations/README.md)): 20 support tickets, four labels.

1. Evaluations → Datasets → New: Tenant `demo`, name `support-intents`, task kind `classification`. Upload `samples/evaluations/classification-support-intents.jsonl` as version 1.
2. Evaluations → Prompts → New: name `support-intents`. Add a version with the messages of `classification-support-intents.prompt.json`.
3. Evaluations → New run: Dataset `support-intents` v1, the prompt v1, evaluator `classification` with the labels of `classification-support-intents.evaluator.json`, temperature 0. Add two configurations: the Provider Model `ollama/qwen2.5:1.5b-instruct-q4_K_M` and the Provider Model `mock/mock-echo`. Set a Max Estimated Cost of $0.10 and start it.

**What to show:** the Comparison view. Both runs used the same Dataset Version, frozen with its content hash. Ollama gets a real accuracy and macro-F1 with a confusion matrix; `mock-echo` repeats the ticket back, so its answers are `unparseable` and it scores 0. Each run shows cost per Case, latency p50/p95 and the frozen configuration hash. Running the same configuration again adds the run-to-run variance. The CPU model takes a few minutes for 20 Cases.

## 7. Clean up

```bash
docker compose --profile demo --profile ollama down -v
```

## Without Ollama

`docker compose --profile demo up -d --build` and `./scripts/seed-demo.sh --no-ollama` run the same demo with `mock/mock-fixed` as the Fallback. Step 6 then compares `mock-fixed` against `mock-echo`, which shows the workflow but not meaningful quality numbers.

## Verification run

Run end to end on 2026-10-17 with images built from `main` just before `d409a67`, on a laptop with a 4-CPU Docker VM, from a fresh `.env`: every service came up healthy and the migrator exited 0; the first request took one Attempt on `mock-echo`; after the injected 503s the same request took three Attempts and was answered by `qwen2.5:1.5b-instruct-q4_K_M` in about 5 s; ten requests with 50 injected 503s took three Attempts each for the first five and one each after the circuit opened; the support-intents Comparison finished with accuracy 0.75 for the Ollama model ($0) and 0 for `mock-echo`.
