# Vigilo.Classification

## Scope

`Vigilo.Classification` owns Vigilo's local-first LLM inference harness. It builds an immutable labelled email context (budgeted to the selected model's practical input limit), executes one bounded thinking-mode model call governed by an ordered decision-procedure prompt with worked examples, negotiates the exact verdict JSON Schema with the runtime, parses the typed trinary verdict, verifies exact source evidence for every positive conclusion, permits one structured repair, applies centralized product policy, maps into `ClassificationResult`, and provides the sanitized offline evaluator plus a persistent content-addressed response cache. It continues to own deterministic MIME content normalization.

This project should stay model-provider independent except for calling `ILocalAiClient`. It should not fetch mail, persist workflow state, or make UI decisions.

Dependency injection resolves `IMessageClassifier` to `HarnessMessageClassifier`. The previous five-node semantic graph, its per-node prompts, and its adjudication stage have been removed in favor of the single grounded verdict.