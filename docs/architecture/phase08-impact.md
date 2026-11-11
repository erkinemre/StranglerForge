# Phase 08 architecture impact assessment

The extracted Catalog, Inventory, and Orders services retain their PostgreSQL ownership; LegacyCommerce retains SQL Server customers, simulated payments, and notifications. Phase 08 does not move data or add a new business service.

| Previous gap | Phase 08 change | Operational boundary |
| --- | --- | --- |
| HTTP and MassTransit operations lacked a shared trace export path | Five HTTP hosts now emit W3C-based OpenTelemetry traces; application spans cover order submission, saga steps, and inventory reservation | Jaeger/Collector runtime requires Docker or equivalent infrastructure |
| Migration counters had no exporter | Collector exports metrics to Prometheus; seven Grafana dashboards are provisioned | Dashboard JSON and metric references are statically validated; live panels are unverified here |
| Downstream HTTP failures could exhaust callers | Separate Orders dependency clients and bounded read-only retries, timeouts, concurrency limits, and circuit breakers | POST calls are never automatically retried; uncertain payment stays pending for review |
| Gateway migration state was local to one process | Optional dedicated PostgreSQL authority holds a row lock through routed requests, validates configuration fingerprints, and persists transitions/audit | Local file mode remains for single-instance development; shared mode is required for replicas |
| Health checks conflated process and dependencies | `/health/live` and `/health/ready` are available on all HTTP hosts | Readiness checks the configured critical database; it does not prove every downstream operation |

No change in this phase makes SQL Server authoritative for extracted Catalog, Inventory, or Orders. Broker outage and Compose startup remain separate runtime validation gates.
