# ADR-041: Non-root minimal .NET containers

Status: Accepted for Phase 09.

Use a multi-stage .NET 10 build and ASP.NET runtime image. Copy only published output, run as the .NET app user, and keep secrets in runtime configuration. Kubernetes additionally drops Linux capabilities and makes the root filesystem read-only. This reduces image contents and privilege. Platform-specific certificate stores, diagnostics, and writable scratch requirements must be checked during runtime qualification.