# Vigilo.LocalAi

## Scope

`Vigilo.LocalAi` owns model availability and inference. It checks and installs ONNX Runtime GenAI model files, reports model status, lazily loads file-managed models, calls loopback OpenAI-compatible model servers for server-managed profiles, defines each profile's practical inference capabilities, and exposes completion through Core's `ILocalAiClient`. Non-loopback inference endpoints are rejected before prompts are sent.

This project should stay focused on model file management and inference execution. Prompt policy belongs in `Vigilo.Classification`, and user interaction for download approval belongs in `Vigilo.App` through `IUserApprovalService`.