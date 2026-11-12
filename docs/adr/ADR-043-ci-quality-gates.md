# ADR-043: Isolated CI quality gates

Status: Accepted for Phase 09.

CI uses read-only repository permission, .NET build/analyzer checks, isolated SQL Server/PostgreSQL tests, static deployment validation, image builds, and an explicit Docker-dependent Testcontainers check. CI does not publish or deploy. Environment qualification remains a separate release gate because YAML parsing and image builds cannot establish service availability or business workflow correctness in Kubernetes.