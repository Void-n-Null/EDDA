# EDDA Log Dashboard

Real-time log viewer for EDDA distributed systems.

## Log Sources

| Service        | Host     | Connection |
|----------------|----------|------------|
| EDDA Server    | basement | SSH → /tmp/edda.log |
| Voice Client   | edda     | SSH → journalctl |
| TTS (Prod)     | basement | SSH → docker logs |
| TTS (Dev)      | local    | docker logs |

## Quick Start

```bash
# Start both backend and frontend
bun run dev
```

This opens:
- Frontend: http://localhost:3000
- Backend WebSocket: ws://localhost:3001

## Commands

```bash
bun run dev      # Run both backend + frontend
bun run server   # Run only backend
bun run client   # Run only frontend (Vite)
bun run build    # Production build
```

## Requirements

- Bun runtime
- SSH keys configured for `basement` and `edda` hosts
- Docker (for local TTS dev logs)

## Features

- Real-time log streaming via WebSocket
- Color-coded log levels (error, warn, info, debug)
- Per-panel filtering
- Auto-scroll with pause detection
- Connection status indicators
- Stream reconnection on disconnect
