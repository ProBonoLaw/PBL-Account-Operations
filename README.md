# Account Operations

Account Operations contains three .NET 10 services:

- `AccountOperations` consumes account and email events and coordinates account workflows.
- `ResourceManager` provisions account search resources.
- `SharePointSiteStarter` prepares an account's SharePoint site.

The repository contains application source, tests, and generic infrastructure templates. It does not contain deployment credentials, tenant identifiers, or production settings.

## Build

Before opening a fresh clone in Dev Containers, create the local environment file:

```sh
cp -n .devcontainer/.env.example .devcontainer/.env
chmod 600 .devcontainer/.env
```

Fill in the values in `.devcontainer/.env`, then run **Dev Containers: Reopen in
Container** in VS Code. The .NET 10 container includes Azure CLI, GitHub CLI,
and the C# extension, and loads the local `.env`. The filled file is ignored by
Git; the empty `.env.example` is shared in the repository.

```sh
dotnet restore AccountOperations.slnx
dotnet test AccountOperations.slnx --configuration Release --no-restore
```

The three Dockerfiles build without access to a private package feed. Tests use example domains and IDs. Azure integration tests run only when explicitly enabled with local credentials and settings.

## SharePoint configuration

Set tenant specific values as GitHub environment variables in the **private deployment repository**, then pass them into its settings generation or deployment process. The account worker reads the resulting settings from Key Vault or environment variables. The site starter reads its `SharePointSiteStarter` configuration section from Key Vault or environment variables.

| GitHub variable | Runtime setting | Purpose |
| --- | --- | --- |
| `SHAREPOINT_HOST` | `SHAREPOINT_HOST` and `SharePointSiteStarter:SharePointHost` | SharePoint tenant hostname, without `https://` |
| `SHAREPOINT_SITE_OWNER_UPN` | `SHAREPOINT_SITE_OWNER_UPN` | Account used as the owner when a site is created |
| `SHAREPOINT_CERTIFICATE_VAULT_URI` | `SHAREPOINT_CERTIFICATE_VAULT_URI` and `SharePointSiteStarter:CertificateKeyVaultUri` | Certificate vault URL |
| `SHAREPOINT_CERTIFICATE_SECRET_NAME` | `SHAREPOINT_CERTIFICATE_SECRET_NAME` and `SharePointSiteStarter:CertificateSecretName` | Certificate secret name |
| `SHAREPOINT_SITE_STARTER_URL` | `SHAREPOINT_SITE_STARTER_URL` | Internal site starter URL |
| `SHAREPOINT_LEGACY_WELCOME_PAGE_NAMES_JSON` | `SharePointSiteStarter:LegacyWelcomePageNames` | Optional JSON array of former starter page filenames to remove during reprovisioning |
| `SHAREPOINT_LEGACY_WELCOME_EVENT_TITLES_JSON` | `SharePointSiteStarter:LegacyWelcomeEventTitles` | Optional JSON array of former starter event titles to remove during reprovisioning |

Keep the certificate, Graph secret, API keys, and connection strings in Key Vault or GitHub secrets. GitHub variables are visible to workflows and are not a credential store. The site starter includes a generic ProBonoLaw image and wording; no tenant logo is embedded.

The account worker needs `ACCOUNT_EMAIL_DOMAIN` from the private deployment configuration. It defaults to the official Office 365 E1 (no Teams) SKU for the ingestion account; `EMAIL_LICENSE_SKU_ID` can override that choice.

## Public to private flow

Create code branches and pull requests in this public repository. Public `main`
requires a pull request and passing CI before merging. Every five minutes, a workflow in the
private repository checks for changes to public `main`. It imports only `src`,
`tests`, `infra`, the solution file, and the Dockerfiles. The private import PR
also synchronizes the Dev Container configuration, feature lock, and empty
environment template. Filled local `.env` files are ignored in both repositories.
The private import PR
merges after its required checks pass, then starts the AKS Test deployment.
Production deployment remains manual in the private repository. The private
repository owns its deployment scripts, infrastructure names, and Azure credentials.

The public repository was prepared from a clean source snapshot. Do not import Git history from the original private repository.
