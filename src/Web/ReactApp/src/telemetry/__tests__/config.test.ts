import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { context, propagation, ROOT_CONTEXT, trace } from '@opentelemetry/api';
import { ExportResultCode } from '@opentelemetry/core';
import { OTLPTraceExporter } from '@opentelemetry/exporter-trace-otlp-http';
import { TracerProvider } from '@opentelemetry/sdk-trace';
import { getWebAutoInstrumentations } from '@opentelemetry/auto-instrumentations-web';
import { registerInstrumentations } from '@opentelemetry/instrumentation';

vi.mock('@opentelemetry/auto-instrumentations-web', () => ({
  getWebAutoInstrumentations: vi.fn(() => []),
}));
vi.mock('@opentelemetry/instrumentation', () => ({
  registerInstrumentations: vi.fn(),
}));
vi.mock('@/services/unifiedLogging', () => ({}));

let telemetry: typeof import('../config');

beforeAll(async () => {
  vi.stubEnv('DEV', false);
  vi.stubEnv('VITE_OTEL_EXPORTER_OTLP_ENDPOINT', 'http://localhost:4318/v1/traces');
  telemetry = await import('../config');
}, 60_000);

afterEach(async () => {
  await telemetry.shutdownTelemetry();
  trace.disable();
  context.disable();
  propagation.disable();
  vi.restoreAllMocks();
  vi.clearAllMocks();
  vi.unstubAllEnvs();
});

describe('telemetry config', () => {
  it('should initialize telemetry module', () => {
    expect(typeof telemetry.initializeTelemetry).toBe('function');
  });

  it('exports spans with resource attributes, stack context and W3C propagation using SDK 3', async () => {
    const register = vi.spyOn(trace, 'setGlobalTracerProvider');
    const exportSpans = vi.spyOn(OTLPTraceExporter.prototype, 'export')
      .mockImplementation((_spans, callback) => callback({ code: ExportResultCode.SUCCESS }));

    telemetry.initializeTelemetry();
    expect(telemetry.isTelemetryInitialized()).toBe(true);
    const provider = register.mock.calls[0][0];
    expect(provider).toBeInstanceOf(TracerProvider);

    const tracer = trace.getTracer('telemetry-config-test');
    const parent = tracer.startSpan('parent');
    context.with(trace.setSpan(ROOT_CONTEXT, parent), () => {
      const child = tracer.startSpan('child');
      expect(child.spanContext().traceId).toBe(parent.spanContext().traceId);
      const carrier: Record<string, string> = {};
      const baggage = propagation.createBaggage({ test: { value: 'preserved' } });
      const active = propagation.setBaggage(trace.setSpan(context.active(), child), baggage);
      propagation.inject(active, carrier);
      expect(carrier.traceparent).toContain(child.spanContext().spanId);
      expect(carrier.baggage).toBe('test=preserved');
      child.end();
    });
    expect(trace.getSpan(context.active())).toBeUndefined();
    parent.end();

    if (!(provider instanceof TracerProvider)) throw new Error('SDK 3 provider was not registered');
    await provider.forceFlush();
    expect(exportSpans).toHaveBeenCalledOnce();
    const spans = exportSpans.mock.calls[0][0];
    expect(spans.map((span) => span.name)).toEqual(['child', 'parent']);
    expect(spans[0].resource.attributes).toMatchObject({
      'service.name': 'PrintFarmer.Frontend',
      'service.version': '1.0.0',
      'service.namespace': 'printfarmer',
    });
    expect(getWebAutoInstrumentations).toHaveBeenCalledWith(expect.objectContaining({
      '@opentelemetry/instrumentation-document-load': { enabled: true },
      '@opentelemetry/instrumentation-user-interaction': { enabled: true },
      '@opentelemetry/instrumentation-fetch': expect.objectContaining({ enabled: true }),
      '@opentelemetry/instrumentation-xml-http-request': expect.objectContaining({ enabled: true }),
    }));
    expect(registerInstrumentations).toHaveBeenCalledOnce();

    await telemetry.shutdownTelemetry();
    expect(telemetry.isTelemetryInitialized()).toBe(false);
  });
});
