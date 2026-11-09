# API routing in Phase 06

YARP is the external entry point. Catalog routes use the catalog cluster; Inventory routes use the inventory cluster; Order create/read routes use the orders cluster; customer, payment, and notification routes use the legacy cluster and preserve method, path, query, and body. Phase 06 order and payment response statuses changed for asynchronous processing, as documented in the Orders architecture. The destination address is read from ReverseProxy:Clusters:legacy:Destinations:monolith:Address. The environment variable form uses double underscores.

| Capability | Routes | Destination |
| --- | --- | --- |
| Customers | /api/customers, /api/customers/{id} | Legacy monolith |
| Catalog | /api/products, /api/products/{id} | Catalog Service |
| Inventory | /api/inventory/{productId}, /api/inventory/{productId}/stock, /api/inventory/reservations and its ID, release, confirm routes | Inventory Service |
| Orders | /api/orders, /api/orders/{id} | Order Service |
| Payments | /api/orders/{id}/payment | Legacy monolith |
| Notifications | /api/notifications/{customerId}, /api/notifications/{id}/dispatch | Legacy monolith |

GET /health is gateway-owned. There is no catch-all route: an unknown path returns 404 rather than silently reaching the monolith. When a new legacy route is introduced, add an explicit gateway route and a forwarding test before exposing it. Later extraction changes a route's cluster after contract and data checks; it does not change the public path.

YARP overwrites X-Forwarded values with the immediate client connection information. The gateway removes inbound Forwarded and does not enable ASP.NET Core forwarded-header consumption. A valid inbound GUID X-Correlation-ID is propagated; otherwise the gateway generates one. A trusted upstream proxy requires an explicit deployment configuration before its forwarded identity may be used. Gateway-owned status responses never depend on the monolith.



## Phase 05 Inventory routing
After the verified development cutover, `inventory`, `inventoryStock`, and four reservation routes target the `inventory` YARP cluster. The monolith uses `InventoryOwnership:Mode=Extracted`. Customer, payment, and notification routes stay on `legacy`; Order create/read routes target `orders`; Catalog routes stay on `catalog`. The isolated end-to-end validation script exercises the extracted setting.

## Phase 07 routing guard

The gateway validates that each state-changing Catalog, Inventory, or Orders route targets its configured authoritative cluster and lifecycle stage. Only `GET /api/products` can use the controlled read canary or shadow path before Catalog cutover. Current post-cutover routes remain modern. See [canary routing](../migration/canary-routing.md) and [shadow execution](../migration/shadow-execution.md).
