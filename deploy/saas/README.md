# AutoMate SaaS configuration

Run the AutoMate image in its own Azure Container App with `HostingProfile__Mode=SaaS`. Do not mount a Docker socket or
customer filesystem. Supply PostgreSQL, Redis, OAuth credentials, and Data Protection persistence through Azure-managed
services and Key Vault references.

The Azure Container Apps managed identity needs only permission to read its own configuration and Key Vault secrets.
Customer deployments continue to authenticate with each customer's linked Azure account and deploy into that customer's
subscription.
