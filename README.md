# Account Operations

Account Operations contains three .NET 10 services:

- `AccountOperations` consumes account and email events and coordinates account workflows.
- `ResourceManager` provisions account search resources.
- `SharePointSiteStarter` prepares an account's SharePoint site.

The repository contains application source, tests, and generic infrastructure templates. It does not contain deployment credentials, tenant identifiers, or production settings.

## Build

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

Keep the certificate, Graph secret, API keys, and connection strings in Key Vault or GitHub secrets. GitHub variables are visible to workflows and are not a credential store. The site starter includes a generic ProBonoLaw image and wording; no tenant logo is embedded.

The account worker needs `ACCOUNT_EMAIL_DOMAIN` from the private deployment configuration. It defaults to the official Office 365 E1 (no Teams) SKU for the ingestion account; `EMAIL_LICENSE_SKU_ID` can override that choice.

## Public to private flow

Changes to public `main` are built and tested here. A scheduled workflow in the private repository imports only `src`, `tests`, `infra`, the solution file, and the Dockerfiles from `ProBonoLaw/PBL-Account-Operations`. It opens a private pull request for review. The private repository owns its deployment scripts, infrastructure names, and Azure credentials. Protect both `main` branches and require CI and review before merging.

The public repository was prepared from a clean source snapshot. Do not import Git history from the original private repository.
