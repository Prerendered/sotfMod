---
name: security-standards
description: The security review checklist and severity scale — load before any security pass.
---

# Security standards

Every pass walks all review classes; mark each cleared or a finding.

## Checklist
| Class | Look for |
|---|---|
| Authn/Authz | missing checks, broken object-level access (IDOR), privilege escalation, trusting client-set identity |
| Injection | SQL/NoSQL, command, path traversal, SSRF, XSS — any unsanitized input reaching an interpreter |
| Input validation | boundary trust: Zod / `v.*` on every external input (standards §14 Fail Fast); mass assignment |
| Secrets & config | hardcoded keys, secrets in logs/URLs, committed `.env`, permissive CORS, debug on in prod |
| Data exposure | over-fetching, PII in responses/logs, missing encryption in transit or at rest |
| Dependencies | known CVEs, unpinned versions, abandoned packages (merge mode audits these) |
| Auth tokens | weak session handling, missing expiry, tokens in localStorage, no CSRF protection |

## Severity
| Tag | Meaning |
|---|---|
| critical | remotely exploitable, no auth needed — RCE or data loss. Blocks merge |
| high | exploitable with auth or user interaction; sensitive data exposure. Blocks merge |
| medium | requires unlikely preconditions; a defense-in-depth gap |
| low | hardening / best-practice nit |

Any critical or high = FAIL. Every finding names the code, the concrete exploit
path, and the fix — never "might be insecure".

## Evidence
Per senior-protocol rule 1, a vulnerability claim needs a pointer: the `file:line`
and the input that reaches it. A hunch without a path is a note, not a finding.
