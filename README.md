# Terminal AI Coding Assistant

A terminal-based AI coding assistant that uses LLMs to read, write, edit, search, and execute code through tool calls. Supports **Ollama** (local models), **OpenRouter**, and **OpenAI**.

## Features

- **Multi-provider** — Ollama (local), OpenRouter, and OpenAI (cloud)
- **Interactive menu** — select provider, model, and enter prompts in a loop
- **14 tools**: Read, Write, Edit (fuzzy match), EditLine (by line number), ApplyPatch (unified diffs, fuzzy), Diff (change previews), Bash, Glob, Grep (ripgrep), WebFetch (URLs), WebSearch (Tavily), Question (interactive), Task (sub-agents), TodoWrite (task lists)
- **Sub-agents** — delegate complex subtasks to independent sub-agents with their own tool loop
- **Streaming** — responses appear in real-time as the model generates them
- **Context window management** — automatic truncation to prevent token overflow
- **Iteration limits** — prevents infinite loops (configurable, default 20)
- **`.env` support** — load API keys and config from a `.env` file
- **`config.json`** — customize providers, models, and endpoints
- **Agent loop** — continues working until the task is done
- **Fuzzy matching** — Edit tool matches even with whitespace/case differences
- **Patch apply** — ApplyPatch applies multi-hunk unified diffs with fuzzy whitespace tolerance; can create new files
- **Diff previews** — Diff tool shows what a change would look like without writing anything
- **Web search** — Tavily integration for fetching up-to-date web content
- **Question tool** — asks the user for decisions with multiple-choice options
- **OpenRouter headers** — sends `HTTP-Referer` and `X-Title` for OpenRouter rankings
- **ANSI color rendering** — renders markdown to ANSI colors for console output
- **Path validation** — validates and sanitizes file paths for security

## Prerequisites

- [.NET 10.0](https://dotnet.microsoft.com/download/dotnet/10.0)
- One of: **Ollama** running locally, **OpenRouter** API key, or **OpenAI** API key
- **ripgrep** (`rg`) for the Grep tool — install via `winget install BurntSushi.ripgrep.MSVC`
- **Tavily API key** (optional) for the WebSearch tool — get one at [tavily.com](https://tavily.com)

## Setup

1. Clone the repo and navigate to the project directory.
2. Create a `.env` file in the project root to store API keys:
   ```sh
   OPENROUTER_API_KEY=sk-or-v1-...
   OPENAI_API_KEY=sk-...
   TAVILY_API_KEY=tvly-...
   ```
3. Run the assistant:
   ```sh
   dotnet run
   ```
4. Use the interactive menu to select a provider, model, and enter your prompt.

### Provider-specific notes

- **Ollama**: Install [Ollama](https://ollama.com) and pull a model (`ollama pull qwen3:8b`). No API key needed. Models are auto-discovered via `ollama list`.
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
| `OPENROUTER_SITE_URL` | No | — | Sent as `HTTP-Referer` header (for OpenRouter rankings) |
| `OPENROUTER_SITE_NAME` | No | — | Sent as `X-Title` header (for OpenRouter rankings) |
| `OPENAI_API_KEY` | For OpenAI | — | API key for OpenAI |
| `OPENAI_BASE_URL` | No | `https://api.openai.com/v1` | OpenAI endpoint |
| `OLLAMA_BASE_URL` | No | `http://localhost:11434/v1` | Ollama endpoint |
| `TAVILY_API_KEY` | For WebSearch | — | API key for Tavily web search (get at https://tavily.com) |
| `SYSTEM_PROMPT` | No | (built-in) | Override the system prompt |
| `MAX_ITERATIONS` | No | `20` | Max tool-call iterations before stopping |
| `MAX_SUB_AGENT_DEPTH` | No | `3` | Max nested sub-agent depth |
| `CONTEXT_WINDOW_SIZE` | No | `32768` (local) / `128000` (cloud) | Max tokens for context window |
| `MAX_TOOL_RESULT_TOKENS` | No | 40% of context window | Max tokens per tool result (auto-truncated) |
| `BASH_TIMEOUT` | No | `120000` (ms) | Timeout for Bash tool commands |
| `NO_COLOR` | No | — | Set to any value to disable colored console output |

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
    },
    "openrouter": {
      "displayName": "OpenRouter (Cloud)",
      "baseUrl": "https://openrouter.ai/api/v1",
      "defaultModel": "openrouter/free",
      "baseUrl": "https://openrouter.ai/api/v1",
      "defaultModel": "openrouter/free",
      "needsApiKey": true,
      "apiKeyEnvVar": "OPENROUTER_API_KEY",
      "siteUrlEnvVar": "OPENROUTER_SITE_URL",
      "siteNameEnvVar": "OPENROUTER_SITE_NAME",
      "models": ["openrouter/free"]
    }
  }
}
```

Available built-in providers: `ollama`, `openrouter`, `openai`. Entries in `config.json` override the built-in defaults. Each provider supports `baseUrl`, `defaultModel`, `needsApiKey`, `apiKeyEnvVar`, and `models`.

## Available Tools

The assistant has 14 tools that it can call autonomously:

| Tool | Description |
|------|-------------|
| **Read** | Read a file (returns content with line numbers) |
| **Write** | Write content to a file (auto-creates directories) |
| **Edit** | Edit a file by exact string replacement (with fuzzy fallback) |
| **EditLine** | Edit by replacing lines by number (requires re-reading between edits) |
| **ApplyPatch** | Apply a unified diff (patch) to a file — multiple hunks in one call, matched with fuzzy whitespace tolerance; can create new files from all-additions patches |
| **Diff** | Preview the changes that would be made to a file without writing anything (file vs new content) |
| **Bash** | Execute a shell command |
| **Glob** | Find files by glob pattern (`**`, `*`, `?`, `{a,b}`) |
| **Grep** | Search file contents with ripgrep (regex, supports include/exclude/case-insensitive) |
| **WebFetch** | Fetch and return the contents of a URL (converts HTML to markdown) |
| **WebSearch** | Search the web for current information using Tavily |
| **Question** | Ask the user a question with multiple-choice options for decisions |
| **Task** | Launch a sub-agent for complex subtasks (independent tool loop, all tools except Task) |
| **TodoWrite** | Create and manage a structured task list to track multi-step progress |

## Project Structure

```
src/
├── Program.cs            # Entry point with interactive loop
├── AppBootstrapper.cs    # Provider and model resolution, cancel handler setup
├── ChatSession.cs        # Chat session management (messages, reset, etc.)
├── ChatOrchestrator.cs   # Agent loop — streaming, tool dispatch, iteration
├── ChatService.cs        # OpenAI SDK client creation (with OpenRouter headers)
├── Configuration.cs      # .env / config.json loading, provider resolution
├── ContextManager.cs     # Token estimation and message truncation
├── GlobHelper.cs         # File globbing via Microsoft.Extensions.FileSystemGlobbing
├── MatchFinder.cs        # Fuzzy text matching for the Edit tool
├── PatchHandler.cs       # ApplyPatch (unified diff application) and Diff (change preview) tools
├── PathValidator.cs      # Validates and sanitizes file paths
├── AnsiRenderer.cs       # Renders markdown to ANSI colors for console output
├── ConsoleStyler.cs      # Helper for styling console output
├── MenuHandler.cs        # Interactive provider/model selection, Ollama discovery
├── QuestionHandler.cs    # Interactive Question tool implementation
├── ProviderConfig.cs     # Provider configuration model
├── ResponseHandler.cs    # Tool call execution (Read, Write, Edit, EditLine, Bash, Glob, Grep)
├── RipgrepHelper.cs      # ripgrep argument builder and path finder
├── SystemPrompt.cs       # System prompts (local vs cloud variants)
├── TavilyModels.cs       # Tavily search response models
├── TaskHandler.cs        # Task tool implementation (sub-agents)
├── TodoWriteHandler.cs   # TodoWrite tool implementation (task lists)
├── ToolHandler.cs        # Tool definitions and OpenAI function schemas
└── WebToolHandlers.cs    # WebFetch and WebSearch tool implementations
```

## Building

```sh
dotnet build
```

The compiled output will be in `bin/Debug/net10.0/`.

## Usage Examples

### Interactive Mode

Run `dotnet run` and follow the prompts to:
1. Select a provider (Ollama, OpenRouter, OpenAI)
2. Select a model
3. Enter your prompt
4. The assistant will use tools to complete the task and return a summary

### Non-Interactive Mode

Set environment variables to skip the menu:

```sh
# Using OpenAI
set AI_PROVIDER=openai
set AI_MODEL=gpt-4o
dotnet run "Explain how to implement a binary search tree in C#"

# Using Ollama with a specific model
set AI_PROVIDER=ollama
set AI_MODEL=qwen3:8b
dotnet run "Create a REST API endpoint for user management"
```

### Tool Usage Examples

The assistant can autonomously use tools like:

- **Read a file**: `Read` tool with `file_path: "src/Program.cs"`
- **Write a new file**: `Write` tool with `file_path: "src/NewFeature.cs"` and content
- **Edit existing code**: `EditLine` tool to replace specific lines
- **Apply a patch**: `ApplyPatch` tool with `{"file_path": "src/Foo.cs", "patch": "@@ -10,3 +10,3 @@ ..."}`
- **Preview a change**: `Diff` tool with `{"file_path": "src/Foo.cs", "new_content": "..."}` to see the diff before applying
- **Search code**: `Grep` tool to find patterns across files
- **Run commands**: `Bash` tool to execute `dotnet build` or run tests
- **Search the web**: `WebSearch` tool for up-to-date information
- **Fetch web content**: `WebFetch` tool to get documentation from URLs
- **Ask questions**: `Question` tool when clarification is needed
- **Delegate tasks**: `Task` tool to launch sub-agents for complex subtasks
- **Manage todos**: `TodoWrite` tool to track progress on multi-step tasks

## Configuration Details

### Provider Configuration

Each provider in `config.json` can have:
- `displayName`: Friendly name shown in the menu
- `baseUrl`: Base URL for API endpoints
- `defaultModel`: Default model to use when none is specified
- `needsApiKey`: Whether the provider requires an API key
- `apiKeyEnvVar`: Environment variable name for the API key
- `siteUrlEnvVar`: Environment variable for HTTP-Referer header (OpenRouter only)
- `siteNameEnvVar`: Environment variable for X-Title header (OpenRouter only)
- `models`: List of available models for this provider

### Context Management

The assistant automatically manages context to prevent token overflow:
- Messages are truncated when they exceed the context window
- Tool results are truncated to a configurable percentage of the context window
- System prompts are preserved when possible

### Tool Result Truncation

Tool results that exceed `MAX_TOOL_RESULT_TOKENS` (default 40% of context window) are automatically truncated to prevent overwhelming the context window.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## License

This project is licensed under the MIT License.