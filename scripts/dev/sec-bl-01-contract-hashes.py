r"""SEC-BL-01 WP-7A §2: reproduce both historical hash methods of the D-R contract's §1-§20 (read-only).

Method H-WP0 ("4a8bd1b1..."): the accepted WP-0 file bytes, i.e. the current file with the A-1 status row
(line 15, "| Amendments | ...") removed and everything from the separator before "## 21." removed, ending with the
final newline of §20's last line. Method H-AWK ("3549e3ba..."): `awk 'NR==15{next} /^## 21\./{exit} {print}'`,
i.e. the same bytes plus the "\n---\n\n" separator that precedes "## 21.".
"""
import hashlib
import sys

path = sys.argv[1] if len(sys.argv) > 1 else "docs/security/SEC_BL_01_DR_CONTRACT_CLOSURE.md"
data = open(path, "rb").read()
if b"\r" in data:
    sys.exit("CR bytes present: refusing (no normalization is applied)")
starts = [0]
for i, c in enumerate(data):
    if c == 0x0A:
        starts.append(i + 1)
line15 = (starts[14], starts[15])          # bytes of line 15 including its newline
s21 = data.index(b"\n## 21.") + 1           # first byte of the "## 21." line
sep = data.rindex(b"\n---\n", 0, s21) + 1  # first byte of the "---" separator line before §21
end_wp0 = sep - 1                           # §20 ends with "certification.\n" followed by one blank line
prefix = data[:line15[0]]
awk = prefix + data[line15[1]:s21]
wp0 = prefix + data[line15[1]:end_wp0]
print(f"file bytes={len(data)} sha256={hashlib.sha256(data).hexdigest()}")
print(f"excluded line 15: bytes [{line15[0]},{line15[1]})")
print(f"H-WP0: bytes [0,{line15[0]}) + [{line15[1]},{end_wp0}) len={len(wp0)} sha256={hashlib.sha256(wp0).hexdigest()}")
print(f"H-AWK: bytes [0,{line15[0]}) + [{line15[1]},{s21}) len={len(awk)} sha256={hashlib.sha256(awk).hexdigest()}")
print(f"H-AWK minus H-WP0 = {awk[len(wp0):]!r} (bytes [{end_wp0},{s21}))")
assert awk.startswith(wp0)
# Per-amendment sections §21..: bytes from the "## N." line up to (excluding) the next "## " heading line.
heads = [i for i in range(len(data)) if (i == 0 or data[i - 1] == 0x0A) and data.startswith(b"## ", i)]
for n, h in enumerate(heads):
    title = data[h:data.index(b"\n", h)]
    if not title[3:6].rstrip(b".").isdigit() or int(title[3:6].rstrip(b".")) < 21:
        continue
    end = heads[n + 1] if n + 1 < len(heads) else len(data)
    print(f"section {title[3:6].decode().rstrip('.')}: bytes [{h},{end}) sha256={hashlib.sha256(data[h:end]).hexdigest()}")
print(f"status row (line 15): sha256={hashlib.sha256(data[line15[0]:line15[1]]).hexdigest()}")
