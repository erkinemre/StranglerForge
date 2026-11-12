# References

## StranglerForge primary evidence

- [Repository and README](https://github.com/erkinemre/StranglerForge)
- [Final engineering review](https://github.com/erkinemre/StranglerForge/blob/main/docs/architecture/final-engineering-review.md)
- [Acceptance matrix](https://github.com/erkinemre/StranglerForge/blob/main/docs/architecture/acceptance-matrix.md)
- [Current architecture](https://github.com/erkinemre/StranglerForge/blob/main/docs/architecture/current-architecture.md)
- [Technical debt](https://github.com/erkinemre/StranglerForge/blob/main/docs/architecture/technical-debt.md)
- [Migration roadmap](https://github.com/erkinemre/StranglerForge/blob/main/docs/migration/migration-roadmap.md)
- [Inventory store](https://github.com/erkinemre/StranglerForge/blob/main/src/Services/Inventory/Inventory.Infrastructure/InventoryStore.cs)
- [Orders Saga runner](https://github.com/erkinemre/StranglerForge/blob/main/src/Services/Orders/Orders.Application/OrderSagaRunner.cs)
- [Gateway migration flags](https://github.com/erkinemre/StranglerForge/blob/main/src/Gateway/StranglerForge.Gateway/MigrationControl/MigrationFlags.cs)
- [Gateway PostgreSQL authority](https://github.com/erkinemre/StranglerForge/blob/main/src/Gateway/StranglerForge.Gateway/MigrationControl/PostgresMigrationAuthority.cs)

## External technical references

- Martin Fowler, [Strangler Fig Application](https://martinfowler.com/bliki/StranglerFigApplication.html).
- Microsoft Learn, [YARP configuration files](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/config-files).
- Microsoft Learn, [YARP authentication and authorization](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/authn-authz?view=aspnetcore-10.0).
- RabbitMQ, [Reliability guide](https://www.rabbitmq.com/docs/reliability) and [consumer acknowledgements and publisher confirms](https://www.rabbitmq.com/docs/confirms).
- PostgreSQL, [Explicit locking](https://www.postgresql.org/docs/current/explicit-locking.html).
- OpenTelemetry, [Signals](https://opentelemetry.io/docs/concepts/signals/) and [context propagation](https://opentelemetry.io/docs/specs/otel/context/).
- Kubernetes, [Performing a rolling update](https://kubernetes.io/docs/tasks/run-application/update-deployment-rolling/).

External references explain general mechanisms; repository files establish what this project actually implements.

