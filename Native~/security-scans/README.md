# Source scans of the libraries this package compiles

The code-signing review's rule for open-source code we build ourselves has two parts: scan the
source for security issues **for each version, before compiling it**, and sign the result if the
license allows. This directory is the first part's evidence. `BUILD_NOTES.md` explains why the
"we compile it ourselves" branch is the one that applies here.

Pinning a revision and scanning it answer different questions, and neither substitutes for the
other. `Native~/dependency-sources/<platform>.tsv` establishes that the source is the one upstream
published — it says nothing about what that source contains. That is what a scan is for.

## What needs a record

Every row in `Native~/dependency-sources/<platform>.tsv`, at the version pinned there. Today:

| Component | Version | Record |
| --- | --- | --- |
| OpenUSD | 26.05 | `openusd-26.05.md` (all platforms; must cover libdeflate, below) |
| oneTBB | 2020.3 | `onetbb-2020.3.md` (macOS, Windows) |
| oneTBB | 2020.3.1 | `onetbb-2020.3.1.md` (Linux) |
| MaterialX | 1.39.4 | `materialx-1.39.4.md` (all platforms; must cover its vendored pugixml) |

The record's file name is the row's component, lowercased, and its version:
`verify_upstream_sources.py` looks for `<component>-<version>.md` and nothing else.

The four libraries OpenUSD vendors into the monolithic build — `pxr/base/tf/pxrCLI11`,
`pxrDoubleConversion`, `pxrLZ4`, `pxrTslRobinMap` — live in the OpenUSD tree, so the OpenUSD scan
covers them. Note their versions in that record rather than giving them files of their own.

libdeflate 1.18 also lives in the OpenUSD tree, at `pxr/imaging/plugin/hioOpenEXR/OpenEXR/deflate`,
and is the one vendored library we compile that OpenUSD itself does not: OpenUSD is built
`--no-imaging`, but `Native~/src/UsdDeflate.c` compiles its decompressor into
`UnityUSDToolkitNative` on every platform, after `Native~/CMakeLists.txt` has checked each file it
uses against the v26.05 digests. A scan scoped to the directories the OpenUSD build compiles would
therefore miss it. The OpenUSD record has to name that directory as scanned, and say so under
"Coverage gaps" if it was not.

MaterialX vendors pugixml 1.9 (with MaterialX's own modifications) at
`source/MaterialXFormat/External/PugiXML`, compiled into `MaterialXFormat`. It is MaterialX's tree,
so the MaterialX record covers it; note its version there, as for OpenUSD's vendored libraries.

The gate asks for a record for every row that is not a patch, prebuilt or not. So Windows needs
`onetbb-2020.3.md` too, even though Intel's TBB there (shipped as `tbb_usdrt.dll`) is a prebuilt
binary, not source we compile. The Windows row pins the same v2020.3 commit as the macOS one, so
one record serves both, but it has to name both rows' archive digests (below). For Windows that
record vouches for the source Intel's binary was released from, not for the binary; the binary's
own evidence is Intel's Authenticode signature and the byte-identity of the shipped file with the
one in `tbb-2020.3-win.zip`, both recorded in `dependency-sources/windows.tsv`.

Two things tie a record to its row. "Commit scanned" has to equal the row's `git_commit` exactly,
and for a row that pins an archive the record has to contain that archive's full SHA-256
somewhere. The MaterialX row pins the source archive and no commit (`-`), so for it the digest is
what identifies the tree that was scanned.

## Running a scan

Cycode is Unity's scanner and already runs on this repository's pull requests (SAST, secrets and
vulnerable dependencies), but it sees only what is committed here — the upstream source is not.
So it has to be pointed at the dependency's own tree, before `build_usd.py` compiles it:

```bash
pip install cycode
cycode auth
cycode -v scan -t sast repository /opt/usd-26.05/src        # the verified OpenUSD clone
```

Scan the clone **after** `verify_upstream_sources.py --openusd-src` has passed on it. Scanning an
unverified tree measures whatever happened to be on the machine.

What is compiled is that commit **with the Unity patches in `Native~/patches` applied**, so scan
the patched tree — the `openusd-src` worktree `build_openusd.py` leaves inside the install — and
say so in the OpenUSD record, with the patch's SHA-256. A patch has no record of its own; it is
covered by the record of the tree it is applied to.

Two things are worth settling with AppSec (`#support-appsec-tools`) rather than assuming: whether
a Cycode SAST run over third-party source is what they want here or whether a different tool is
prescribed, and whether `ThirdPartyNotices~/sbom.cdx.json` can be ingested for CVE tracking, which
would cover known vulnerabilities as opposed to code patterns.

## What a record must contain

Enough for someone else to repeat the run and reach the same conclusion:

- Component, version, and the commit the scan ran against — the same commit
  `dependency-sources/<platform>.tsv` pins.
- Tool and version, the exact command, and the date.
- The findings: how many, at what severity, and for each one kept open, why it is acceptable in
  this build. "Zero findings" is a result and should be recorded as one.
- Anything the scan could not reach, said plainly.

## Recording it in the build

`build_openusd.py --require-scan` checks that a record exists for every pinned version before
anything is built, and the stamp it writes into the install root records whether it was used. A
stamp written without it makes the wrapper build print a warning: the payload can still be
produced, but nothing claims its source was scanned. A release build should use it:

```bash
python3 Native~/build_openusd.py --platform linux \
  --openusd-src /opt/usd-26.05/src --install /opt/usd-26.05/install --require-scan
```

The gate reads the record rather than only finding it. It fails a record that:

- still has template placeholders, or is missing a field of the header table;
- names a different component, version or commit (the full SHA) from the pin;
- does not name the SHA-256 of each patch applied to that commit — the scanned tree has to be the
  patched one;
- lacks a numeric count and kept-open figure for each of Critical, High, Medium and Low;
- keeps any Critical or High finding open — that takes a fix, or an exception AppSec grants;
- keeps Medium or Low findings open without saying why under "Findings kept open", or leaves the
  conclusion empty.

Passing the gate is still not the review. Someone has to read the record and agree with it.

Both the release and the stamp record it. `--check-stamp <install> --require-scan` fails an install
built without `--require-scan`. `Native~/CMakeLists.txt` uses that check to leave `.unscanned-build`
in a payload whose install was built without it, and `.yamato/pack.yml` requires a passing record
for every platform's pins and runs `generate_native_hashes.py --release`, which refuses a marked
payload. A local build or the integrity job only warns, so work goes on while a scan is outstanding;
the payload just cannot be packed.

## Status

**No scan has been run yet.** Cycode covers this repository's own code, not the upstream source,
so there is no record here for OpenUSD, oneTBB or MaterialX. The three committed payloads carry
`.unscanned-build` for that reason, and the pack job refuses them until they are rebuilt from
source with a passing record for each version.
