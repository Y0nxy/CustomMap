using System;
using System.Collections;
using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;

namespace CustomMap
{
    [RequireComponent(typeof(BoxCollider))]
    [RequireComponent(typeof(MeshRenderer))]
    public class VoronoiShatter : MonoBehaviourPunCallbacks
    {
        // ── Inspector ─────────────────────────────────────────────────────────
        [Header("Shards")]
        public GameObject shardPrefab;
        public PhysicsMaterial physicsMaterial;
        public int shardLayer = 10;
        public float totalMass = 20f;
        public float cellInset = 0f;

        [Header("Impact")]
        public float impactThreshold = 5f;
        public float minExplodeImpulse = 0f;
        public float maxExplodeImpulse = float.PositiveInfinity;
        public float perShardImpulseFraction = 0.3f;
        public float maxShardVelocity = float.PositiveInfinity;

        [Header("Misc")]
        public bool resetOnReload = true;
        public Transform parentObject;

        [Tooltip("Unique root PhotonView ID. Set from your mod loader before Awake.")]
        public int rootViewID = 0;

        // ── Runtime ───────────────────────────────────────────────────────────
        private BoxCollider col;
        private MeshRenderer meshRend;
        private Rigidbody body;
        private Material mat;
        private bool shattered;
        private bool readyForCollision;

        private struct Pending { public GameObject go; public Vector3 force; public Vector3 pos; }
        private readonly List<Pending> pending = new List<Pending>();
        private readonly List<GameObject> cells = new List<GameObject>();
        private readonly Dictionary<int, int> allocatedIDs = new Dictionary<int, int>();

        // ── Lifecycle ─────────────────────────────────────────────────────────
        void Awake()
        {
            col = GetComponent<BoxCollider>();
            meshRend = GetComponent<MeshRenderer>();
            body = GetComponent<Rigidbody>();
            mat = meshRend.sharedMaterial;

            PhotonView pv = GetComponent<PhotonView>() ?? gameObject.AddComponent<PhotonView>();
            if (rootViewID > 0) pv.ViewID = rootViewID;
            else Debug.LogError($"[VoronoiShatter] rootViewID not set on '{name}'!");
        }

        public override void OnEnable()
        {
            base.OnEnable();
            readyForCollision = false;
            StartCoroutine(AllowCollision());
        }

        IEnumerator AllowCollision()
        {
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            readyForCollision = true;
        }

        public void SetRootViewID(int id)
        {
            rootViewID = id;
            PhotonView pv = GetComponent<PhotonView>() ?? gameObject.AddComponent<PhotonView>();
            pv.ViewID = id;
        }

        // ── Collision ─────────────────────────────────────────────────────────
        void OnCollisionEnter(Collision c)
        {
            if (!readyForCollision || shattered) return;
            if (c.impulse.magnitude < impactThreshold) return;
            shattered = true;

            Vector3 pt = c.contacts[0].point;
            Vector3 imp = c.impulse;
            int sd = UnityEngine.Random.Range(0, int.MaxValue);

            if (!PhotonNetwork.IsConnected || !PhotonNetwork.InRoom)
            { StartCoroutine(DoShatter(pt, imp, sd)); return; }

            photonView.RPC(nameof(RPC_Shatter), RpcTarget.All,
                pt.x, pt.y, pt.z, imp.x, imp.y, imp.z, sd);
        }

        [PunRPC]
        void RPC_Shatter(float px, float py, float pz,
                         float ix, float iy, float iz, int seed)
        {
            if (shattered && cells.Count > 0) return;
            shattered = true;
            StartCoroutine(DoShatter(new Vector3(px, py, pz), new Vector3(ix, iy, iz), seed));
        }

        // ── Shatter sequence ──────────────────────────────────────────────────
        IEnumerator DoShatter(Vector3 contactWorld, Vector3 impulse, int seed)
        {
            col.enabled = false;
            meshRend.enabled = false;
            if (body != null) body.isKinematic = true;
            yield return new WaitForFixedUpdate();

            Physics.IgnoreLayerCollision(shardLayer, shardLayer, true);

            pending.Clear();
            BuildShards(contactWorld, impulse, seed);

            yield return new WaitForFixedUpdate();
            foreach (var p in pending) if (p.go) p.go.SetActive(true);

            yield return new WaitForFixedUpdate();
            foreach (var p in pending)
            {
                if (!p.go) continue;
                var rb = p.go.GetComponent<Rigidbody>();
                if (rb && !rb.isKinematic)
                    rb.AddForceAtPosition(p.force, p.pos, ForceMode.Impulse);
            }
            pending.Clear();

            yield return new WaitForSeconds(0.25f);
            Physics.IgnoreLayerCollision(shardLayer, shardLayer, false);

            if (PhotonNetwork.IsConnected && PhotonNetwork.InRoom && PhotonNetwork.IsMasterClient)
                StartCoroutine(AllocIDs());
        }

        // ─────────────────────────────────────────────────────────────────────
        // Core: build shards
        // All mesh vertices are in the SHARD'S OWN LOCAL SPACE.
        // The shard GO is placed at the shard centroid in world space.
        // The shard GO's rotation = the glass pane's rotation.
        // So local X/Y/Z of shard == local X/Y/Z of glass pane.
        // We just need to know which local axis is "thin" (thickness).
        // ─────────────────────────────────────────────────────────────────────
        void BuildShards(Vector3 contactWorld, Vector3 impulse, int seed)
        {
            // Pane local size (before scale)
            Vector3 sz = col.size;
            Vector3 sc = transform.lossyScale;
            // World-space extents
            float wx = sz.x * sc.x;
            float wy = sz.y * sc.y;
            float wz = sz.z * sc.z;

            // Identify thickness axis by smallest world extent
            // axisA, axisB = the two wide axes (pane plane)
            // axisT        = thin axis (thickness)
            // halfA, halfB = half-extents of the plane
            // halfT        = half-thickness
            float halfA, halfB, halfT;
            // We'll work in LOCAL SPACE of the pane, then place shards
            // using localPosition relative to the pane.
            int thinAxis; // 0=X, 1=Y, 2=Z
            if (wx <= wy && wx <= wz) { thinAxis = 0; halfT = sz.x * 0.5f; halfA = sz.y * 0.5f; halfB = sz.z * 0.5f; }
            else if (wy <= wx && wy <= wz) { thinAxis = 1; halfT = sz.y * 0.5f; halfA = sz.x * 0.5f; halfB = sz.z * 0.5f; }
            else { thinAxis = 2; halfT = sz.z * 0.5f; halfA = sz.x * 0.5f; halfB = sz.y * 0.5f; }

            float paneW = halfA * 2f;
            float paneH = halfB * 2f;
            float area = paneW * paneH;
            float massPerArea = totalMass / Mathf.Max(area, 0.0001f);

            // Contact in pane local space
            Vector3 localContact = transform.InverseTransformPoint(contactWorld) - col.center;
            float cu, cv;
            LocalToAB(localContact, thinAxis, out cu, out cv);
            cu = Mathf.Clamp(cu, -halfA, halfA);
            cv = Mathf.Clamp(cv, -halfB, halfB);

            // Voronoi seeds in 2D (A,B) space
            UnityEngine.Random.State saved = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed);

            int total = Mathf.Clamp((int)(area * 18f), 8, 55);
            float[] sa = new float[total];
            float[] sb = new float[total];

            int sc2 = total / 3;
            for (int i = 0; i < sc2; i++)
            {
                sa[i] = UnityEngine.Random.Range(-halfA, halfA);
                sb[i] = UnityEngine.Random.Range(-halfB, halfB);
            }
            float maxR = Mathf.Min(halfA, halfB) * 0.9f;
            for (int i = sc2; i < total; i++)
            {
                for (int t = 0; t < 500; t++)
                {
                    float r = UnityEngine.Random.Range(0f, maxR) * UnityEngine.Random.Range(0f, 1f);
                    Vector2 d = UnityEngine.Random.insideUnitCircle.normalized;
                    float a2 = cu + d.x * r;
                    float b2 = cv + d.y * r;
                    if (a2 >= -halfA && a2 <= halfA && b2 >= -halfB && b2 <= halfB)
                    { sa[i] = a2; sb[i] = b2; break; }
                }
            }
            UnityEngine.Random.state = saved;

            // Voronoi diagram
            var edges = ComputeVoronoi(sa, sb, -halfA, halfA, -halfB, halfB, total);
            var cellVerts = new List<Vector2>[total];
            for (int k = 0; k < total; k++) cellVerts[k] = new List<Vector2>();

            foreach (var e in edges)
            {
                AddUnique(cellVerts[e.s1], new Vector2(e.x1, e.y1));
                AddUnique(cellVerts[e.s1], new Vector2(e.x2, e.y2));
                AddUnique(cellVerts[e.s2], new Vector2(e.x1, e.y1));
                AddUnique(cellVerts[e.s2], new Vector2(e.x2, e.y2));
            }
            CornerToNearest(new Vector2(-halfA, -halfB), sa, sb, total, cellVerts);
            CornerToNearest(new Vector2(-halfA, halfB), sa, sb, total, cellVerts);
            CornerToNearest(new Vector2(halfA, -halfB), sa, sb, total, cellVerts);
            CornerToNearest(new Vector2(halfA, halfB), sa, sb, total, cellVerts);

            Vector3 impDir = impulse.normalized;
            float baseForce = Mathf.Clamp(impulse.magnitude, minExplodeImpulse, maxExplodeImpulse)
                                  * perShardImpulseFraction;

            Transform spawnRoot = parentObject != null ? parentObject
                                : transform.parent != null ? transform.parent
                                : transform;

            for (int n = 0; n < total; n++)
            {
                var verts2D = cellVerts[n];
                if (verts2D.Count < 3) continue;

                // Centroid in 2D
                Vector2 cen = Vector2.zero;
                foreach (var v in verts2D) cen += v;
                cen /= verts2D.Count;

                // Sort CCW
                verts2D.Sort((a, b) =>
                    Mathf.Atan2(a.y - cen.y, a.x - cen.x)
                    .CompareTo(Mathf.Atan2(b.y - cen.y, b.x - cen.x)));

                int count = verts2D.Count;
                if (count < 3) continue;

                if (cellInset > 0f)
                {
                    var inset = new List<Vector2>(count);
                    for (int q = 0; q < count; q++)
                    {
                        Vector2 prev = verts2D[(q - 1 + count) % count];
                        Vector2 curr = verts2D[q];
                        Vector2 next = verts2D[(q + 1) % count];
                        Vector2 d1 = (curr - prev).normalized;
                        Vector2 d2 = (next - curr).normalized;
                        Vector2 bis = (new Vector2(-d1.y, d1.x) + new Vector2(-d2.y, d2.x)).normalized;
                        inset.Add(curr + bis * cellInset);
                    }
                    verts2D = inset; count = verts2D.Count;
                }

                // ── Build mesh in SHARD LOCAL SPACE ──────────────────────────
                // Shard local space == pane local space (same rotation).
                // Centroid is the origin of the shard GO.
                // AB coords → local 3D via ABTToLocal, offset from centroid.

                // We build 3 groups of vertices with explicit normals for
                // correct lighting on all faces.

                // Front face:  thinAxis = +halfT (relative to centroid = 0)
                // Back face:   thinAxis = -halfT
                // Side walls:  one quad per edge, with outward normals

                // ── Face normals ──
                Vector3 frontNorm = ABTToLocal(0f, 0f, 1f, thinAxis).normalized;
                Vector3 backNorm = ABTToLocal(0f, 0f, -1f, thinAxis).normalized;

                int totalVerts = count * 2        // front (count) + back (count)
                                 + count * 4;       // sides: 4 verts per edge (own normals)
                var mv = new Vector3[totalVerts];
                var mn = new Vector3[totalVerts];
                var muv = new Vector2[totalVerts];
                var tris = new List<int>();

                // Pane UV scale for texturing
                float uvScaleA = 1f / (halfA * 2f);
                float uvScaleB = 1f / (halfB * 2f);

                // ── Front face verts [0 .. count-1] ──
                for (int q = 0; q < count; q++)
                {
                    Vector2 rel = verts2D[q] - cen;
                    mv[q] = ABTToLocal(rel.x, rel.y, halfT, thinAxis);
                    mn[q] = frontNorm;
                    muv[q] = new Vector2((verts2D[q].x + halfA) * uvScaleA,
                                         (verts2D[q].y + halfB) * uvScaleB);
                }
                // Front fan CCW
                for (int q = 1; q < count - 1; q++)
                { tris.Add(0); tris.Add(q); tris.Add(q + 1); }

                // ── Back face verts [count .. 2*count-1] ──
                int bOff = count;
                for (int q = 0; q < count; q++)
                {
                    Vector2 rel = verts2D[q] - cen;
                    mv[bOff + q] = ABTToLocal(rel.x, rel.y, -halfT, thinAxis);
                    mn[bOff + q] = backNorm;
                    muv[bOff + q] = new Vector2((verts2D[q].x + halfA) * uvScaleA,
                                               (verts2D[q].y + halfB) * uvScaleB);
                }
                // Back fan CW (reversed winding for back face)
                for (int q = 1; q < count - 1; q++)
                { tris.Add(bOff); tris.Add(bOff + q + 1); tris.Add(bOff + q); }

                // ── Side walls: 4 unique verts per edge, with outward normals ──
                int sOff = count * 2;
                for (int q = 0; q < count; q++)
                {
                    int next = (q + 1) % count;
                    Vector2 rA = verts2D[q] - cen;
                    Vector2 rB = verts2D[next] - cen;

                    Vector3 vFrontA = ABTToLocal(rA.x, rA.y, halfT, thinAxis);
                    Vector3 vFrontB = ABTToLocal(rB.x, rB.y, halfT, thinAxis);
                    Vector3 vBackA = ABTToLocal(rA.x, rA.y, -halfT, thinAxis);
                    Vector3 vBackB = ABTToLocal(rB.x, rB.y, -halfT, thinAxis);

                    // Outward normal = perpendicular to edge in AB plane, pointing out
                    Vector2 edgeDir = (rB - rA).normalized;
                    Vector2 outDir2D = new Vector2(edgeDir.y, -edgeDir.x); // rotate 90° outward
                    Vector3 sideNorm = ABTToLocal(outDir2D.x, outDir2D.y, 0f, thinAxis).normalized;

                    int vi = sOff + q * 4;
                    mv[vi + 0] = vFrontA; mn[vi + 0] = sideNorm;
                    mv[vi + 1] = vFrontB; mn[vi + 1] = sideNorm;
                    mv[vi + 2] = vBackB; mn[vi + 2] = sideNorm;
                    mv[vi + 3] = vBackA; mn[vi + 3] = sideNorm;

                    // UVs along the side: U = distance along edge, V = 0 (front) or 1 (back)
                    float edgeLen = (rB - rA).magnitude;
                    muv[vi + 0] = new Vector2(0f, 1f);
                    muv[vi + 1] = new Vector2(edgeLen, 1f);
                    muv[vi + 2] = new Vector2(edgeLen, 0f);
                    muv[vi + 3] = new Vector2(0f, 0f);

                    // Two triangles for quad (CCW from outside)
                    tris.Add(vi + 0); tris.Add(vi + 1); tris.Add(vi + 2);
                    tris.Add(vi + 0); tris.Add(vi + 2); tris.Add(vi + 3);
                }

                var mesh = new Mesh { name = "shard" + n };
                mesh.vertices = mv;
                mesh.normals = mn;
                mesh.uv = muv;
                mesh.triangles = tris.ToArray();
                mesh.RecalculateBounds();

                // Polygon area for mass
                float polyArea = 0f;
                for (int q = 0; q < count; q++)
                {
                    Vector2 a2 = verts2D[q], b2 = verts2D[(q + 1) % count];
                    polyArea += a2.x * b2.y - b2.x * a2.y;
                }
                polyArea = Mathf.Abs(polyArea) * 0.5f;

                // ── Create shard GO ───────────────────────────────────────────
                GameObject shard;
                MeshFilter mf = null;
                MeshRenderer mr = null;
                AudioSource sfx = null;

                if (shardPrefab == null)
                {
                    shard = new GameObject("shard" + n) { layer = shardLayer };
                }
                else
                {
                    shard = Instantiate(shardPrefab); shard.name = "shard" + n;
                    mf = shard.GetComponent<MeshFilter>();
                    mr = shard.GetComponent<MeshRenderer>();
                    sfx = shard.GetComponent<AudioSource>();
                    var ec = shard.GetComponent<Collider>();
                    if (ec) Destroy(ec);
                }

                shard.SetActive(false);
                shard.layer = shardLayer;

                if (!mf) mf = shard.AddComponent<MeshFilter>();
                mf.mesh = mesh;
                if (!mr) mr = shard.AddComponent<MeshRenderer>();
                if (mat) mr.sharedMaterial = mat;

                // ── Position shard in world space ─────────────────────────────
                // Centroid in pane local space (offset by collider center)
                Vector3 cenLocal = ABTToLocal(cen.x, cen.y, 0f, thinAxis) + col.center;
                Vector3 cenWorld = transform.TransformPoint(cenLocal);

                shard.transform.SetPositionAndRotation(cenWorld, transform.rotation);
                shard.transform.SetParent(spawnRoot, worldPositionStays: true);

                // BoxCollider from mesh bounds
                var bc = shard.AddComponent<BoxCollider>();
                bc.sharedMaterial = physicsMaterial;
                bc.center = Vector3.zero;
                bc.size = mesh.bounds.size + Vector3.one * 0.001f;

                var rb = shard.AddComponent<Rigidbody>();
                rb.mass = Mathf.Max(0.05f, polyArea * massPerArea);

                if (!sfx) sfx = shard.AddComponent<AudioSource>();
                sfx.spatialBlend = 1f; sfx.playOnAwake = false;
                sfx.pitch = Mathf.Clamp(8f / rb.mass, 0.85f, 1.15f);

                float fmag = Mathf.Clamp(baseForce, 0f, maxShardVelocity * rb.mass);
                Vector3 seedL = ABTToLocal(sa[n], sb[n], 0f, thinAxis) + col.center;
                Vector3 seedW = transform.TransformPoint(seedL);
                Vector3 fpos = Vector3.Lerp(contactWorld, seedW, 0.3f);

                pending.Add(new Pending { go = shard, force = -impDir * fmag, pos = fpos });
                cells.Add(shard);
            }
        }

        // ── Coordinate helpers ────────────────────────────────────────────────
        // thinAxis: 0=X, 1=Y, 2=Z
        // A,B are the two wide axes; T is along the thin axis.
        // Returns a LOCAL SPACE Vector3.
        static Vector3 ABTToLocal(float a, float b, float t, int thinAxis)
        {
            switch (thinAxis)
            {
                case 0: return new Vector3(t, a, b);   // thin=X, A=Y, B=Z
                case 1: return new Vector3(a, t, b);   // thin=Y, A=X, B=Z
                default: return new Vector3(a, b, t);  // thin=Z, A=X, B=Y
            }
        }

        static void LocalToAB(Vector3 local, int thinAxis, out float a, out float b)
        {
            switch (thinAxis)
            {
                case 0: a = local.y; b = local.z; break;
                case 1: a = local.x; b = local.z; break;
                default: a = local.x; b = local.y; break;
            }
        }

        // ── Voronoi ───────────────────────────────────────────────────────────
        struct Edge { public float x1, y1, x2, y2; public int s1, s2; }

        static List<Edge> ComputeVoronoi(float[] sx, float[] sy,
            float minA, float maxA, float minB, float maxB, int n)
        {
            var result = new List<Edge>();
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    float ax = sx[i], ay = sy[i], bx = sx[j], by = sy[j];
                    float mx = (ax + bx) * .5f, my = (ay + by) * .5f;
                    float dx = -(by - ay), dy = bx - ax;
                    float len = Mathf.Sqrt(dx * dx + dy * dy);
                    if (len < 1e-7f) continue;
                    dx /= len; dy /= len;
                    float ext = (maxA - minA + maxB - minB) * 4f;
                    float ex1 = mx - dx * ext, ey1 = my - dy * ext, ex2 = mx + dx * ext, ey2 = my + dy * ext;
                    if (!Clip(ref ex1, ref ey1, ref ex2, ref ey2, minA, maxA, minB, maxB)) continue;
                    if ((ex2 - ex1) * (ex2 - ex1) + (ey2 - ey1) * (ey2 - ey1) < 1e-8f) continue;
                    float smx = (ex1 + ex2) * .5f, smy = (ey1 + ey2) * .5f;
                    float dI = D2(smx, smy, ax, ay), dJ = D2(smx, smy, bx, by);
                    float thr = Mathf.Max(dI, dJ) * 1.0001f;
                    bool ok = true;
                    for (int k = 0; k < n && ok; k++)
                        if (k != i && k != j && D2(smx, smy, sx[k], sy[k]) < thr) ok = false;
                    if (ok) result.Add(new Edge { x1 = ex1, y1 = ey1, x2 = ex2, y2 = ey2, s1 = i, s2 = j });
                }
            return result;
        }

        static float D2(float ax, float ay, float bx, float by)
        { float dx = ax - bx, dy = ay - by; return dx * dx + dy * dy; }

        static bool Clip(ref float x0, ref float y0, ref float x1, ref float y1,
                         float l, float r, float b, float t)
        {
            int C(float x, float y) { int c = 0; if (x < l) c |= 1; else if (x > r) c |= 2; if (y < b) c |= 4; else if (y > t) c |= 8; return c; }
            int c0 = C(x0, y0), c1 = C(x1, y1);
            for (; ; )
            {
                if ((c0 | c1) == 0) return true;
                if ((c0 & c1) != 0) return false;
                int co = c0 != 0 ? c0 : c1; float x, y;
                if ((co & 8) != 0) { x = x0 + (x1 - x0) * (t - y0) / (y1 - y0); y = t; }
                else if ((co & 4) != 0) { x = x0 + (x1 - x0) * (b - y0) / (y1 - y0); y = b; }
                else if ((co & 2) != 0) { y = y0 + (y1 - y0) * (r - x0) / (x1 - x0); x = r; }
                else { y = y0 + (y1 - y0) * (l - x0) / (x1 - x0); x = l; }
                if (co == c0) { x0 = x; y0 = y; c0 = C(x0, y0); } else { x1 = x; y1 = y; c1 = C(x1, y1); }
            }
        }

        static void AddUnique(List<Vector2> list, Vector2 v)
        { if (!list.Contains(v)) list.Add(v); }

        static void CornerToNearest(Vector2 corner, float[] sa, float[] sb,
                                     int n, List<Vector2>[] cv)
        {
            float best = float.MaxValue; int bi = 0;
            for (int i = 0; i < n; i++)
            {
                float d = (corner.x - sa[i]) * (corner.x - sa[i]) + (corner.y - sb[i]) * (corner.y - sb[i]);
                if (d < best) { best = d; bi = i; }
            }
            AddUnique(cv[bi], corner);
        }

        // ── Photon ViewIDs ────────────────────────────────────────────────────
        IEnumerator AllocIDs()
        {
            yield return null;
            allocatedIDs.Clear();
            for (int i = 0; i < cells.Count; i++)
                allocatedIDs[i] = PhotonNetwork.AllocateViewID(true);
            ApplyIDs(allocatedIDs);
            int[] idx = new int[allocatedIDs.Count], ids = new int[allocatedIDs.Count];
            int k = 0;
            foreach (var kv in allocatedIDs) { idx[k] = kv.Key; ids[k] = kv.Value; k++; }
            photonView.RPC(nameof(RPC_ReceiveIDs), RpcTarget.Others, idx, ids);
        }

        [PunRPC]
        void RPC_ReceiveIDs(int[] idx, int[] ids)
        {
            var m = new Dictionary<int, int>();
            for (int i = 0; i < idx.Length; i++) m[idx[i]] = ids[i];
            ApplyIDs(m);
        }

        [PunRPC]
        void RPC_RequestIDs(int actor)
        {
            if (!PhotonNetwork.IsMasterClient || allocatedIDs.Count == 0) return;
            int[] idx = new int[allocatedIDs.Count], ids = new int[allocatedIDs.Count];
            int k = 0; foreach (var kv in allocatedIDs) { idx[k] = kv.Key; ids[k] = kv.Value; k++; }
            var pl = PhotonNetwork.CurrentRoom.GetPlayer(actor);
            if (pl != null) photonView.RPC(nameof(RPC_ReceiveIDs), pl, idx, ids);
        }

        void ApplyIDs(Dictionary<int, int> map)
        {
            bool master = PhotonNetwork.IsMasterClient;
            foreach (var kv in map)
            {
                if (kv.Key >= cells.Count) continue;
                var go = cells[kv.Key];
                var pv = go.GetComponent<PhotonView>() ?? go.AddComponent<PhotonView>();
                var tv = go.GetComponent<PhotonTransformView>() ?? go.AddComponent<PhotonTransformView>();
                var rv = go.GetComponent<PhotonRigidbodyView>() ?? go.AddComponent<PhotonRigidbodyView>();
                tv.m_SynchronizePosition = true; tv.m_SynchronizeRotation = true; tv.m_SynchronizeScale = false;
                rv.m_SynchronizeVelocity = true; rv.m_SynchronizeAngularVelocity = true;
                pv.ObservedComponents = new List<Component> { tv, rv };
                pv.Synchronization = ViewSynchronization.UnreliableOnChange;
                pv.OwnershipTransfer = OwnershipOption.Fixed;
                pv.ViewID = kv.Value;
                var rb = go.GetComponent<Rigidbody>();
                if (rb) rb.isKinematic = !master;
            }
        }

        public override void OnMasterClientSwitched(Photon.Realtime.Player p)
        {
            if (!p.IsLocal) return;
            allocatedIDs.Clear();
            for (int i = 0; i < cells.Count; i++)
            {
                var pv = cells[i].GetComponent<PhotonView>();
                if (pv) allocatedIDs[i] = pv.ViewID;
            }
            foreach (var c in cells)
            {
                var rb = c.GetComponent<Rigidbody>();
                if (rb) rb.isKinematic = false;
            }
        }

        // ── Reset ─────────────────────────────────────────────────────────────
        public void ResetState()
        {
            if (!resetOnReload) return;
            foreach (var c in cells) if (c) Destroy(c);
            cells.Clear(); pending.Clear(); allocatedIDs.Clear();
            shattered = false;
            col.enabled = true; meshRend.enabled = true;
            if (body) body.isKinematic = false;
            readyForCollision = false;
            StartCoroutine(AllowCollision());
        }
    }
}