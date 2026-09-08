# Vigilo.Storage

## Scope

`Vigilo.Storage` owns local persistence and workflow state. It maps Core entities to SQLite, applies and validates versioned EF Core migrations, manages account settings and protected passwords, maintains the processing ledger, creates and updates tracked items, calculates live reports, detects sent replies, and applies retention policies.

This project should remain the authority for durable workflow transitions. It should not own mailbox transport, UI rendering, prompt construction, or ONNX model execution.