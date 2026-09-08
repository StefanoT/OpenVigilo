# Vigilo.App

## Scope

`Vigilo.App` is the Windows desktop host for Vigilo. It owns the WPF user interface, tray integration, startup and shutdown lifecycle, local run logging, user approval prompts, and composition of the application services through `Microsoft.Extensions.Hosting`.

This project should stay focused on user interaction and process hosting. Domain models, mailbox access, classification, local AI inference, and persistence belong in the feature projects that `Vigilo.App` composes.