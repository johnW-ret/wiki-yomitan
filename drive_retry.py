import json, os, shutil, time, zipfile
import gdown

SCRATCH = os.path.dirname(os.path.abspath(__file__))
TARGETS = ('周溝', '周濠')
MIN_FREE_GB = 6
PASS_SLEEP = 60 * 60
MAX_HOURS = 20

all_files = dict((p, i) for i, p in json.load(open(os.path.join(SCRATCH, 'drive_files.json'))))
results = json.load(open(os.path.join(SCRATCH, 'drive_scan_results.json')))
pending = [p for p, v in results.items() if isinstance(v, str)]
log = open(os.path.join(SCRATCH, 'drive_retry.log'), 'w', buffering=1)

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

start = time.time()
npass = 0
while pending and time.time() - start < MAX_HOURS * 3600:
    npass += 1
    log.write(f'--- pass {npass}: {len(pending)} pending ---\n')
    still = []
    for path in pending:
        if shutil.disk_usage('/System/Volumes/Data').free / 1e9 < MIN_FREE_GB:
            log.write('ABORT: low disk\n')
            still += pending[pending.index(path):]
            break
        name = os.path.basename(path)
        dest = os.path.join(SCRATCH, 'dl_' + name)
        try:
            gdown.download(id=all_files[path], output=dest, quiet=True)
            hits = scan_zip(dest)
            results[path] = hits
            size_mb = os.path.getsize(dest) / 1e6
            log.write(f'{name} ({size_mb:.0f}MB): ' + (f'HITS {hits}\n' if hits else 'no\n'))
        except Exception as e:
            still.append(path)
            log.write(f'{name}: still failing ({str(e)[:60]})\n')
        finally:
            if os.path.exists(dest):
                os.remove(dest)
        time.sleep(30)
    pending = still
    json.dump(results, open(os.path.join(SCRATCH, 'drive_scan_results.json'), 'w'), ensure_ascii=False)
    if pending:
        log.write(f'sleeping {PASS_SLEEP}s before next pass\n')
        time.sleep(PASS_SLEEP)

hitcount = sum(1 for v in results.values() if isinstance(v, list) and v)
log.write(f'FINISHED: {len(results) - len(pending)} scanned, {hitcount} hits, {len(pending)} never succeeded\n')
for p in pending:
    log.write(f'  UNSCANNED: {p}\n')
print(f'FINISHED: hits={hitcount}, unscanned={len(pending)}')
