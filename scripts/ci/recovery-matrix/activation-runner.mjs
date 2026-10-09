export function waitForDuringActivationPoint({
  markerAdvanced,
  isComplete,
  runHook,
  sleep = (milliseconds) => Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, milliseconds),
  timeoutMs = 60_000,
}) {
  const deadline = Date.now() + timeoutMs;
  while (!markerAdvanced()) {
    if (isComplete()) {
      throw new Error('during_activate_marker_not_observed');
    }
    if (Date.now() > deadline) {
      throw new Error('during_activate_marker_timeout');
    }
    sleep(100);
  }
  if (isComplete()) {
    throw new Error('during_activate_completed_before_hook');
  }
  runHook();
}
