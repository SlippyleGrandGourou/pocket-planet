using System.Collections.Generic;
using UnityEngine;

namespace LivingPlanet
{
    // Un fragment de glace détaché (banquise ou front glaciaire) qui dérive au gré des courants/vents puis fond.
    public struct Iceberg
    {
        public Vector3 dir;   // position (direction unitaire sur la sphère)
        public float size;    // volume en multiples de six fois l'aire moyenne d'une cellule
        public float age;
    }

    // Banquise (mer gelée en surface), glaciers de montagne qui vêlent des icebergs, et fonte qui relâche de
    // l'eau douce et des nutriments (bloom de plancton près des glaces) : s'appuie uniquement sur les champs
    // déjà simulés (temp, snow, courants, vent) pour rester bon marché.
    public sealed partial class PlanetSim
    {
        public float[] seaIce, glacier, iceMeltNutrient;
        public readonly List<Iceberg> icebergs = new List<Iceberg>();
        const float SeaIceFreeze = -1.8f, SeaIceMeltT = 0.5f, SeaIceCap = 6f;
        const float GlacierAltitude = 90f, GlacierCap = 40f;

        float MeanCellArea { get { return 4f * Mathf.PI * G.Radius * G.Radius / M; } }

        internal void MigrateIcebergVolumes()
        {
            for (int i = 0; i < icebergs.Count; i++)
            {
                var iceberg = icebergs[i];
                iceberg.size *= G.Area[G.CellFromDir(iceberg.dir)] / MeanCellArea;
                icebergs[i] = iceberg;
            }
        }

        void InitIce()
        {
            seaIce = new float[M];
            glacier = new float[M];
            iceMeltNutrient = new float[M];
        }

        public void StepIce(System.Random rnd)
        {
            if (seaIce == null) InitIce();
            var nbr = G.Nbr;

            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    iceMeltNutrient[c] *= 0.9f; // s'estompe si rien ne la renouvelle localement
                    float g = Ground(c);
                    float t = temp[c];
                    if (g < SeaLevel)
                    {
                        float ice = seaIce[c];
                        if (t < SeaIceFreeze)
                        {
                            float freeze = Mathf.Min(SeaIceCap - ice, (SeaIceFreeze - t) * 0.02f);
                            float frozenWater = Mathf.Min(freeze * 0.2f, water[c]);
                            water[c] -= frozenWater;
                            ice += frozenWater / 0.2f;
                        }
                        else if (t > SeaIceMeltT)
                        {
                            float melt = Mathf.Min(ice, (t - SeaIceMeltT) * 0.05f);
                            if (melt > 0f)
                            {
                                ice -= melt;
                                // L'eau de fonte de la banquise est douce même en pleine mer salée.
                                water[c] += melt * 0.2f;
                                iceMeltNutrient[c] = Mathf.Min(1.5f, iceMeltNutrient[c] + melt * 0.6f);
                            }
                        }
                        seaIce[c] = ice;
                    }
                    else
                    {
                        water[c] += seaIce[c] * 0.2f;
                        seaIce[c] = 0f;
                        // Au-delà du stock saisonnier de neige (déjà géré par "snow", qui fond chaque année), un
                        // excédent qui survit au froid en haute altitude se tasse lentement en glace de glacier,
                        // persistante d'une année sur l'autre au lieu de fondre intégralement chaque saison.
                        float gl = glacier[c];
                        if (g - SeaLevel > GlacierAltitude && snow[c] > 20f && t < -1f)
                        {
                            float compact = Mathf.Min(Mathf.Min(snow[c] - 20f, 0.02f), Mathf.Max(0f, GlacierCap - gl));
                            snow[c] -= compact;
                            gl += compact;
                        }
                        if (gl > 0f && t > 1f)
                        {
                            float melt = Mathf.Min(gl, (t - 1f) * 0.01f);
                            gl -= melt;
                            water[c] += melt;
                        }
                        glacier[c] = gl;
                    }
                }
            });

            // Vêlage : un glacier épais qui atteint la mer, ou une banquise épaisse en bordure d'eau libre, se
            // détache ponctuellement en iceberg qui dérive ensuite avec les courants/le vent. L'érosion glaciaire
            // associée use légèrement le sédiment du front, comme un vrai glacier qui racle son lit en avançant.
            if (icebergs.Count < 48)
            {
                for (int tries = 0; tries < 6; tries++)
                {
                    int c = rnd.Next(M);
                    bool ocean = Ground(c) < SeaLevel;
                    float ice = ocean ? seaIce[c] : glacier[c];
                    float capFrac = ocean ? SeaIceCap * 0.6f : GlacierCap * 0.5f;
                    if (ice < capFrac) continue;
                    bool nearOpenSea = false;
                    for (int k = 0; k < 4; k++)
                    {
                        int n = nbr[c * 4 + k];
                        if (n < 0) continue;
                        if (Ground(n) < SeaLevel && seaIce[n] < SeaIceCap * 0.3f) { nearOpenSea = true; break; }
                    }
                    if (!nearOpenSea || rnd.NextDouble() > 0.25) continue;
                    float taken = Mathf.Min(6f, ice * 0.3f * (ocean ? 0.2f : 1f));
                    if (ocean) seaIce[c] -= taken / 0.2f;
                    else
                    {
                        glacier[c] -= taken;
                        float eroded = Mathf.Min(sed[c], taken * 0.05f);
                        sed[c] -= eroded;
                        susp[c] += eroded;
                    }
                    icebergs.Add(new Iceberg { dir = G.Dir[c], size = taken / 6f * G.Area[c] / MeanCellArea, age = 0f });
                    Log("Un iceberg se détache à " + Where(c), c);
                    break;
                }
            }

            for (int i = icebergs.Count - 1; i >= 0; i--)
            {
                var ib = icebergs[i];
                int c = G.CellFromDir(ib.dir);
                Vector3 drift = Vector3.zero;
                if (current != null) drift += current[c];
                drift += wind[c] * 0.08f;
                drift = Vector3.ProjectOnPlane(drift, ib.dir);
                if (drift.sqrMagnitude > 1e-9f) ib.dir = (ib.dir + drift * 0.01f).normalized;
                bool ocean = Ground(c) < SeaLevel;
                float melt = Mathf.Min(ib.size, Mathf.Max(0.001f, (temp[c] + 1f) * 0.0025f) * (ocean ? 1f : 2.5f));
                ib.size -= melt;
                ib.age += 1f;
                water[c] += melt * 6f * MeanCellArea / G.Area[c];
                if (ocean)
                {
                    iceMeltNutrient[c] = Mathf.Min(1.5f, iceMeltNutrient[c] + melt * 1.2f);
                }
                if (ib.size <= 0.01f || ib.age > 4000f)
                {
                    water[c] += ib.size * 6f * MeanCellArea / G.Area[c];
                    icebergs.RemoveAt(i);
                    continue;
                }
                icebergs[i] = ib;
            }
        }
    }
}
