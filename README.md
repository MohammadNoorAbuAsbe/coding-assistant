# Terminal AI Coding Assistant

A terminal-based AI coding assistant that uses LLMs to understand code and perform actions through tool calls. Supports both **Ollama** (local models) and **OpenRouter** (cloud API).

## Features

- **Read** files
- **Write** files (auto-creates directories)
- **Execute** shell commands (PowerShell on Windows, Bash on Linux/macOS)
- **Agent loop** — continues working until the task is done

## Prerequisites

- [.NET 10.0](https://dotnet.microsoft.com/download/dotnet/10.0)
- Either **Ollama** running locally, or an **OpenRouter** API key

## Setup

### Option A: Ollama (local, free)

1. Install [Ollama](https://ollama.com) and pull a model:
   ```sh
   ollama pull qwen3:8b
   ```
2. Run the assistant:
   ```sh
   dotnet run --project TerminalAiAssistant.csproj -- -p "explain what this project does"
   ```

### Option B: OpenRouter (cloud API)

1. Get an API key from [OpenRouter](https://openrouter.ai)
2. Set the environment variable:
   ```sh
   # Windows PowerShell
   $env:OPENROUTER_API_KEY="your-key-here"

   # Linux/macOS
   export OPENROUTER_API_KEY="your-key-here"
   ```
3. Run:
   ```sh
   dotnet run --project TerminalAiAssistant.csproj -- -p "read Program.cs and explain it"
   ```

## Environment Variables

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `AI_PROVIDER` | No | `openrouter` | Set to `ollama` for local models |
| `OPENROUTER_API_KEY` | Yes (OpenRouter) | — | API key for OpenRouter |
| `OPENROUTER_BASE_URL` | No | `https://openrouter.ai/api/v1` | OpenRouter endpoint |
| `OPENROUTER_MODEL` | No | `anthropic/claude-haiku-4.5` | Model to use via OpenRouter |
| `OLLAMA_BASE_URL` | No | `http://localhost:11434/v1` | Ollama endpoint |
| `OLLAMA_MODEL` | No | `llama3.1` | Model to use via Ollama |

## Usage

```sh
dotnet run --project TerminalAiAssistant.csproj -- -p "<your prompt>"
```

## Building

```sh
dotnet build
```

The compiled output will be in `bin/Debug/net10.0/`.
