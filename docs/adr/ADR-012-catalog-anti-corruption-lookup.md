# ADR-012: Catalog anti-corruption lookup

Status: Accepted and implemented in Phase 04.

## Context
Order creation needs product price and inventory needs product existence. Sharing EF entities or reading PostgreSQL from the monolith would couple ownership.

## Decision
Use IProductCatalog and a bounded HTTP adapter translating Catalog JSON into ProductSnapshot. No SQL fallback exists in extracted mode.

## Alternatives considered
Direct cross-database query; shared domain/EF model; stale local product cache.

## Consequences
Catalog outages return 503 for new orders and stock additions. Existing orders and payment remain available.

## Trade-offs
A synchronous dependency adds latency, but avoids introducing events before Phase 05 and keeps data authority explicit.
