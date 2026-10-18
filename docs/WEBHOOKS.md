# Outbound Webhook Delivery

This page describes how `WebhookService`
(`src/infra/Services/Webhooks/WebhookService.cs`) delivers events to webhook
subscriptions, and the signals operators can use to observe it. Behaviour
described here was introduced by issue #3344.

## Delivery Model

- `Enqueue` adds events to an in-memory queue with a best-effort cap of 10,000
  events (the size check is not atomic, so concurrent producers can briefly
  exceed it). When the cap is reached the event is dropped and counted.
- A single background drain loop delivers each event to every active subscription
  for that event type. The JSON envelope (`id`, `event`, `timestamp`, `data`)
  is built once per subscription and reused across that subscription's attempts.
- Each attempt uses its own DI scope and `DbContext`, so a failure for one
  subscription never poisons the change tracker for the next one.
- One subscription failing (HTTP error, timeout, network error, or an unexpected
  exception) never prevents later subscriptions from receiving the same event.

## Timeouts, Retries, And Auto-Disable

- The `WebhookDelivery` `HttpClient` timeout is 10 seconds. A timeout is a failed
  attempt: it is logged, recorded in the delivery log, retried, and counted
  toward the subscription's consecutive failures.
- Each delivery gets up to 3 attempts. Retries are scheduled 5 seconds and then
  30 seconds after the previous failure, in an in-memory retry buffer drained by
  the same loop. Waiting for a retry does not block new events.
- Before a retry, the subscription is reloaded. If it was deleted, deactivated,
  or no longer subscribes to the event type, the retry is abandoned and **not**
  counted as a failure.
- When all attempts fail, `ConsecutiveFailures` is incremented; when it reaches
  `MaxConsecutiveFailures` (if greater than zero) the subscription is disabled.
  A success resets the counter.
- Destinations resolving to private or reserved addresses are blocked
  immediately as a failure and are not retried.
- The retry buffer holds at most 1,000 pending retries. When it is full, the
  delivery is dropped and counted as a failed delivery.

> **Behaviour change.** Before #3344, a hanging subscriber's timeout was treated
> like shutdown and silently skipped. Timeouts now count as failures, so a
> subscriber that consistently times out will be auto-disabled after
> `MaxConsecutiveFailures` exhausted deliveries. A hanging subscriber can still
> occupy the drain loop for up to 10 seconds per attempt until it is disabled.

## Guarantees And Limits

- **Ordering is not guaranteed.** A retried delivery can arrive after later
  events. Receivers should order by the envelope `timestamp` and de-duplicate by
  envelope `id`, which is stable across retries of the same delivery.
- **Delivery is at-most-once across restarts.** Queued events and pending retries
  live only in memory; on shutdown both are discarded, their counts are logged,
  and the `queue_depth` / `pending_retries` gauges are reduced accordingly.
  Events enqueued after shutdown begins are never delivered.
- Consecutive-failure counting follows completion order, which can differ from
  event order when retries interleave.

## Metrics

Meter name: `Farm.Infrastructure.Services.Webhooks` (exported through the
OpenTelemetry pipeline registered in `TelemetryStartup`).

| Instrument | Type | Tags | Meaning |
|---|---|---|---|
| `webhook.queue_depth` | UpDownCounter | — | Events waiting in the in-memory queue |
| `webhook.pending_retries` | UpDownCounter | — | Deliveries waiting in the retry buffer |
| `webhook.queue_age_ms` | Histogram | — | Time an event waited in the queue before processing |
| `webhook.attempt_duration_ms` | Histogram | `outcome`: `success`, `http_error`, `timeout`, `network_error`, `blocked_destination` | Duration of each delivery attempt |
| `webhook.events_dropped` | Counter | `reason`: `queue_full` | Events rejected by `Enqueue` |
| `webhook.deliveries_dropped` | Counter | `reason`: `retry_queue_full`, `subscription_removed`, `subscription_inactive`, `event_type_unsubscribed` | Deliveries abandoned before exhausting attempts |
| `webhook.deliveries_exhausted` | Counter | — | Deliveries that failed every attempt |
| `webhook.isolated_delivery_failures` | Counter | — | Unexpected exceptions contained to a single delivery |

No production measurements have been collected for these instruments yet; tune
the retry and buffer limits only after observing real traffic.
