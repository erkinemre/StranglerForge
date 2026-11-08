# ADR-014: Extract Inventory behind a single-writer cutover

Status: Accepted for Phase 05 implementation; deployment cutover pending.

Inventory receives its own .NET 10 service and PostgreSQL database. SQL Server stock remains authoritative until a frozen import and reconciliation pass. YARP and monolith Inventory mode switch together. We keep old SQL tables for audit. This makes the ownership boundary explicit and avoids two independent writers. Routing alone was rejected because orders would still mutate SQL stock. A live dual-write bridge was rejected because it cannot guarantee ordering or consistency without a reconciliation protocol. The tradeoff is a controlled write pause and an operational cutover gate.
