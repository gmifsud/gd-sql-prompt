# ADR-0004: Platform targets

- Status: Accepted
- Date: 2026-10-22

## Decision

| Component | Target |
|---|---|
| Engine libraries, LSP server, CLI | `net10.0` (LTS), cross-platform (Windows, macOS, Linux) |
| First UI surface | Windows-first; cross-platform reach decided by spike S4 |
| Database | SQL Server and Azure SQL Database via `Microsoft.Data.SqlClient` |

## Consequences

- CI builds and tests the engine on Linux and Windows.
- Credential storage needs an abstraction with per-OS implementations (DPAPI / Credential Manager, Keychain, libsecret) — spike S5.
- Engine code must not depend on Windows-only APIs.
