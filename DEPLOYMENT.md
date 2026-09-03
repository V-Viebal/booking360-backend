# Booking360 backend deployment contract

This repository deploys the `.NET 10` backend through a repository-linked
Coolify Docker Compose Application. GitHub Actions is the only build and
publish surface. The target host pulls the immutable image; it does not build
the application source.

## Locked topology

| Item | Contract |
|---|---|
| GitHub repository | `V-Viebal/booking360-backend` |
| Workflow | `.github/workflows/deploy.yaml` |
| Runner | GitHub-hosted `ubuntu-latest` |
| Image | `ghcr.io/v-viebal/booking360-backend@sha256:<digest>` |
| Image tags | full source SHA and target role (`staging` or `production`) |
| Build metadata | BuildKit GHA cache, SBOM, max provenance |
| Coolify project | `booking360` (`b4sha277lbvm1fbwnmeucz8d`) |
| Server | `3rd` (UUID is supplied by the target environment contract) |
| Compose file | `/compose.yaml` |
| Custom start | `bash scripts/coolify_ghcr_start.sh` |
| Image environment key | `BOOKING360_BACKEND_IMAGE` |
| Infisical project | `booking360` (`booking360-d-x-db`) |
| Infisical CI path | `/github-actions` |

## Target roles

Each row is an independent GitHub Environment, Infisical OIDC identity,
Coolify Application, image channel, domain, health URL, and rollback record.
The workflow reads target UUIDs and public origins from the corresponding
GitHub Environment variables; it fails closed when any value is absent.

| Role | Git ref | Infisical env/path | Compose project | Network alias | Required GitHub Environment |
|---|---|---|---|---|---|
| production | `main` | `prod` / `/backend/production` | `booking360-backend-production` | `booking360-backend` | `production` |
| non-production | `staging` | `staging` / `/backend/staging` | `booking360-backend-staging` | `booking360-backend-staging` | `staging` |

Required target-bound variables in both GitHub Environments:

```text
INFISICAL_IDENTITY_ID
COOLIFY_PROJECT_UUID
COOLIFY_ENVIRONMENT_UUID
COOLIFY_DESTINATION_UUID
COOLIFY_SERVER_UUID
COOLIFY_APPLICATION_UUID
COOLIFY_APPLICATION_NAME
COOLIFY_PUBLIC_DOMAIN
BACKEND_HEALTH_URL
```

`ALLOW_INITIAL_RELEASE=true` is a temporary, target-scoped exception for the
first deployment only, when no previous immutable image exists. It must be
removed or set to `false` after the first verified release. Every later
release requires a previous GHCR digest so rollback remains available.

The production public domain is intentionally not embedded in source. The
existing `api-book360.hmz.one` hostname is owned by a legacy `core/book360`
Application and must not be assigned to the canonical `booking360` backend
until that ownership conflict is explicitly resolved. The staging and
production GitHub Environment variables are therefore the source of truth for
domains and health URLs.

## Runtime and rollback rules

1. Validate repository, branch, project, environment, destination, server,
   Compose location, Application name, repository, and domain before mutation.
2. Stage short-lived GitHub GHCR credentials only on the locked Application.
3. Persist the exact image digest, trigger Coolify, and poll the returned
   deployment UUID to a terminal state.
4. `coolify_ghcr_start.sh` pulls the exact digest on the target host, verifies
   `RepoDigests`, removes the temporary registry keys from the application
   artifact, starts Compose, and emits a value-safe runtime identity marker.
5. The deploy script verifies that marker and requires stable public `/health`
   samples.
6. On failure after image mutation, restore the previous immutable digest,
   redeploy, poll, and verify health before clearing the temporary credentials.
7. The in-job cleanup is backed by an independent `always()` cleanup job. The
   cleanup enumerates and removes both preview and non-preview rows, then
   reads back zero matching rows.

No secret values, registry tokens, Infisical tokens, or Coolify API tokens
belong in this repository, workflow output, artifacts, or documentation.
