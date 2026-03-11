# Ai.AgentFramwork.Massar.Web.IntegrationTests

Live xUnit integration tests for a local `AdventureWorks2025` SQL Server Express database.

## Expected local connection

Default test connection string:

```json
{
  "ConnectionStrings": {
    "AdventureWorks2025": "Server=.\\SQLEXPRESS;Database=AdventureWorks2025;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True;Encrypt=False"
  }
}
```

The test project resolves the connection string in this order:

1. Environment variable `AdventureWorks2025__ConnectionString`
2. `appsettings.Test.json` → `ConnectionStrings:AdventureWorks2025`

## Run all integration tests

```bash
dotnet test --filter Category=Integration
```

## Run one test class

```bash
dotnet test --filter FullyQualifiedName~AdventureWorksConnectivityTests
```

## Notes

- These tests are read-only.
- They are intended for live database verification, not mocking.
- Keep them separate from the pure unit test project.
