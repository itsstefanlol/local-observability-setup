import { NextRequest } from "next/server";
import {
  context,
  propagation,
  SpanKind,
  SpanStatusCode,
  trace
} from "@opentelemetry/api";

const backendUrl = process.env.BACKEND_INTERNAL_URL || "http://backend:8080";
const tracer = trace.getTracer("nextjs-frontend");

async function proxyRequest(
  request: NextRequest,
  routeContext: { params: { path?: string[] } }
) {
  const path = routeContext.params.path?.join("/") || "";
  const targetUrl = `${backendUrl}/${path}`;

  return tracer.startActiveSpan(
    `proxy ${request.method} /api/backend/${path}`,
    {
      kind: SpanKind.SERVER,
      attributes: {
        "http.method": request.method,
        "http.route": "/api/backend/[...path]",
        "http.target": targetUrl,
        "backend.route": `/${path}`,
        "app.flow": "frontend-to-backend-to-database",
        "journey.name":
          path === "slow-query"
            ? "Run Slow Query"
            : path === "orders"
              ? "Load Orders"
              : path
      }
    },
    async (span) => {
      try {
        const headers: Record<string, string> = {
          "Content-Type": "application/json"
        };

        propagation.inject(context.active(), headers);

        const response = await fetch(targetUrl, {
          method: request.method,
          headers,
          cache: "no-store"
        });

        const body = await response.text();

        span.setAttributes({
          "http.status_code": response.status
        });

        if (response.status >= 500) {
          span.setStatus({
            code: SpanStatusCode.ERROR,
            message: `Backend returned HTTP ${response.status}`
          });
        }

        return new Response(body, {
          status: response.status,
          headers: {
            "Content-Type": response.headers.get("content-type") || "text/plain"
          }
        });
      } catch (error) {
        span.recordException(error as Error);
        span.setStatus({
          code: SpanStatusCode.ERROR,
          message: String(error)
        });

        return new Response(String(error), {
          status: 500
        });
      } finally {
        span.end();
      }
    }
  );
}

export async function GET(
  request: NextRequest,
  routeContext: { params: { path?: string[] } }
) {
  return proxyRequest(request, routeContext);
}