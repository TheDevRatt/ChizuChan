# ChizuChan local LLM setup

ChizuChan now talks to a local Ollama server by default:

```json
"Ollama": {
  "BaseUrl": "http://localhost:11434/api/chat",
  "BearerToken": "",
  "Model": "qwen2.5:3b",
  "RequestTimeoutSeconds": 180
}
```

## Recommended model for a GTX 1650 4GB

Start with:

```powershell
ollama pull qwen2.5:3b
ollama run qwen2.5:3b
```

Why this one:

- `qwen2.5:3b` is about 1.9 GB in Ollama, leaving room for context/KV cache inside 4 GB VRAM.
- It is stronger than 1B-class models for chat, instruction following, basic coding, and Chizu's personality prompt.
- It should feel more responsive than trying to squeeze a 7B model onto a 4 GB card.

Good fallbacks:

```powershell
ollama pull phi3.5      # about 2.2 GB, stronger reasoning but may be a little heavier
ollama pull gemma3:1b   # about 815 MB, fastest and safest if the GPU is struggling
ollama pull gemma3:4b   # vision capable, heavier, may spill to system RAM depending context size
```

## Install Ollama on Windows

1. Install Ollama from https://ollama.com/download
2. Open PowerShell.
3. Pull the recommended model:

```powershell
ollama pull qwen2.5:3b
```

4. Confirm the local API works:

```powershell
curl http://localhost:11434/api/chat `
  -H "Content-Type: application/json" `
  -d '{"model":"qwen2.5:3b","stream":false,"messages":[{"role":"user","content":"say hi as Chizu"}]}'
```

5. Start or restart ChizuChan. No cloud/reverse-proxy token is required.

## Switching models in Discord

Use Chizu's `/model` command. The choices are now tuned for a 4 GB GPU:

- Qwen 2.5 3B, recommended default
- Phi 3.5 Mini 3.8B
- Gemma 3 1B, fastest
- Gemma 3 4B, vision-capable but heavier

Make sure the model is pulled in Ollama before selecting it:

```powershell
ollama pull qwen2.5:3b
ollama pull phi3.5
ollama pull gemma3:1b
ollama pull gemma3:4b
```

## If it feels slow or crashes

- Prefer `gemma3:1b` or `qwen2.5:1.5b` if VRAM is tight.
- Keep Discord responses short by leaving Chizu's system prompt as-is.
- Avoid huge context windows on a 4 GB card.
- Check GPU usage with `nvidia-smi` while Chizu is generating.
- If Ollama spills layers to CPU/RAM, it will still work but responses can become slow.
