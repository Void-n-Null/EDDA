import { useState, useEffect } from "react";
import type { LogSource, LogEntry } from "../App";

interface CopyPanelProps {
  active: boolean;
  sources: LogSource[];
  allLogs: Map<string, LogEntry[]>;
  startTime: number | null;
  endTime: number | null;
  onClear: () => void;
  onClose: () => void;
}

type OrderMode = "chronological" | "grouped";
type TimeMode = "absolute" | "relative";

export function CopyPanel({
  active,
  sources,
  allLogs,
  startTime,
  endTime,
  onClear,
  onClose,
}: CopyPanelProps) {
  const [selectedSources, setSelectedSources] = useState<Set<string>>(
    new Set(sources.map((s) => s.id))
  );
  const [orderMode, setOrderMode] = useState<OrderMode>("chronological");
  const [timeMode, setTimeMode] = useState<TimeMode>("absolute");

  // Update selected sources when sources change
  useEffect(() => {
    setSelectedSources(new Set(sources.map((s) => s.id)));
  }, [sources]);

  if (!active) return null;

  const toggleSource = (id: string) => {
    setSelectedSources((prev) => {
      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  };

  const formatTimeMs = (ts: number): string => {
    const d = new Date(ts);
    const h = d.getHours().toString().padStart(2, "0");
    const m = d.getMinutes().toString().padStart(2, "0");
    const s = d.getSeconds().toString().padStart(2, "0");
    const ms = d.getMilliseconds().toString().padStart(3, "0");
    return `${h}:${m}:${s}.${ms}`;
  };

  const formatRelativeTime = (ts: number, baseTime: number): string => {
    const diff = ts - baseTime;
    const totalSeconds = diff / 1000;
    const sign = totalSeconds >= 0 ? "+" : "-";
    const abs = Math.abs(totalSeconds);
    const s = Math.floor(abs);
    const ms = Math.round((abs - s) * 1000);
    return `${sign}${s}.${ms.toString().padStart(3, "0")}s`;
  };

  const formatTimeDisplay = (ts: number | null) => {
    if (!ts) return "--:--:--.---";
    return formatTimeMs(ts);
  };

  const hasRange = startTime !== null && endTime !== null;
  const rangeStart = hasRange ? Math.min(startTime, endTime) : null;
  const rangeEnd = hasRange ? Math.max(startTime, endTime) : null;

  const compareSourceNames = (a: LogSource, b: LogSource) =>
    a.name.localeCompare(b.name, undefined, { sensitivity: "base" });

  const getFirstLogTimestampForSourceInRange = (sourceId: string): number => {
    if (!rangeStart || !rangeEnd) return Number.POSITIVE_INFINITY;
    const sourceLogs = allLogs.get(sourceId) || [];
    for (const entry of sourceLogs) {
      if (entry.timestamp >= rangeStart && entry.timestamp <= rangeEnd) {
        return entry.timestamp;
      }
    }
    return Number.POSITIVE_INFINITY;
  };

  const orderedSources = (() => {
    // No selection yet: keep it predictable.
    if (!rangeStart || !rangeEnd) {
      return [...sources].sort(compareSourceNames);
    }

    // Selection present: show earliest-appearing sources first (causality-ish).
    return [...sources].sort((a, b) => {
      const ta = getFirstLogTimestampForSourceInRange(a.id);
      const tb = getFirstLogTimestampForSourceInRange(b.id);

      if (ta === tb) return compareSourceNames(a, b);
      return ta - tb;
    });
  })();

  const getLogsInRange = (): string => {
    if (!rangeStart || !rangeEnd) return "";

    const allEntriesInRange: Array<{ source: LogSource; entry: LogEntry }> = [];

    // Collect all logs from selected sources within range
    for (const source of sources) {
      if (!selectedSources.has(source.id)) continue;
      const logs = allLogs.get(source.id) || [];
      for (const entry of logs) {
        if (entry.timestamp >= rangeStart && entry.timestamp <= rangeEnd) {
          allEntriesInRange.push({ source, entry });
        }
      }
    }

    // Sort by timestamp
    allEntriesInRange.sort((a, b) => a.entry.timestamp - b.entry.timestamp);

    const baseTime = allEntriesInRange[0]?.entry.timestamp ?? rangeStart;

    // Format based on mode
    if (orderMode === "chronological") {
      return allEntriesInRange
        .map(({ source, entry }) => {
          const time =
            timeMode === "absolute"
              ? formatTimeMs(entry.timestamp)
              : formatRelativeTime(entry.timestamp, baseTime);
          return `[${time}] [${source.name}] ${entry.line}`;
        })
        .join("\n");
    } else {
      // Grouped by source
      const lines: string[] = [];
      const selectedSourcesList = orderedSources.filter((s) =>
        selectedSources.has(s.id)
      );

      for (const source of selectedSourcesList) {
        const sourceLogs = allEntriesInRange
          .filter((e) => e.source.id === source.id)
          .sort((a, b) => a.entry.timestamp - b.entry.timestamp);

        if (sourceLogs.length === 0) continue;

        lines.push(`=== ${source.name} ===`);
        for (const { entry } of sourceLogs) {
          const time =
            timeMode === "absolute"
              ? formatTimeMs(entry.timestamp)
              : formatRelativeTime(entry.timestamp, baseTime);
          lines.push(`[${time}] ${entry.line}`);
        }
        lines.push("");
      }

      return lines.join("\n").trim();
    }
  };

  const handleCopy = async () => {
    const text = getLogsInRange();
    if (!text) return;

    try {
      await navigator.clipboard.writeText(text);
      // Brief visual feedback
      const btn = document.getElementById("copy-btn");
      if (btn) {
        btn.textContent = "Copied!";
        setTimeout(() => {
          btn.textContent = "Copy to Clipboard";
        }, 1500);
      }
    } catch (e) {
      console.error("Failed to copy:", e);
    }
  };

  const logCount = hasRange
    ? Array.from(allLogs.entries())
        .filter(([id]) => selectedSources.has(id))
        .reduce((count, [, logs]) => {
          return (
            count +
            logs.filter(
              (l) => l.timestamp >= rangeStart! && l.timestamp <= rangeEnd!
            ).length
          );
        }, 0)
    : 0;

  return (
    <div
      style={{
        position: "fixed",
        bottom: 0,
        left: 0,
        right: 0,
        background: "var(--bg-secondary)",
        borderTop: "2px solid var(--accent-blue)",
        padding: "12px 24px",
        display: "flex",
        flexDirection: "column",
        gap: "12px",
        zIndex: 1000,
        boxShadow: "0 -4px 20px rgba(0,0,0,0.4)",
      }}
    >
      {/* Top Row: Time Range + Sources */}
      <div
        style={{
          display: "flex",
          alignItems: "center",
          justifyContent: "space-between",
          gap: "24px",
        }}
      >
        {/* Time Range */}
        <div style={{ display: "flex", alignItems: "center", gap: "24px" }}>
          <div>
            <div
              style={{
                fontSize: "10px",
                color: "var(--text-muted)",
                marginBottom: "2px",
                textTransform: "uppercase",
                letterSpacing: "0.5px",
              }}
            >
              {!startTime
                ? "Click a log line to set start time"
                : !endTime
                  ? "Click another line to set end time"
                  : "Time Range"}
            </div>
            <div
              style={{
                fontSize: "14px",
                fontWeight: 600,
                fontFamily: "'JetBrains Mono', monospace",
                color: hasRange ? "var(--accent-blue)" : "var(--text-muted)",
              }}
            >
              {formatTimeDisplay(startTime)} → {formatTimeDisplay(endTime)}
            </div>
          </div>

          {hasRange && (
            <div
              style={{
                fontSize: "12px",
                color: "var(--text-secondary)",
                padding: "4px 10px",
                background: "var(--bg-tertiary)",
                borderRadius: "4px",
              }}
            >
              {logCount} log{logCount !== 1 ? "s" : ""}
            </div>
          )}
        </div>

        {/* Source Toggles */}
        <div
          style={{
            display: "flex",
            alignItems: "center",
            gap: "8px",
            flexWrap: "wrap",
          }}
        >
          {orderedSources.map((source) => (
            <button
              key={source.id}
              onClick={() => toggleSource(source.id)}
              style={{
                padding: "4px 10px",
                fontSize: "11px",
                fontWeight: 500,
                background: selectedSources.has(source.id)
                  ? "var(--bg-tertiary)"
                  : "transparent",
                border: `1px solid ${
                  selectedSources.has(source.id)
                    ? source.color
                    : "var(--border-color)"
                }`,
                borderRadius: "4px",
                color: selectedSources.has(source.id)
                  ? source.color
                  : "var(--text-muted)",
                cursor: "pointer",
                transition: "all 0.15s ease",
                opacity: selectedSources.has(source.id) ? 1 : 0.5,
              }}
            >
              {source.name}
            </button>
          ))}
        </div>
      </div>

      {/* Bottom Row: Format Options + Actions */}
      <div
        style={{
          display: "flex",
          alignItems: "center",
          justifyContent: "space-between",
          gap: "24px",
        }}
      >
        {/* Format Options */}
        <div style={{ display: "flex", alignItems: "center", gap: "20px" }}>
          {/* Order Mode */}
          <div style={{ display: "flex", alignItems: "center", gap: "6px" }}>
            <span
              style={{
                fontSize: "11px",
                color: "var(--text-muted)",
                textTransform: "uppercase",
                letterSpacing: "0.3px",
              }}
            >
              Order:
            </span>
            <div
              style={{
                display: "flex",
                background: "var(--bg-tertiary)",
                borderRadius: "4px",
                overflow: "hidden",
                border: "1px solid var(--border-color)",
              }}
            >
              <button
                onClick={() => setOrderMode("chronological")}
                style={{
                  padding: "4px 10px",
                  fontSize: "11px",
                  fontWeight: 500,
                  background:
                    orderMode === "chronological"
                      ? "var(--accent-blue)"
                      : "transparent",
                  border: "none",
                  color:
                    orderMode === "chronological"
                      ? "#fff"
                      : "var(--text-secondary)",
                  cursor: "pointer",
                  transition: "all 0.1s ease",
                }}
              >
                Time Order
              </button>
              <button
                onClick={() => setOrderMode("grouped")}
                style={{
                  padding: "4px 10px",
                  fontSize: "11px",
                  fontWeight: 500,
                  background:
                    orderMode === "grouped"
                      ? "var(--accent-blue)"
                      : "transparent",
                  border: "none",
                  color:
                    orderMode === "grouped" ? "#fff" : "var(--text-secondary)",
                  cursor: "pointer",
                  transition: "all 0.1s ease",
                }}
              >
                By Source
              </button>
            </div>
          </div>

          {/* Time Mode */}
          <div style={{ display: "flex", alignItems: "center", gap: "6px" }}>
            <span
              style={{
                fontSize: "11px",
                color: "var(--text-muted)",
                textTransform: "uppercase",
                letterSpacing: "0.3px",
              }}
            >
              Timestamps:
            </span>
            <div
              style={{
                display: "flex",
                background: "var(--bg-tertiary)",
                borderRadius: "4px",
                overflow: "hidden",
                border: "1px solid var(--border-color)",
              }}
            >
              <button
                onClick={() => setTimeMode("absolute")}
                style={{
                  padding: "4px 10px",
                  fontSize: "11px",
                  fontWeight: 500,
                  background:
                    timeMode === "absolute"
                      ? "var(--accent-blue)"
                      : "transparent",
                  border: "none",
                  color:
                    timeMode === "absolute" ? "#fff" : "var(--text-secondary)",
                  cursor: "pointer",
                  transition: "all 0.1s ease",
                }}
              >
                12:01:31.234
              </button>
              <button
                onClick={() => setTimeMode("relative")}
                style={{
                  padding: "4px 10px",
                  fontSize: "11px",
                  fontWeight: 500,
                  background:
                    timeMode === "relative"
                      ? "var(--accent-blue)"
                      : "transparent",
                  border: "none",
                  color:
                    timeMode === "relative" ? "#fff" : "var(--text-secondary)",
                  cursor: "pointer",
                  transition: "all 0.1s ease",
                }}
              >
                +0.000s
              </button>
            </div>
          </div>
        </div>

        {/* Actions */}
        <div style={{ display: "flex", alignItems: "center", gap: "10px" }}>
          <button
            onClick={onClear}
            style={{
              padding: "6px 14px",
              fontSize: "12px",
              fontWeight: 500,
              background: "transparent",
              border: "1px solid var(--border-color)",
              borderRadius: "5px",
              color: "var(--text-secondary)",
              cursor: "pointer",
              transition: "all 0.15s ease",
            }}
            onMouseOver={(e) => {
              e.currentTarget.style.borderColor = "var(--accent-orange)";
              e.currentTarget.style.color = "var(--accent-orange)";
            }}
            onMouseOut={(e) => {
              e.currentTarget.style.borderColor = "var(--border-color)";
              e.currentTarget.style.color = "var(--text-secondary)";
            }}
          >
            Clear
          </button>

          <button
            id="copy-btn"
            onClick={handleCopy}
            disabled={!hasRange || logCount === 0}
            style={{
              padding: "6px 18px",
              fontSize: "12px",
              fontWeight: 600,
              background:
                hasRange && logCount > 0
                  ? "var(--accent-blue)"
                  : "var(--bg-tertiary)",
              border: "none",
              borderRadius: "5px",
              color: hasRange && logCount > 0 ? "#fff" : "var(--text-muted)",
              cursor: hasRange && logCount > 0 ? "pointer" : "not-allowed",
              transition: "all 0.15s ease",
            }}
          >
            Copy to Clipboard
          </button>

          <button
            onClick={onClose}
            style={{
              padding: "6px 10px",
              fontSize: "12px",
              background: "transparent",
              border: "1px solid var(--border-color)",
              borderRadius: "5px",
              color: "var(--text-muted)",
              cursor: "pointer",
              transition: "all 0.15s ease",
            }}
            onMouseOver={(e) => {
              e.currentTarget.style.borderColor = "var(--accent-red)";
              e.currentTarget.style.color = "var(--accent-red)";
            }}
            onMouseOut={(e) => {
              e.currentTarget.style.borderColor = "var(--border-color)";
              e.currentTarget.style.color = "var(--text-muted)";
            }}
          >
            ✕
          </button>
        </div>
      </div>
    </div>
  );
}
