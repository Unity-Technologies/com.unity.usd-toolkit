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
import re
import uuid

REPO = pathlib.Path(__file__).resolve().parent.parent
OUTPUT = REPO / "ThirdPartyNotices~" / "sbom.cdx.json"

# --- what the payload was built from ------------------------------------------------------
USD_TAG = "v26.05"
USD_VERSION = "26.05"
USD_COMMIT = "2095faf"  # "Merge release v26.05"
USD_BUILD_FLAGS = (
    "--build-variant release --build-monolithic --no-python --no-imaging --no-usdview "
    "--no-examples --no-tutorials --no-tests --materialx "
    # usdMtlx links MaterialXCore and MaterialXFormat and nothing else, but MaterialX defaults
    # MATERIALX_BUILD_RENDER on, which builds the viewer's render back-ends. On Linux that wants
    # libXt and the configure step fails outright ("Error in building MaterialXRenderGlsl: Xt was
    # not found"); on macOS it would quietly succeed against NSOpenGL and leave the two platforms
    # with different recipes. Neither renderer is shipped - Unity draws the scene - so this is off,
    # which also keeps X11 out of the payload's link closure.
    #
    # One flag, and it has to stay one flag: MaterialX nests the whole render tree inside
    # if(MATERIALX_BUILD_RENDER) (CMakeLists.txt:473), so RENDER=OFF already skips
    # RENDER_PLATFORMS, RenderGlsl, RenderMsl, RenderHw and RenderOsl. Spelling the second
    # option out would need a quoted value with a space in it, and build_openusd.py:288 splits
    # these flags with str.split() rather than shlex, so the quotes do not survive.
    "--build-args MaterialX,-DMATERIALX_BUILD_RENDER=OFF"
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
    "component. MaterialX 1.39.4 is built by the same build_usd.py run from the upstream source "
    "archive whose SHA-256 is pinned on the MaterialX source component; that archive, like every "
    "pinned archive, is verified before build_usd.py runs and again after it finishes. "
    "The builds are not bit-reproducible: no SOURCE_DATE_EPOCH, "
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

# usdMtlx is compiled into the monolithic library, and MaterialX is built shared, so
# libusd_ms carries libMaterialXCore and libMaterialXFormat as DT_NEEDED and the package
# ships both. They are components in their own right, not an OpenUSD implementation detail.
MATERIALX_VERSION = "1.39.4"
MATERIALX_REFS = [{"type": "vcs",
                   "url": "https://github.com/AcademySoftwareFoundation/MaterialX"}]
# The archive build_usd.py builds MaterialX from; the same pin as the MaterialX row in
# Native~/dependency-sources/<platform>.tsv, which Native~/build_openusd.py enforces.
MATERIALX_ARCHIVE = (
    f"https://github.com/AcademySoftwareFoundation/MaterialX/archive/v{MATERIALX_VERSION}.zip")
MATERIALX_ARCHIVE_SHA256 = "3910b2498d7da6c8e5ac150b9e6a74e530626044309edb077829c024ed691e0e"
MATERIALX_BUILD_FLAGS = "--materialx --build-args MaterialX,-DMATERIALX_BUILD_RENDER=OFF"
# pugixml as MaterialX vendors it; PUGIXML_VERSION 190 in its pugixml.hpp.
PUGIXML_VERSION = "1.9"

# The MaterialX libraries usdMtlx reads at run time, installed beside every payload. Identical on
# all three platforms; Unity's .meta files are not part of the data.
MTLX_LIBRARIES = "lib/usd/usdMtlx/resources/libraries"
MTLX_LIBRARY_ROOTS = {
    "macOS universal": "Runtime/Plugins/macOS",
    "Windows x64": "Runtime/Plugins/x86_64/Windows",
    "Linux x64": "Runtime/Plugins/x86_64/Linux",
}

# The wrapper's ABI version is whatever the exporter reports, so it is read from there.
API_VERSION_SOURCE = "Native~/src/UsdExporter.cpp"


def sha256_of(relative_path):
    return hashlib.sha256((REPO / relative_path).read_bytes()).hexdigest()


def first_present(*relative_paths):
    """The first of these that exists, for a file whose name changed between payload builds."""
    for path in relative_paths:
        if (REPO / path).is_file():
            return path
    raise SystemExit("None of these shipped files exists: " + ", ".join(relative_paths))


def native_api_version():
    match = re.search(r"constexpr\s+int\s+kApiVersion\s*=\s*(\d+)\s*;",
                      (REPO / API_VERSION_SOURCE).read_text(encoding="utf-8"))
    if not match:
        raise SystemExit(f"No `constexpr int kApiVersion = N;` in {API_VERSION_SOURCE}")
    return match.group(1)


def tree_sha256(relative_dir):
    """SHA-256 of `sha256sum`-style lines ("<sha256>  ./<path>", sorted by path) for every file
    under the directory except Unity's .meta files; reproducible with
    `find . -type f ! -name '*.meta' | LC_ALL=C sort | xargs shasum -a 256 | shasum -a 256`."""
    root = REPO / relative_dir
    if not root.is_dir():
        raise SystemExit(f"Shipped directory missing: {relative_dir}")
    files = sorted((p for p in root.rglob("*") if p.is_file() and p.suffix != ".meta"),
                   key=lambda p: ("./" + p.relative_to(root).as_posix()).encode())
    lines = "".join(f"{hashlib.sha256(p.read_bytes()).hexdigest()}  ./"
                    f"{p.relative_to(root).as_posix()}\n" for p in files)
    return hashlib.sha256(lines.encode()).hexdigest(), len(files)


def spdx(identifier):
    return [{"license": {"id": identifier}}]


def named_license(name):
    return [{"license": {"name": name}}]


def component(bom_ref, name, version, *, shipped_path=None, purl=None, licenses=None,
              description=None, properties=(), external_refs=None, nested=None, pedigree=None,
              kind="library"):
    entry = {"bom-ref": bom_ref, "type": kind, "name": name, "version": version,
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


def tbb_binary(bom_ref, shipped_path, platform, description, version="2020.3"):
    """`version` is the source tag the binary was built from (Linux builds the v2020.3.1 point
    release, see Native~/dependency-sources/linux.tsv), not the 2020.3 the library reports."""
    return component(
        bom_ref, "oneTBB", version, shipped_path=shipped_path,
        purl=f"pkg:github/oneapi-src/oneTBB@v{version}",
        licenses=spdx("Apache-2.0"), description=description,
        properties=[("unity:platform", platform), ("unity:interfaceVersion", "11103"),
                    ("unity:licenseText", "ThirdPartyNotices~/licenses/oneTBB-LICENSE.txt")],
        external_refs=TBB_REFS)


def materialx_binary(bom_ref, shipped_path, platform, description):
    return component(
        bom_ref, "MaterialX", MATERIALX_VERSION, shipped_path=shipped_path,
        purl=f"pkg:github/AcademySoftwareFoundation/MaterialX@v{MATERIALX_VERSION}",
        licenses=spdx("Apache-2.0"), description=description,
        properties=[("unity:platform", platform),
                    ("unity:licenseText", "ThirdPartyNotices~/licenses/MaterialX-LICENSE.txt")],
        external_refs=MATERIALX_REFS)


def wrapper_binary(bom_ref, shipped_path, platform, package_version):
    return component(
        bom_ref, "Unity USD Toolkit native wrapper", package_version, shipped_path=shipped_path,
        licenses=named_license(UNITY_LICENSE),
        description=f"First-party native wrapper (Native~/src) built for {platform}.",
        properties=[("unity:platform", platform), ("unity:toolchain", TOOLCHAIN[platform]),
                    ("unity:nativeApiVersion", native_api_version())])


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
    """Third-party code that lives inside the OpenUSD source tree and ships even though it is not
    a separate file. All but libdeflate are linked into the monolithic library; libdeflate is not
    in any OpenUSD binary (its only user, hioOpenEXR, is not built under --no-imaging) but is
    compiled into the wrapper, so the wrappers, not openusd-source, depend on it."""
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
        component(
            "libdeflate", "libdeflate", "1.18", purl="pkg:github/ebiggers/libdeflate@v1.18",
            licenses=spdx("MIT"),
            description=("Vendored inside OpenUSD as pxr/imaging/plugin/hioOpenEXR/OpenEXR/deflate, "
                         "but in no OpenUSD binary: hioOpenEXR is not built under --no-imaging. "
                         "Its decompressor is compiled into the wrapper (Native~/src/UsdDeflate.c) "
                         "as the inflate the image codecs use on every platform; "
                         "Native~/CMakeLists.txt checks the 29 files against recorded digests."),
            properties=[("unity:licenseText", "ThirdPartyNotices~/licenses/libdeflate-LICENSE.txt"),
                        ("unity:sourceDigests", "Native~/CMakeLists.txt (_RUSD_DEFLATE_FILES)")],
            external_refs=[{"type": "vcs", "url": "https://github.com/ebiggers/libdeflate"}]),
    ]


def materialx_source():
    """The MaterialX source archive, with the pugixml it compiles into libMaterialXFormat."""
    pugixml = component(
        "pugixml", "pugixml", PUGIXML_VERSION, purl=f"pkg:github/zeux/pugixml@v{PUGIXML_VERSION}",
        licenses=spdx("MIT"),
        description=("Vendored inside MaterialX as source/MaterialXFormat/External/PugiXML and "
                     "compiled into libMaterialXFormat (PUGIXML_VERSION 190). Its MIT notice is "
                     "at the end of that pugixml.hpp."),
        external_refs=[{"type": "vcs", "url": "https://github.com/zeux/pugixml"}])
    return component(
        "materialx-source", "MaterialX", MATERIALX_VERSION,
        purl=f"pkg:github/AcademySoftwareFoundation/MaterialX@v{MATERIALX_VERSION}",
        licenses=spdx("Apache-2.0"),
        description=(f"The source every shipped MaterialX library is built from: the upstream "
                     f"v{MATERIALX_VERSION} archive, pinned by SHA-256 and verified before and "
                     "after build_usd.py, built by that run with render back-ends off."),
        properties=[("unity:sourceArchive", MATERIALX_ARCHIVE),
                    ("unity:sourceArchiveSha256", MATERIALX_ARCHIVE_SHA256),
                    ("unity:buildFlags", MATERIALX_BUILD_FLAGS),
                    ("unity:licenseText", "ThirdPartyNotices~/licenses/MaterialX-LICENSE.txt")],
        external_refs=MATERIALX_REFS + [
            {"type": "distribution", "url": MATERIALX_ARCHIVE,
             "hashes": [{"alg": "SHA-256", "content": MATERIALX_ARCHIVE_SHA256}]}],
        nested=[pugixml])


def mtlx_libraries():
    """usdMtlx's resource data, one component for the identical tree each platform ships."""
    digests = {platform: tree_sha256(f"{root}/{MTLX_LIBRARIES}")
               for platform, root in MTLX_LIBRARY_ROOTS.items()}
    properties = []
    for platform, root in MTLX_LIBRARY_ROOTS.items():
        digest, count = digests[platform]
        properties += [("unity:shippedPath", f"{root}/{MTLX_LIBRARIES}"),
                       ("unity:treeSha256", f"{platform}: {digest} ({count} files)")]
    return component(
        "materialx-libraries", "MaterialX data libraries", MATERIALX_VERSION, kind="data",
        licenses=[{"expression": "Apache-2.0 AND BSD-3-Clause"}],
        description=("The MaterialX standard data libraries (stdlib, pbrlib, bxdf, cmlib, lights, "
                     "nprlib, targets: .mtlx definitions and GLSL/MSL/OSL/MDL shader source) that "
                     "build_usd.py installs for usdMtlx, from the same MaterialX build. Mostly "
                     "\"Contributors to the MaterialX Project\" under Apache-2.0; the mdl/materialx "
                     "files are NVIDIA CORPORATION, Apache-2.0; stdlib/genglsl/lib/mx_noise.glsl "
                     "and mx_hsv.glsl, the stdlib/genosl/include headers and part of "
                     "mdl/materialx/noise.mdl are derived from Open Shading Language (Sony "
                     "Pictures Imageworks / Contributors to the OSL project), BSD-3-Clause. "
                     "unity:treeSha256 is the SHA-256 of sorted `shasum -a 256` lines over the "
                     "tree, .meta files excluded."),
        properties=properties + [
            ("unity:licenseText", "ThirdPartyNotices~/licenses/MaterialX-LICENSE.txt")],
        external_refs=MATERIALX_REFS)


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

    mtlx_source = materialx_source()

    components = [
        openusd_source,
        mtlx_source,
        mtlx_libraries(),
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
        materialx_binary("materialx-core-linux",
                         "Runtime/Plugins/x86_64/Linux/lib/libMaterialXCore.so.1", "Linux x64",
                         "MaterialX data model, linked by usdMtlx inside libusd_ms."),
        materialx_binary("materialx-format-linux",
                         "Runtime/Plugins/x86_64/Linux/lib/libMaterialXFormat.so.1", "Linux x64",
                         "MaterialX document reader/writer from the same v1.39.4 build."),
        materialx_binary("materialx-core-windows",
                         "Runtime/Plugins/x86_64/Windows/MaterialXCore.dll", "Windows x64",
                         "MaterialX data model, imported by usdMtlx inside usd_rt.dll."),
        materialx_binary("materialx-format-windows",
                         "Runtime/Plugins/x86_64/Windows/MaterialXFormat.dll", "Windows x64",
                         "MaterialX document reader/writer from the same v1.39.4 build."),
        tbb_binary("tbb-linux", "Runtime/Plugins/x86_64/Linux/lib/libtbb.so.2", "Linux x64",
                   "oneTBB built from the v2020.3.1 point-release source tag, as OpenUSD's "
                   "build_usd.py pins it on Linux; the library reports 2020.3.",
                   version="2020.3.1"),
        wrapper_binary("wrapper-macos", "Runtime/Plugins/macOS/UnityUSDToolkitNative.dylib",
                       "macOS universal", package_version),
        wrapper_binary("wrapper-windows", "Runtime/Plugins/x86_64/Windows/UnityUSDToolkitNative.dll",
                       "Windows x64", package_version),
        wrapper_binary("wrapper-linux", "Runtime/Plugins/x86_64/Linux/libUnityUSDToolkitNative.so",
                       "Linux x64", package_version),
    ]

    # The macOS payload carries MaterialX from the first build with --materialx on; one built
    # before that has no MaterialX to list.
    macos_materialx = []
    for ref, path, description in (
            ("materialx-core-macos", "Runtime/Plugins/macOS/libMaterialXCore.1.dylib",
             "MaterialX data model, linked by usdMtlx inside libusd_ms."),
            ("materialx-format-macos", "Runtime/Plugins/macOS/libMaterialXFormat.1.dylib",
             "MaterialX document reader/writer from the same v1.39.4 build.")):
        if (REPO / path).is_file():
            components.append(materialx_binary(ref, path, "macOS universal", description))
            macos_materialx.append(ref)

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
             "dependsOn": [c["bom-ref"] for c in components
                           if c["bom-ref"] not in ("openusd-source", "materialx-source")]},
            # libdeflate sits in the OpenUSD tree but no OpenUSD binary contains it.
            {"ref": "openusd-source",
             "dependsOn": [c["bom-ref"] for c in vendored if c["bom-ref"] != "libdeflate"]},
            {"ref": "materialx-source", "dependsOn": ["pugixml"]},
            {"ref": "materialx-libraries", "dependsOn": ["materialx-source"]},
            {"ref": "usd-macos", "dependsOn": ["openusd-source", "tbb-macos"] + macos_materialx},
            {"ref": "usd-windows", "dependsOn": ["openusd-source", "tbb-windows",
                                                "materialx-core-windows", "materialx-format-windows"]},
            {"ref": "usd-linux", "dependsOn": ["openusd-source", "tbb-linux",
                                              "materialx-core-linux", "materialx-format-linux"]},
            {"ref": "materialx-core-linux", "dependsOn": ["materialx-source"]},
            {"ref": "materialx-format-linux",
             "dependsOn": ["materialx-source", "pugixml", "materialx-core-linux"]},
            {"ref": "materialx-core-windows", "dependsOn": ["materialx-source"]},
            {"ref": "materialx-format-windows",
             "dependsOn": ["materialx-source", "pugixml", "materialx-core-windows"]},
        ] + ([{"ref": "materialx-core-macos", "dependsOn": ["materialx-source"]}]
             if "materialx-core-macos" in macos_materialx else []) + (
            [{"ref": "materialx-format-macos",
              "dependsOn": ["materialx-source", "pugixml", "materialx-core-macos"]}]
            if "materialx-format-macos" in macos_materialx else []) + [
            # The wrapper compiles libdeflate's decompressor in (Native~/src/UsdDeflate.c).
            {"ref": "wrapper-macos", "dependsOn": ["usd-macos", "tbb-macos", "libdeflate"]},
            {"ref": "wrapper-windows", "dependsOn": ["usd-windows", "tbb-windows", "libdeflate"]},
            {"ref": "wrapper-linux", "dependsOn": ["usd-linux", "tbb-linux", "libdeflate"]},
        ],
    }


def main():
    OUTPUT.write_text(json.dumps(build_bom(), indent=2) + "\n", encoding="utf-8")
    print(f"Wrote {OUTPUT.relative_to(REPO)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
