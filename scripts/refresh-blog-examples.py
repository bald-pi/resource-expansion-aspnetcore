"""Capture live responses and refresh the blog's example/source appendix (Python 3)."""
import argparse
import json
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen

ROOT = Path(__file__).resolve().parents[1]
MARKER = "<!-- generated examples and source below -->"
CASES = [
    ("baseline", "/api/memberships/1001", None, 200),
    ("member", "/api/memberships/1001?expand=member", None, 200),
    ("visits", "/api/memberships/1001?expand=visits", None, 200),
    ("nested", "/api/memberships/1001?expand=member,visits.club&relatedLimit=3", None, 200),
    ("truncated", "/api/memberships/1001?expand=visits&relatedLimit=1", None, 200),
    ("empty", "/api/memberships/1002?expand=visits", None, 200),
    ("unsupported", "/api/memberships/1001?expand=payments", None, 400),
    ("depth", "/api/memberships/1001?expand=visits.club.address", None, 400),
    ("limit", "/api/memberships/1001?expand=visits&relatedLimit=51", None, 400),
    ("missing-membership", "/api/memberships/9999", None, 404),
    ("normalized", "/api/memberships/1001?expand=%20MEMBER%20,visits.club,member", None, 200),
]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://localhost:5080")
    args = parser.parse_args()
    captured = {}
    manifest = []
    for name, path, token, expected in CASES:
        headers = {"Authorization": f"Bearer {token}"} if token else {}
        request = Request(args.base_url.rstrip("/") + path, headers=headers)
        try:
            response = urlopen(request, timeout=15)
        except HTTPError as error:
            response = error
        with response:
            if response.status != expected:
                raise RuntimeError(f"{name}: expected {expected}, got {response.status}: {response.read()!r}")
            body = json.loads(response.read())
        # Dynamic tracing metadata is not part of the stable example contract.
        body.pop("traceId", None)
        captured[name] = json.dumps(body, indent=2, ensure_ascii=False) + "\n"
        manifest.append({"name": name, "method": "GET", "path": path, "token": token, "status": expected})

    output = ROOT / "examples" / "responses"
    output.mkdir(parents=True, exist_ok=True)
    for name, body in captured.items():
        (output / f"{name}.json").write_text(body, encoding="utf-8")
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

    blog = ROOT / "docs" / "blog-walkthrough.md"
    content = blog.read_text(encoding="utf-8").split(MARKER)[0].rstrip() + "\n\n" + MARKER + "\n"
    content += "\n## Example requests and responses\n\nCaptured from the running PostgreSQL-backed API. Dynamic trace IDs, if present, are omitted. "
    content += f"All {len(CASES)} captured cases are available in [examples/responses](../examples/responses); the manifest records request paths, tokens, and statuses.\n"
    selected = {"baseline", "member", "visits", "nested", "truncated", "empty", "unsupported", "depth", "missing-membership"}
    for name, path, token, status in CASES:
        if name not in selected:
            continue
        content += f"\n### {name.replace('-', ' ').title()}\n\n```http\nGET {path}\n"
        if token:
            content += f"Authorization: Bearer {token}\n"
        content += f"```\n\nHTTP {status}\n\n```json\n{captured[name]}```\n"

    content += "\n## Complete API implementation\n\nThe source below matches the runnable files. Tests live in `tests/ResourceExpansion.Tests`.\n"
    files = [
        "Directory.Build.props", "compose.yaml", "src/ResourceExpansion.Api/ResourceExpansion.Api.csproj",
        "src/ResourceExpansion.Api/Program.cs", "src/ResourceExpansion.Api/Domain/Membership.cs",
        "src/ResourceExpansion.Api/Infrastructure/MembershipsDbContext.cs",
        "src/ResourceExpansion.Api/Infrastructure/DemoData.cs",
        "src/ResourceExpansion.Api/Expansion/IExpandableRequest.cs",
        "src/ResourceExpansion.Api/Expansion/ExpandPlan.cs",
        "src/ResourceExpansion.Api/Expansion/ExpandRequestValidator.cs",
        "src/ResourceExpansion.Api/Expansion/ExpansionRules.cs",
        "src/ResourceExpansion.Api/Expansion/Related.cs",
        "src/ResourceExpansion.Api/Features/Memberships/GetMembership/GetMembershipRequest.cs",
        "src/ResourceExpansion.Api/Features/Memberships/GetMembership/MembershipExpansions.cs",
        "src/ResourceExpansion.Api/Features/Memberships/GetMembership/Response.cs",
        "src/ResourceExpansion.Api/Features/Memberships/GetMembership/Endpoint.cs",
        "src/ResourceExpansion.Api/Features/Members/GetMember/Endpoint.cs",
        "src/ResourceExpansion.Api/Features/Memberships/GetMembershipVisits/Endpoint.cs",
        "src/ResourceExpansion.Api/Features/Clubs/GetClub/Endpoint.cs",
        "src/ResourceExpansion.Api/appsettings.json",
        "src/ResourceExpansion.Api/Properties/launchSettings.json",
    ]
    for name in files:
        path = ROOT / name
        language = {".cs": "csharp", ".csproj": "xml", ".props": "xml", ".yaml": "yaml", ".json": "json"}[path.suffix]
        content += f"\n### {name}\n\n```{language}\n{path.read_text(encoding='utf-8-sig').rstrip()}\n```\n"
    blog.write_text(content, encoding="utf-8")
    print(f"Verified {len(CASES)} live HTTP examples; refreshed response files and blog source appendix.")


if __name__ == "__main__":
    main()
