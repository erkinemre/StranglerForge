#!/usr/bin/env python3
"""Fail CI if the .NET vulnerability audit reports an affected package."""
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    report = json.load(source)
affected = []
for project in report.get("projects", []):
    for framework in project.get("frameworks", []):
        for kind in ("topLevelPackages", "transitivePackages"):
            for package in framework.get(kind, []):
                if package.get("vulnerabilities"):
                    affected.append(f"{project['path']}: {package['id']} {package.get('resolvedVersion')}")
if affected:
    print("\n".join(affected), file=sys.stderr)
    sys.exit(1)
print("No reported vulnerable .NET packages.")