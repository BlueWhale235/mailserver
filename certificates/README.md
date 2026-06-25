Place PEM files in this directory:

- `mail-chain.pem` and `mail-key.pem` for SMTP/IMAP.
- `admin-chain.pem` and `admin-key.pem` for the management site.

The certificate files may contain a complete chain. Private keys may be PKCS#8 or RSA PEM and may be encrypted. Configure passwords through environment variables and never commit private keys.
