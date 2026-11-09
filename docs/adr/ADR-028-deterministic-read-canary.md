# ADR-028: Stable read-only canary assignment

Status: Accepted for pre-cutover Catalog list reads.

A SHA-256 bucket of the supplied stable `X-Migration-Key` chooses the modern Catalog read at a configured 0, 10, 25, 50, or 100 percent. Missing keys choose modern instead of varying per request. The key is not an identity credential. The control applies only to `GET /api/products` when the legacy list is explicitly available and the migration stage is `CanaryReady`. Writes never use percentage routing. Inventory and Orders have no safe legacy read canary in the current deployment; enabling one would return stale or incomplete data.
