# Sample evaluations

Two small synthetic Datasets for trying the Evaluation Lab ([docs/evaluation.md](../../docs/evaluation.md)). Every name and email is invented, and the emails use only the reserved `example.com`, `example.org` and `example.net` domains. There is no personal data here.

| Dataset | Task kind | Cases | Prompt Version | Evaluator |
|---|---|---|---|---|
| `extraction-contacts.jsonl` | `extraction` | 20 | `extraction-contacts.prompt.json` | `extraction-contacts.evaluator.json` (`json_fields` v1) |
| `classification-support-intents.jsonl` | `classification` | 20 | `classification-support-intents.prompt.json` | `classification-support-intents.evaluator.json` (`classification` v1) |

`SampleEvaluationsTests` in `tests/ModelSwitch.UnitTests` checks that every prompt parses, every evaluator config is valid and every Case renders. A Case that is answered perfectly must score 1.

## Extraction: contact details

Each Case's `input.text` is a short support message, and `expected` is `{"name", "email"}`. A value is `null` when the message does not contain it, as in Cases with no name, no email, or neither. The Cases include a few traps: an email in capitals, a `+tag` address, an old address next to the new one, and an email that belongs to a shared desk rather than a person.

The evaluator is `json_fields` with the `casefold` comparator on both fields, so `ANA@Example.com` still matches. `passThreshold: 1` means a Case passes only when both fields are right. The run reports the mean score, per-field accuracy and `parse_failure_rate`. The prompt asks for `responseFormat: json_object`, so target a model that has the JSON-output Capability. A reply wrapped in a ```` ```json ```` fence is still accepted.

## Classification: support-ticket intent

Each Case's `input.ticket` is one support ticket, and `expected` is one of `billing`, `bug`, `feature_request` or `account_access`. There are five of each. The evaluator is `classification` with that label set. The output is trimmed and must equal a label exactly, case-sensitive. Anything else is counted as `unparseable` and fails the Case. The run reports accuracy, per-class precision, recall and F1, macro-F1, and a confusion matrix with an `unparseable` column.

## Running one

Using the admin API ([docs/api.md §3](../../docs/api.md)) with a signed-in Administrator session:

1. Create the Dataset with `POST /admin/datasets` (`{"tenantId": "…", "name": "contacts", "taskKind": "extraction"}`). Then upload the JSONL as its first version with `POST /admin/datasets/{id}/versions`. The upload is validated as a whole, so one bad line rejects it with `400 validation_failed`.
2. Create a prompt with `POST /admin/prompts`. Then post the contents of the `*.prompt.json` file to `POST /admin/prompts/{id}/versions`.
3. Start a Comparison with `POST /admin/evaluations`. Put the contents of the `*.evaluator.json` file in each configuration's `evaluator`:

```json
{ "tenantId": "…", "datasetId": "…", "datasetVersion": 1, "name": "contacts: two aliases",
  "configurations": [
    { "target": { "alias": "balanced" }, "prompt": { "id": "…", "version": 1 },
      "params": { "temperature": 0, "maxOutputTokens": 128 },
      "evaluator": { "kind": "json_fields", "version": "1",
                     "config": { "fields": { "name": { "comparator": "casefold" }, "email": { "comparator": "casefold" } } } } },
    { "target": { "alias": "cheap" }, "prompt": { "id": "…", "version": 1 },
      "params": { "temperature": 0, "maxOutputTokens": 128 },
      "evaluator": { "kind": "json_fields", "version": "1",
                     "config": { "fields": { "name": { "comparator": "casefold" }, "email": { "comparator": "casefold" } } } } } ],
  "concurrency": 4, "maxEstimatedCost": 0.50 }
```

The dashboard's run builder does the same steps with a form. The field names of the dataset and prompt request bodies follow the admin API as it is implemented. If they differ from the sketch above, the API wins.
