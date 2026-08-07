import { describe, it, expect, vi, beforeAll, beforeEach } from 'vitest';

// Mock OpenTelemetry modules
vi.mock('@opentelemetry/sdk-trace-web', () => ({
  WebTracerProvider: class MockWebTracerProvider {
    register = vi.fn();
  },
  BatchSpanProcessor: class MockBatchSpanProcessor {},
  ConsoleSpanExporter: class MockConsoleSpanExporter {},
}));

vi.mock('@opentelemetry/auto-instrumentations-web', () => ({
  getWebAutoInstrumentations: vi.fn(() => []),
}));

vi.mock('@opentelemetry/resources', () => ({
  resourceFromAttributes: vi.fn((attrs) => attrs),
}));

vi.mock('@opentelemetry/exporter-trace-otlp-http', () => ({
  OTLPTraceExporter: class MockOTLPTraceExporter {},
}));

vi.mock('@opentelemetry/instrumentation', () => ({
  registerInstrumentations: vi.fn(),
}));

vi.mock('../services/unifiedLogging', () => ({}));

let initializeTelemetry: typeof import('../config')['initializeTelemetry'];
let resourceFromAttributes: typeof import('@opentelemetry/resources')['resourceFromAttributes'];

beforeAll(async () => {
  ({ initializeTelemetry } = await import('../config'));
  ({ resourceFromAttributes } = await import('@opentelemetry/resources'));
}, 60_000);

describe('telemetry config', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('should initialize telemetry module', () => {
    expect(typeof initializeTelemetry).toBe('function');
  });

  it('should create WebTracerProvider with correct resource attributes', () => {
    vi.clearAllMocks();
    initializeTelemetry();

    expect(resourceFromAttributes).toHaveBeenCalledWith(
      expect.objectContaining({
        'service.name': 'PrintFarmer.Frontend',
        'service.version': '1.0.0',
        'service.namespace': 'printfarmer',
      })
    );
  });
});
