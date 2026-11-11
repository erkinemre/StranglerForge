# ADR-040: Kustomize for Kubernetes application deployment

Status: Accepted for Phase 09.

Use one base and environment overlays for five stateless HTTP hosts. Kustomize avoids duplicate Helm/chart and raw-manifest configuration paths. The base includes service discovery, health probes, resource bounds, security contexts, ingress, and gateway HPA/PDB. An operator must replace image digests, Secrets, ingress DNS/TLS, and external endpoints before applying. This is a reference, not production certification.