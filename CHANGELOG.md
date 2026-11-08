# Changelog

This project has no tagged release yet. The entries below describe implemented development phases, not production releases.

## Unreleased

- Established the LegacyCommerce modular monolith, YARP gateway, and extracted Catalog, Inventory, and Orders services.
- Added PostgreSQL ownership boundaries, SQL Server migration tools, persistent Orders workflow, MassTransit outbox/inbox configuration, and isolated automated tests.
- Documented that live RabbitMQ delivery, Docker Compose startup, and multi-replica saga operation remain unverified in the current environment.
