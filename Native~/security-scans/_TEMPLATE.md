# <Component> <version> — source scan

Copy this to `<component>-<version>.md` (lowercase component, the version exactly as
`Native~/dependency-sources/<platform>.tsv` pins it) and fill it in. Delete this line.

| | |
| --- | --- |
| Component | <OpenUSD / oneTBB> |
| Version | <26.05> |
| Commit scanned | <full sha, the one pinned in dependency-sources> |
| Source | <clone URL> |
| Scanned by | <name> |
| Date | <YYYY-MM-DD> |
| Tool | <Cycode CLI x.y.z, SAST> |

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
this build — for example, code on a path this package does not compile in, given the
`--no-imaging --no-python --no-materialx` flags. If there are none, say none.>

## Coverage gaps

<What the scan could not reach, and why. Generated code, vendored sub-trees, anything excluded.>

## Conclusion

<Whether this version is acceptable to compile and ship, and who decided.>
