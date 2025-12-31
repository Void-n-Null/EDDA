import { WebSocketServer, WebSocket } from "ws";
import { Client as SSHClient } from "ssh2";
import { spawn, type Subprocess } from "bun";
import { readFileSync, existsSync } from "fs";
import { homedir } from "os";
import { join } from "path";

// ============================================================================
// Configuration
// ============================================================================

interface LogSource {
  id: string;
  name: string;
  type: "ssh" | "local";
  host?: string;
  user?: string;
  command: string;
  color: string;
}

const LOG_SOURCES: LogSource[] = [
  {
    id: "server",
    name: "EDDA Server",
    type: "ssh",
    host: "10.0.0.176",
    user: "blake",
    command: "journalctl -u edda-server -f --no-hostname -o cat -n 50",
    color: "#4ade80", // green
  },
  {
    id: "pi",
    name: "Voice Client (Pi)",
    type: "ssh",
    host: "edda-pi.local",
    user: "blake",
    command: "journalctl -u edda-client -f --no-hostname -o cat -n 50",
    color: "#60a5fa", // blue
  },
  {
    id: "tts-prod",
    name: "TTS (Basement)",
    type: "ssh",
    host: "10.0.0.176",
    user: "blake",
    command: "cd /home/blake/edda-server/docker && docker compose logs -f tts 2>&1 || echo 'Docker not running'",
    color: "#f472b6", // pink
  },
  {
    id: "tts-dev",
    name: "TTS (Local Dev)",
    type: "local",
    command: "docker compose -f /mnt/dev/EDDA/docker/docker-compose.dev.yml logs -f 2>&1 || echo 'Docker not running'",
    color: "#facc15", // yellow
  },
];

// ============================================================================
// State Management
// ============================================================================

interface StreamState {
  source: LogSource;
  ssh?: SSHClient;
  process?: Subprocess;
  connected: boolean;
  retryCount: number;
  retryTimeout?: ReturnType<typeof setTimeout>;
}

const streams = new Map<string, StreamState>();
const clients = new Set<WebSocket>();

// ============================================================================
// SSH Key Loading
// ============================================================================

function getSSHKey(): Buffer | undefined {
  const keyPaths = [
    join(homedir(), ".ssh", "id_ed25519"),
    join(homedir(), ".ssh", "id_rsa"),
  ];

  for (const keyPath of keyPaths) {
    if (existsSync(keyPath)) {
      console.log(`Using SSH key: ${keyPath}`);
      return readFileSync(keyPath);
    }
  }

  console.warn("No SSH key found");
  return undefined;
}

const sshKey = getSSHKey();

// ============================================================================
// Message Broadcasting
// ============================================================================

interface LogMessage {
  type: "log";
  sourceId: string;
  line: string;
  timestamp: number;
}

interface StatusMessage {
  type: "status";
  sourceId: string;
  connected: boolean;
  error?: string;
}

interface SourcesMessage {
  type: "sources";
  sources: Array<{ id: string; name: string; color: string }>;
}

function broadcast(message: LogMessage | StatusMessage | SourcesMessage) {
  const json = JSON.stringify(message);
  for (const client of clients) {
    if (client.readyState === WebSocket.OPEN) {
      client.send(json);
    }
  }
}

function sendStatus(sourceId: string, connected: boolean, error?: string) {
  broadcast({ type: "status", sourceId, connected, error });
}

function sendLog(sourceId: string, line: string) {
  broadcast({
    type: "log",
    sourceId,
    line,
    timestamp: Date.now(),
  });
}

// ============================================================================
// SSH Stream Handler
// ============================================================================

function startSSHStream(source: LogSource) {
  const state: StreamState = {
    source,
    connected: false,
    retryCount: 0,
  };
  streams.set(source.id, state);

  const connect = () => {
    if (!sshKey) {
      sendStatus(source.id, false, "No SSH key found");
      return;
    }

    const ssh = new SSHClient();
    state.ssh = ssh;

    ssh.on("ready", () => {
      console.log(`SSH connected: ${source.name}`);
      state.connected = true;
      state.retryCount = 0;
      sendStatus(source.id, true);

      ssh.exec(source.command, (err, stream) => {
        if (err) {
          console.error(`Exec error for ${source.name}:`, err);
          sendStatus(source.id, false, err.message);
          ssh.end();
          return;
        }

        stream.on("data", (data: Buffer) => {
          const lines = data.toString().split("\n").filter(Boolean);
          for (const line of lines) {
            sendLog(source.id, line);
          }
        });

        stream.stderr.on("data", (data: Buffer) => {
          const lines = data.toString().split("\n").filter(Boolean);
          for (const line of lines) {
            sendLog(source.id, `[stderr] ${line}`);
          }
        });

        stream.on("close", () => {
          console.log(`Stream closed: ${source.name}`);
          state.connected = false;
          sendStatus(source.id, false);
          scheduleReconnect(source.id);
        });
      });
    });

    ssh.on("error", (err) => {
      console.error(`SSH error for ${source.name}:`, err.message);
      state.connected = false;
      sendStatus(source.id, false, err.message);
      scheduleReconnect(source.id);
    });

    ssh.on("close", () => {
      state.connected = false;
      sendStatus(source.id, false);
    });

    ssh.connect({
      host: source.host,
      username: source.user,
      privateKey: sshKey,
      readyTimeout: 10000,
      keepaliveInterval: 30000,
    });
  };

  connect();
}

// ============================================================================
// Local Process Handler
// ============================================================================

function startLocalStream(source: LogSource) {
  const state: StreamState = {
    source,
    connected: false,
    retryCount: 0,
  };
  streams.set(source.id, state);

  const spawn_process = () => {
    console.log(`Starting local process: ${source.name}`);

    const proc = spawn({
      cmd: ["sh", "-c", source.command],
      stdout: "pipe",
      stderr: "pipe",
    });

    state.process = proc;
    state.connected = true;
    sendStatus(source.id, true);

    // Handle stdout
    (async () => {
      const reader = proc.stdout.getReader();
      const decoder = new TextDecoder();
      let buffer = "";

      try {
        while (true) {
          const { done, value } = await reader.read();
          if (done) break;

          buffer += decoder.decode(value, { stream: true });
          const lines = buffer.split("\n");
          buffer = lines.pop() || "";

          for (const line of lines) {
            if (line.trim()) {
              sendLog(source.id, line);
            }
          }
        }
      } catch (e) {
        // Stream closed
      }
    })();

    // Handle stderr
    (async () => {
      const reader = proc.stderr.getReader();
      const decoder = new TextDecoder();
      let buffer = "";

      try {
        while (true) {
          const { done, value } = await reader.read();
          if (done) break;

          buffer += decoder.decode(value, { stream: true });
          const lines = buffer.split("\n");
          buffer = lines.pop() || "";

          for (const line of lines) {
            if (line.trim()) {
              sendLog(source.id, `[stderr] ${line}`);
            }
          }
        }
      } catch (e) {
        // Stream closed
      }
    })();

    // Wait for process to exit
    proc.exited.then((code) => {
      console.log(`Local process exited: ${source.name} (code: ${code})`);
      state.connected = false;
      sendStatus(source.id, false);
      scheduleReconnect(source.id);
    });
  };

  spawn_process();
}

// ============================================================================
// Reconnection Logic
// ============================================================================

function scheduleReconnect(sourceId: string) {
  const state = streams.get(sourceId);
  if (!state) return;

  const delay = Math.min(1000 * Math.pow(2, state.retryCount), 30000);
  state.retryCount++;

  console.log(`Scheduling reconnect for ${state.source.name} in ${delay}ms`);

  state.retryTimeout = setTimeout(() => {
    if (state.source.type === "ssh") {
      state.ssh?.end();
      startSSHStream(state.source);
    } else {
      startLocalStream(state.source);
    }
  }, delay);
}

// ============================================================================
// WebSocket Server
// ============================================================================

const wss = new WebSocketServer({ port: 3001 });

wss.on("connection", (ws) => {
  console.log("Client connected");
  clients.add(ws);

  // Send available sources
  ws.send(
    JSON.stringify({
      type: "sources",
      sources: LOG_SOURCES.map((s) => ({ id: s.id, name: s.name, color: s.color })),
    })
  );

  // Send current status for all sources
  for (const [sourceId, state] of streams) {
    ws.send(
      JSON.stringify({
        type: "status",
        sourceId,
        connected: state.connected,
      })
    );
  }

  ws.on("message", (data) => {
    try {
      const msg = JSON.parse(data.toString());
      
      // Handle control messages
      if (msg.type === "restart" && msg.sourceId) {
        const state = streams.get(msg.sourceId);
        if (state) {
          console.log(`Restart requested for: ${state.source.name}`);
          if (state.retryTimeout) clearTimeout(state.retryTimeout);
          state.ssh?.end();
          state.process?.kill();
          
          if (state.source.type === "ssh") {
            startSSHStream(state.source);
          } else {
            startLocalStream(state.source);
          }
        }
      }
    } catch (e) {
      // Ignore invalid messages
    }
  });

  ws.on("close", () => {
    console.log("Client disconnected");
    clients.delete(ws);
  });
});

// ============================================================================
// Startup
// ============================================================================

console.log("Starting EDDA Log Dashboard backend on ws://localhost:3001");

for (const source of LOG_SOURCES) {
  if (source.type === "ssh") {
    startSSHStream(source);
  } else {
    startLocalStream(source);
  }
}

console.log("Log streams initialized");
