# <Component> <version> — source scan

Copy this to `<component>-<version>.md` (lowercase component, the version exactly as
`Native~/dependency-sources/<platform>.tsv` pins it) and fill it in. Delete this line.
`verify_upstream_sources.py --require-scan` reads the record: keep the table labels and the
section headings as they are, replace every `<...>`, and give every count as a number.

| | |
| --- | --- |
| Component | <OpenUSD / oneTBB / MaterialX> |
| Version | <26.05> |
| Commit scanned | <full sha, the one pinned in dependency-sources; `-` where the row pins none, as MaterialX's does> |
| Source | <clone URL> |
| Scanned by | <name> |
| Date | <YYYY-MM-DD> |
| Tool | <Cycode CLI x.y.z, SAST> |
| Patches covered | <sha256 of each Native~/patches file applied to this commit before the scan, or none> |
| Archive scanned | <full sha256 of each pinned archive this record covers (oneTBB, MaterialX: every platform's row), or - for a clone> |

## Command

```bash
<the exact command, including the path scanned>
```

## Result

<Counts by severity. "Zero findings" is a result — say so explicitly rather than leaving the
section empty.>

| Severity | Count | Kept open |
| --- | --- | --- |
| Critical | 0 | 0 |
| High | | |
| Medium | | |
| Low | | |

## Findings kept open

<One entry per finding not fixed, with the rule, the file and line, and why it is acceptable in
this build. Only Medium and Low findings can be kept open here: a Critical or High one fails the
gate until it is fixed, or AppSec grants an exception (record the ticket here and in the table).
A finding in code this package does not compile can be a reason — but check before claiming it:
OpenUSD is built `--no-imaging --no-python` with MaterialX on, and `Native~/src/UsdDeflate.c`
compiles libdeflate from `pxr/imaging/plugin/hioOpenEXR/OpenEXR/deflate` into the wrapper, so
not everything under `pxr/imaging` is out of the build. If there are none, say none.>

## Coverage gaps

<What the scan could not reach, and why. Generated code, vendored sub-trees, anything excluded.
For OpenUSD, say whether `pxr/imaging/plugin/hioOpenEXR/OpenEXR/deflate` was scanned; for
MaterialX, whether `source/MaterialXFormat/External/PugiXML` was.>

## Conclusion

<Whether this version is acceptable to compile and ship, and who decided.>
