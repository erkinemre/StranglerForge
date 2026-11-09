# ADR-030: Structured contract comparison

Status: Accepted.

Shadow results compare status, JSON structure, required Catalog fields, identifiers, collection length, and business values. Only documented nondeterministic metadata fields may be normalized. Records include route, destination names, correlation, timestamp, outcome, and category without raw response bodies. The bounded in-memory record set supports diagnosis without retaining sensitive payloads. A sampled match does not prove complete parity; any mismatch blocks canary advancement until reviewed.
