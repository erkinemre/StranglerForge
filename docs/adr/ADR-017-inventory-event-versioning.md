# ADR-017: Versioned Inventory fact contracts

Status: Accepted.

Integration facts are immutable `V1` records with stable EventId, CorrelationId, UTC occurrence time, explicit EventVersion, and relevant business IDs. Domain entities remain private to Inventory. Changes in event meaning require a new version and a compatibility period. Reusing domain entities was rejected because it leaks persistence details and makes consumers dependent on internal changes. Generic unversioned payloads were rejected because incompatible changes become difficult to detect.
