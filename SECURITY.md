# Security policy

WattLedger is a local-only desktop tool. It does not open network connections, does not require a kernel driver, and does not request administrator rights.

If you find a security issue:

1. Do not open a public issue with exploit details.
2. Use GitHub's private vulnerability reporting on the repository, or contact the maintainers through the address in the repository profile.
3. Include the WattLedger version, Windows build, and a minimal reproduction.

The most relevant risk areas are native interop (SetupAPI / battery IOCTL / PDH) and parsing of local database or export data. Reports about crashes, memory-safety issues, or unexpected file/network access are welcome.
