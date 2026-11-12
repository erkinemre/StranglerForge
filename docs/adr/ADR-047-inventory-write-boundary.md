# ADR-047: Protect Inventory write commands during hybrid operation

Status: Accepted for Phase 10.

The gateway exposes stock adjustment and reservation routes. In production-mode Inventory hosts, stock adjustments require a stock-admin token and reservation creation, release, and confirmation require a separate reservation token. Orders receives only the reservation token. A missing configured token returns service unavailable; a missing or incorrect request token returns unauthorized. Development hosts without configured tokens retain the existing local educational workflow.

This limits anonymous stock inflation and reservation manipulation while preserving routes and payloads. It does not replace end-user identity, workload identity, mTLS, network isolation, or secret rotation. A later platform authorization design should replace the shared tokens without changing the domain use cases.