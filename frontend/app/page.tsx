"use client";

import { useState } from "react";

export default function HomePage() {
  const [output, setOutput] = useState<string>("No request sent yet.");

  async function callApi(path: string) {
    try {
      const response = await fetch(path);
      const text = await response.text();

      setOutput(`HTTP ${response.status}\n\n${text}`);
    } catch (error) {
      setOutput(String(error));
    }
  }

  return (
    <main style={{ fontFamily: "Arial", margin: "40px" }}>
      <h1>Local Observability Demo</h1>

      <p>
        This frontend calls a .NET backend. The backend talks to PostgreSQL and
        exposes application and database metrics to Prometheus.
      </p>

      <div style={{ display: "flex", gap: "12px", flexWrap: "wrap" }}>
        <button onClick={() => callApi("/api/backend")}>
          Call Backend
        </button>

        <button onClick={() => callApi("/api/backend/orders")}>
          Load Orders
        </button>

        <button onClick={() => callApi("/api/backend/slow-query")}>
          Run Slow Query
        </button>

        <button onClick={() => callApi("/api/backend/db-health")}>
          Check DB Health
        </button>
      </div>

      <pre
        style={{
          marginTop: "24px",
          padding: "16px",
          background: "#f4f4f4",
          borderRadius: "8px",
          minHeight: "180px",
          whiteSpace: "pre-wrap"
        }}
      >
        {output}
      </pre>
    </main>
  );
}