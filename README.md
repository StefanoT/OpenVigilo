# Vigilo

Vigilo is a Windows desktop email triage assistant. It monitors multiple IMAP mailboxes, uses a local language model to identify messages requiring action, and groups them by deadline, reply, or review status. Commercial offers stay separate from active work.

Inference runs in process with ONNX Runtime GenAI or through a local OpenAI-compatible server. The client rejects non-loopback initial inference URIs. Email bodies and workflow data remain in local SQLite storage; mailbox passwords use Windows DPAPI. See the [privacy boundary and limitations](DESIGN.md#22-privacy-and-security-design).

- A tray dashboard shows tracked messages, deadlines, account identity, processed-message totals, and the current run log. Summary cards jump to their sections; visible columns are selectable.
- Settings manages accounts, sender rules, monitoring, retention, and local models. Presets cover Yahoo, Gmail, iCloud Mail, AOL Mail, Fastmail, and Zoho Mail, with custom IMAP settings available.
- Message details support review, reply, deadline edits, Done, Dismiss, and Snooze. Manual section choices on Needs review items survive classifier reruns.
- Optional Classic Outlook integration mirrors each tracked message's section as one category through a durable local queue.

## Start here

- [Install and configure Vigilo](docs/getting-started.md): Windows x64 downloads, checksums, and first run.
- [Set up a local model](docs/local-model-setup.md): ONNX settings and the Gemma LiteRT-LM server.
- [Configure Classic Outlook](docs/outlook.md): category tagging, filtering, and troubleshooting.
- [Build, test, and package](docs/development.md): developer prerequisites, locked build scripts, and releases.

Models are supplied separately from the self-contained application packages. Classification can be slow on modest hardware. Normal scans discover recent mail within a one-month window; uncertain results need human review. Read the [current limitations](DESIGN.md#27-current-design-limitations-and-maintenance-hazards) before relying on the workflow.

## Architecture and contributions

[DESIGN.md](DESIGN.md) describes the current system and its rationale, including [module boundaries](DESIGN.md#4-solution-structure-and-dependency-direction), [processing flow](DESIGN.md#6-primary-email-processing-flow), and the [change-impact guide](DESIGN.md#28-change-impact-guide). The [harness reference](docs/architecture/llm-harness.md) covers the classification contract and evaluation workflow.

Account UI changes belong in `SettingsWindow.xaml` and the shared `MainWindowViewModel`. Production classification invalidation uses `IClassificationVersionSource`, combining the harness version with prompt-content hashes; static `ClassificationMetadata` values are fallbacks. See the [ledger version contract](DESIGN.md#111-ledger-gate) before changing classification behavior.

Each project's README under `src/` records its scope and append-only technical-decision history. Those dated entries describe decisions when made; DESIGN.md and current scope sections describe today's behavior. Keep useful rationale in concise current decisions if producing a public copy without the history. Preserve the history in this working repository.

## License

Vigilo is licensed under the [GNU General Public License v3.0](LICENSE). By contributing, you agree that your contributions are licensed under GPL-3.0.
