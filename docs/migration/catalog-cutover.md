# Catalog cutover and rollback

## Before cutover

SQL Server Products is authoritative. Route /api/products to the monolith and run it with CatalogOwnership__Mode=Legacy. Inspect source count, duplicate names, inventory references, and order references. Prepare PostgreSQL with the Catalog migration. Do not run Catalog with writes enabled yet.

## Freeze and import

Restart the monolith with CatalogOwnership__Mode=ReadOnly, which keeps legacy reads but refuses POST /api/products. Drain in-flight writes and prevent direct SQL product writes through operational access controls. Keep Catalog__WritesEnabled=false. Set CATALOG_WRITE_FREEZE_ACK=true only after this freeze. Run Catalog.Migration --import and --verify. Import preserves GUIDs and reconciles changed source values while SQL remains authoritative. It rejects target-only IDs because those may represent already accepted service writes. It compares a source snapshot before and after transfer and rolls back the PostgreSQL transaction if the source changes. Rerunning before cutover is idempotent.

Record source count, target count, fingerprint, and migration ID. Verify seed and representative products by ID. The script checks every distinct stock and order ProductId against source Products. Historical references to deleted products require explicit review before cutover; the tool blocks until resolved.

## Switch

Start Catalog with Catalog__WritesEnabled=true and a healthy PostgreSQL database. Restart monolith with CatalogOwnership__Mode=Extracted and CatalogService__BaseUrl set. Route both /api/products and /api/products/{id} through YARP to Catalog. Keep every other route on the legacy cluster. Exercise Catalog create/list, inventory, order, payment, and notification through YARP. Do not enable dual writes.

## Rollback

Before the first new Catalog write, route Catalog back to legacy, set monolith to Legacy, and disable Catalog writes. Once Catalog accepts a write, routing rollback alone is unsafe: SQL Products is stale, and orders/inventory may refer to GUIDs absent there. Keep Catalog available or freeze both writers and perform an audited PostgreSQL-to-SQL reconciliation with count/ID/price checks before returning writes to SQL. No automatic reverse migration is supplied. Keep the SQL Product table and migrations until rollback is no longer required.

The pre-cutover importer and verifier must not run after new Catalog writes: target-only IDs are intentionally rejected. Monitor Catalog health, HTTP 503s from ACL lookups, YARP errors, and reconciliation evidence.
