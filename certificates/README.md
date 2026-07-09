Place `admin-chain.pem` and `admin-key.pem` here for the management site.

SMTP/IMAP certificates are managed per mail domain from the administration site and are stored under the configured `SecurityAssets:RootPath`.

The admin certificate file may contain a complete chain. Private keys may be PKCS#8 or RSA PEM and may be encrypted. Configure passwords through environment variables and never commit private keys.
