# EDDA Architecture

EDDA (short for "Electronic Device for Dialogue Assistance" or whatever backronym you prefer) is a personal voice assistant system. Think Jarvis, not Alexa — built from scratch with full control over every component.

The system runs across multiple devices and services, coordinated through WebSockets and HTTP APIs.

## System Overview

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                             BASEMENT SERVER                                  │
│  (High-performance Linux box with NVIDIA GPU)                               │
│                                                                              │
│  ┌─────────────────────┐    ┌─────────────────────┐                         │
│  │   EDDA Server       │    │   TTS Service       │                         │
│  │   (C#/.NET 8)       │◄───│   (Chatterbox)      │                         │
│  │                     │    │   Docker + CUDA     │                         │
│  │  - WebSocket API    │    │   Port 5000         │                         │
│  │  - Whisper STT      │    └─────────────────────┘                         │
│  │  - LLM via OpenRouter    ┌─────────────────────┐                         │
│  │  - Agent + Tools    │    │   Piper TTS         │                         │
│  │  - Memory/RAG       │    │   (Fast alternative)│                         │
│  │  Port 8080          │    │   Docker, CPU only  │                         │
│  └──────────┬──────────┘    │   Port 5001         │                         │
│             │               └─────────────────────┘                         │
│             │               ┌─────────────────────┐                         │
│             │               │   Qdrant            │                         │
│             └───────────────│   (Vector DB)       │                         │
│                             │   Port 6333/6334    │                         │
│                             └─────────────────────┘                         │
└─────────────────────────────────────────────────────────────────────────────┘
                                      ▲
                                      │ WebSocket
                                      │ (ws://:8080/ws)
                                      ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                           RASPBERRY PI ("edda")                             │
│                                                                              │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │   Voice Client (Python)                                              │    │
│  │                                                                       │    │
│  │   - Audio capture (PyAudio)                                          │    │
│  │   - VAD (Silero)                                                      │    │
│  │   - Speech detection state machine                                    │    │
│  │   - Audio playback with caching                                       │    │
│  │   - Reconnection handling                                             │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                                                              │
│   [Microphone] ────► [Client] ◄────► [Speaker/DAC]                          │
└─────────────────────────────────────────────────────────────────────────────┘
```

## Components

### 1. EDDA Server (`/server`)

The brain of the operation. A C#/.NET 8 application that handles all voice processing logic.

**Location:** `server/src/EDDA.Server/`  
**Runtime:** .NET 8  
**Port:** 8080

#### Core Services

| Service | File | Purpose |
|---------|------|---------|
| `WebSocketHandler` | `Handlers/WebSocketHandler.cs` | Connection management, message routing |
| `VoiceSession` | `Models/VoiceSession.cs` | Per-connection state machine (activation, STT pipeline) |
| `WhisperService` | `Services/WhisperService.cs` | Local speech-to-text via Whisper.net (large-v3-turbo) |
| `EddaAgent` | `Agent/EddaAgent.cs` | LLM orchestration with streaming and tool calling |
| `ResponsePipeline` | `Services/ResponsePipeline.cs` | TTS orchestration, sentence streaming, tempo adjustment |
| `TtsService` | `Services/TtsService.cs` | HTTP client for TTS microservice |

#### LLM Configuration

Two-tier model architecture via OpenRouter:
- **Main model** (`google/gemini-3-flash-preview`): Agent responses, tool calling
- **Fast model** (`anthropic/claude-haiku-4.5`): Wake word detection (cheap, instant)

#### Agent System

The agent (`EddaAgent`) handles:
- **Streaming LLM responses** with incremental sentence detection
- **Tool calling** with automatic execution and continuation (up to 10 rounds)
- **Context injection** via pluggable providers
- **Per-turn memory search** for RAG (retrieval-augmented generation)
- **Reasoning model support**: Preserves `reasoning_details` from models like Gemini 3

Context providers (registered in `Program.cs`):
| Provider | Key | Purpose |
|----------|-----|---------|
| `TimeContextProvider` | `time_context` | Current time, day of week |
| `ConversationContextProvider` | `conversation_context` | Recent conversation history |
| `MemoryContextProvider` | `memory_context` | Relevant memories from past conversations |

#### Tool System

Tools are auto-discovered via reflection from classes implementing `ILlmTool`.

| Tool | File | Purpose |
|------|------|---------|
| `WebSearchTool` | `Tools/WebSearchTool.cs` | Search the web via Tavily |
| `NewsSearchTool` | `Tools/NewsSearchTool.cs` | Search recent news |
| `WebExtractTool` | `Tools/WebExtractTool.cs` | Extract content from URLs |
| `SetVolumeTool` | `Tools/SetVolumeTool.cs` | Control client volume |
| `EndConversationTool` | `Tools/EndConversationTool.cs` | Deactivate session after response |

Tools use a type-safe parameter system with JSON schema generation for LLM function calling.

#### Memory System

Vector-based conversation memory using Qdrant:

| Service | File | Purpose |
|---------|------|---------|
| `QdrantMemoryService` | `Services/Memory/QdrantMemoryService.cs` | Vector storage, search with time-decay |
| `OpenRouterEmbeddingService` | `Services/Memory/OpenRouterEmbeddingService.cs` | Embeddings via Qwen3-Embedding-8B (1024d) |

Memory features:
- **Time-decay search**: Balances semantic relevance with recency
- **Per-conversation persistence**: Exchanges are stored when conversation ends
- **Filtered search**: By type, conversation ID, date range

### 2. Voice Client (`/pi-client`)

Python client running on a Raspberry Pi (or any Linux box with audio).

**Location:** `pi-client/`  
**Runtime:** Python 3.11+  
**Entry point:** `client.py`

#### Components

| Module | File | Purpose |
|--------|------|---------|
| `AudioDevice` | `edda/audio/device.py` | PyAudio wrapper with device selection |
| `AudioProcessor` | `edda/audio/processor.py` | Silero VAD integration |
| `AudioPlayer` | `edda/audio/playback.py` | Non-blocking audio playback |
| `SpeechDetector` | `edda/speech/detector.py` | State machine for speech segmentation |
| `InputPipeline` | `edda/speech/pipeline.py` | Audio capture → VAD → server streaming |
| `ServerConnection` | `edda/network/connection.py` | WebSocket client with reconnection |
| `MessageHandler` | `edda/network/handler.py` | Handle server messages (audio, status) |
| `CacheManager` | `edda/cache.py` | Local audio caching for loading sounds |

#### Audio Flow

```
Microphone → Capture (48kHz) → Resample (16kHz) → VAD → Speech Detector
                                                           │
                                     ┌─────────────────────┘
                                     ▼
                              [Speech Start]
                                     │
                                     ▼
                         Stream chunks to server
                                     │
                                     ▼
                              [Speech End]
                                     │
                                     ▼
                          Send end_speech message
```

### 3. TTS Service (`/tts-service`)

GPU-accelerated text-to-speech using Chatterbox Turbo.

**Location:** `tts-service/`  
**Runtime:** Python 3.11+ with PyTorch  
**Container:** Docker with NVIDIA runtime  
**Port:** 5000

#### Features

- **Chatterbox Turbo** (350M params) for high-quality speech
- **Voice cloning** via cached reference audio
- **CUDA optimization** with torch.compile
- **Warmup inference** for consistent latency
- **Paralinguistic tags**: Supports `[chuckle]`, `[sigh]`, `[laugh]`, etc.
- **Exponential backoff retry** on model load failure

#### Endpoints

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/health` | Health check with GPU info |
| POST | `/tts` | Generate speech from text |
| GET | `/voice/{id}` | Check if voice is cached |
| POST | `/voice/{id}` | Upload voice for caching |

#### TTS Client Features (Server-side)

The C# server's `TtsService` includes production-grade reliability:
- **Circuit breaker** with configurable threshold and timeout
- **Multi-endpoint failover** for Chatterbox (priority-based)
- **Voice file embedding** - voices are compiled into server binary, uploaded on-demand to TTS service
- **Automatic retry** with exponential backoff

### 4. Piper Service (`/piper-service`)

Fast CPU-based TTS alternative using Piper (ONNX).

**Location:** `piper-service/`  
**Runtime:** Python 3.11+  
**Container:** Docker (CPU only)  
**Port:** 5001

20-50x realtime performance, lower quality than Chatterbox. Useful for development or when GPU is unavailable.

### 5. Qdrant (`/docker`)

Vector database for semantic memory search.

**Container:** `qdrant/qdrant:v1.12.1`  
**Ports:** 6333 (REST), 6334 (gRPC)  
**Storage:** Docker volume `qdrant-data`

Collection: `edda_memories`
- Dimensions: 1024 (Qwen3-Embedding-8B with Matryoshka)
- Distance: Cosine similarity
- Indexes: type, conversation_id, created_at

### 6. Dashboard (`/dashboard`)

React/TypeScript log viewer for debugging distributed systems.

**Location:** `dashboard/`  
**Runtime:** Bun  
**Frontend:** Vite + React  
**Backend:** Bun WebSocket server

Aggregates logs from:
- EDDA Server (SSH → file)
- Voice Client (SSH → journalctl)
- TTS Services (Docker logs)

## Message Protocol

### Client → Server

| Message Type | Payload | Purpose |
|--------------|---------|---------|
| `audio_chunk` | `{ data: base64 }` | PCM audio chunk (16kHz, 16-bit, mono) |
| `end_speech` | `{}` | VAD detected end of speech |

### Server → Client

| Message Type | Payload | Purpose |
|--------------|---------|---------|
| `status` | `{ status: "active" \| "inactive" \| "deactivated" }` | Session state change |
| `sentence` | `{ data, index, total, duration_ms, sample_rate, tempo }` | TTS audio for playback |
| `cache_play` | `{ key, loop }` | Play cached audio (loading sound) |
| `cache_store` | `{ key, data, sample_rate, channels, duration_ms }` | Store audio in client cache |
| `response_complete` | `{}` | All TTS sentences sent |

## Wake Word System

The system uses LLM-based wake word detection rather than a dedicated model:

1. Client always streams audio when speech is detected
2. Server transcribes with Whisper
3. If inactive, transcription is sent to WakeWordService (fast LLM check)
4. Wake word patterns: "Hey Nyxie", "Nixie", etc.
5. On detection: session activates, conversation starts

Deactivation:
- Say "done for now" (hardcoded phrase)
- Tool-based: LLM calls `end_conversation` tool
- Timeout (future): after period of inactivity

## Personality

Nyxie (the assistant persona) is defined in `server/src/EDDA.Server/Agent/Prompts/Templates/system.md`:

- British female voice (matches TTS model)
- Sharp, direct, opinionated
- Not sycophantic or corporate
- Supports paralinguistic tags: `[sigh]`, `[chuckle]`, `[laugh]`, etc.
- Uncensored operation (personal project, not a product)

## Configuration

### Server Environment Variables

| Variable | Purpose | Default |
|----------|---------|---------|
| `OPENROUTER_API_KEY` | LLM API key (required) | - |
| `OPENROUTER_DEFAULT_MODEL` | Main LLM model | `google/gemini-3-flash-preview` |
| `OPENROUTER_FAST_MODEL` | Fast model (wake word detection) | `anthropic/claude-haiku-4.5` |
| `TTS_BASE_URL` | TTS service URL | `http://localhost:5000` |
| `WEBSEARCH_API_KEY` | Tavily API key (optional) | - |

### Client Configuration (`config.yaml`)

```yaml
server:
  host: "10.0.0.176"
  port: 8080

audio:
  input_device_name: "SoloCast"
  capture_rate: 48000
  target_rate: 16000

vad:
  threshold: 0.5
  silence_duration_ms: 900

cache:
  directory: "./cache"
```

## Deployment

### Scripts (`/scripts`)

| Script | Purpose |
|--------|---------|
| `deploy-server.sh` | Deploy C# server to basement |
| `deploy-pi.sh` | Deploy Python client to Pi |
| `docker-up.sh` | Start Docker services |
| `docker-down.sh` | Stop Docker services |
| `dashboard.sh` | Start log dashboard |

### Systemd Services

| Service | Host | File |
|---------|------|------|
| `edda-server.service` | basement | `scripts/edda-server.service` |
| `edda-client.service` | edda (Pi) | `scripts/edda-client.service` |

## Data Flow (Full Request)

```
1. User speaks into microphone
2. Pi client captures audio, runs VAD
3. Speech detected → stream chunks to server via WebSocket
4. Silence detected → send end_speech
5. Server transcribes with Whisper
6. If inactive: check for wake word, activate if found
7. If active: search memory for context
8. Build system prompt with context providers
9. Stream to LLM (OpenRouter)
10. Agent processes response:
    - Extract sentences as they complete
    - Execute tools if requested
    - Continue until response complete
11. For each sentence:
    - Send to TTS service
    - Apply tempo adjustment
    - Stream WAV to client
12. Client plays audio, pauses capture during playback
13. Response complete → resume listening
14. If deactivation requested → persist conversation to memory
```

## Future Considerations

- **Multi-user support**: VoiceSession is already per-connection isolated
- **Local LLM**: Whisper runs locally, LLM could too with enough GPU
- **Proactive notifications**: Agent could initiate conversation (calendar, reminders)
- **Home automation**: Add tools for lights, thermostat, etc.
- **Mobile client**: WebSocket protocol is device-agnostic
