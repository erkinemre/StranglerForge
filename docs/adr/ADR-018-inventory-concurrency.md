# ADR-018: Database-enforced reservation concurrency

Status: Accepted.

A PostgreSQL conditional update increases `Reserved` only when `Quantity - Reserved` covers the requested amount. Unique reservation and order indexes prevent repeated holds. Release and confirmation conditionally change a `Reserved` row to one terminal state in the same transaction as the stock update. Duplicate races roll back the losing transaction and reread the winner. In-process locks were rejected because they fail across replicas. Reading stock then writing without a condition was rejected because concurrent orders could oversell. The tradeoff is handling database conflicts and bounded retries explicitly.
