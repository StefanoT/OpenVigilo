# Vigilo.Email

## Scope

`Vigilo.Email` owns mailbox access and mailbox monitoring. It connects to configured IMAP accounts, scans inbox and sent folders, converts provider messages into `FetchedEmail` records, reports scan progress, runs the background monitor loop, and schedules the periodic local-data retention pass.

This project should stay focused on email transport and scheduling. It may call storage and classification services through Core contracts, but it should not own persistence schema, UI state, or model inference details.