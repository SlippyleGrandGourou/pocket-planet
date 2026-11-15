using UnityEngine;

namespace LivingPlanet
{
    // Nappes phréatiques : la pluie s'infiltre selon la perméabilité du sol et de la roche, s'accumule,
    // s'écoule latéralement vers le bas et ressort en sources sur les versants et au fond des vallées.
    public sealed partial class PlanetSim
    {
        public float[] gw, gwN, permB, outG;

        // perméabilité (0..1) : terre, sable, gravier, argile
        static readonly float[] SoilPerm = { 0.45f, 0.9f, 1.0f, 0.05f };
        // granit, basalte, grès, calcaire, poudingue, argilite (roche sédimentaire fine = aquiclude, quasi imperméable)
        static readonly float[] RockPerm = { 0.04f, 0.12f, 0.55f, 0.35f, 0.6f, 0.03f };

        void InitGroundwater()
        {
            gw = new float[M]; gwN = new float[M]; permB = new float[M]; outG = new float[M * 4]; fire = new float[M];
            InitGas();
        }

        public float Perm(int c)
        {
            int b = c * 4;
            float sp = mix[b] * SoilPerm[0] + mix[b + 1] * SoilPerm[1] + mix[b + 2] * SoilPerm[2] + mix[b + 3] * SoilPerm[3];
            float p = Mathf.Lerp(RockPerm[rockT[c]], sp, Mathf.Clamp01(sed[c] / 0.6f));
            return Mathf.Clamp01(p + permB[c]);
        }

        public float GwCapacity(int c)
        {
            float por = 0.15f + 0.5f * RockPerm[rockT[c]];
            return 0.3f + sed[c] * 0.8f + por;
        }

        public float GwFill(int c) { return Mathf.Clamp01(gw[c] / GwCapacity(c)); }

        public void StepGroundwater()
        {
            var nbr = G.Nbr; var back = G.Back; var dist = G.Dist;
            if (evap == null) evap = new float[M];
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    int b = c * 4;
                    float g = rock[c] + sed[c];
                    if (g <= SeaLevel)
                    {
                        water[c] += gw[c];
                        gw[c] = 0f;
                        outG[b] = outG[b + 1] = outG[b + 2] = outG[b + 3] = 0f;
                        continue;
                    }
                    float perm = Perm(c);
                    float cap = GwCapacity(c);
                    float v = gw[c];
                    if (temp[c] > 0f && water[c] > 0f && Salinity(c) <= DrinkableSalinity)
                    {
                        float room = Mathf.Clamp01(1f - v / cap);
                        float inf = Mathf.Min(water[c] * 0.5f, perm * 0.03f * room);
                        water[c] -= inf; v += inf;
                    }
                    // pertes par évapotranspiration (plantes, chaleur)
                    float saturation = Sat(temp[c]);
                    float dry = Mathf.Clamp01(1f - hum[c] / Mathf.Max(saturation, 0.05f));
                    float airRoom = Mathf.Max(0f, (saturation - hum[c]) * rainK - evap[c]);
                    float evapGround = Mathf.Min(v, Mathf.Min(airRoom,
                        0.0003f * Mathf.Max(0f, temp[c]) / 20f * (0.3f + cover[c]) * dry));
                    v -= evapGround;
                    evap[c] += evapGround;
                    gw[c] = v;
                    float total = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        int n = nbr[b + k];
                        float o = 0f;
                        if (n >= 0 && v > 1e-5f)
                        {
                            float dz = g - rock[n] - sed[n];
                            if (dz > 0f) o = Mathf.Min(dz / dist[b + k], 1f) * perm * 0.25f * v;
                        }
                        outG[b + k] = o; total += o;
                    }
                    float lim = v * 0.9f;
                    if (total > lim) { float sc = lim / total; for (int k = 0; k < 4; k++) outG[b + k] *= sc; }
                }
            });

            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    int b = c * 4;
                    float g = rock[c] + sed[c];
                    if (g <= SeaLevel)
                    {
                        float inflow = 0f;
                        for (int k = 0; k < 4; k++)
                        {
                            int n = nbr[b + k];
                            if (n >= 0) inflow += outG[n * 4 + back[b + k]] * G.Area[n] / G.Area[c];
                        }
                        water[c] += inflow;
                        gwN[c] = 0f;
                        continue;
                    }
                    float v = gw[c] - (outG[b] + outG[b + 1] + outG[b + 2] + outG[b + 3]);
                    float drop = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        int n = nbr[b + k];
                        if (n < 0) continue;
                        v += outG[n * 4 + back[b + k]] * G.Area[n] / G.Area[c];
                        float d = (g - rock[n] - sed[n]) / dist[b + k];
                        if (d > drop) drop = d;
                    }
                    float cap = GwCapacity(c);
                    // nappe saturée : la source déborde ; sur les versants la nappe affleure plus tôt
                    float thr = drop > 0.5f ? 0.45f : 0.85f;
                    float ex = Mathf.Max(0f, v - cap * thr) * (drop > 0.5f ? 0.04f : 0.12f);
                    if (ex > 0f) { v -= ex; water[c] += ex; }
                    gwN[c] = v;
                    float fill = Mathf.Clamp01(v / cap);
                    if (fill * 0.9f > moist[c]) moist[c] += (fill * 0.9f - moist[c]) * 0.01f;
                    if (permB[c] > 0f) permB[c] = Mathf.Max(0f, permB[c] - 0.00002f);
                }
            });
            var t = gw; gw = gwN; gwN = t;
        }
    }
}
