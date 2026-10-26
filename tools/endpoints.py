#!/usr/bin/env python3
"""Every API endpoint, read from the controllers (2026-10-26).

Writes docs/api-endpoints.md: one row per endpoint (method, path, controller, auth, rate limit, summary). CI runs it with --check
and fails when a controller changed and the index was not regenerated, so the list can never drift from the code. Request and
response shapes stay in docs/api-contracts.md.

Usage: python tools/endpoints.py            (rewrite the index)
       python tools/endpoints.py --check    (fail if it is out of date)
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
CONTROLLERS = ROOT / "src" / "OpportunityPilot.Api" / "Controllers"
OUT = ROOT / "docs" / "api-endpoints.md"

VERB = re.compile(r"\[Http(Get|Post|Put|Patch|Delete)(?:\(\s*\"([^\"]*)\"[^)]*\))?\]")
ROUTE = re.compile(r"\[Route\(\"([^\"]*)\"\)\]")
CLASS = re.compile(r"\bclass\s+(\w+)Controller\b")
SUMMARY = re.compile(r"///\s*<summary>(.*?)</summary>", re.S)
LIMIT = re.compile(r"EnableRateLimiting\(([\w.]+)\)")


def clean(text: str) -> str:
    text = re.sub(r"\s*///\s*", " ", text)
    text = re.sub(r"<see cref=\"([^\"]+)\"\s*/>", r"\1", text)
    text = re.sub(r"<[^>]+>", "", text)
    return " ".join(text.split()).replace("|", "\\|")


def endpoints():
    rows = []
    for file in sorted(CONTROLLERS.glob("*.cs")):
        src = file.read_text(encoding="utf-8")
        cls = CLASS.search(src)
        if not cls:
            continue
        head = src[: cls.start()]
        route = ROUTE.findall(head)
        base = route[-1] if route else ""
        class_anon = "AllowAnonymous" in head
        class_limit = LIMIT.findall(head)
        class_auth = re.findall(r"Authorize\(Policy\s*=\s*[\w.]*?(\w+)\)", head)
        for m in VERB.finditer(src, cls.end()):
            verb, template = m.group(1).upper(), m.group(2) or ""
            # The doc comment and attributes of this action: the lines above it that are comments, attributes or blank, and the
            # attribute lines below it up to the method signature.
            lines_above = src[: m.start()].split("\n")
            kept = [lines_above.pop()]
            while lines_above and re.match(r"\s*(///|\[|$)", lines_above[-1]):
                kept.insert(0, lines_above.pop())
            sig = re.search(r"\n\s*public\b", src[m.end() :])
            block = "\n".join(kept) + src[m.start() : m.end() + (sig.start() if sig else 0)]
            path = template if template.startswith("/") or template.startswith("~") else "/".join(p for p in (base, template) if p)
            path = "/" + path.lstrip("~/")
            path = re.sub(r"\{(\w+):[^}]+\}", r"{\1}", path)
            anon = class_anon or "AllowAnonymous" in block
            auth = "anonymous" if anon else (re.findall(r"Authorize\(Policy\s*=\s*[\w.]*?(\w+)\)", block) or class_auth or ["user"])[-1]
            limit = (LIMIT.findall(block) or class_limit or ["—"])[-1].split(".")[-1]
            summary = SUMMARY.findall(block)
            name = re.search(r"\bpublic\b[^(=]*?\s(\w+)\s*\(", src[m.end() :])
            action = re.sub(r"(?<=[a-z])(?=[A-Z])", " ", name.group(1)).capitalize() if name else ""
            rows.append((path, verb, f"{cls.group(1)} · {action}", auth, limit, clean(summary[-1]) if summary else ""))
    return sorted(rows, key=lambda r: (r[0], ["GET", "POST", "PUT", "PATCH", "DELETE"].index(r[1])))


def render(rows) -> str:
    lines = [
        "# API endpoints (generated)",
        "",
        "Generated from `src/OpportunityPilot.Api/Controllers` by `python tools/endpoints.py`; CI fails when it is out of date.",
        "Do not edit by hand. Request and response shapes: [api-contracts.md](api-contracts.md).",
        "",
        f"{len(rows)} endpoints. Auth: `user` = any signed-in person (guests included unless the policy says otherwise),",
        "`NotGuest` = signed-in account only, `anonymous` = no sign-in. Rate limit: the per-endpoint policy in `Hosting/RateLimits.cs`",
        "(every request also counts against the global 120 a minute).",
        "",
        "| Method | Path | Controller · action | Auth | Rate limit | Summary |",
        "|---|---|---|---|---|---|",
    ]
    lines += [f"| {v} | `{p}` | {c} | {a} | {l} | {s} |" for p, v, c, a, l, s in rows]
    return "\n".join(lines) + "\n"


if __name__ == "__main__":
    text = render(endpoints())
    if "--check" in sys.argv:
        current = OUT.read_text(encoding="utf-8") if OUT.exists() else ""
        if current.replace("\r\n", "\n") != text:
            sys.exit("docs/api-endpoints.md is out of date: run `python tools/endpoints.py` and commit the result.")
        print(f"docs/api-endpoints.md is up to date ({text.count(chr(10)) - 11} endpoints).")
    else:
        OUT.write_text(text, encoding="utf-8", newline="\n")
        print(f"Wrote {OUT.relative_to(ROOT)}")
