# Security policy

Apogee Engine is pre-alpha software: there are no supported release lines yet, and fixes land on `main`.

## Reporting a vulnerability

Please do not open a public issue for a security problem. Report it privately through the repository's **Security** tab
("Report a vulnerability"), with what you found, how to reproduce it, and the version or commit.

The engine loads untrusted data (asset packs, mod packages and their Luau scripts, network packets, saved files), so reports in these areas are especially welcome:
a Luau script that escapes its sandbox, a pack or file that corrupts memory in the readers, a network message that crashes or exploits a host. A report is acknowledged within a week; this is a small
team, so the fix may take longer, and we will tell you where it stands.

Problems in a vendored third-party library (`ThirdParty/`) should also go to that library's maintainers.
