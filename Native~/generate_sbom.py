#!/usr/bin/env python3
"""Writes ThirdPartyNotices~/sbom.cdx.json, the CycloneDX software bill of materials.

The package ships prebuilt OpenUSD and oneTBB libraries that load in-process, so a consumer
needs a machine-readable record of what native code they are loading and where it came from
(SECURITY-282834, CWE-494 remediation 4). This regenerates that record from the payload that is
actually checked in: the SHA-256 of every shipped native file is read from the file itself, and
the source tags, commits, build flags and toolchains are the constants below.

    python3 Native~/generate_sbom.py

Rerun it in the same commit as any payload rebuild, next to
`python3 Native~/generate_native_hashes.py`, and update the constants below when a dependency
version or a build machine changes.

What this does NOT do: prove the payload is authentic upstream code. The digests describe what
shipped; provenance comes from the payload being built from the public OpenUSD tag recorded here
(see BUILD_NOTES.md). The builds are not bit-reproducible, so a third party rebuilding from the
same tag will not get the same bytes.
"""

import hashlib
import json
import pathlib
import uuid

REPO = pathlib.Path(__file__).resolve().parent.parent
OUTPUT = REPO / "ThirdPartyNotices~" / "sbom.cdx.json"

# --- what the payload was built from ------------------------------------------------------
USD_TAG = "v26.05"
USD_VERSION = "26.05"
USD_COMMIT = "2095faf"  # "Merge release v26.05"
USD_BUILD_FLAGS = (
    "--build-variant release --build-monolithic --no-python --no-imaging --no-usdview "
    "--no-examples --no-tutorials --no-tests --no-materialx"
)
# The one Unity patch applied to that tree before it is built (SECURITY-282834); pinned by SHA-256
# in Native~/dependency-sources/<platform>.tsv and applied by Native~/build_openusd.py.
USD_PATCH = "Native~/patches/openusd-26.05-lz4-1.10.0.patch"
LZ4_VERSION = "1.10.0"
LZ4_UPSTREAM_VERSION = "1.9.2"  # what OpenUSD v26.05 itself vendors
USD_LICENSE = "Tomorrow Open Source Technology License 1.0"
UNITY_LICENSE = "Unity Companion License / see LICENSE.md"

# Recorded so a third party can reproduce the build environment, as the review asks. Update
# these whenever a payload is rebuilt on a different machine (see BUILD_NOTES.md).
TOOLCHAIN = {
    "macOS universal": (
        "macOS 26.6.2 (25G83), Xcode 26.6 (17F113), Apple clang 21.0.0 (clang-2100.1.1.101), "
        "CMake 4.3.2, SDK 26.5, x86_64+arm64"
    ),
    "Windows x64": (
        "Windows, VS Build Tools 2022 (MSVC 14.44), Windows SDK 10.0.26100, CMake 4.3.3, x64"
    ),
    "Linux x64": (
        "Ubuntu 22.04, g++ 11.4.0, CMake 3.31.6, patchelf 0.19.1, x64 (requires glibc >= 2.34)"
    ),
}

PROVENANCE_NOTE = (
    "Every OpenUSD payload is built from the public v26.05 tag (commit 2095faf) with one Unity "
    "patch applied (recorded in the OpenUSD component's pedigree) and the flags recorded on that "
    "component. The builds are not bit-reproducible: no SOURCE_DATE_EPOCH, "
    "absolute build paths are embedded, and the macOS payload is post-processed with "
    "install_name_tool and codesign. The SHA-256 values here identify what shipped; they do not "
    "prove upstream authenticity."
)

USD_REFS = [
    {"type": "vcs", "url": "https://github.com/PixarAnimationStudios/OpenUSD"},
    {"type": "distribution",
     "url": f"https://github.com/PixarAnimationStudios/OpenUSD/releases/tag/{USD_TAG}"},
]
# The repository moved from oneapi-src to the UXL Foundation; the old URL only redirects. The purl in
# tbb_binary() deliberately keeps oneapi-src: v2020.3 was published under that name, and that is the
# name vulnerability databases key the release on.
TBB_REFS = [{"type": "vcs", "url": "https://github.com/uxlfoundation/oneTBB"}]


def sha256_of(relative_path):
    return hashlib.sha256((REPO / relative_path).read_bytes()).hexdigest()


def first_present(*relative_paths):
    """The first of these that exists, for a file whose name changed between payload builds."""
    for path in relative_paths:
        if (REPO / path).is_file():
            return path
    raise SystemExit("None of these shipped files exists: " + ", ".join(relative_paths))


def spdx(identifier):
    return [{"license": {"id": identifier}}]


def named_license(name):
    return [{"license": {"name": name}}]


def component(bom_ref, name, version, *, shipped_path=None, purl=None, licenses=None,
              description=None, properties=(), external_refs=None, nested=None, pedigree=None):
    entry = {"bom-ref": bom_ref, "type": "library", "name": name, "version": version,
             "scope": "required"}
    if description:
        entry["description"] = description
    if purl:
        entry["purl"] = purl
    if licenses:
        entry["licenses"] = licenses
    props = list(properties)
    if shipped_path:
        entry["hashes"] = [{"alg": "SHA-256", "content": sha256_of(shipped_path)}]
        props.insert(0, ("unity:shippedPath", shipped_path))
    if props:
        entry["properties"] = [{"name": key, "value": value} for key, value in props]
    if external_refs:
        entry["externalReferences"] = external_refs
    if pedigree:
        entry["pedigree"] = pedigree
    if nested:
        entry["components"] = nested
    return entry


def openusd_binary(bom_ref, shipped_path, platform):
    return component(
        bom_ref, "OpenUSD (monolithic runtime library)", USD_VERSION,
        shipped_path=shipped_path,
        purl=f"pkg:github/PixarAnimationStudios/OpenUSD@{USD_TAG}",
        licenses=named_license(USD_LICENSE),
        description=f"OpenUSD {USD_VERSION} built monolithic for {platform}.",
        properties=[("unity:platform", platform), ("unity:toolchain", TOOLCHAIN[platform]),
                    ("unity:sourceCommit", USD_COMMIT)],
        external_refs=USD_REFS)


def tbb_binary(bom_ref, shipped_path, platform, description):
    return component(
        bom_ref, "oneTBB", "2020.3", shipped_path=shipped_path,
        purl="pkg:github/oneapi-src/oneTBB@v2020.3",
        licenses=spdx("Apache-2.0"), description=description,
        properties=[("unity:platform", platform), ("unity:interfaceVersion", "11103"),
                    ("unity:licenseText", "ThirdPartyNotices~/licenses/oneTBB-LICENSE.txt")],
        external_refs=TBB_REFS)


def wrapper_binary(bom_ref, shipped_path, platform, package_version):
    return component(
        bom_ref, "Unity USD Toolkit native wrapper", package_version, shipped_path=shipped_path,
        licenses=named_license(UNITY_LICENSE),
        description=f"First-party native wrapper (Native~/src) built for {platform}.",
        properties=[("unity:platform", platform), ("unity:toolchain", TOOLCHAIN[platform]),
                    ("unity:nativeApiVersion", "5")])


CVE_2021_3520 = {
    "type": "security", "id": "CVE-2021-3520",
    "source": {"name": "NVD", "url": "https://nvd.nist.gov/vuln/detail/CVE-2021-3520"},
}


def usd_patch():
    """The CycloneDX description of the Unity patch, with its digest so the record is checkable."""
    return {
        "type": "unofficial",
        "diff": {"url": USD_PATCH},
        "resolves": [
            dict(CVE_2021_3520, name=("LZ4 1.9.2 memmove() with a negative size; OpenUSD's "
                                      "crate reader could pass an output size above INT_MAX")),
            {"type": "security", "id": "SECURITY-282834",
             "name": ("TfFastCompression::DecompressFromBuffer did not check chunk count or "
                      "chunk sizes read from the file against the buffer")},
        ],
    }


def vendored_components():
    """Third-party code that lives inside the OpenUSD source tree and is linked into the
    monolithic library, so it ships even though it is not a separate file."""
    return [
        component(
            "cli11", "CLI11", "2.3.1", purl="pkg:github/CLIUtils/CLI11@v2.3.1",
            licenses=spdx("BSD-3-Clause"),
            description="Vendored inside OpenUSD as pxr/base/tf/pxrCLI11.",
            properties=[("unity:licenseText", "ThirdPartyNotices~/licenses/CLI11-LICENSE.txt")],
            external_refs=[{"type": "vcs", "url": "https://github.com/CLIUtils/CLI11"}]),
        component(
            "double-conversion", "double-conversion", "3.3.0",
            purl="pkg:github/google/double-conversion@v3.3.0", licenses=spdx("BSD-3-Clause"),
            description="Vendored inside OpenUSD as pxr/base/tf/pxrDoubleConversion.",
            properties=[("unity:licenseText",
                         "ThirdPartyNotices~/licenses/double-conversion-LICENSE.txt")],
            external_refs=[{"type": "vcs", "url": "https://github.com/google/double-conversion"}]),
        component(
            "lz4", "LZ4", LZ4_VERSION, purl=f"pkg:github/lz4/lz4@v{LZ4_VERSION}",
            licenses=spdx("BSD-2-Clause"),
            description=(f"Vendored inside OpenUSD as pxr/base/tf/pxrLZ4. OpenUSD v26.05 vendors "
                         f"{LZ4_UPSTREAM_VERSION}; the Unity patch replaces it with upstream "
                         f"v{LZ4_VERSION}, keeping Pixar's namespace edits."),
            properties=[("unity:licenseText", "ThirdPartyNotices~/licenses/LZ4-LICENSE.txt")],
            external_refs=[{"type": "vcs", "url": "https://github.com/lz4/lz4"}],
            pedigree={
                "ancestors": [{"type": "library", "name": "LZ4", "version": LZ4_UPSTREAM_VERSION,
                               "purl": f"pkg:github/lz4/lz4@v{LZ4_UPSTREAM_VERSION}"}],
                "patches": [usd_patch()],
            }),
        component(
            "tsl-robin-map", "tsl robin-map", "unknown", licenses=spdx("MIT"),
            description=("Vendored inside OpenUSD as pxr/base/tf/pxrTslRobinMap. The vendored copy "
                         "declares no version; it is whatever OpenUSD v26.05 ships."),
            properties=[("unity:licenseText",
                         "ThirdPartyNotices~/licenses/tsl-robin-map-LICENSE.txt")],
            external_refs=[{"type": "vcs", "url": "https://github.com/Tessil/robin-map"}]),
    ]


def build_bom():
    package = json.loads((REPO / "package.json").read_text(encoding="utf-8"))
    package_version = package["version"]
    vendored = vendored_components()

    openusd_source = component(
        "openusd-source", "OpenUSD", USD_VERSION,
        purl=f"pkg:github/PixarAnimationStudios/OpenUSD@{USD_TAG}",
        licenses=named_license(USD_LICENSE),
        description=("The source every shipped OpenUSD payload is built from: public tag "
                     f"{USD_TAG}, commit {USD_COMMIT} (\"Merge release v26.05\"), with the Unity "
                     "patch in this component's pedigree applied, monolithic, the same flags on "
                     "every platform."),
        properties=[("unity:sourceTag", USD_TAG), ("unity:sourceCommit", USD_COMMIT),
                    ("unity:patch", USD_PATCH), ("unity:patchSha256", sha256_of(USD_PATCH)),
                    ("unity:buildFlags", USD_BUILD_FLAGS),
                    ("unity:licenseText", "ThirdPartyNotices~/licenses/OpenUSD-LICENSE.txt"),
                    ("unity:noticeText", "ThirdPartyNotices~/licenses/OpenUSD-NOTICE.txt")],
        external_refs=USD_REFS, nested=vendored,
        pedigree={"patches": [usd_patch()]})

    components = [
        openusd_source,
        openusd_binary("usd-macos", "Runtime/Plugins/macOS/libusd_ms.dylib", "macOS universal"),
        openusd_binary("usd-windows", "Runtime/Plugins/x86_64/Windows/usd_rt.dll", "Windows x64"),
        openusd_binary("usd-linux", "Runtime/Plugins/x86_64/Linux/lib/libusd_ms.so", "Linux x64"),
        tbb_binary("tbb-macos", "Runtime/Plugins/macOS/libtbb.dylib", "macOS universal",
                   "oneTBB built from the v2020.3 source tag, as OpenUSD's build_usd.py pins it "
                   "on macOS."),
        tbb_binary("tbbmalloc-macos", "Runtime/Plugins/macOS/libtbbmalloc.dylib",
                   "macOS universal", "oneTBB scalable allocator from the same v2020.3 build."),
        tbb_binary("tbbmalloc-proxy-macos", "Runtime/Plugins/macOS/libtbbmalloc_proxy.dylib",
                   "macOS universal", "oneTBB allocator proxy from the same v2020.3 build."),
        tbb_binary("tbb-windows",
                   # Renamed from tbb.dll so the Windows loader cannot substitute the Editor's own
                   # copy; a payload built before that rename still carries the original name.
                   first_present("Runtime/Plugins/x86_64/Windows/tbb_usdrt.dll",
                                 "Runtime/Plugins/x86_64/Windows/tbb.dll"),
                   "Windows x64",
                   "oneTBB from the prebuilt tbb-2020.3-win.zip release, as OpenUSD's "
                   "build_usd.py pins it on Windows. Redistributed byte-for-byte, renamed only."),
        tbb_binary("tbb-linux", "Runtime/Plugins/x86_64/Linux/lib/libtbb.so.2", "Linux x64",
                   "oneTBB built from the v2020.3.1 point-release source tag, as OpenUSD's "
                   "build_usd.py pins it on Linux; the library reports 2020.3."),
        wrapper_binary("wrapper-macos", "Runtime/Plugins/macOS/UnityUSDToolkitNative.dylib",
                       "macOS universal", package_version),
        wrapper_binary("wrapper-windows", "Runtime/Plugins/x86_64/Windows/UnityUSDToolkitNative.dll",
                       "Windows x64", package_version),
        wrapper_binary("wrapper-linux", "Runtime/Plugins/x86_64/Linux/libUnityUSDToolkitNative.so",
                       "Linux x64", package_version),
    ]

    serial = uuid.uuid5(uuid.NAMESPACE_URL,
                        f"https://unity.com/sbom/{package['name']}/{package_version}")

    return {
        "$schema": "http://cyclonedx.org/schema/bom-1.6.schema.json",
        "bomFormat": "CycloneDX",
        "specVersion": "1.6",
        "serialNumber": f"urn:uuid:{serial}",
        "version": 1,
        "metadata": {
            "lifecycles": [{"phase": "build"}],
            "tools": {"components": [
                {"type": "application", "name": "Native~/generate_sbom.py", "version": "1"}]},
            "supplier": {"name": "Unity Technologies", "url": ["https://unity.com"]},
            "component": {
                "bom-ref": package["name"],
                "type": "library",
                "name": package["name"],
                "version": package_version,
                "description": " ".join(package.get("description", "").split())[:300],
                "licenses": named_license(UNITY_LICENSE),
            },
            "properties": [
                {"name": "unity:securityTicket", "value": "SECURITY-282834"},
                {"name": "unity:provenanceNote", "value": PROVENANCE_NOTE},
            ],
        },
        "components": components,
        "dependencies": [
            {"ref": package["name"],
             "dependsOn": [c["bom-ref"] for c in components if c["bom-ref"] != "openusd-source"]},
            {"ref": "openusd-source", "dependsOn": [c["bom-ref"] for c in vendored]},
            {"ref": "usd-macos", "dependsOn": ["openusd-source", "tbb-macos"]},
            {"ref": "usd-windows", "dependsOn": ["openusd-source", "tbb-windows"]},
            {"ref": "usd-linux", "dependsOn": ["openusd-source", "tbb-linux"]},
            {"ref": "wrapper-macos", "dependsOn": ["usd-macos", "tbb-macos"]},
            {"ref": "wrapper-windows", "dependsOn": ["usd-windows", "tbb-windows"]},
            {"ref": "wrapper-linux", "dependsOn": ["usd-linux", "tbb-linux"]},
        ],
    }


def main():
    OUTPUT.write_text(json.dumps(build_bom(), indent=2) + "\n", encoding="utf-8")
    print(f"Wrote {OUTPUT.relative_to(REPO)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
