# ADR-001: Strangler Fig Pattern

Status: Accepted for target architecture; implementation deferred.

## Context
Live commerce cannot tolerate a single risky replacement.

## Decision
Route selected capabilities to replacements while retaining legacy behavior.

## Alternatives considered
Big bang rewrite; indefinite monolith.

## Consequences
Parallel operation needs contract checks.

## Trade-offs
The decision favors controlled migration and explicit ownership at the cost described above.
