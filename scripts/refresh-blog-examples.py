"""Capture live responses and refresh the blog's example/source appendix (Python 3)."""
import argparse
import json
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen

ROOT = Path(__file__).resolve().parents[1]
MARKER = "<!-- generated examples and source below -->"
CASES = [
    ("baseline", "/api/orders/1001", None, 200),
    ("customer", "/api/orders/1001?expand=customer", None, 200),
    ("items", "/api/orders/1001?expand=items", None, 200),
    ("nested", "/api/orders/1001?expand=customer,items.product&relatedLimit=3", None, 200),
    ("truncated", "/api/orders/1001?expand=items.product&relatedLimit=1", None, 200),
    ("empty", "/api/orders/1002?expand=items", None, 200),
    ("unsupported", "/api/orders/1001?expand=payments", None, 400),
    ("depth", "/api/orders/1001?expand=items.product.supplier", None, 400),
    ("limit", "/api/orders/1001?expand=items&relatedLimit=51", None, 400),
    ("missing-order", "/api/orders/9999", None, 404),
    ("normalized", "/api/orders/1001?expand=%20CUSTOMER%20,items.product,customer", None, 200),
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
    selected = {"baseline", "customer", "nested", "truncated", "empty", "unsupported", "depth", "missing-order"}
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
        "src/ResourceExpansion.Api/Program.cs", "src/ResourceExpansion.Api/Domain/Order.cs",
        "src/ResourceExpansion.Api/Infrastructure/OrdersDbContext.cs",
        "src/ResourceExpansion.Api/Infrastructure/DemoData.cs",
        "src/ResourceExpansion.Api/Features/Orders/GetOrder/OrderExpand.cs",
        "src/ResourceExpansion.Api/Features/Orders/GetOrder/GetOrderQueryValidator.cs",
        "src/ResourceExpansion.Api/Features/Orders/GetOrder/OrderQuery.cs",
        "src/ResourceExpansion.Api/Features/Orders/GetOrder/Response.cs",
        "src/ResourceExpansion.Api/Features/Orders/GetOrder/Endpoint.cs",
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
