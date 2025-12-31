import { useState, useCallback } from "react";
import { LogPanel } from "./components/LogPanel";
import { Header } from "./components/Header";
import { CopyPanel } from "./components/CopyPanel";
import { useWebSocket } from "./hooks/useWebSocket";

export interface LogSource {
  id: string;
  name: string;
  color: string;
}

export interface LogEntry {
  sourceId: string;
  line: string;
  timestamp: number;
}

export interface SourceStatus {
  connected: boolean;
  error?: string;
}

function App() {
  const [sources, setSources] = useState<LogSource[]>([]);
  const [logs, setLogs] = useState<Map<string, LogEntry[]>>(new Map());
  const [status, setStatus] = useState<Map<string, SourceStatus>>(new Map());
  const [connected, setConnected] = useState(false);

  // Copy mode state
  const [copyMode, setCopyMode] = useState(false);
  const [copyStartTime, setCopyStartTime] = useState<number | null>(null);
  const [copyEndTime, setCopyEndTime] = useState<number | null>(null);

  const handleMessage = useCallback((data: any) => {
    switch (data.type) {
      case "sources":
        setSources(data.sources);
        // Initialize empty log arrays
        setLogs((prev) => {
          const next = new Map(prev);
          for (const source of data.sources) {
            if (!next.has(source.id)) {
              next.set(source.id, []);
            }
          }
          return next;
        });
        break;

      case "status":
        setStatus((prev) => {
          const next = new Map(prev);
          next.set(data.sourceId, {
            connected: data.connected,
            error: data.error,
          });
          return next;
        });
        break;

      case "log":
        setLogs((prev) => {
          const next = new Map(prev);
          const existing = next.get(data.sourceId) || [];
          // Keep last 1000 lines per source
          const updated = [...existing, data].slice(-1000);
          next.set(data.sourceId, updated);
          return next;
        });
        break;
    }
  }, []);

  const { send, reconnect } = useWebSocket({
    url: "ws://localhost:3001",
    onMessage: handleMessage,
    onOpen: () => setConnected(true),
    onClose: () => setConnected(false),
  });

  const handleClear = useCallback((sourceId: string) => {
    setLogs((prev) => {
      const next = new Map(prev);
      next.set(sourceId, []);
      return next;
    });
  }, []);

  const handleRestart = useCallback(
    (sourceId: string) => {
      send({ type: "restart", sourceId });
    },
    [send]
  );

  const handleLogClick = useCallback(
    (timestamp: number) => {
      if (!copyMode) return;

      if (copyStartTime === null) {
        setCopyStartTime(timestamp);
      } else if (copyEndTime === null) {
        setCopyEndTime(timestamp);
      } else {
        // Reset and start new selection
        setCopyStartTime(timestamp);
        setCopyEndTime(null);
      }
    },
    [copyMode, copyStartTime, copyEndTime]
  );

  const handleCopyModeToggle = useCallback(() => {
    setCopyMode((prev) => !prev);
    if (copyMode) {
      // Exiting copy mode, clear selection
      setCopyStartTime(null);
      setCopyEndTime(null);
    }
  }, [copyMode]);

  const handleCopyClear = useCallback(() => {
    setCopyStartTime(null);
    setCopyEndTime(null);
  }, []);

  const handleCopyClose = useCallback(() => {
    setCopyMode(false);
    setCopyStartTime(null);
    setCopyEndTime(null);
  }, []);

  // Calculate which timestamps are in the selected range for highlighting
  const rangeStart =
    copyStartTime !== null && copyEndTime !== null
      ? Math.min(copyStartTime, copyEndTime)
      : null;
  const rangeEnd =
    copyStartTime !== null && copyEndTime !== null
      ? Math.max(copyStartTime, copyEndTime)
      : null;

  return (
    <div
      style={{
        height: "100vh",
        display: "flex",
        flexDirection: "column",
        background: "var(--bg-primary)",
      }}
    >
      <Header
        connected={connected}
        onReconnect={reconnect}
        copyMode={copyMode}
        onCopyModeToggle={handleCopyModeToggle}
      />

      <div
        style={{
          flex: 1,
          display: "grid",
          gridTemplateColumns: "repeat(2, 1fr)",
          gridTemplateRows: "repeat(2, 1fr)",
          gap: "1px",
          background: "var(--border-color)",
          padding: "1px",
          overflow: "hidden",
          // Add bottom padding when copy panel is visible
          paddingBottom: copyMode ? "110px" : "1px",
        }}
      >
        {sources.map((source) => (
          <LogPanel
            key={source.id}
            source={source}
            logs={logs.get(source.id) || []}
            status={status.get(source.id) || { connected: false }}
            onClear={() => handleClear(source.id)}
            onRestart={() => handleRestart(source.id)}
            copyMode={copyMode}
            onLogClick={handleLogClick}
            selectionStart={rangeStart}
            selectionEnd={rangeEnd}
          />
        ))}

        {sources.length === 0 && (
          <div
            style={{
              gridColumn: "1 / -1",
              gridRow: "1 / -1",
              display: "flex",
              alignItems: "center",
              justifyContent: "center",
              background: "var(--bg-secondary)",
              color: "var(--text-muted)",
            }}
          >
            {connected
              ? "Waiting for log sources..."
              : "Connecting to backend..."}
          </div>
        )}
      </div>

      <CopyPanel
        active={copyMode}
        sources={sources}
        allLogs={logs}
        startTime={copyStartTime}
        endTime={copyEndTime}
        onClear={handleCopyClear}
        onClose={handleCopyClose}
      />
    </div>
  );
}

export default App;
