# ADR-031: Single authoritative writer during migration

Status: Accepted.

Catalog, Inventory, and Orders PostgreSQL are the development data owners. Gateway checks reject a write when owner, lifecycle stage, and YARP cluster conflict. The monolith's extracted-mode write guards remain defense in depth. A failed modern write is not retried against legacy after an ambiguous outcome. This gives up automatic availability fallback to avoid duplicate orders, reservations, payments, or lost post-cutover data. Customer, payment, and notification ownership remains in SQL Server.
