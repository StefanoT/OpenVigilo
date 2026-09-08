# Vigilo.Tests

## Scope

`Vigilo.Tests` contains automated coverage for Vigilo's core workflow, classifier behavior, storage rules and schema evolution, email-derived content normalization, report behavior, retention, and local AI integration smoke testing.

This project should favor fast deterministic tests that do not require real mailboxes or real model files by default. Expensive or environment-dependent checks must stay opt-in.