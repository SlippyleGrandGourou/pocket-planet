using System;
using UnityEngine;

namespace LivingPlanet
{
    // Orages, cendres volcaniques et magma
    public sealed partial class PlanetSim
    {
        public float[] storm, ash, ashN, magma, magmaN, plumeInf, magmaInflation, mantleSolid;
        public float[] mantleResidual;
        public float[] mantleSolidN, mantleResidualN;
        public const float MagmaSolidScale = 4f;
        const float InitialMantleSolid = 80f;
        public Action<int> onLightning;
        public readonly System.Collections.Generic.List<int> strikes = new System.Collections.Generic.List<int>();
        float lastStormLog = -100f;

        public void ClearTransientEffectsAfterLoad()
        {
            strikes.Clear();
            astFx.Clear();
            newQuakes.Clear();
            events.Clear();
            lastStormLog = -100f;
        }

        void InitWeather()
        {
            storm = new float[M]; ash = new float[M]; ashN = new float[M];
            magma = new float[M]; magmaN = new float[M]; plumeInf = new float[M]; magmaInflation = new float[M]; cloud = new float[M];
            mantleSolid = new float[M];
            mantleResidual = new float[M];
            mantleSolidN = new float[M]; mantleResidualN = new float[M];
            for (int c = 0; c < M; c++) mantleSolid[c] = InitialMantleSolid;
        }

        // ------------------------------------------------------------ magma
        void InitMagma(System.Random rnd)
        {
            Array.Clear(magmaInflation, 0, magmaInflation.Length);
            Array.Clear(mantleResidual, 0, mantleResidual.Length);
            for (int c = 0; c < M; c++) mantleSolid[c] = InitialMantleSolid;
            const int plumes = 6;
            var pl = new Vector3[plumes];
            for (int i = 0; i < plumes; i++)
            {
                Vector3 d;
                do { d = new Vector3((float)rnd.NextDouble() * 2 - 1, (float)rnd.NextDouble() * 2 - 1, (float)rnd.NextDouble() * 2 - 1); }
                while (d.sqrMagnitude > 1f || d.sqrMagnitude < 0.05f);
                pl[i] = d.normalized;
            }
            float cosR = Mathf.Cos(Mathf.Max(0.06f, Mathf.PI * 0.75f / G.N));
            for (int c = 0; c < M; c++)
            {
                float inf = 0f;
                for (int i = 0; i < plumes; i++)
                {
                    float d = Vector3.Dot(G.Dir[c], pl[i]);
                    if (d > cosR) inf = Mathf.Max(inf, (d - cosR) / (1f - cosR));
                }
                plumeInf[c] = inf;
                float m = (tectonics && conv != null ? conv[c] * 0.7f + rift[c] * 0.4f : 0f) + inf * 0.9f;
                magma[c] = Mathf.Min(1.5f, m * Activity * (0.5f + (float)rnd.NextDouble() * 0.7f));
                mantleSolid[c] -= magma[c] * MagmaSolidScale;
            }
        }

        // appelé après chaque pas tectonique : la subduction, les rifts et les points chauds alimentent le magma
        void StepMagma(float stepScale)
        {
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    float boundary = tectonics && conv != null ? conv[c] * 0.5f + rift[c] * 0.3f : 0f;
                    // Les points chauds sont alimentés par des panaches profonds du manteau, indépendants de la
                    // convergence des plaques : poids plus fort que les zones de subduction/rift pour qu'ils
                    // produisent un volcanisme fiable même quand l'activité tectonique globale est faible.
                    float gen = (boundary + plumeInf[c] * 1.3f) * Activity;
                    float cooled = magma[c] * (1f - Mathf.Pow(0.999f, stepScale));
                    float produced = Mathf.Min(gen * 0.003f * stepScale,
                        Mathf.Min(mantleSolid[c] / MagmaSolidScale, 1.5f - magma[c] + cooled));
                    mantleSolid[c] += (cooled - produced) * MagmaSolidScale;
                    magmaN[c] = magma[c] - cooled + produced;
                }
            });
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    float m = magmaN[c];
                    int best = -1;
                    float here = tectonics && conv != null ? Mathf.Max(conv[c], Mathf.Max(rift[c], plumeInf[c])) : plumeInf[c];
                    float path = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        int n = G.Nbr[c * 4 + k];
                        if (n < 0) continue;
                        float candidate = tectonics && conv != null ? Mathf.Max(conv[n], Mathf.Max(rift[n], plumeInf[n])) : plumeInf[n];
                        if (candidate < 0.08f || magmaN[n] >= m) continue;
                        float potential = candidate * 0.75f + Mathf.Clamp01((m - magmaN[n]) / 1.5f) * 0.25f;
                        if (potential > path) { path = potential; best = k; }
                    }
                    for (int k = 0; k < 4; k++) thOut[c * 4 + k] = 0f;
                    if (best >= 0)
                        thOut[c * 4 + best] = Mathf.Min(m, m * 0.08f * stepScale * Mathf.Clamp01(path + here * 0.25f));
                }
            });
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    float outflow = 0f, inflow = 0f;
                    int b = c * 4;
                    for (int k = 0; k < 4; k++)
                    {
                        outflow += thOut[b + k];
                        int n = G.Nbr[b + k];
                        if (n >= 0) inflow += thOut[n * 4 + G.Back[b + k]] * G.Area[n] / G.Area[c];
                    }
                    float amount = magmaN[c] - outflow + inflow;
                    float overflow = Mathf.Max(0f, amount - 1.5f);
                    mantleSolid[c] += overflow * MagmaSolidScale;
                    magmaN[c] = Mathf.Clamp(amount, 0f, 1.5f);
                }
            });
            var old = magma; magma = magmaN; magmaN = old;
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    float pressure = magma[c] * 2f, weight = 2f;
                    for (int k = 0; k < 4; k++)
                    {
                        int n = G.Nbr[c * 4 + k];
                        if (n >= 0) { pressure += magma[n]; weight += 1f; }
                    }
                    float target = 2f * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.35f, 1.5f, pressure / weight));
                    rock[c] += target - magmaInflation[c];
                    magmaInflation[c] = target;
                }
            });
        }

        // ------------------------------------------------------------ dépressions / anticyclones (Coriolis)
        const int PressN = 14;
        Vector3[] pressDir;
        float[] pressAmp, pressSgn, pressRad, pressBase;
        int[] pressAge, pressLife;
        readonly PlanetRandom prnd = new PlanetRandom(4242);
        internal int WeatherRandomState { get { return prnd.State; } set { prnd.State = value; } }
        public readonly System.Collections.Generic.List<int> fumaroles = new System.Collections.Generic.List<int>();

        float Pr() { return (float)prnd.NextDouble(); }

        void SpawnPress(int i, bool initial)
        {
            bool low = i % 2 == 0;
            float lat = Mathf.Lerp(low ? 30f : 12f, low ? 68f : 34f, Pr()) * (Pr() < 0.5f ? -1f : 1f) * Mathf.Deg2Rad;
            float lon = Pr() * Mathf.PI * 2f;
            pressDir[i] = new Vector3(Mathf.Cos(lat) * Mathf.Cos(lon), Mathf.Sin(lat), Mathf.Cos(lat) * Mathf.Sin(lon));
            pressSgn[i] = low ? 1f : -1f;
            pressRad[i] = (low ? 0.24f : 0.3f) + 0.2f * Pr();
            pressBase[i] = low ? 0.7f + 0.7f * Pr() : 0.5f + 0.4f * Pr();
            pressLife[i] = 40 + prnd.Next(60);
            pressAge[i] = initial ? prnd.Next(pressLife[i]) : 0;
        }

        void StepPressure()
        {
            if (pressDir == null)
            {
                pressDir = new Vector3[PressN]; pressAmp = new float[PressN]; pressSgn = new float[PressN]; pressRad = new float[PressN];
                pressBase = new float[PressN]; pressAge = new int[PressN]; pressLife = new int[PressN];
                for (int i = 0; i < PressN; i++) SpawnPress(i, true);
            }
            for (int i = 0; i < PressN; i++)
            {
                if (++pressAge[i] >= pressLife[i]) SpawnPress(i, false);
                Vector3 d = pressDir[i];
                Vector3 east = new Vector3(d.z, 0f, -d.x);
                east = east.sqrMagnitude < 1e-6f ? Vector3.right : east.normalized;
                Vector3 north = Vector3.up - d * d.y;
                north = north.sqrMagnitude < 1e-6f ? Vector3.forward : north.normalized;
                float hs = d.y >= 0f ? 1f : -1f;
                // les dépressions dérivent vers l'est et les pôles, les anticyclones restent presque en place
                float se = pressSgn[i] > 0f ? 0.03f : -0.004f;
                float sn = pressSgn[i] > 0f ? 0.008f * hs : 0f;
                pressDir[i] = (d + east * se + north * sn).normalized;
                pressAmp[i] = pressBase[i] * Mathf.Sqrt(Mathf.Sin(Mathf.PI * pressAge[i] / pressLife[i]));
            }
        }

        // volcans fumants : zones de magma proche de la surface, émettent de la vapeur
        void RefreshFumaroles(System.Random rnd)
        {
            fumaroles.Clear();
            for (int i = 0; i < 3000 && fumaroles.Count < 24; i++)
            {
                int c = rnd.Next(M);
                if (magma[c] > 0.85f && rock[c] + sed[c] > 12f && lava[c] < 0.1f) fumaroles.Add(c);
            }
        }

        void StepWindErosion()
        {
            var state = aeolian;
            var nbr = G.Nbr;
            var tan = G.Tan;
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    state.target[c] = -1;
                    state.amount[c] = 0f;
                    float speed = wind[c].magnitude;
                    if (speed < 0.2f || sed[c] < 0.05f || water[c] > 0.15f || snow[c] > 0.5f) continue;

                    int b = c * 4, downwind = -1;
                    float direction = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        if (nbr[b + k] < 0) continue;
                        float d = Vector3.Dot(wind[c], tan[b + k]);
                        if (d > direction) { direction = d; downwind = k; }
                    }
                    if (downwind < 0) continue;

                    int m = c * 4;
                    float dirt = mix[m], sand = mix[m + 1], gravel = mix[m + 2], clay = mix[m + 3];
                    float threshold = 0.12f + dirt * 0.18f + gravel * 0.62f + clay * 0.35f;
                    float excess = Mathf.Max(0f, speed - threshold);
                    float exposure = (1f - 0.8f * cover[c]) * (1f - 0.75f * moist[c]);
                    exposure *= 1f - Mathf.Clamp01(water[c] * 2f);
                    float erodible = Mathf.Clamp01(sand + dirt * 0.65f + clay * 0.25f + gravel * 0.1f);

                    float random = Mathf.Repeat(Mathf.Sin((c + 1) * 31.7f + tick * 17.13f) * 15731.743f, 1f);
                    int maxDistance = Mathf.Clamp(1 + Mathf.FloorToInt(excess * 5f), 1, 5);
                    int distance = 1 + Mathf.FloorToInt(random * maxDistance);
                    int destination = c;
                    for (int step = 0; step < distance; step++)
                    {
                        int cell = destination, edge = -1;
                        float best = 0f;
                        for (int k = 0; k < 4; k++)
                        {
                            int n = nbr[cell * 4 + k];
                            if (n < 0) continue;
                            float d = Vector3.Dot(wind[cell], tan[cell * 4 + k]);
                            if (d > best) { best = d; edge = k; }
                        }
                        if (edge < 0) break;
                        destination = nbr[cell * 4 + edge];
                        if (water[destination] > 0.15f || snow[destination] > 0.5f ||
                            cover[destination] > 0.6f || wind[destination].magnitude < threshold * 0.75f) break;
                    }
                    if (destination == c) continue;

                    // Coefficient relevé (x2.2) pour une érosion/déflation éolienne (dunes, reg) bien plus visible.
                    float removed = ErodeSoil(c, Mathf.Min(sed[c], 0.0033f * excess * excess * exposure * erodible));
                    if (removed <= 1e-7f) continue;
                    int mixOffset = c * 4;
                    float remaining = sed[c];
                    state.composition[mixOffset] = Mathf.Max(0f, dirt * (remaining + removed) - mix[mixOffset] * remaining) / removed;
                    state.composition[mixOffset + 1] = Mathf.Max(0f, sand * (remaining + removed) - mix[mixOffset + 1] * remaining) / removed;
                    state.composition[mixOffset + 2] = Mathf.Max(0f, gravel * (remaining + removed) - mix[mixOffset + 2] * remaining) / removed;
                    state.composition[mixOffset + 3] = Mathf.Max(0f, clay * (remaining + removed) - mix[mixOffset + 3] * remaining) / removed;
                    state.target[c] = destination;
                    state.amount[c] = removed;
                }
            });

            for (int c = 0; c < M; c++) state.head[c] = -1;
            for (int c = 0; c < M; c++)
            {
                int destination = state.target[c];
                if (destination < 0) continue;
                state.next[c] = state.head[destination];
                state.head[destination] = c;
            }

            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    int b = c * 4;
                    float total = sed[c];
                    float m0 = mix[b] * total, m1 = mix[b + 1] * total;
                    float m2 = mix[b + 2] * total, m3 = mix[b + 3] * total;
                    for (int source = state.head[c]; source >= 0; source = state.next[source])
                    {
                        float amount = state.amount[source] * G.Area[source] / G.Area[c];
                        int sb = source * 4;
                        total += amount;
                        m0 += state.composition[sb] * amount;
                        m1 += state.composition[sb + 1] * amount;
                        m2 += state.composition[sb + 2] * amount;
                        m3 += state.composition[sb + 3] * amount;
                    }
                    waterN[c] = total;
                    if (total > 1e-7f)
                    {
                        mixN[b] = m0 / total; mixN[b + 1] = m1 / total;
                        mixN[b + 2] = m2 / total; mixN[b + 3] = m3 / total;
                    }
                    else
                    {
                        mixN[b] = mix[b]; mixN[b + 1] = mix[b + 1];
                        mixN[b + 2] = mix[b + 2]; mixN[b + 3] = mix[b + 3];
                    }
                    // bilan net érosion (-) / dépôt (+) éolien de cette cellule, pour la couche visuelle "érosion du vent"
                    float netWindChange = (total - sed[c]) - state.amount[c];
                    windErode[c] += (netWindChange * ErodeViewScale - windErode[c]) * ErodeViewRate;
                }
            });
            var sediment = sed; sed = waterN; waterN = sediment;
            var composition = mix; mix = mixN; mixN = composition;
        }

        // ------------------------------------------------------------ orages et cendres
        public void StepWeather(System.Random rnd)
        {
            if (tick % 40 == 0) RefreshFumaroles(rnd);
            if (rainK > 0f)
                for (int i = 0; i < fumaroles.Count; i++)
                {
                    int c = fumaroles[i];
                    float steam = Mathf.Min(gw[c], Mathf.Min(0.03f, Mathf.Max(0f, 1.2f - hum[c])) * rainK);
                    gw[c] -= steam;
                    hum[c] += steam / rainK;
                }
            if (tick % 2 != 0) return;
            StepWindErosion();
            var nbr = G.Nbr; var tan = G.Tan;
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    float a = ash[c];
                    Vector3 w = wind[c];
                    if (w.sqrMagnitude > 1e-4f)
                    {
                        int bk = -1; float bd = 0f;
                        for (int k = 0; k < 4; k++)
                        {
                            if (nbr[c * 4 + k] < 0) continue;
                            float d = -Vector3.Dot(w, tan[c * 4 + k]);
                            if (d > bd) { bd = d; bk = k; }
                        }
                        if (bk >= 0)
                        {
                            float f = Mathf.Min(0.45f, bd * 0.3f);
                            a = a * (1f - f) + ash[nbr[c * 4 + bk]] * f;
                        }
                    }
                    // Les dépôts solides sont déjà comptabilisés lors de l'éruption ; ici la cendre est un traceur visuel.
                    float loss = a * 0.03f;
                    a -= loss;
                    if (a < 0.003f) a = 0f;
                    ashN[c] = a;
                    if (loss > 0.002f && rock[c] + sed[c] > SeaLevel)
                    {
                        fert[c] = Mathf.Min(1f, fert[c] + loss * 0.2f);
                    }

                    float tgt = Mathf.Clamp01((rainNow[c] - 0.008f) / 0.03f) * Mathf.Clamp01((temp[c] - 4f) / 14f);
                    float st = storm[c] + (tgt - storm[c]) * (tgt > storm[c] ? 0.06f : 0.1f);
                    storm[c] = Mathf.Max(st, Mathf.Min(1f, a) * 0.9f);
                }
            });
            var t = ash; ash = ashN; ashN = t;

            for (int i = 0; i < volcanoes.Count; i++)
            {
                int vc = volcanoes[i].cell;
                float exposure = Mathf.Clamp01(1f - Mathf.Max(0f, SeaLevel - Ground(vc)) / 30f);
                float emit = (0.1f + 2.6f * volcanoes[i].power * volcanoes[i].env) * exposure;
                ash[vc] = Mathf.Min(3f, ash[vc] + emit);
                for (int k = 0; k < 4; k++)
                {
                    int n = G.Nbr[vc * 4 + k];
                    if (n >= 0) ash[n] = Mathf.Min(3f, ash[n] + 0.5f * emit);
                }
            }

            for (int i = 0; i < 24; i++)
            {
                int c = rnd.Next(M);
                float st = storm[c];
                if (st > 0.6f && ash[c] < 0.3f && rnd.NextDouble() < st * 0.3)
                {
                    if (strikes.Count < 12) strikes.Add(c);
                    if (onLightning != null) onLightning(c);
                }
                if (st > 0.85f && ash[c] < 0.3f && Time.realtimeSinceStartup - lastStormLog > 45f)
                {
                    lastStormLog = Time.realtimeSinceStartup;
                    Log("Tempête violente (" + Where(c) + ")", c);
                }
            }
        }

        // le vent s'écarte d'un volcan en éruption
        void VolcanoWind()
        {
            for (int i = 0; i < volcanoes.Count; i++)
            {
                int vc = volcanoes[i].cell;
                if (Ground(vc) < SeaLevel - 30f) continue;
                Region(vc, 8);
                for (int j = 0; j < regionA.Count; j++)
                {
                    int c = regionA[j];
                    Vector3 o = Vector3.ProjectOnPlane(G.Dir[c] - G.Dir[vc], G.Dir[c]);
                    if (o.sqrMagnitude < 1e-10f) continue;
                    wind[c] += o.normalized * (0.9f * (1f - regionD[j] / 9f));
                }
            }
        }
    }
}
