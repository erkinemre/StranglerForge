# ADR-044: Testcontainers for isolated infrastructure

Status: Accepted for Phase 09.

Use Testcontainers 4.16.0 for a short-lived PostgreSQL and RabbitMQ smoke check. Container readiness is bounded and resources are disposed after the test. The test is separate from the normal solution suite so a developer without Docker can still run source tests; CI must execute it. Existing MassTransit integration tests remain and should be expanded for full live broker recovery.