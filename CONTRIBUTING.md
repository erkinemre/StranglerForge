# Contributing

StranglerForge is an educational architecture reference. Contributions should preserve the staged migration history and the current public HTTP contracts.

1. Read [AGENTS.md](AGENTS.md) and the [architecture overview](docs/architecture/current-architecture.md).
2. Keep domain logic independent of API and persistence code. Add an ADR for a material architectural choice.
3. Run `dotnet restore StranglerForge.slnx`, `dotnet build StranglerForge.slnx --no-restore`, and relevant tests. The full test suite requires isolated SQL Server and PostgreSQL databases; see the [README](README.md#restore-build-and-test).
4. Never commit local credentials, `.env`, development settings, database copies, or logs. Report infrastructure dependent tests separately.
5. Use a Conventional Commit message and describe the behavior, validation, and limitations in the pull request.
