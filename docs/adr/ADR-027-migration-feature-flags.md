# ADR-027: Validated routing flags separate from ownership

Status: Accepted.

The gateway reads typed migration flags from reloadable ASP.NET Core configuration on each request and validates allowed traffic percentages, shadow sampling, and ownership constraints. Invalid updates fail closed. YARP write clusters must agree with the configured owner and recorded lifecycle stage. Emergency pause blocks new public Catalog, Inventory, and Order writes while leaving existing recovery boundaries available. Deployment configuration changes do not constitute data migration or a database owner transfer. This avoids dual writers and preserves the current PostgreSQL authority.
