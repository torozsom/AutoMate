# Account models

Local and remote account state shared by authentication, ownership and connected-provider workflows.

## Contents

| File                           | Types and responsibility                                                            |
|--------------------------------|-------------------------------------------------------------------------------------|
| [User.cs](User.cs)             | User: abstract account identity, username/email and owned applications.             |
| [LocalUser.cs](LocalUser.cs)   | LocalUser: password hash, email-verification state, token and expiry.               |
| [RemoteUser.cs](RemoteUser.cs) | RemoteUser: remote account identity, GitHub token and Azure account/token metadata. |

## Usage and integration

User is the common owner of Application
records. [AuthService](../../../Infrastructure/ApplicationServices/Auth/AuthService.cs)
and [UserService](../../../Infrastructure/ApplicationServices/Data/Users/UserService.cs) implement account
operations. [AzureArmCredentialsProvider](../../../Infrastructure/Azure/Identity/AzureArmCredentialsProvider.cs)
resolves/refreshes Azure credentials.

## Contracts and lifecycle

LocalUser stores a password hash and verification metadata; hashing, token validation and expiry checks belong to
authentication services. RemoteUser carries required AccountId plus optional GitHub/Azure connection fields. Null
provider fields do not establish a usable authorization session.

Infrastructure maps subtypes with a discriminator and protects GitHub access tokens and Azure access/refresh tokens
through Data Protection converters. In-memory values remain sensitive; entity construction does not encrypt them. Do not
serialize whole accounts into diagnostics or public responses.

User.Applications expresses ownership; calling workflows must still enforce authorization. Account deletion affects
owned projects and owner-scoped AI budget metadata through persistence relationships.

## Related documentation

- [Entities index](../README.md)
- [Subtype mappings and protection](../../../Infrastructure/Data/AutoMateModelConfiguration.cs)
- [Authentication contract](../../../Application/Auth/README.md)
- [Owned applications](../Projects/README.md)
- [Authentication tests](../../../Test/Infra/Auth/AuthServiceTests.cs)
