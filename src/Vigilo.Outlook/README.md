# Vigilo.Outlook

## Scope

`Vigilo.Outlook` provides the optional, Windows-only Classic Outlook category adapter. It connects only to a running Outlook COM session, serializes all Object Model access on one dedicated STA thread, enumerates stores, creates missing managed categories, safely matches mail items, applies idempotent category updates, and drains the persistent storage queue in a hosted worker.

It does not launch Outlook or read message bodies and attachments. It has no APIs for folders, rules, moving, flags, reminders, deletion, archiving, replying, or sending. Raw COM objects never leave an STA operation, and normal tests require neither Outlook nor a Microsoft account.