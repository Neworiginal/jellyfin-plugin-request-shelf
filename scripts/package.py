"""Build the Jellyfin feed from a Release build. Run from the repository root."""
import argparse
import hashlib
import json
import os
import re
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile, ZipInfo

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--repo", default=os.getenv("GITHUB_REPOSITORY", "neworiginal/jellyfin-plugin-request-shelf"))
args = parser.parse_args()
if not re.fullmatch(r"[A-Za-z0-9-]+/[A-Za-z0-9_.-]+", args.repo):
    parser.error("Expected --repo OWNER/REPOSITORY")

project = ET.parse(root / "src/RequestShelf.csproj")
version = project.findtext(".//Version")
if not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", version or ""):
    raise ValueError("Set a four-part plugin version in RequestShelf.csproj")
assembly = project.findtext(".//AssemblyName") + ".dll"
framework = project.findtext(".//TargetFramework")
abi = project.find(".//PackageReference[@Include='Jellyfin.Controller']").get("Version") + ".0"
guid = "2cb52794-23e3-4181-80e3-d43d18fde3a9"
plugin = dict(guid=guid, name="Request Shelf", category="General", owner=args.repo.split("/")[0],
              overview="Request movies and TV seasons through Seerr.",
              description="Search Jellyfin for a title, request missing movies and seasons, and check download progress.")
manifest = root / "manifest.json"
versions = json.loads(manifest.read_text())[0]["versions"] if manifest.exists() else []
filename = f"RequestShelf_{version}.zip"
folder = root / "packages"
folder.mkdir(exist_ok=True)
package = folder / filename
entry = next((v for v in versions if v["version"] == version), None)
notes = (root / "release-notes.txt").read_text().strip()
if entry is None:
    dll = (root / "src/bin/Release" / framework / assembly).read_bytes()
    meta = dict(plugin, targetAbi=abi, version=version, status="Active", autoUpdate=True, assemblies=[assembly], changelog=notes)
    with ZipFile(package, "w") as archive:
        for name, content in [(assembly, dll), ("meta.json", json.dumps(meta, indent=2).encode() + b"\n")]:
            archive.writestr(ZipInfo(name), content, compress_type=ZIP_DEFLATED)
    versions.append(dict(version=version, targetAbi=abi, changelog=notes,
                         timestamp=datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
                         checksum=hashlib.md5(package.read_bytes()).hexdigest()))
for item in versions:
    name = f"RequestShelf_{item['version']}.zip"
    asset = folder / name
    if hashlib.md5(asset.read_bytes()).hexdigest() != item["checksum"]:
        raise ValueError(f"Checksum mismatch for previously published {name}")
    item["sourceUrl"] = f"https://raw.githubusercontent.com/{args.repo}/main/packages/{name}"
versions.sort(key=lambda v: tuple(map(int, v["version"].split("."))), reverse=True)
manifest.write_text(json.dumps([dict(plugin, versions=versions)], indent=2) + "\n")
print(f"Feed ready for {args.repo} with Request Shelf {version}")
