# Terminal AI Coding Assistant

A terminal-based AI coding assistant that uses LLMs to read, write, edit, search, and execute code through tool calls. Supports **Ollama** (local models), **OpenRouter**, and **OpenAI**.

## Features

- **Multi-provider** — Ollama (local), OpenRouter, and OpenAI (cloud)
- **Interactive menu** — select provider, model, and enter prompts in a loop
- **7 tools**: Read, Write, Edit (fuzzy match), EditLine (by line number), Bash, Glob, Grep (ripgrep)
- **Streaming** — responses appear in real-time as the model generates them
- **Context window management** — automatic truncation to prevent token overflow
- **Iteration limits** — prevents infinite loops (configurable, default 20)
- **`.env` support** — load API keys and config from a `.env` file
- **`config.json`** — customize providers, models, and endpoints
- **Agent loop** — continues working until the task is done
- **Fuzzy matching** — Edit tool matches even with whitespace/case differences

## Prerequisites

- [.NET 10.0](https://dotnet.microsoft.com/download/dotnet/10.0)
- One of: **Ollama** running locally, **OpenRouter** API key, or **OpenAI** API key

## Setup

1. Clone the repo and navigate to the project directory.
2. (Optional) Create a `.env` file in the project root to store API keys:
   ```sh
   OPENROUTER_API_KEY=sk-or-v1-...
   OPENAI_API_KEY=sk-...
   ```
3. Run the assistant:
   ```sh
   dotnet run
   ```
4. Use the interactive menu to select a provider, model, and enter your prompt.

### Provider-specific notes

- **Ollama**: Install [Ollama](https://ollama.com) and pull a model (`ollama pull qwen3:8b`). No API key needed.
- **OpenRouter**: Get an API key from [OpenRouter](https://openrouter.ai) and set `OPENROUTER_API_KEY`.
- **OpenAI**: Get an API key from the [Azure Portal](https://portal.azure.com) or [OpenAI](https://platform.openai.com) and set `OPENAI_API_KEY`.

### Quick non-interactive mode

Set `AI_PROVIDER` and optionally `AI_MODEL` environment variables to skip the menu:

```sh
set AI_PROVIDER=openai
set AI_MODEL=gpt-4o
dotnet run
```

## Environment Variables

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `AI_PROVIDER` | No | `ollama` | Provider to use (`ollama`, `openrouter`, `openai`) |
| `AI_MODEL` | No | provider default | Model to use (skips model selection menu) |
| `OPENROUTER_API_KEY` | For OpenRouter | — | API key for OpenRouter |
| `OPENROUTER_BASE_URL` | No | `https://openrouter.ai/api/v1` | OpenRouter endpoint |
| `OPENROUTER_SITE_URL` | No | — | Sent as `HTTP-Referer` header |
| `OPENROUTER_SITE_NAME` | No | — | Sent as `X-Title` header |
| `OPENAI_API_KEY` | For OpenAI | — | API key for OpenAI |
| `OPENAI_BASE_URL` | No | `https://api.openai.com/v1` | OpenAI endpoint |
| `OLLAMA_BASE_URL` | No | `http://localhost:11434/v1` | Ollama endpoint |
| `SYSTEM_PROMPT` | No | (built-in) | Override the system prompt |
| `MAX_ITERATIONS` | No | `20` | Max tool-call iterations before stopping |
| `CONTEXT_WINDOW_SIZE` | No | `32768` (local) / `128000` (cloud) | Max tokens for context window |
| `MAX_TOOL_RESULT_TOKENS` | No | 40% of context window | Max tokens per tool result (auto-truncated) |

Any provider's base URL can be overridden via `{PROVIDER}_BASE_URL` (e.g., `OPENAI_BASE_URL`).

## Configuration File

You can customize providers in `config.json` (in the project root or any parent directory). Example:

```json
{
  "providers": {
    "openai": {
      "displayName": "OpenAI (Cloud)",
      "baseUrl": "https://api.openai.com/v1",
      "defaultModel": "gpt-4o",
      "needsApiKey": true,
      "apiKeyEnvVar": "OPENAI_API_KEY",
      "models": ["gpt-4o", "gpt-4o-mini", "o3-mini", "o4-mini"]
    }
  }
}
```

Available built-in providers: `ollama`, `openrouter`, `openai`. Entries in `config.json` override the built-in defaults.

## Available Tools

The assistant has 7 tools that it can call autonomously:

| Tool | Description |
|------|-------------|
| **Read** | Read a file (returns content with line numbers) |
| **Write** | Write content to a file (auto-creates directories) |
| **Edit** | Edit a file by exact string replacement (with fuzzy fallback) |
| **EditLine** | Edit by replacing lines by number (requires re-reading between edits) |
| **Bash** | Execute a shell command |
| **Glob** | Find files by glob pattern |
| **Grep** | Search file contents with ripgrep (regex) |

## Building

```sh
dotnet build
```

The compiled output will be in `bin/Debug/net10.0/`.
