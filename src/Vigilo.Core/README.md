# Vigilo.Core

## Scope

`Vigilo.Core` defines the shared domain model, service contracts, options, model inference-capability records, and cross-project result records for Vigilo. It is the vocabulary used by the app host, email scanner, classifier, local AI client, storage layer, and tests.

This project should remain dependency-free within the solution and should not take infrastructure dependencies such as EF Core, MailKit, WPF, or ONNX Runtime. Implementations belong in the feature projects.