"""Independent watertightness check of a binary STL (numpy only, no code shared with the converter).

Corners are merged only when bit-identical (as MeshLab and FreeCAD do when loading). Reports open edges (one triangle),
non-manifold edges (more than two), edges used twice in the same direction (inconsistent orientation), folded edges
(two triangles lying flat on each other, a flap without thickness), triangles
without area and the number of connected pieces.

usage: python tools/meshcheck.py file.stl [file2.stl ...]
"""
import sys
import numpy as np


def read_binary_stl(path):
    data = np.fromfile(path, dtype=np.uint8)
    n = int(np.frombuffer(data[80:84].tobytes(), dtype='<u4')[0])
    rec = np.dtype([('n', '<f4', 3), ('v', '<f4', (3, 3)), ('attr', '<u2')])
    tris = np.frombuffer(data[84:84 + n * 50].tobytes(), dtype=rec)['v']
    return tris  # (n, 3, 3) float32


def check(path):
    tris = read_binary_stl(path)
    n = len(tris)
    pts, idx = np.unique(tris.reshape(-1, 3), axis=0, return_inverse=True)
    f = idx.reshape(-1, 3).astype(np.int64)
    v = pts.astype(np.float64)
    area2 = np.linalg.norm(np.cross(v[f[:, 1]] - v[f[:, 0]], v[f[:, 2]] - v[f[:, 0]]), axis=1)
    repeated = (f[:, 0] == f[:, 1]) | (f[:, 1] == f[:, 2]) | (f[:, 0] == f[:, 2])
    flat = int(np.sum(area2 == 0))

    # directed edges a->b of every triangle
    a = np.concatenate([f[:, 0], f[:, 1], f[:, 2]])
    b = np.concatenate([f[:, 1], f[:, 2], f[:, 0]])
    ok = a != b
    a, b = a[ok], b[ok]
    m = len(pts)
    und = np.minimum(a, b) * m + np.maximum(a, b)
    _, counts = np.unique(und, return_counts=True)
    open_edges = int(np.sum(counts == 1))
    non_manifold = int(np.sum(counts > 2))
    _, dcounts = np.unique(a * m + b, return_counts=True)
    same_direction = int(np.sum(dcounts > 1))

    # folded edges: the two triangles on an edge lie flat on each other (normals opposite within about 1 degree)
    nrm = np.cross(v[f[:, 1]] - v[f[:, 0]], v[f[:, 2]] - v[f[:, 0]])
    ln = np.linalg.norm(nrm, axis=1)
    nrm = nrm / np.where(ln > 0, ln, 1)[:, None]
    tri_e = np.concatenate([np.arange(n)] * 3)[ok]
    o = np.argsort(und, kind='stable')
    us, ts = und[o], tri_e[o]
    pair = np.where(us[1:] == us[:-1])[0]
    single = np.ones(len(pair), bool)
    single &= (pair + 2 >= len(us)) | (us[np.minimum(pair + 2, len(us) - 1)] != us[pair])
    single &= (pair == 0) | (us[np.maximum(pair - 1, 0)] != us[pair])
    p = pair[single]
    t1, t2 = ts[p], ts[p + 1]
    dots = np.sum(nrm[t1] * nrm[t2], axis=1)
    folded = int(np.sum((dots < -0.9998) & (ln[t1] > 0) & (ln[t2] > 0)))

    # connected pieces over shared edges (union-find on triangles)
    parent = np.arange(n)

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x
    tri_of = np.concatenate([np.arange(n)] * 3)[ok]
    order = np.argsort(und, kind='stable')
    su, st = und[order], tri_of[order]
    for i in range(1, len(su)):
        if su[i] == su[i - 1]:
            ra, rb = find(st[i]), find(st[i - 1])
            if ra != rb:
                parent[ra] = rb
    pieces = len({find(i) for i in range(n)})

    closed = open_edges == 0 and non_manifold == 0
    print(f"== {path}")
    print(f"  triangles {n:,}, distinct corners {m:,}")
    print(f"  open edges {open_edges:,}, non-manifold edges {non_manifold:,}, same-direction edges {same_direction:,}, folded edges {folded:,}")
    print(f"  triangles with a repeated corner {int(np.sum(repeated)):,}, without area {flat:,}, pieces {pieces:,}")
    print(f"  -> {'CLOSED' if closed else 'NOT closed'}" + (f" (but {folded:,} folded edges: the surface lies flat on itself there)" if folded else ""))


if __name__ == '__main__':
    for p in sys.argv[1:]:
        check(p)
