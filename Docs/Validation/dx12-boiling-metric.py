import sys, struct, glob, os

# DX12 pass-graph migration — regime-(b) BOILING metric helper (W3).
# Usage: python dx12-boiling-metric.py <runDir1> [<runDir2> ...]
#   Each runDir holds frame_NN.bmp from the motion-dump harness (BALLISTIC_DX12_GI_MOTION_DUMP, temporal
#   ACTIVE = NO BALLISTIC_DETERMINISTIC). Reports each run's boiling scalar + the cross-run band (mean/sigma).
# See dx12-noise-floor.json (RegimeB_BoilingBand) for the measured values + the gate recommendation.
#
# Boiling metric for the regime-(b) temporal-stability oracle.
# Reads a directory of frame_NN.bmp dumped by the motion harness (temporal ACTIVE, play-mode camera motion)
# and computes the BOILING metric = mean abs delta of consecutive frames, averaged over all consecutive pairs.
# A stable temporal chain -> deltas decay toward 0; a boiling chain -> deltas stay high. We report the
# run's single scalar (mean over pairs) plus the per-pair series so the band's variance is meaningful.

def read_bmp(path):
    with open(path, 'rb') as f:
        data = f.read()
    assert data[0:2] == b'BM', "not BMP"
    pixoff = struct.unpack('<I', data[10:14])[0]
    w = struct.unpack('<i', data[18:22])[0]
    h = struct.unpack('<i', data[22:26])[0]
    bpp = struct.unpack('<H', data[28:30])[0]
    return data, pixoff, w, abs(h), bpp

def frame_delta(pa, pb):
    da, oa, wa, ha, ba = read_bmp(pa)
    db, ob, wb, hb, bb = read_bmp(pb)
    bypp = ba // 8
    rowsize = ((ba * wa + 31) // 32) * 4
    n = 0; tot = 0.0; mx = 0
    for y in range(0, ha, 2):          # sample every 2nd row/col for speed
        ra = oa + y * rowsize; rb = ob + y * rowsize
        for x in range(0, wa, 2):
            pa_ = ra + x * bypp; pb_ = rb + x * bypp
            d = abs(da[pa_] - db[pb_]) + abs(da[pa_+1] - db[pb_+1]) + abs(da[pa_+2] - db[pb_+2])
            tot += d; n += 1
            if d > mx: mx = d
    return tot / n / 3.0, mx              # mean abs per-channel delta (0..255), max per-pixel sum

def run_metric(dirpath):
    frames = sorted(glob.glob(os.path.join(dirpath, "frame_*.bmp")))
    if len(frames) < 2:
        print(f"{dirpath}: <2 frames"); return None
    series = []
    maxes = []
    for i in range(len(frames) - 1):
        m, mx = frame_delta(frames[i], frames[i+1])
        series.append(m); maxes.append(mx)
    mean = sum(series) / len(series)
    return mean, series, maxes, len(frames)

if __name__ == "__main__":
    # boil.py <dir1> [<dir2> ...]  — each dir is one run; reports each run's scalar + the cross-run band.
    run_means = []
    for d in sys.argv[1:]:
        r = run_metric(d)
        if r is None: continue
        mean, series, maxes, nf = r
        run_means.append(mean)
        print(f"{os.path.basename(d)}: boiling(meanAbsDelta/255)={mean:.6f}  "
              f"perPair=[{', '.join(f'{s:.4f}' for s in series)}]  maxPairDelta={max(maxes)}")
    if len(run_means) >= 2:
        n = len(run_means)
        mu = sum(run_means) / n
        var = sum((x - mu) ** 2 for x in run_means) / n      # population variance over runs
        sd = var ** 0.5
        print(f"\nCROSS-RUN BAND over {n} runs: mean={mu:.6f}  sigma={sd:.6f}  "
              f"min={min(run_means):.6f} max={max(run_means):.6f}  "
              f"gate(mean+3sigma)={mu + 3*sd:.6f}  gate(mean+2sigma)={mu + 2*sd:.6f}")
