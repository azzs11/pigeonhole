# 0002. API key format and storage

- Status: accepted
- Date: 2026-11-25

## Context

The scaffold stored plaintext keys, seeded a fixed key (`test-api-key-12345`) in every
deployment, and compared keys with `==`.

## Decision

- Format: `rly_{8-char id}_{43-char base64url secret}` (256 bits of entropy). The first
  12 characters (`rly_xxxxxxxx`) are a visible prefix, safe to show in logs and the UI.
- Storage: only the prefix and `SHA-256(full key)` are stored. A fast hash is enough
  because the key is random and high-entropy, unlike a password.
- Verification: look up by the unique prefix, then compare hashes with
  `CryptographicOperations.FixedTimeEquals`. An unknown prefix still hashes and compares
  against a dummy value, so the response time doesn't reveal whether the prefix exists.
- Keys carry scopes (`messages:send`, `messages:read`). A revoked key or an inactive tenant fails.
- No key is seeded. Keys are created with `relay keys create` and shown once.
  Rotation means creating a new key and revoking the old one with `relay keys revoke`.
- Transport: `Authorization: Bearer <key>`. One authentication handler does all checks.

## Consequences

- A database leak doesn't reveal usable keys.
- A lost key can't be recovered, only replaced.
