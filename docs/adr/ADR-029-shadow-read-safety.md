# ADR-029: Bounded Catalog shadow reads

Status: Accepted for pre-cutover Catalog validation.

Shadowing sends only a safe Catalog GET path, query, correlation ID, and JSON Accept header to the secondary destination. It does not copy credentials or bodies. A 256 KiB response limit, 300 ms shadow timeout, and eight-request concurrency limit bound cost. Primary status and body remain authoritative; shadow failures are recorded without fallback. The bounded wait can add latency, which is acceptable for this educational control but should be measured before broad use. Current post-cutover development flags disable shadowing because the monolith list returns 410.
