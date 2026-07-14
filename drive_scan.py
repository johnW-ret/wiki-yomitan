import json, os, shutil, sys, time, zipfile
import gdown

SCRATCH = os.path.dirname(os.path.abspath(__file__))
TARGETS = ('周溝', '周濠')
MIN_FREE_GB = 6

files = [(i, p) for i, p in json.load(open(os.path.join(SCRATCH, 'drive_files.json')))
         if not p.startswith('StarterPack/')]
results = {}
log = open(os.path.join(SCRATCH, 'drive_scan.log'), 'w', buffering=1)

def scan_zip(path):
    hits = []
    with zipfile.ZipFile(path) as z:
        for name in z.namelist():
            if 'term_bank' not in name:
                continue
            try:
                bank = json.loads(z.read(name))
            except Exception:
                continue
            for r in bank:
                if isinstance(r, list) and r and r[0] in TARGETS:
                    gloss = json.dumps(r[5], ensure_ascii=False)[:200] if len(r) > 5 else ''
                    hits.append((r[0], r[1] if len(r) > 1 else '', gloss))
    return hits

for idx, (fid, path) in enumerate(files):
    name = os.path.basename(path)
    free_gb = shutil.disk_usage('/System/Volumes/Data').free / 1e9
    if free_gb < MIN_FREE_GB:
        log.write(f'ABORT: only {free_gb:.1f}GB free\n')
        break
    dest = os.path.join(SCRATCH, 'dl_' + name)
    try:
        gdown.download(id=fid, output=dest, quiet=True)
        size_mb = os.path.getsize(dest) / 1e6
        hits = scan_zip(dest)
        results[path] = hits
        log.write(f'[{idx+1}/{len(files)}] {name} ({size_mb:.0f}MB): '
                  + (f'HITS {hits}\n' if hits else 'no\n'))
    except Exception as e:
        results[path] = f'ERROR: {e}'
        log.write(f'[{idx+1}/{len(files)}] {name}: ERROR {e}\n')
    finally:
        if os.path.exists(dest):
            os.remove(dest)
    time.sleep(2)

json.dump(results, open(os.path.join(SCRATCH, 'drive_scan_results.json'), 'w'), ensure_ascii=False)
hitcount = sum(1 for v in results.values() if isinstance(v, list) and v)
errcount = sum(1 for v in results.values() if isinstance(v, str))
log.write(f'DONE: {len(results)} scanned, {hitcount} with hits, {errcount} errors\n')
print(f'DONE: {len(results)} scanned, {hitcount} with hits, {errcount} errors')
