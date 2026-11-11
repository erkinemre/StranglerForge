# Security threat model

| Boundary | Plausible abuse | Existing control | Remaining action |
| --- | --- | --- | --- |
| Internet to gateway | Unauthorized customer/order/payment requests, route probing, overload | Gateway routing guard and concurrency limit | Add end-user identity, authorization, WAF/ingress TLS, abuse monitoring. |
| Gateway to Inventory | Stock inflation or forged reservation commands | Phase 10 production-mode stock-admin and reservation tokens; separate headers | Replace shared tokens with workload identity, restrict network sources, rotate secrets. |
| Orders to LegacyCommerce | Forged payment or notification command | Shared internal token and idempotent order keys | Scope service identity and audit authorization failures. |
| Gateway replica to migration authority | Stale or conflicting owner switch | PostgreSQL row locks, expected revisions, config fingerprint, fail-closed routing | Grant database least privilege, validate multi-pod outage/recovery. |
| Application to RabbitMQ | Duplicate or poison event | Outbox/inbox and bounded retry | Assert error-queue workflow, restrict vhost permissions, define replay procedure. |
| CI to repository and registries | Secret disclosure or malicious dependency | Read-only GitHub permissions, no publish/deploy, NuGet audit | Pin actions to reviewed SHAs, sign images, review dependency lock strategy. |

No compliance certification is implied. The [technical debt register](technical-debt.md) prioritizes unresolved controls.