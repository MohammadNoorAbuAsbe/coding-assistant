# Coding Assistant

An AI coding assistant that uses LLMs to read, write, edit, search, and execute code through tool calls. Ships as a **modern Windows desktop app** (WebView2 UI: file explorer, diff-backed changes panel, task lists, session tabs). Supports **Ollama** (local models), **OpenRouter**, **OpenAI**, and **Google Gemini** (free tier).

## Features

- **Desktop app (Windows)** — borderless native shell with session tabs, sidebar file tree, live tool cards, reasoning viewer, undo journal, command palette and settings drawer; runs on the WebView2 Runtime
- **Multi-provider** — Ollama (local), OpenRouter, OpenAI, and Google Gemini (cloud)
- **Interactive menu** — select provider, model, and enter prompts in a loop
- **13 tools**: Read, Write, Edit (fuzzy match), ApplyPatch (unified diffs, fuzzy), Diff (change previews), Bash, Glob, Grep (ripgrep), WebFetch (URLs), WebSearch (Tavily), Question (interactive), Task (sub-agents), TodoWrite (task lists)
- **Sub-agents** — delegate complex subtasks to independent sub-agents with their own tool loop
- **Streaming** — responses appear in real-time as the model generates them
- **Context window management** — automatic truncation to prevent token overflow
- **Agent loop** — continues working until the task is done (optional `MAX_ITERATIONS` cap)
- **Autopilot mode** — experimental: the agent runs in an infinite loop improving the project on its own until you press Ctrl+C; Question tool calls are auto-answered ("decide yourself and continue") instead of pausing for input
- **Undo / rollback** — every Write/Edit/ApplyPatch records a before-image; `/undo` restores the most recent change (or deletes newly created files) and `/history` lists recorded changes
- **`.env` support** — load API keys and config from a `.env` file
- **`config.json`** — customize providers, models, and endpoints
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
- **WebView2 Runtime** (for the desktop app) — preinstalled on Windows 11 and most Windows 10 machines; install from https://developer.microsoft.com/microsoft-edge/webview2 if missing
- One of: **Ollama** running locally, **OpenRouter** API key, **OpenAI** API key, or **Google Gemini** API key (free tier)
- **ripgrep** (`rg`) for the Grep tool — install via `winget install BurntSushi.ripgrep.MSVC`
- **Tavily API key** (optional) for the WebSearch tool — get one at [tavily.com](https://tavily.com)

## Setup

1. Clone the repo and navigate to the project directory.
2. Create a `.env` file in the project root to store API keys:
   ```sh
   OPENROUTER_API_KEY=sk-or-v1-...
   OPENAI_API_KEY=sk-...
   GEMINI_API_KEY=...
   TAVILY_API_KEY=tvly-...
   ```
3. Run the assistant:
   ```sh
   dotnet run
   ```
4. On Windows this launches the **desktop app** — a true GUI executable with no console window (the working folder is the folder you launch from; change it anytime with the folder button or `Ctrl+O`). The model's streamed answer, reasoning and tool calls appear only in the app.

### Desktop app

- **Sessions** — each tab is an independent conversation (`Ctrl+N` for a new session); sessions are in-memory for the current run
- **Sidebar** — browse the workspace, filter files, open read-only previews; the *Changes* panel mirrors the undo journal (revert any entry); *Tasks* tracks the agent's todo list
- **Composer** — slash commands (`/help`, `/new`, `/undo`, `/history`, `/autopilot`, `/theme`, `/exit`), `Ctrl+K` palette, `Ctrl+,` settings, `Ctrl+B` sidebar
- **Settings** — switch provider/model (persisted to `%LOCALAPPDATA%\CodingAssistant\settings.json`), theme and font size
- **Build a release**:
  ```sh
  dotnet publish -c Release -p:PublishProfile=win-x64
  ```
  Produces a single-file, self-contained `bin\publish\win-x64\CodingAssistant.exe`.

### Provider-specific notes

- **Ollama**: Install [Ollama](https://ollama.com) and pull a model (`ollama pull qwen3:8b`, `ollama pull llama3`, `ollama pull mistral`, or `ollama pull phi3`). No API key needed. Models are auto-discovered via `ollama list`. Use `AI_PROVIDER=ollama` and `AI_MODEL=qwen3:8b` (or other model names) to switch.

  > **Setup**: Ensure Ollama is running and the model is pulled. The assistant will auto-detect available models via `ollama list`.

  > **Usage**: Set `AI_PROVIDER=ollama` and `AI_MODEL=llama3` (or other model names) to use these models.

  > **Configurable Models**: Edit `config.json` to add/remove models under the `ollama` provider. Example: `"models": ["qwen3:8b", "llama3", "mistral", "phi3"]`.

  > **Non-interactive mode**: Use environment variables like `AI_PROVIDER=ollama` and `AI_MODEL=phi3` to specify the model without prompts.
- **OpenRouter**: Get an API key from [OpenRouter](https://openrouter.ai) and set `OPENROUTER_API_KEY`.
- **OpenAI**: Get an API key from the [Azure Portal](https://portal.azure.com) or [OpenAI](https://platform.openai.com) and set `OPENAI_API_KEY`.
- **Google Gemini**: Get a free API key from [Google AI Studio](https://aistudio.google.com/apikey) and set `GEMINI_API_KEY`. No credit card required — the free tier covers Flash models (e.g., `gemini-3.6-flash`, `gemini-2.5-flash`). Uses the OpenAI-compatible endpoint, so all tools work.

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
| `AI_PROVIDER` | No | `ollama` | Provider to use (`ollama`, `openrouter`, `openai`, `gemini`) |
| `AI_MODEL` | No | provider default | Model to use (skips model selection menu) |
| `OPENROUTER_API_KEY` | For OpenRouter | — | API key for OpenRouter |
| `OPENROUTER_BASE_URL` | No | `https://openrouter.ai/api/v1` | OpenRouter endpoint |
| `OPENROUTER_SITE_URL` | No | — | Sent as `HTTP-Referer` header (for OpenRouter rankings) |
| `OPENROUTER_SITE_NAME` | No | — | Sent as `X-Title` header (for OpenRouter rankings) |
| `OPENAI_API_KEY` | For OpenAI | — | API key for OpenAI |
| `OPENAI_BASE_URL` | No | `https://api.openai.com/v1` | OpenAI endpoint |
| `GEMINI_API_KEY` | For Gemini | — | API key for Google Gemini (get at https://aistudio.google.com/apikey) |
| `GEMINI_BASE_URL` | No | `https://generativelanguage.googleapis.com/v1beta/openai` | Google Gemini OpenAI-compatible endpoint |
| `OLLAMA_BASE_URL` | No | `http://localhost:11434/v1` | Ollama endpoint |
| `TAVILY_API_KEY` | For WebSearch | — | API key for Tavily web search (get at https://tavily.com) |
| `SYSTEM_PROMPT` | No | (built-in) | Override the system prompt |
| `MAX_ITERATIONS` | No | unlimited | Optional cap on tool-call iterations; unset = run until done |
| `MAX_SUB_AGENT_DEPTH` | No | `3` | Max nested sub-agent depth |
| `CONTEXT_WINDOW_SIZE` | No | `32768` (local) / `128000` (cloud) | Max tokens for context window |
| `MAX_TOOL_RESULT_TOKENS` | No | 20% of context window (local) / 40% (cloud) | Max tokens per tool result (auto-truncated; local models get a tighter budget so they read in focused ranges) |
| `MODEL_TEMPERATURE` | No | `0` | Sampling temperature; 0 gives the most deterministic, reliable tool calls (small models benefit the most) |
| `BASH_TIMEOUT` | No | `120000` (ms) | Timeout for Bash tool commands |
| `AUTOPILOT` | No | — | Set to `1` to start directly in autonomous mode: the agent continuously improves the project (one improvement per cycle, picking the next itself) until you stop it with Ctrl+C |
| `AUTO_VERIFY` | No | `false` (local) / `true` (cloud) | Auto-run the verify command after file-modifying tool calls; off by default for local models to keep the agent loop fast. Always disabled in autopilot mode (the running process locks the build output). |
| `VERIFY_COMMAND` | No | `dotnet build --nologo -v q` | Command used by auto-verification |
| `VERIFY_TIMEOUT` | No | `120000` (ms) | Timeout for the verify command |
| `UNDO_HISTORY_LIMIT` | No | `100` | Max file changes kept in the undo journal |
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
      "needsApiKey": true,
      "apiKeyEnvVar": "OPENROUTER_API_KEY",
      "siteUrlEnvVar": "OPENROUTER_SITE_URL",
      "siteNameEnvVar": "OPENROUTER_SITE_NAME",
      "models": ["openrouter/free"]
    },
    "gemini": {
      "displayName": "Google Gemini (Cloud)",
      "baseUrl": "https://generativelanguage.googleapis.com/v1beta/openai",
      "defaultModel": "gemini-3.6-flash",
      "needsApiKey": true,
      "apiKeyEnvVar": "GEMINI_API_KEY",
      "models": ["gemini-3.6-flash", "gemini-3-flash", "gemini-2.5-flash", "gemini-2.5-pro"]
    }
  }
}
```

Available built-in providers: `ollama`, `openrouter`, `openai`, `gemini`. Entries in `config.json` override the built-in defaults. Each provider supports `baseUrl`, `defaultModel`, `needsApiKey`, `apiKeyEnvVar`, and `models`.

## Available Tools

The assistant has 13 tools that it can call autonomously:

| Tool | Description |
|------|-------------|
| **Read** | Read a file (always returns the whole file with line numbers; `start_line`/`end_line` are ignored — enormous files are truncated to the token budget) |
| **Write** | Write content to a file (auto-creates directories) |
| **Edit** | Edit a file by string replacement (fuzzy tolerance for whitespace/case differences) |
| **ApplyPatch** | Apply a unified diff (patch) to a file — multiple hunks in one call, matched with fuzzy whitespace tolerance; can create new files from all-additions patches |
| **Diff** | Preview the changes that would be made to a file without writing anything (file vs new content) |
| **PowerShell** | Execute a PowerShell command on Windows (`powershell.exe -Command`) — not bash, PowerShell syntax only |
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
├── Program.cs            # Entry point — always launches the desktop app
├── AppUi.cs              # Event bus to the UI (stream, tools, reasoning, todos, changes…)
├── Desktop/              # WebView2 host: borderless MainForm, message router, file tree,
│                         #   settings store, embedded-web extraction (Windows only)
├── Web/                  # Frontend: index.html, styles.css, app.js, vendor libs (embedded)
├── AppBootstrapper.cs    # Provider and model resolution, cancel handler setup
├── Autopilot.cs          # Autonomous mode — infinite self-improvement loop
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
├── ResponseHandler.cs    # Tool call execution (Read, Write, Edit, Bash, Glob, Grep)
├── RipgrepHelper.cs      # ripgrep argument builder and path finder
├── SystemPrompt.cs       # System prompts (local vs cloud variants)
├── TavilyModels.cs       # Tavily search response models
├── TaskHandler.cs        # Task tool implementation (sub-agents)
├── TodoWriteHandler.cs   # TodoWrite tool implementation (task lists)
├── ToolHandler.cs        # Tool definitions and OpenAI function schemas
├── UndoJournal.cs        # Before-image journal backing the /undo and /history commands
└── WebToolHandlers.cs    # WebFetch and WebSearch tool implementations
```

## Building

```sh
dotnet build
```

The compiled output will be in `bin/Debug/net10.0-windows/`.

## Testing

The project includes an xUnit test suite covering the fuzzy matching engine, patch application, diff generation, path validation, context management, tool schemas, and end-to-end tool behavior:

```sh
dotnet test
```

Test projects live under `tests/`. CI (GitHub Actions, `.github/workflows/ci.yml`) runs the full suite on both Windows and Ubuntu on every push and pull request. Grep end-to-end tests are skipped automatically when ripgrep is not installed.

## Usage Examples

### Interactive Mode

Run `dotnet run` and follow the prompts to:
1. Select a provider (Ollama, OpenRouter, OpenAI, Google Gemini)
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

### Autopilot Mode (experimental)

Run the assistant as a self-improving loop: the agent picks an improvement to the project, implements it, then picks the next one — forever, until you press Ctrl+C.

```sh
set AUTOPILOT=1
dotnet run
```

Or with a flag (works in any shell):

```sh
dotnet run -- --autopilot
```

On PowerShell, use `$env:AUTOPILOT = "1"` instead of `set AUTOPILOT=1` (PowerShell's `set` does not set environment variables).

Or type `/autopilot` in the interactive session. How it works:

- Each cycle runs the normal agent loop with a mission prompt ("pick the next highest-value improvement and implement it, never stop, never ask").
- If the agent calls the **Question** tool, it is auto-answered with a message echoing the question/options and telling it to decide for itself — the loop never blocks on user input.
- Build verification is disabled (the running process locks the build output, so `dotnet build` would fail with file-lock errors); the agent is instructed to never run build/test commands and to reason about correctness instead.
- Changes are written to disk each cycle; they take effect only when the app is restarted. Undo history works per cycle, and git provides a fallback rollback.

There are **no safety rails** — no iteration cap, no cost guard, no command denylist. Keep the console visible and be ready to press Ctrl+C.

### Slash Commands

Type these instead of a prompt to control the session:

| Command | Description |
|---------|-------------|
| `/undo` | Restore the most recent file change made by Write/Edit/ApplyPatch (or delete the file if it was created by the change). The model is informed of the rollback so its context stays accurate. |
| `/history` | List the recorded file changes for this session, newest first. |
| `/autopilot` | Enter autonomous mode: the agent keeps improving the project in an infinite loop (one improvement per cycle) until you press Ctrl+C. Question tool calls are auto-answered with "decide yourself and continue". |
| `/new`, `/reset` | Reset the conversation (also clears the undo history). |
| `/exit`, `/quit` | Exit the assistant. |

### Tool Usage Examples

The assistant can autonomously use tools like:

- **Read a file**: `Read` tool with `file_path: "src/Program.cs"`
- **Write a new file**: `Write` tool with `file_path: "src/NewFeature.cs"` and content
- **Edit existing code**: `Edit` tool for targeted string replacements
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
- When older messages are dropped, a compact session summary (original request, current task list, last build status) is pinned in their place so the model keeps working context on long tasks
- Tool results are truncated to a configurable percentage of the context window
- System prompts are preserved when possible

### Tool Result Truncation

Tool results that exceed `MAX_TOOL_RESULT_TOKENS` (default 40% of context window) are automatically truncated to prevent overwhelming the context window.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## License

This project is licensed under the MIT License.