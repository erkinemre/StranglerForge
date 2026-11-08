# ADR-024: Payment Idempotency by OrderId

Status: Accepted.

## Context
An HTTP timeout can occur after payment authorization has committed but before Orders receives the response.

## Decision
The monolith's internal authorization records at most one PaymentAttempt per OrderId. Retried calls with the same amount and decision return the prior result; conflicting decisions are rejected. Orders does not release stock on uncertain authorization.

## Consequences
The public payment route returns 202 for extracted orders. A prolonged uncertain outcome needs operator review; no automatic release or duplicate charge is allowed.
