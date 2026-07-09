# Security assets

This directory is the default persistent store for domain TLS certificates and DKIM private keys.

- Manage domain certificates and DKIM from the administration site.
- Never commit generated files or private keys.
- On Linux, directories are restricted to mode `0700` and private files to `0600`.
- In Docker deployments this path is backed by the `security-assets` named volume.
