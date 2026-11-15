using System;
using UnityEngine;

namespace LivingPlanet
{
    // Ressources alimentaires diffuses par cellule : base du reseau trophique (plancton -> krill, vegetation -> insectes)
    // et du recyclage (charogne -> fertilite du sol / nutriments marins). Tout est piloté par le climat, le sol et la flore existants.
    public sealed class EcoFields
    {
        public readonly float[] plankton, krill, nut, insects, carrion, fruit;
        readonly PlanetSim S;
        readonly Ecosystem E;
        readonly int M;

        public EcoFields(PlanetSim s, Ecosystem ecosystem)
        {
            S = s; E = ecosystem; M = s.M;
            plankton = new float[M]; krill = new float[M]; nut = new float[M]; insects = new float[M]; carrion = new float[M];
            fruit = new float[M];
            for (int c = 0; c < M; c++) nut[c] = 0.5f;
            for (int i = 0; i < 4; i++) Step(6f);
        }

        static float Tr(float x, float a, float b, float c, float d)
        {
            if (x <= a || x >= d) return 0f;
            if (x < b) return (x - a) / (b - a);
            if (x <= c) return 1f;
            return (d - x) / (d - c);
        }

        public bool IsOcean(int c) { return S.Ground(c) < S.SeaLevel - 1.2f && S.water[c] > 1.2f; }

        public void AddCarrion(int c, float v) { carrion[c] = Mathf.Min(2f, carrion[c] + v); }

        public float TakeFruit(int c, float amount)
        {
            if (E.FruitType(c) < 0 || amount <= 0f) return 0f;
            float taken = Mathf.Min(fruit[c], amount);
            fruit[c] -= taken;
            return taken;
        }

        // days : jours de jeu écoulés
        public void Step(float days)
        {
            float age = S.planetAge;
            float kNut = 1f - Mathf.Exp(-0.15f * days), kPlk = 1f - Mathf.Exp(-0.9f * days);
            float kKri = 1f - Mathf.Exp(-0.35f * days), kIns = 1f - Mathf.Exp(-0.5f * days), kCar = 1f - Mathf.Exp(-0.7f * days);
            float kFruit = 1f - Mathf.Exp(-0.15f * days);
            float plkOn = age >= 0.6f ? 1f : 0f, kriOn = age >= 1f ? 1f : 0f, insOn = age >= 2.5f ? 1f : 0f;
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    float g = S.Ground(c) - S.SeaLevel;
                    float decay = carrion[c] * kCar;
                    carrion[c] -= decay;
                    int plant = E.cellPlant[c];
                    float fruitCapacity = 0f;
                    if (plant >= 0 && E.FruitType(c) >= 0)
                    {
                        var p = E.plants[plant];
                        fruitCapacity = Mathf.Clamp01(p.size) * Mathf.Clamp01(p.health);
                    }
                    fruit[c] = Mathf.Min(fruit[c], fruitCapacity);
                    fruit[c] += (fruitCapacity - fruit[c]) * kFruit;
                    if (IsOcean(c))
                    {
                        float shallow = Mathf.Clamp01(1.1f - (-g) / 25f);
                        // L'eau de fonte des glaces (banquise, glaciers, icebergs à la dérive) est riche en
                        // nutriments : elle alimente des efflorescences de plancton près des glaces, comme dans la réalité.
                        // La circulation profonde (thermohaline) remonte aussi des nutriments en profondeur : plus elle
                        // est active localement (zones de remontée), plus elle enrichit la couche de surface.
                        float upwelling = S.deepCurrent != null ? S.deepCurrent[c].magnitude * 1.5f : 0f;
                        nut[c] += (0.25f + 0.75f * shallow - nut[c]) * kNut + decay * 0.5f + S.iceMeltNutrient[c] * 0.3f + upwelling * 0.2f;
                        if (nut[c] > 1.5f) nut[c] = 1.5f;
                        float light = Mathf.Clamp01(Mathf.Cos(S.latA[c] * 0.9f) * 1.1f);
                        float pt = light * Tr(S.temp[c], -2f, 6f, 24f, 34f) * Mathf.Clamp01(nut[c]) * plkOn;
                        float p = plankton[c];
                        p += (pt - p) * kPlk - 0.12f * krill[c] * days;
                        plankton[c] = p < 0f ? 0f : p;
                        float k = krill[c];
                        k += (Mathf.Clamp01(plankton[c] * 1.1f) * kriOn - k) * kKri;
                        krill[c] = k < 0f ? 0f : k;
                        insects[c] = 0f;
                    }
                    else
                    {
                        plankton[c] = 0f; krill[c] = 0f;
                        float target = 0f;
                        if (g > 0.5f && S.burn[c] < 0.2f && S.lava[c] < 0.1f)
                        {
                            float tf = Tr(S.temp[c], -4f, 10f, 32f, 46f);
                            float mf = Mathf.Clamp01(S.moist[c] * 1.4f + 0.25f * Mathf.Min(1f, S.water[c]));
                            float veg = Mathf.Clamp01(S.cover[c] * 1.2f + S.grass[c] * 0.6f);
                            float sn = 1f - 0.8f * Mathf.Clamp01(S.snow[c] / 3f);
                            target = tf * mf * (0.15f + 0.85f * veg) * (0.5f + 0.5f * Mathf.Clamp01(S.fert[c] * 2f + 0.3f)) * sn * insOn;
                        }
                        insects[c] += (target - insects[c]) * kIns;
                        if (decay > 0f) S.fert[c] = Mathf.Min(1f, S.fert[c] + decay * 0.2f);
                    }
                }
            });
        }

        // moyennes pour les graphiques : plancton, krill (océan), insectes, charogne (terre)
        public void Means(float[] o)
        {
            double p = 0, k = 0, i = 0, cr = 0; int no = 0, nl = 0;
            for (int c = 0; c < M; c += 3)
            {
                if (IsOcean(c)) { p += plankton[c]; k += krill[c]; no++; }
                else { i += insects[c]; nl++; }
                cr += carrion[c];
            }
            o[0] = no > 0 ? (float)(p / no) : 0f; o[1] = no > 0 ? (float)(k / no) : 0f;
            o[2] = nl > 0 ? (float)(i / nl) : 0f; o[3] = (float)(cr / Math.Max(1, (no + nl)));
        }
    }
}
