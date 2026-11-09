# Contract comparison

The shadow comparator checks status, JSON shape, object fields, array length, identifiers, and business values. It reports `StatusCode`, `InvalidJson`, `Structure`, `MissingField`, `CollectionLength`, `Identifier`, `RequiredField`, or `BusinessValue`. A successful Catalog list response must contain product IDs, names, and numeric prices. Query strings are sent unchanged to both destinations, so differences in filtering or pagination appear in the compared collection or fields.

Only known nondeterministic metadata fields (`generatedAt`, `generatedAtUtc`, `lastModifiedUtc`, `traceId`, `requestId`) may be configured for normalization. Identifier, price, and status fields cannot be ignored. A comparison record contains the correlation ID, route, primary and shadow destination names, timestamp, outcome, and category. It does not retain response bodies. Recent records are bounded to 200 in memory; counts are exposed through the local administrative API.

A matching response is evidence about the sampled request only. It is not proof of complete data parity or permission to transfer write ownership.
