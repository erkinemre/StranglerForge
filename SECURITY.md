# Security policy

This repository demonstrates architecture and migration techniques. It has no production support or security response guarantee.

Please report suspected vulnerabilities privately through GitHub's **Report a vulnerability** feature if it is enabled for the repository. Otherwise, contact the repository owner privately through their GitHub profile. Do not include live credentials or personal data in a public issue.

For local use, provide SQL Server, PostgreSQL, RabbitMQ, and internal HTTP credentials through environment variables or a secret manager. The checked-in Compose file requires operator-supplied values. Restrict internal service endpoints and stock adjustment operations to trusted networks. Authentication and deployment hardening remain outside the validated reference scope.
