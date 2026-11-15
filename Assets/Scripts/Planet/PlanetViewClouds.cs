using System.Collections.Generic;
using UnityEngine;

namespace LivingPlanet
{
    // Nuages : un maillage low-poly par nuage (cumulus, stratus, cirrus, cumulonimbus), 3 niveaux d'opacité,
    // advectés par le vent (donc tournent autour des dépressions), colonnes de cendres des volcans.
    public sealed partial class PlanetView
    {
        const int CW = 0, CG = 1, CS = 2, CT = 3, CA = 4;           // couleurs
        const int SH_CUM = 0, SH_STR = 1, SH_CIR = 2, SH_TWR = 3;   // formes
        const int CloudColors = 5, OpLevels = 3;

        struct CloudInst { public Vector3 pos; public Quaternion rot; public Vector3 scale; public int shape, color, lvl; }
        struct Bolt { public Vector3 a, b; public float life, max; public int seed; }

        Vector3[] siteDir;
        float[] siteYaw, siteSize, siteSt, sitePhase;
        int[] siteCell;
        int siteK;
        Bucket[] cloudB;
        Bucket boltB;
        readonly List<CloudInst> clouds = new List<CloudInst>(2048);
        readonly List<Bolt> bolts = new List<Bolt>();

        public float TimeScale = 1f;
        bool lensOn;
        Vector3 lensO, lensD;

        public void SetLens(bool on, Vector3 origin, Vector3 dir) { lensOn = on; lensO = origin; lensD = dir; }

        void BuildClouds()
        {
            siteK = Mathf.Clamp(G.N / 10, 16, 32);
            int count = 6 * siteK * siteK;
            siteDir = new Vector3[count];
            siteYaw = new float[count];
            siteSize = new float[count];
            siteSt = new float[count];
            sitePhase = new float[count];
            siteCell = new int[count];
            int i = 0;
            for (int f = 0; f < 6; f++)
                for (int b = 0; b < siteK; b++)
                    for (int a = 0; a < siteK; a++)
                    {
                        float u = ((a + (float)vr.NextDouble() * 0.9f + 0.05f) / siteK) * 2f - 1f;
                        float v = ((b + (float)vr.NextDouble() * 0.9f + 0.05f) / siteK) * 2f - 1f;
                        siteDir[i] = PlanetGrid.FacePoint(f, u, v).normalized;
                        siteCell[i] = G.CellFromDir(siteDir[i]);
                        siteYaw[i] = (float)vr.NextDouble() * 360f;
                        sitePhase[i] = (float)vr.NextDouble();
                        i++;
                    }

            var meshes = new Mesh[4];
            var mb = new MeshBuilder();
            mb.Sphere(new Vector3(0f, 0.25f, 0f), 1f, 0.7f, 4, 7);
            mb.Sphere(new Vector3(0.95f, 0.05f, 0.2f), 0.7f, 0.7f, 4, 7);
            mb.Sphere(new Vector3(-0.9f, 0f, -0.1f), 0.65f, 0.7f, 4, 7);
            mb.Sphere(new Vector3(0.2f, 0.6f, -0.6f), 0.6f, 0.7f, 4, 7);
            mb.Sphere(new Vector3(-0.1f, 0.75f, 0.5f), 0.55f, 0.7f, 4, 7);
            meshes[SH_CUM] = mb.Build();
            mb = new MeshBuilder();
            mb.Sphere(Vector3.zero, 1.6f, 0.1f, 3, 9);
            mb.Sphere(new Vector3(0.9f, 0f, 0.6f), 1f, 0.12f, 3, 8);
            mb.Sphere(new Vector3(-0.8f, 0.02f, -0.7f), 0.9f, 0.12f, 3, 8);
            meshes[SH_STR] = mb.Build();
            mb = new MeshBuilder();
            mb.Sphere(new Vector3(0f, 0f, -1f), 0.6f, 0.12f, 3, 6);
            mb.Sphere(new Vector3(0.3f, 0f, 0f), 0.6f, 0.12f, 3, 6);
            mb.Sphere(new Vector3(-0.2f, 0f, 1f), 0.6f, 0.12f, 3, 6);
            meshes[SH_CIR] = mb.Build();
            mb = new MeshBuilder();
            mb.Sphere(new Vector3(0f, 0.3f, 0f), 1f, 0.6f, 4, 7);
            mb.Sphere(new Vector3(0f, 1.1f, 0f), 0.85f, 0.8f, 4, 7);
            mb.Sphere(new Vector3(0.15f, 1.9f, 0f), 0.7f, 0.9f, 4, 7);
            mb.Sphere(new Vector3(-0.1f, 2.6f, 0.1f), 0.55f, 0.9f, 4, 7);
            mb.Sphere(new Vector3(0f, 3.1f, 0f), 1.7f, 0.18f, 4, 8);
            meshes[SH_TWR] = mb.Build();

            Color[] cols =
            {
                new Color(0.97f, 0.97f, 1f), new Color(0.55f, 0.56f, 0.62f), new Color(0.28f, 0.29f, 0.34f),
                new Color(0.8f, 0.82f, 0.86f), new Color(0.2f, 0.17f, 0.15f)
            };
            float[] alpha = { 0.4f, 0.72f, 1f };
            var mats = new Material[CloudColors * OpLevels];
            for (int k = 0; k < CloudColors; k++)
                for (int l = 0; l < OpLevels; l++)
                {
                    var c = cols[k]; c.a = alpha[l];
                    var m = MakeLit(c, 0f, true);
                    if (l < OpLevels - 1) MakeTransparent(m, false);
                    mats[k * OpLevels + l] = m;
                }
            cloudB = new Bucket[4 * CloudColors * OpLevels];
            for (int s = 0; s < 4; s++)
                for (int k = 0; k < CloudColors * OpLevels; k++)
                    cloudB[s * CloudColors * OpLevels + k] = new Bucket { mesh = meshes[s], mat = mats[k] };

            var bb = new MeshBuilder();
            bb.Box(Vector3.zero, new Vector3(1f, 1f, 1f));
            boltB = new Bucket { mesh = bb.Build(), mat = MakeUnlit(new Color(0.95f, 0.97f, 1f)) };
        }

        static int OpLevel(float cs) { return cs < 0.4f ? 0 : (cs < 0.75f ? 1 : 2); }

        void AddCloud(Vector3 pos, Quaternion rot, Vector3 scale, int shape, int color, int lvl)
        {
            clouds.Add(new CloudInst { pos = pos, rot = rot, scale = scale, shape = shape, color = color, lvl = lvl });
        }

        float HeightScale;   // les nuages montent avec la taille de la planète

        void UpdateClouds(Vector3 camPos, float dt)
        {
            clouds.Clear();
            Vector3 cdir = camPos.normalized;
            float alt = camPos.magnitude - R;
            float minDot = Mathf.Cos(Mathf.Min(Mathf.Acos(Mathf.Clamp01(R / camPos.magnitude)) + 0.25f, Mathf.PI));
            if (alt < 40f) minDot = -0.2f;
            float spacing = R * 1.45f / siteK;
            float L = spacing * 2.2f;
            float blend = 1f - Mathf.Exp(-dt * 0.4f);
            float vis = TimeScale <= 0f ? 0f : 1f + 0.4f * Mathf.Log(Mathf.Max(1f, TimeScale), 2f);

            for (int i = 0; i < siteDir.Length; i++)
            {
                Vector3 sd = siteDir[i];
                int c = siteCell[i];
                float ph = sitePhase[i] + dt * vis / (35f + siteYaw[i] * 0.1f);
                if (ph >= 1f)
                {
                    ph -= Mathf.Floor(ph);
                    float y = 2f * (float)vr.NextDouble() - 1f;
                    float angle = 2f * Mathf.PI * (float)vr.NextDouble();
                    float horizontal = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                    sd = new Vector3(horizontal * Mathf.Cos(angle), y, horizontal * Mathf.Sin(angle));
                    c = G.CellFromDir(sd);
                    siteSize[i] = 0f;
                    siteSt[i] = 0f;
                }
                Vector3 advection = (i & 7) == 0 ? S.windHigh[c] : S.wind[c];
                sd = (sd + Vector3.ProjectOnPlane(advection, sd) * (0.028f * L / R * dt * vis)).normalized;
                siteDir[i] = sd;
                c = G.CellFromDir(sd);
                siteCell[i] = c;
                sitePhase[i] = ph;
                bool near = Vector3.Dot(sd, cdir) > minDot;
                if (!near && siteSize[i] < 0.02f) continue;

                float st = S.storm[c], ash = S.ash[c];
                float sat = Mathf.Max(PlanetSim.Sat(S.temp[c]), 0.05f);
                float hr = S.hum[c] / sat;
                float rain = Mathf.Clamp01(S.rainNow[c] * 40f);
                // la couverture vient du modèle de nuages de la simulation ; les petites valeurs restent du ciel dégagé
                float cov = Mathf.Clamp01((S.cloud[c] - 0.18f) / 0.82f);
                cov = Mathf.Max(cov, Mathf.Min(1f, ash));
                siteSize[i] += (cov - siteSize[i]) * blend;
                siteSt[i] += (st - siteSt[i]) * blend;
                st = siteSt[i];
                float cs = siteSize[i];
                if (!near) continue;

                Vector3 w = Vector3.ProjectOnPlane(S.wind[c], sd);
                float wm = w.magnitude;
                Vector3 wd = wm > 1e-4f ? w / wm : Vector3.zero;
                float fade = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(Mathf.Min(ph, 1f - ph) / 0.12f));
                Vector3 d = sd;
                float g = Mathf.Max(0f, S.rock[c] + S.sed[c]);
                float h = R + 95f * HeightScale + 0.35f * g;
                Quaternion rot = wd != Vector3.zero
                    ? Quaternion.LookRotation(wd, d) * Quaternion.Euler(0f, siteYaw[i] % 25f - 12.5f, 0f)
                    : Quaternion.FromToRotation(Vector3.up, d) * Quaternion.Euler(0f, siteYaw[i], 0f);

                // cirrus : hauts, étirés dans le sens du vent
                if ((i & 7) == 0)
                {
                    float cc = Mathf.Clamp01(1f - Mathf.Abs(hr - 0.5f) * 5f) * 0.8f * (1f - cs) * fade;
                    if (cc > 0.1f)
                    {
                        float r0 = spacing * cc;
                        Vector3 high = Vector3.ProjectOnPlane(S.windHigh[c], d);
                        Quaternion cr = high.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(high, d) : rot;
                        AddCloud(d * (R + 260f * HeightScale), cr, new Vector3(r0 * 0.5f, r0 * 0.3f, r0 * 1.3f), SH_CIR, CT, 0);
                    }
                }

                if (cs < 0.08f || fade < 0.05f) continue;
                float r = spacing * (0.22f + 0.28f * cs) * fade;
                int lvl = OpLevel(cs * fade + 0.15f);

                if (ash > 0.1f)
                {
                    float ra = r * (0.8f + 0.5f * Mathf.Min(1f, ash));
                    AddCloud(d * (h + 10f * HeightScale), rot, new Vector3(ra, ra, ra), SH_CUM, CA, 2);
                }
                else if (st > 0.3f)
                {
                    float tower = r * (0.55f + 0.35f * st);
                    AddCloud(d * (h - 4f * HeightScale), rot, new Vector3(tower, tower * (0.8f + 0.9f * st), tower), SH_TWR, st > 0.7f ? CS : CG, st > 0.6f ? 2 : 1);
                    if (st > 0.6f && bolts.Count < 16 && alt < 900f * HeightScale && vr.NextDouble() < 0.009 * st)
                    {
                        Vector3 top = d * (h + tower * 0.4f);
                        Vector3 gnd = d * (R + g);
                        if ((top - camPos).sqrMagnitude < 1500f * 1500f) AddBolt(top, gnd);
                    }
                }
                else if (rain > 0.15f || hr > 0.95f)
                {
                    AddCloud(d * (h - 4f * HeightScale), rot, new Vector3(r * 0.9f, r * (0.8f + 0.3f * cs), r * 0.9f), SH_CUM, CG, Mathf.Max(1, lvl));
                }
                else if (S.temp[c] < 8f && hr > 0.75f)
                {
                    AddCloud(d * (R + 65f * HeightScale + 0.35f * g), rot, new Vector3(r * 1.1f, r, r * 1.1f), SH_STR, CT, lvl == 0 ? 0 : 1);
                }
                else if (S.frontN[c] > 0.45f && wm > 0.18f)
                {
                    float stretch = 1f + Mathf.Min(0.9f, wm);
                    AddCloud(d * h, rot, new Vector3(r * 0.9f, r, r * stretch), SH_STR, CW, Mathf.Min(lvl, 1));
                }
                else
                {
                    AddCloud(d * h, rot, new Vector3(r, r * (0.7f + 0.4f * cs), r), SH_CUM, CW, lvl);
                }
            }

            AddVolcanoClouds(camPos, minDot, spacing);
        }

        // colonnes de cendres des volcans en éruption et panaches de vapeur des volcans fumants
        void AddVolcanoClouds(Vector3 camPos, float minDot, float spacing)
        {
            Vector3 cdir = camPos.normalized;
            var vs = S.volcanoes;
            for (int i = 0; i < vs.Count; i++)
            {
                int vc = vs[i].cell;
                if (S.Ground(vc) < S.SeaLevel - 30f) continue;
                Vector3 d = G.Dir[vc];
                if (Vector3.Dot(d, cdir) < minDot) continue;
                float k = Mathf.Clamp01(vs[i].power * vs[i].env * 1.3f + 0.1f);
                float g = Mathf.Max(0f, S.rock[vc] + S.sed[vc]);
                Vector3 wd = Vector3.ProjectOnPlane(S.wind[vc], d);
                wd = wd.sqrMagnitude > 1e-6f ? wd.normalized : Vector3.zero;
                Quaternion rot = Quaternion.FromToRotation(Vector3.up, d);
                int segs = 3 + (int)(8f * k);
                float r0 = spacing * 0.18f * (0.6f + k);
                for (int j = 0; j < segs; j++)
                {
                    float hh = R + g + 6f + j * r0 * 1.1f;
                    float rj = r0 * (0.7f + 0.22f * j);
                    Vector3 p = d * hh + wd * (j * j * (0.5f + 1.5f * k));
                    AddCloud(p, rot * Quaternion.Euler(0f, j * 70f, 0f), new Vector3(rj, rj, rj), SH_CUM, CA, 2);
                }
                float rt = r0 * (2f + 3f * k);
                AddCloud(d * (R + g + 6f + segs * r0 * 1.1f) + wd * (segs * segs * (0.5f + 1.5f * k)), rot, new Vector3(rt, rt * 0.6f, rt), SH_STR, CA, 1);
            }
            var fu = S.fumaroles;
            for (int i = 0; i < fu.Count; i++)
            {
                int c = fu[i];
                Vector3 d = G.Dir[c];
                if (Vector3.Dot(d, cdir) < minDot) continue;
                float g = Mathf.Max(0f, S.rock[c] + S.sed[c]);
                Vector3 wd = Vector3.ProjectOnPlane(S.wind[c], d);
                wd = wd.sqrMagnitude > 1e-6f ? wd.normalized : Vector3.zero;
                Quaternion rot = Quaternion.FromToRotation(Vector3.up, d);
                float r = spacing * 0.1f;
                for (int j = 0; j < 3; j++)
                    AddCloud(d * (R + g + 4f + j * r * 1.4f) + wd * (j * j * 0.7f), rot * Quaternion.Euler(0f, j * 90f + i * 13f, 0f),
                        new Vector3(r * (1f + 0.3f * j), r, r * (1f + 0.3f * j)), SH_CUM, CW, j == 0 ? 1 : 0);
            }
        }

        void AddBolt(Vector3 top, Vector3 gnd)
        {
            if (bolts.Count >= 16) return;
            bolts.Add(new Bolt { a = top, b = gnd, life = 0.4f, max = 0.4f, seed = vr.Next(1, 100000) });
            Flash(gnd + gnd.normalized * 15f, 1f);
        }

        void TickBolts(float dt)
        {
            // les impacts de foudre calculés par la simulation deviennent des éclairs visibles
            var st = S.strikes;
            for (int i = 0; i < st.Count; i++)
            {
                int c = st[i];
                Vector3 d = G.Dir[c];
                float g = Mathf.Max(0f, S.rock[c] + S.sed[c]);
                Vector3 gnd = d * (R + g);
                if ((gnd - lastCamPos).sqrMagnitude < 3500f * 3500f) AddBolt(gnd + d * (45f + 20f * (float)vr.NextDouble()), gnd);
            }
            st.Clear();
            for (int i = bolts.Count - 1; i >= 0; i--)
            {
                var b = bolts[i];
                b.life -= dt;
                if (b.life <= 0f) bolts.RemoveAt(i); else bolts[i] = b;
            }
        }

        static float Hash(int s, int i) { return Mathf.Repeat(Mathf.Sin(s * 12.9898f + i * 78.233f) * 43758.547f, 1f) * 2f - 1f; }
        public bool ShowClouds = true;

        void DrawClouds(Bounds bounds)
        {
            if (cloudB == null) return;
            for (int k = 0; k < cloudB.Length; k++) cloudB[k].n = 0;
            bool lens = lensOn;
            int per = CloudColors * OpLevels;
            for (int i = 0; ShowClouds && i < clouds.Count; i++)
            {
                var p = clouds[i];
                Vector3 s = p.scale;
                if (lens)
                {
                    Vector3 v = p.pos - lensO;
                    float t = Vector3.Dot(v, lensD);
                    if (t > 1f)
                    {
                        float perp = (v - lensD * t).magnitude;
                        float ang = (perp - Mathf.Max(s.x, s.z) * 0.5f) / t;
                        float f = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.045f, 0.09f, ang));
                        if (f < 0.02f) continue;
                        s *= f;
                    }
                }
                cloudB[p.shape * per + p.color * OpLevels + p.lvl].Add(Matrix4x4.TRS(p.pos, p.rot, s));
            }
            for (int k = 0; k < cloudB.Length; k++) if (cloudB[k].n > 0) DrawBucket(cloudB[k], bounds, false);

            boltB.n = 0;
            for (int i = 0; i < bolts.Count; i++)
            {
                var bo = bolts[i];
                // scintillement : l'éclair s'allume par à-coups
                if ((int)(bo.life * 40f) % 3 == 1) continue;
                Vector3 dv = bo.b - bo.a;
                float len = dv.magnitude;
                if (len < 1f) continue;
                Vector3 dn = dv / len;
                Vector3 u = Vector3.Cross(dn, Vector3.up);
                if (u.sqrMagnitude < 1e-4f) u = Vector3.right;
                u.Normalize();
                Vector3 w2 = Vector3.Cross(dn, u);
                float width = Mathf.Max(0.8f, (bo.a - lastCamPos).magnitude * 0.004f);
                const int N = 6;
                Vector3 prev = bo.a;
                for (int k = 1; k <= N; k++)
                {
                    Vector3 pt = Vector3.Lerp(bo.a, bo.b, k / (float)N);
                    if (k < N) pt += (u * Hash(bo.seed, k) + w2 * Hash(bo.seed, k + 17)) * len * 0.07f;
                    Vector3 sv = pt - prev;
                    float sl = sv.magnitude;
                    if (sl > 0.01f) boltB.Add(Matrix4x4.TRS((prev + pt) * 0.5f, Quaternion.LookRotation(sv / sl), new Vector3(width, width, sl)));
                    prev = pt;
                }
            }            DrawBucket(boltB, bounds, false);
        }
    }
}
