# Phase 09 deployment and recovery runbook

## Prepare

Install .NET 10, Docker Engine with Compose, and kubectl for the target cluster. Provide isolated credentials through environment variables or an ignored `.env`. For Compose, inspect `docker compose -f deploy/docker/compose.yml config`, then use `docker compose -f deploy/docker/compose.yml up -d --build`. The Compose file retains existing named volumes; never use `down -v` against development data. Run the documented Catalog, Inventory, and Orders imports only under their write freezes. Do not repeat an import after its target accepts writes.

## Kubernetes

Build each image using `docker build -f deploy/docker/Dockerfile --build-arg PROJECT=<project.csproj> --build-arg ASSEMBLY=<assembly.dll> -t <registry>/<name>:<version> .`. Publish images through a reviewed release pipeline outside this reference CI. Replace all manifest images with immutable digests. Provision a namespace and five Secrets named `gateway-secrets`, `legacy-secrets`, `catalog-secrets`, `inventory-secrets`, `orders-secrets`. Required keys:

| Secret | Keys |
| --- | --- |
| gateway | `ConnectionStrings__MigrationControl`, `MigrationControl__AdminToken` |
| legacy | `ConnectionStrings__LegacyCommerce`, `OrdersInternal__Token` |
| catalog | `ConnectionStrings__Catalog` |
| inventory | `ConnectionStrings__Inventory`, `RabbitMq__Host`, `RabbitMq__Username`, `RabbitMq__Password` |
| orders | `ConnectionStrings__Orders`, `RabbitMq__Host`, `RabbitMq__Username`, `RabbitMq__Password`, `OrdersInternal__Token` |

Create secrets through a managed secret controller or `kubectl -n stranglerforge create secret generic <name> --from-env-file=<ignored-file>`; do not commit that file. Set the real ingress host and TLS in a reviewed overlay. Run `kubectl kustomize deploy/kubernetes/overlays/production` and review the output before `kubectl apply -k deploy/kubernetes/overlays/production`. Install an ingress controller and metrics server first. Deploy managed SQL Server, PostgreSQL databases, RabbitMQ, and observability endpoints separately; the manifests do not create stateful workloads.

## Observe and recover

Check `kubectl -n stranglerforge get deploy,pods,svc,ingress`, `kubectl -n stranglerforge rollout status deployment/gateway`, and `kubectl -n stranglerforge logs deployment/gateway --since=10m`. Liveness reports process health; readiness checks dependencies. On database or broker failure, inspect connection failures, outbox age, and pending saga metrics before restarting workers. Restart a deployment with `kubectl -n stranglerforge rollout restart deployment/orders` only after confirming dependencies and backlog. Use `kubectl -n stranglerforge rollout undo deployment/gateway` for an image/config rollout regression; this does not transfer data ownership. Follow [safe rollback](../migration/safe-rollback.md) for migration-state rollback. Back up each authoritative database, migration authority, RabbitMQ definitions/durable messages, and observability configuration; test restores in isolation.

A gateway replica restart must not create local authority. During migration PostgreSQL outage, route readiness fails and migration-sensitive requests fail closed. Restore the coordination database and verify shared revision on every replica before resuming admin transitions. Never disable the guard to regain availability.