# Vigilo.Configuration

## Scope

`Vigilo.Configuration` provides the shared, version-aware file persistence boundary for local configuration owned by feature projects. It serializes concurrent access by canonical path, validates serialized JSON before commit, flushes and verifies same-directory temporary files, atomically replaces destinations, and captures checksummed snapshots for transaction recovery.

It does not define product settings, own database transactions, encrypt secrets, or depend on WPF, EF Core, mailbox, Outlook, or local-AI implementations. Feature projects remain responsible for their document schemas, migrations, validation, and encryption.