# Web Tests

Credential-free tests of the Web composition root and hosting-profile configuration. Tests resolve the dashboard's
shared cloud dependencies without starting hosted workers or contacting PostgreSQL, Redis, GitHub, or Azure.

Run with `dotnet test Web.Tests/Web.Tests.csproj`.

Deployment-history tests verify saved terminal output survives metric-backend failure and empty output has an explicit
notice. Metric display tests check restored numeric units. Browser-wrapper regression tests exercise resize feedback,
hidden terminals, history replacement, and disposal using Node's built-in test runner:

`node --test Web.Tests/JavaScript/xterm-wrapper.test.cjs`
