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

For a one-page summary of what shipped, see `CDP/iterations/2/SEGMENTATION-V1-SUMMARY.md`.

## Extending segmentation

Read `CDP/iterations/2/SEGMENTATION-RULES-SCHEMA.md` first — it covers the rules JSON wire format, operator types, and a how-to for adding new fields (direct column / computed / secondary / new entity). The rest of this section covers code-level gotchas inside this repo.

### Field key resolution (where the magic happens)

Rule field keys like `profile.firstName` resolve at runtime in `RuleEvaluationService.BuildRulePredicate`:

1. Split on `.` → `entityKey` + `propertyName`
2. Match `entityKey` against a switch (`profile`, `espIntegration`, `onlineStoreIntegration`, `ticketingIntegration`, `cometIntegration`)
3. For `profile`: special-case computed fields (`ageGroup`, `totalPurchaseValue`, `engagementRate`) **before** falling back to reflection (`char.ToUpper(propertyName[0]) + propertyName[1..]` against `typeof(Profile)`)
4. For integration entities: build `Profile.{Navigation}.Any(i => <condition>)`

**To add a computed field on Profile:** add a `case` to the leading switch in `BuildProfilePredicate`, then write a helper that returns `Expression<Func<Profile, bool>>`. Existing helpers (`BuildAgeGroupPredicate`, `BuildTotalPurchaseValuePredicate`, `BuildEngagementRatePredicate`) are the templates.

**To add a new entity** that joins through something other than the four direct integration navigations: add a new `case` to `BuildRulePredicate`. This is open work — `cometRegistration.*` fields are seeded but disabled because the join logic isn't there yet.

### Tenant resolution is duplicated

`GetTenantDbAsync` is copy-pasted in three services: `SegmentationService`, `SegmentFieldService`, `RuleEvaluationService`. All three resolve the same way (5-min cache via `IMemoryCache` keyed by `tenant-conn:{orgId}`, 120s `CommandTimeout`, `EnableRetryOnFailure`).

If you change one (e.g. cache TTL), update all three. A future refactor should extract this to a shared helper or base class — flagged in `SEGMENTATION-V1-SUMMARY.md` Outstanding.

### Seed data lives in two places

The default segment field definitions exist in:

1. `Services/SegmentFieldService.GetSeedFields` — used by `POST /api/v1/segment-fields/seed`
2. `ProDataStack.CDP.DataModel/Migrations/20260409215614_SeedSegmentFields.cs` — runs on tenant provisioning

**Both must be updated together** when adding a default field. The migration is idempotent (`IF NOT EXISTS (SELECT 1 FROM SegmentFields)`) so it won't double-seed, but it also won't add new fields to a tenant that's already been seeded — those need a manual insert or a wipe-and-reseed.

Long-term we should pick one source of truth.

### Rule grouping is gone but the schema still supports it

The builder (UI) emits exactly one group per stakeholder feedback round 1. The rule engine still parses multi-group JSON (legacy data + forward-compat). If you reintroduce groups in the UI, the engine and JSON shape are already there — you just need to update the builder.
