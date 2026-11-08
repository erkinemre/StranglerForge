# ADR-023: Orders PostgreSQL Ownership

Status: Accepted; development cutover verified.

## Context
Legacy SQL Server holds historical orders. New orders need an independent database and one authoritative writer.

## Decision
Copy historical identifiers, customer and product references, quantity, price and OrderItem snapshots, and original status text under a write freeze. Route order create/read to Orders and make monolith order endpoints refuse direct access in extracted mode. Payment and Notification remain monolith-owned behind HTTP boundaries.

## Consequences
SQL order rows remain historical. Original order timestamps cannot be preserved because the source schema has none; import time is recorded and labeled. Rollback after new writes requires reconciliation.
