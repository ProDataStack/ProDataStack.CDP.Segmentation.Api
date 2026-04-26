# CDP Segmentation API

Segmentation service for the Customer Data Platform. Provides segment CRUD, rule evaluation engine, admin-configurable field definitions, member queries, and CSV export.

**Status:** v1 shipped (Iteration 2). Backend + UI live in testing. See `CDP/iterations/2/SEGMENTATION-V1-SUMMARY.md` for what shipped.

## Project Structure

```
ProDataStack.CDP.Segmentation.Api/
  ProDataStack.CDP.Segmentation.Api/
    Controllers/
      SegmentationController.cs   # All segment, field, member, export endpoints
    Services/
      SegmentationService.cs      # Segment CRUD, archive/restore/clone, stats
      SegmentFieldService.cs      # Field definitions list/update/categories (seed lives in DataModel migration)
      RuleEvaluationService.cs    # Rule JSON → LINQ; estimate, evaluate, members, export
    Models/
      SegmentModels.cs
      SegmentFieldModels.cs
      ExportModels.cs
    Dockerfile
    appsettings.json
    appsettings.Development.json
    Program.cs                    # ProDataStack.Chassis.Program.Run<Startup>(args)
    Startup.cs                    # Extends StartupBase; registers TenantCatalog factory + services
    ProDataStack.CDP.Segmentation.Api.csproj
  ProDataStack.CDP.Segmentation.Api.sln
  nuget.config                    # Points at ProDataStack GitHub Packages
  Deployment/
    helm/
      Chart.yaml
      values.yaml                 # Token substitution (__AppName__, __KeyVaultName__, etc.)
      templates/
        deployment.yaml
        service.yaml
        httproute.yaml            # Gateway API HTTPRoute (NOT Ingress)
        backendtrafficpolicy.yaml # 300s requestTimeout for long CSV exports
        secrets.yaml              # SecretProviderClass
        serviceaccount.yaml
        configmap.yaml
```

**No `Context/` folder** — this service does not own a DbContext. It uses `CdpDbContext` from `ProDataStack.CDP.DataModel` per-request via tenant resolution.

## Key Configuration

```json
{
  "ServiceName": "CDP Segmentation API",
  "Authentication": { "Mode": "Clerk" },
  "Clerk": { "AuthorizedParty": "cdp-api" }
}
```

## Tenant Resolution

Uses direct tenant resolution (NOT `Chassis.MultiTenant` middleware). Extract `org_id` from JWT → query tenant catalog → cache connection string → create `CdpDbContext` per-request. See Import API's `ImportService.GetTenantDbAsync` for the established pattern.

## Infrastructure

- **Resource group**: `cdp-segmentation-api`
- **Managed identity**: `cdp-segmentation-api-identity`
- **Key Vaults**: `cdp-seg-api-test` / `cdp-seg-api-prod` (24 char limit)
- **Key Vault secrets**: `TenantCatalogDatabaseConnection`, `ClerkSecretKey`, `ClerkWebhookSecret`
- **Docker image**: `cdp.segmentation.api`
- **Namespaces**: `cdp-testing` / `cdp-production`
- **Hostname**: `cdp-segmentation-api.testing.pdsnextgen.com` / `cdp-segmentation-api.prodatastack.com`

## Dependencies

- `ProDataStack.Chassis` — shared service chassis (NuGet, GitHub Packages)
- `ProDataStack.CDP.DataModel` — shared data model entities and migrations (NuGet)
- `ProDataStack.CDP.TenantCatalog` — tenant catalog DbContext and entities (NuGet)

When `DataModel` changes, this service must be redeployed to pick up the new NuGet package.

## Iteration 2 scope

See `CDP/iterations/2/ITERATION-2-TICKETS.md` § Epic 2/3 for the segmentation work (CDP-201 through CDP-205, CDP-303 through CDP-305). The I2 DataModel bump also adds connector runtime tables (`ConnectorConfig`, `ConnectorSyncJob`, `ConnectorSyncStaging`, `ConnectorSyncError`) used by the new `ProDataStack.CDP.Connectors.Api` — this service doesn't consume them, but they ship in the same NuGet version, so redeploy after the DataModel bump.
