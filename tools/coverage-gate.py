#!/usr/bin/env python3
"""Coverage floor for CI (2026-10-26).

Reads every coverage.cobertura.xml under the given folder (unit and integration runs) and fails when the business layers fall
below their floor. The floors were set at the measured values minus a small margin (Application 59%, Domain 68% lines on
2026-10-26); raise them as tests are added, never lower them. Infrastructure (EF migrations, HTTP clients) and the API host are
covered by the integration tests and are reported, not gated.

Usage: python tools/coverage-gate.py coverage
"""
import glob
import sys
import xml.etree.ElementTree as ET

FLOORS = {"OpportunityPilot.Application": 0.57, "OpportunityPilot.Domain": 0.65}

folder = sys.argv[1] if len(sys.argv) > 1 else "coverage"
files = glob.glob(f"{folder}/**/coverage.cobertura.xml", recursive=True)
if not files:
    sys.exit(f"coverage-gate: no coverage.cobertura.xml under {folder}")

best: dict[str, float] = {}
for path in files:
    for package in ET.parse(path).getroot().iter("package"):
        name, rate = package.get("name"), float(package.get("line-rate", 0))
        best[name] = max(best.get(name, 0.0), rate)

failed = False
for name in sorted(best):
    floor = FLOORS.get(name)
    status = "" if floor is None else ("ok" if best[name] >= floor else "BELOW FLOOR")
    print(f"{name:40} {best[name]:6.1%}  {'floor ' + format(floor, '.0%') if floor else 'reported'}  {status}")
    failed |= floor is not None and best[name] < floor
missing = [n for n in FLOORS if n not in best]
if missing:
    print(f"coverage-gate: no coverage for {', '.join(missing)}")
sys.exit(1 if failed or missing else 0)
