# Migration principles
Keep the monolith working. Establish contract checks, backfill and reconcile, then transfer one authoritative writer. An anti-corruption layer translates legacy schema and semantics. Extract one bounded context at a time.
