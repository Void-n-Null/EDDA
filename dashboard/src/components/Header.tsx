interface HeaderProps {
  connected: boolean;
  onReconnect: () => void;
  copyMode: boolean;
  onCopyModeToggle: () => void;
}

export function Header({
  connected,
  onReconnect,
  copyMode,
  onCopyModeToggle,
}: HeaderProps) {
  return (
    <header
      style={{
        display: "flex",
        alignItems: "center",
        justifyContent: "space-between",
        padding: "12px 20px",
        background: "var(--bg-secondary)",
        borderBottom: `1px solid ${copyMode ? "var(--accent-blue)" : "var(--border-color)"}`,
        transition: "border-color 0.2s ease",
      }}
    >
      <div style={{ display: "flex", alignItems: "center", gap: "12px" }}>
        <div
          style={{
            fontSize: "20px",
            fontWeight: 700,
            letterSpacing: "-0.5px",
            background:
              "linear-gradient(135deg, var(--accent-green), var(--accent-blue))",
            WebkitBackgroundClip: "text",
            WebkitTextFillColor: "transparent",
          }}
        >
          EDDA
        </div>
        <div
          style={{
            fontSize: "14px",
            color: "var(--text-muted)",
            fontWeight: 500,
          }}
        >
          Log Dashboard
        </div>
      </div>

      <div style={{ display: "flex", alignItems: "center", gap: "16px" }}>
        {/* Copy Mode Toggle */}
        <button
          onClick={onCopyModeToggle}
          style={{
            padding: "6px 14px",
            fontSize: "12px",
            fontWeight: 600,
            background: copyMode ? "var(--accent-blue)" : "var(--bg-tertiary)",
            border: `1px solid ${copyMode ? "var(--accent-blue)" : "var(--border-color)"}`,
            borderRadius: "6px",
            color: copyMode ? "#fff" : "var(--text-secondary)",
            cursor: "pointer",
            transition: "all 0.15s ease",
            display: "flex",
            alignItems: "center",
            gap: "6px",
          }}
          onMouseOver={(e) => {
            if (!copyMode) {
              e.currentTarget.style.borderColor = "var(--accent-blue)";
              e.currentTarget.style.color = "var(--accent-blue)";
            }
          }}
          onMouseOut={(e) => {
            if (!copyMode) {
              e.currentTarget.style.borderColor = "var(--border-color)";
              e.currentTarget.style.color = "var(--text-secondary)";
            }
          }}
        >
          <span style={{ fontSize: "14px" }}>📋</span>
          {copyMode ? "Exit Copy Mode" : "Copy Logs"}
        </button>

        {/* Connection Status */}
        <div style={{ display: "flex", alignItems: "center", gap: "8px" }}>
          <div
            style={{
              width: "8px",
              height: "8px",
              borderRadius: "50%",
              background: connected
                ? "var(--accent-green)"
                : "var(--accent-red)",
              boxShadow: connected
                ? "0 0 8px var(--accent-green)"
                : "0 0 8px var(--accent-red)",
            }}
            className={connected ? "" : "animate-pulse"}
          />
          <span
            style={{
              fontSize: "13px",
              color: connected ? "var(--accent-green)" : "var(--accent-red)",
              fontWeight: 500,
            }}
          >
            {connected ? "Connected" : "Disconnected"}
          </span>
        </div>

        {!connected && (
          <button
            onClick={onReconnect}
            style={{
              padding: "6px 12px",
              fontSize: "12px",
              fontWeight: 500,
              background: "var(--bg-tertiary)",
              border: "1px solid var(--border-color)",
              borderRadius: "6px",
              color: "var(--text-primary)",
              cursor: "pointer",
              transition: "all 0.15s ease",
            }}
            onMouseOver={(e) => {
              e.currentTarget.style.borderColor = "var(--accent-blue)";
            }}
            onMouseOut={(e) => {
              e.currentTarget.style.borderColor = "var(--border-color)";
            }}
          >
            Reconnect
          </button>
        )}
      </div>
    </header>
  );
}
