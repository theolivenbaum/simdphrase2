#!/usr/bin/env python3
import os, re, subprocess, glob

OUT = "/tmp/bench_runs"
REPO = "/home/user/simdphrase2"

# ordered oldest->newest first-parent commits
commits = subprocess.check_output(
    ["git", "-C", REPO, "log", "--reverse", "--first-parent", "--format=%h\t%s",
     "4e6e43e^..HEAD"], text=True).strip().splitlines()

UNIT = {"ns": 1e-3, "us": 1.0, "µs": 1.0, "ms": 1e3, "s": 1e6}

def to_us(val, unit):
    return float(val.replace(",", "")) * UNIT[unit]

# method label -> short key
METHODS = {
    "SimdPhrase_Search_SingleTerm": "SingleTerm",
    "SimdPhrase_Search_Phrase_Len2": "Phrase2",
    "SimdPhrase_Search_Phrase_Len3": "Phrase3",
    "Lucene_Search_SingleTerm": "SingleTerm",
    "Lucene_Search_Phrase_Len2": "Phrase2",
    "Lucene_Search_Phrase_Len3": "Phrase3",
}

row_re = re.compile(r"^\|\s*([A-Za-z0-9_]+)\s*\|")

def parse_log(path):
    """Return {method_key: (mean_us, alloc_str)}."""
    res = {}
    if not os.path.exists(path):
        return res, "MISSING"
    txt = open(path, encoding="utf-8", errors="replace").read()
    if "DID_NOT_COMPILE" in txt:
        return res, "DID_NOT_COMPILE"
    for line in txt.splitlines():
        m = row_re.match(line)
        if not m:
            continue
        method = m.group(1)
        if method not in METHODS:
            continue
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        # cells[0]=Method cells[1]=N cells[2]=Mean ... last=Allocated
        mean_cell = cells[2]
        mm = re.match(r"([0-9.,]+)\s*([a-zµ]+)", mean_cell)
        if mm:
            mean = to_us(mm.group(1), mm.group(2))
        elif "NA" in mean_cell:
            mean = None
        else:
            continue
        alloc = cells[-1]
        res[METHODS[method]] = (mean, alloc)
    return res, "OK"

print("# SIMD benchmark (SimdPhraseBenchmark, N=10000, ShortRun) — mean in microseconds (us)\n")
hdr = f"| {'commit':8} | {'SingleTerm':>12} | {'Phrase2':>12} | {'Phrase3':>12} | status | subject |"
print(hdr)
print("|" + "-"*10 + "|" + "-"*14 + "|" + "-"*14 + "|" + "-"*14 + "|--------|---------|")

def fmt(v):
    if v is None:
        return "NA"
    return f"{v:.2f}"

for line in commits:
    h, subj = line.split("\t", 1)
    res, status = parse_log(f"{OUT}/{h}.simd.log")
    st = res.get("SingleTerm", (None,))[0]
    p2 = res.get("Phrase2", (None,))[0]
    p3 = res.get("Phrase3", (None,))[0]
    stattxt = status if status != "OK" else ("OK" if res else "NORESULT")
    print(f"| {h:8} | {fmt(st):>12} | {fmt(p2):>12} | {fmt(p3):>12} | {stattxt} | {subj[:48]} |")

print("\n# Lucene.Net (current HEAD, N=10000, ShortRun) — mean in microseconds (us)\n")
lres, lstatus = parse_log(f"{OUT}/lucene.simd.log")
print(f"SingleTerm={fmt(lres.get('SingleTerm',(None,))[0])}  "
      f"Phrase2={fmt(lres.get('Phrase2',(None,))[0])}  "
      f"Phrase3={fmt(lres.get('Phrase3',(None,))[0])}  status={lstatus}")
