# Harness evaluation fixtures

`synthetic-multilingual.jsonl` is a sanitized, deterministic regression corpus for the semantic harness. Each JSON line carries dimension labels for recipient action, required reply, deadline, escalation, and final track/ignore policy, plus optional evidence or notes. The format is intentionally suitable for larger teacher-generated datasets without requiring private production mail.

`HarnessEvaluationRunner` consumes this format through the same `IEmailAnalysisHarness` and context factory used by production. Reports contain per-dimension confusion counts, precision, recall, F1, false-positive/false-negative fixture IDs, latency percentiles, model-call cost, and the complete model/harness/policy/prompt version set. `Compare` produces baseline-to-candidate deltas between recorded harness runs.

The checked-in corpus does not claim measured accuracy. Run it with the selected local model before using metrics in product claims.
