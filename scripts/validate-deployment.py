#!/usr/bin/env python3
"""Validate checked-in deployment shape without a Kubernetes cluster."""
from pathlib import Path
import json
import re
import sys

import yaml

ROOT = Path(__file__).resolve().parents[1]
K8S = ROOT / "deploy" / "kubernetes"
errors = []

for path in [*K8S.rglob("*.yaml"), ROOT / "deploy/docker/compose.yml",
             *ROOT.glob("deploy/observability/**/*.yaml"), ROOT / ".github/workflows/ci.yml"]:
    try:
        documents = list(yaml.safe_load_all(path.read_text(encoding="utf-8")))
    except (yaml.YAMLError, UnicodeError) as exc:
        errors.append(f"{path}: {exc}")
        continue
    if not documents or all(item is None for item in documents):
        errors.append(f"{path}: empty YAML")
    if path.name == "kustomization.yaml":
        for item in documents[0].get("resources", []):
            if not (path.parent / item).exists():
                errors.append(f"{path}: missing resource {item}")

apps = list(yaml.safe_load_all((K8S / "base/applications.yaml").read_text(encoding="utf-8")))
deployments = [item for item in apps if item.get("kind") == "Deployment"]
expected = {"gateway", "legacy", "catalog", "inventory", "orders"}
if {item["metadata"]["name"] for item in deployments} != expected:
    errors.append("Kubernetes deployments do not match the five HTTP hosts")
for item in deployments:
    name = item["metadata"]["name"]
    pod = item["spec"]["template"]["spec"]
    container = pod["containers"][0]
    if not pod.get("securityContext", {}).get("runAsNonRoot"):
        errors.append(f"{name}: non-root security context missing")
    if not container.get("securityContext", {}).get("readOnlyRootFilesystem"):
        errors.append(f"{name}: read-only root missing")
    for probe in ("startupProbe", "livenessProbe", "readinessProbe"):
        if probe not in container:
            errors.append(f"{name}: {probe} missing")
    if not any("secretRef" in entry for entry in container.get("envFrom", [])):
        errors.append(f"{name}: external Secret reference missing")

for path in (ROOT / "deploy/observability/grafana/dashboards").glob("*.json"):
    try:
        json.loads(path.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, UnicodeError) as exc:
        errors.append(f"{path}: {exc}")

for path in [ROOT / "README.md", *ROOT.glob("docs/**/*.md")]:
    if not path.exists():
        continue
    for target in re.findall(r"\[[^\]]+\]\(([^)]+)\)", path.read_text(encoding="utf-8")):
        target = target.split("#", 1)[0]
        if target and not target.startswith(("http://", "https://", "mailto:")) and not (path.parent / target).exists():
            errors.append(f"{path}: broken relative link {target}")

if errors:
    print("\n".join(errors), file=sys.stderr)
    sys.exit(1)
print(f"Validated {len(deployments)} Kubernetes workloads, YAML, dashboards, and documentation links.")