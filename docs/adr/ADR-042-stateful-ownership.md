# ADR-042: Managed stateful dependencies

Status: Accepted for Phase 09.

Kubernetes manifests deploy no database, broker, or telemetry storage StatefulSets. Production-oriented examples use externally managed SQL Server, PostgreSQL, RabbitMQ, and observability storage with independent backup/restore policies. The Compose single-host volumes are for development and are not highly available. Service database ownership and the dedicated migration authority remain unchanged.