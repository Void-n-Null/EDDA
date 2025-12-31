import { useEffect, useRef, useState } from "react";
import type { LogSource, LogEntry, SourceStatus } from "../App";

interface LogPanelProps {
  source: LogSource;
  logs: LogEntry[];
  status: SourceStatus;
  onClear: () => void;
  onRestart: () => void;
  copyMode?: boolean;
  onLogClick?: (timestamp: number) => void;
  selectionStart?: number | null;
  selectionEnd?: number | null;
}

// Simple log level detection for coloring
function getLogLevel(
  line: string
): "error" | "warn" | "info" | "debug" | "default" {
  const lower = line.toLowerCase();
  if (
    lower.includes("error") ||
    lower.includes("exception") ||
    lower.includes("fail") ||
    lower.includes("[stderr]")
  ) {
    return "error";
  }
  if (lower.includes("warn")) {
    return "warn";
  }
  if (lower.includes("debug") || lower.includes("trace")) {
    return "debug";
  }
  if (lower.includes("info")) {
    return "info";
  }
  return "default";
}

function getLogColor(level: ReturnType<typeof getLogLevel>): string {
  switch (level) {
    case "error":
      return "var(--accent-red)";
    case "warn":
      return "var(--accent-orange)";
    case "info":
      return "var(--accent-blue)";
    case "debug":
      return "var(--text-muted)";
    default:
      return "var(--text-primary)";
  }
}

function formatTimeWithMs(ts: number): string {
  const d = new Date(ts);
  const h = d.getHours().toString().padStart(2, "0");
  const m = d.getMinutes().toString().padStart(2, "0");
  const s = d.getSeconds().toString().padStart(2, "0");
  const ms = d.getMilliseconds().toString().padStart(3, "0");
  return `${h}:${m}:${s}.${ms}`;
}

export function LogPanel({
  source,
  logs,
  status,
  onClear,
  onRestart,
  copyMode = false,
  onLogClick,
  selectionStart,
  selectionEnd,
}: LogPanelProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const [autoScroll, setAutoScroll] = useState(true);
  const [filter, setFilter] = useState("");

  // Auto-scroll when new logs arrive (but not in copy mode)
  useEffect(() => {
    if (autoScroll && containerRef.current && !copyMode) {
      containerRef.current.scrollTop = containerRef.current.scrollHeight;
    }
  }, [logs, autoScroll, copyMode]);

  // Detect manual scroll
  const handleScroll = () => {
    if (!containerRef.current) return;
    const { scrollTop, scrollHeight, clientHeight } = containerRef.current;
    const isAtBottom = scrollHeight - scrollTop - clientHeight < 50;
    setAutoScroll(isAtBottom);
  };

  const filteredLogs = filter
    ? logs.filter((log) =>
        log.line.toLowerCase().includes(filter.toLowerCase())
      )
    : logs;

  const isInSelection = (timestamp: number): boolean => {
    if (selectionStart == null || selectionEnd == null) return false;
    return timestamp >= selectionStart && timestamp <= selectionEnd;
  };

  return (
    <div
      style={{
        display: "flex",
        flexDirection: "column",
        background: "var(--bg-panel)",
        overflow: "hidden",
      }}
    >
      {/* Panel Header */}
      <div
        style={{
          display: "flex",
          alignItems: "center",
          justifyContent: "space-between",
          padding: "10px 14px",
          background: "var(--bg-secondary)",
          borderBottom: "1px solid var(--border-color)",
        }}
      >
        <div style={{ display: "flex", alignItems: "center", gap: "10px" }}>
          {/* Status indicator */}
          <div
            style={{
              width: "8px",
              height: "8px",
              borderRadius: "50%",
              background: status.connected ? source.color : "var(--accent-red)",
              boxShadow: status.connected
                ? `0 0 6px ${source.color}`
                : "0 0 6px var(--accent-red)",
            }}
            className={status.connected ? "" : "animate-pulse"}
          />
          <span
            style={{
              fontSize: "13px",
              fontWeight: 600,
              color: source.color,
            }}
          >
            {source.name}
          </span>
          {status.error && (
            <span
              style={{
                fontSize: "11px",
                color: "var(--accent-red)",
                marginLeft: "4px",
              }}
            >
              ({status.error})
            </span>
          )}
        </div>

        <div style={{ display: "flex", alignItems: "center", gap: "8px" }}>
          {/* Copy mode indicator */}
          {copyMode && (
            <span
              style={{
                fontSize: "10px",
                color: "var(--accent-blue)",
                padding: "2px 8px",
                background: "rgba(96, 165, 250, 0.15)",
                borderRadius: "4px",
                fontWeight: 500,
              }}
            >
              Click to select
            </span>
          )}

          {/* Search/Filter */}
          <input
            type="text"
            placeholder="Filter..."
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            style={{
              width: "100px",
              padding: "4px 8px",
              fontSize: "11px",
              background: "var(--bg-tertiary)",
              border: "1px solid var(--border-color)",
              borderRadius: "4px",
              color: "var(--text-primary)",
              outline: "none",
            }}
          />

          {/* Auto-scroll indicator */}
          <button
            onClick={() => {
              setAutoScroll(true);
              if (containerRef.current) {
                containerRef.current.scrollTop =
                  containerRef.current.scrollHeight;
              }
            }}
            style={{
              padding: "4px 8px",
              fontSize: "11px",
              background: autoScroll ? "var(--bg-tertiary)" : "transparent",
              border: `1px solid ${autoScroll ? source.color : "var(--border-color)"}`,
              borderRadius: "4px",
              color: autoScroll ? source.color : "var(--text-muted)",
              cursor: "pointer",
              transition: "all 0.15s ease",
            }}
            title={
              autoScroll ? "Auto-scroll enabled" : "Click to enable auto-scroll"
            }
          >
            ↓
          </button>

          {/* Clear button */}
          <button
            onClick={onClear}
            style={{
              padding: "4px 8px",
              fontSize: "11px",
              background: "transparent",
              border: "1px solid var(--border-color)",
              borderRadius: "4px",
              color: "var(--text-muted)",
              cursor: "pointer",
              transition: "all 0.15s ease",
            }}
            onMouseOver={(e) => {
              e.currentTarget.style.borderColor = "var(--accent-orange)";
              e.currentTarget.style.color = "var(--accent-orange)";
            }}
            onMouseOut={(e) => {
              e.currentTarget.style.borderColor = "var(--border-color)";
              e.currentTarget.style.color = "var(--text-muted)";
            }}
            title="Clear logs"
          >
            ✕
          </button>

          {/* Restart button */}
          <button
            onClick={onRestart}
            style={{
              padding: "4px 8px",
              fontSize: "11px",
              background: "transparent",
              border: "1px solid var(--border-color)",
              borderRadius: "4px",
              color: "var(--text-muted)",
              cursor: "pointer",
              transition: "all 0.15s ease",
            }}
            onMouseOver={(e) => {
              e.currentTarget.style.borderColor = source.color;
              e.currentTarget.style.color = source.color;
            }}
            onMouseOut={(e) => {
              e.currentTarget.style.borderColor = "var(--border-color)";
              e.currentTarget.style.color = "var(--text-muted)";
            }}
            title="Reconnect stream"
          >
            ↻
          </button>
        </div>
      </div>

      {/* Log Content */}
      <div
        ref={containerRef}
        onScroll={handleScroll}
        className="mono"
        style={{
          flex: 1,
          overflow: "auto",
          padding: "8px 12px",
          fontSize: "11px",
          lineHeight: "1.5",
          cursor: copyMode ? "crosshair" : "default",
        }}
      >
        {filteredLogs.length === 0 ? (
          <div
            style={{
              color: "var(--text-muted)",
              fontStyle: "italic",
              padding: "20px",
              textAlign: "center",
            }}
          >
            {status.connected
              ? filter
                ? "No matching logs"
                : "Waiting for logs..."
              : "Disconnected"}
          </div>
        ) : (
          filteredLogs.map((log, i) => {
            const level = getLogLevel(log.line);
            const inSelection = isInSelection(log.timestamp);
            return (
              <div
                key={`${log.timestamp}-${i}`}
                className={copyMode ? "" : "animate-slide-in"}
                onClick={() => copyMode && onLogClick?.(log.timestamp)}
                style={{
                  color: getLogColor(level),
                  whiteSpace: "pre-wrap",
                  wordBreak: "break-word",
                  padding: "2px 4px",
                  marginLeft: "-4px",
                  marginRight: "-4px",
                  borderRadius: "3px",
                  background: inSelection
                    ? "rgba(96, 165, 250, 0.2)"
                    : "transparent",
                  borderLeft: inSelection
                    ? "2px solid var(--accent-blue)"
                    : "2px solid transparent",
                  cursor: copyMode ? "pointer" : "default",
                  transition: "background 0.1s ease",
                }}
                onMouseOver={(e) => {
                  if (copyMode) {
                    e.currentTarget.style.background = inSelection
                      ? "rgba(96, 165, 250, 0.3)"
                      : "rgba(96, 165, 250, 0.1)";
                  }
                }}
                onMouseOut={(e) => {
                  if (copyMode) {
                    e.currentTarget.style.background = inSelection
                      ? "rgba(96, 165, 250, 0.2)"
                      : "transparent";
                  }
                }}
              >
                <span
                  style={{ color: "var(--text-muted)", marginRight: "8px" }}
                >
                  {formatTimeWithMs(log.timestamp)}
                </span>
                {log.line}
              </div>
            );
          })
        )}
      </div>

      {/* Footer with log count */}
      <div
        style={{
          padding: "6px 12px",
          fontSize: "10px",
          color: "var(--text-muted)",
          background: "var(--bg-secondary)",
          borderTop: "1px solid var(--border-color)",
          display: "flex",
          justifyContent: "space-between",
        }}
      >
        <span>
          {filteredLogs.length} {filter ? "matching" : "lines"}
          {filter && ` / ${logs.length} total`}
        </span>
        {!autoScroll && !copyMode && (
          <span style={{ color: "var(--accent-orange)" }}>Scroll paused</span>
        )}
        {copyMode && (
          <span style={{ color: "var(--accent-blue)" }}>Copy mode active</span>
        )}
      </div>
    </div>
  );
}
