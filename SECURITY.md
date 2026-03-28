# Security Policy

## Supported Versions

RoslynMcp is in alpha. Security fixes are applied to the latest release on the `dev` branch.

## Important: Execution Model

RoslynMcp runs as a local process with your user permissions. It does **not** sandbox filesystem access — any tool can read or write files your user account can access. Only run RoslynMcp with agents you trust, on projects you control.

See [Issue #9](https://github.com/MadQ/RoslynMcp/issues/9) for the filesystem access boundary roadmap.

## Reporting a Vulnerability

If you discover a security vulnerability, please report it responsibly:

1. **Do not** open a public GitHub issue.
2. Email the maintainer directly or use [GitHub's private vulnerability reporting](https://github.com/MadQ/RoslynMcp/security/advisories/new).
3. Include steps to reproduce and any relevant details.

You should receive an acknowledgment within 48 hours. We will work with you to understand the scope and coordinate a fix before any public disclosure.
