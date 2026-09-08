# Local model setup

[Back to Vigilo](../README.md) · [Model architecture and profiles](../DESIGN.md#10-local-ai-design)

Select a model in Settings > Local AI. ONNX profiles run in process; Gemma profiles use a local OpenAI-compatible server. The initial inference endpoint must pass `Uri.IsLoopback`; see the [transport boundary](../DESIGN.md#221-local-processing-promise).

Default model settings are in `src/Vigilo.App/appsettings.json`. User model selection is persisted to:

```text
%LOCALAPPDATA%\Vigilo\model-settings.json
```

If `ModelRoot` is omitted for an ONNX profile, model files are stored under:

```text
%LOCALAPPDATA%\Vigilo\Models\<model version>
```

To use either Gemma LiteRT-LM profile on Windows, install LiteRT-LM with Python 3.10 or newer:

```powershell
python --version
python -m pip install --upgrade pip
python -m pip install --upgrade litert-lm
litert-lm --help
```

Import the Gemma 4 E2B model into LiteRT-LM's local registry with the model ID Vigilo expects:

```powershell
litert-lm import `
  --from-huggingface-repo litert-community/gemma-4-E2B-it-litert-lm `
  gemma-4-E2B-it.litertlm `
  gemma4-e2b
```

For the E4B profile, import its model using the corresponding Vigilo model ID:

```powershell
litert-lm import `
  --from-huggingface-repo litert-community/gemma-4-E4B-it-litert-lm `
  gemma-4-E4B-it.litertlm `
  gemma4-e4b
```

Start the local OpenAI-compatible server before selecting either Gemma profile in Vigilo:

```powershell
litert-lm serve --host 127.0.0.1 --port 9379
```

Alternatively, let Vigilo run the server itself: set `Model:RuntimeExecutablePath` to the `litert-lm` executable and `Model:RuntimeArguments` to `serve --host 127.0.0.1 --port 9379`, and the runtime supervisor starts, monitors, restarts, and stops that process as needed.

Leave an externally started server running while Vigilo uses the Gemma profile. To verify the server is reachable:

```powershell
Invoke-RestMethod -Uri "http://localhost:9379/v1/models"
```

LiteRT-LM setup references:

- Installation: <https://developers.google.com/edge/litert-lm/cli/installation>
- Model management: <https://developers.google.com/edge/litert-lm/cli/model_management>
- OpenAI-compatible server: <https://developers.google.com/edge/litert-lm/cli/openai_server>

By default the preset is `Phi4Mini`:

```json
{
  "Model": {
    "Preset": "Phi4Mini",
    "Version": "Phi-4-mini-instruct-onnx/cpu-int4-rtn-block-32-acc-level-4",
    "ManifestBaseUrl": "https://huggingface.co/microsoft/Phi-4-mini-instruct-onnx/resolve/main/cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/",
    "InferenceTimeoutSeconds": 300
  }
}
```
