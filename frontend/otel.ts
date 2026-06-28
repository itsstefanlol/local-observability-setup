import { trace } from "@opentelemetry/api";
import { BatchSpanProcessor } from "@opentelemetry/sdk-trace-node";
import { NodeTracerProvider } from "@opentelemetry/sdk-trace-node";
import { OTLPTraceExporter } from "@opentelemetry/exporter-trace-otlp-http";
import { Resource } from "@opentelemetry/resources";
import { SemanticResourceAttributes } from "@opentelemetry/semantic-conventions";

const serviceName = process.env.OTEL_SERVICE_NAME || "nextjs-frontend";
const endpoint =
  process.env.OTEL_EXPORTER_OTLP_ENDPOINT || "http://tempo:4318";

const provider = new NodeTracerProvider({
  resource: new Resource({
    [SemanticResourceAttributes.SERVICE_NAME]: serviceName
  })
});

const exporter = new OTLPTraceExporter({
  url: `${endpoint}/v1/traces`
});

provider.addSpanProcessor(new BatchSpanProcessor(exporter));
provider.register();

trace.getTracer(serviceName);