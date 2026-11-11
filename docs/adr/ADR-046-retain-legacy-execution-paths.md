# ADR-046: Retain guarded legacy execution paths

Status: Accepted for Phase 10.

Catalog, Inventory, and Orders PostgreSQL are authoritative after their verified development cutovers. Legacy SQL rows and code remain for historical access and controlled recovery analysis. The monolith's extracted-mode routes do not act as competing writers. Phase 10 found no complete inventory of external callers, background writers, reporting uses, or expired rollback window. Removing the legacy paths or deleting SQL records would therefore discard a recovery option without proving safety.

Retirement requires a dependency inventory, historical read plan, reconciliation evidence, API compatibility review, and an approved rollback-window closure. Until then, keep the guarded paths and record the cost in the technical debt register. Customer, Payment, and Notification modules remain active in the monolith.