using System.Collections.Generic;
using UnityEngine;

namespace LivingPlanet
{
    // Feu : intensité par cellule, alimenté par l'herbe et les plantes, propagé par le vent, éteint par la pluie.
    // Après le feu (et le magma) le sol est plus fertile et plus perméable.
    public sealed partial class PlanetSim
    {
        public float[] fire;
        public List<int> fireCells = new List<int>();
        readonly PlanetRandom fireRnd = new PlanetRandom(77);
        internal int FireRandomState { get { return fireRnd.State; } set { fireRnd.State = value; } }

        public int FireCount { get { return fireCells.Count; } }

        public void StartFire(int c, float amount)
        {
            if (fire == null) fire = new float[M];
            if (rock[c] + sed[c] <= SeaLevel || lava[c] > 0.1f || water[c] > 0.3f) return;
            if (fire[c] <= 0f) fireCells.Add(c);
            fire[c] = Mathf.Max(fire[c], amount);
        }

        public void StepFire()
        {
            if (fire == null) fire = new float[M];
            var nbr = G.Nbr;
            for (int i = fireCells.Count - 1; i >= 0; i--)
            {
                int c = fireCells[i];
                float f = fire[c];
                float fuel = Mathf.Clamp01(grass[c] + cover[c] * 1.5f) * (1f - 0.8f * burn[c]);
                f += 0.05f * (fuel - 0.22f) - 0.006f;
                // le combustible se consume : sans lui le feu retombe et s'éteint
                grass[c] *= 0.96f;
                cover[c] *= 0.995f;
                burn[c] = Mathf.Min(1f, burn[c] + 0.025f);
                // la pluie, la neige et l'eau éteignent d'autant plus vite que le feu est faible
                f -= rainNow[c] * 5f * (1.3f - f * 0.7f);
                if (snow[c] > 0.3f || water[c] > 0.3f) f -= 0.3f;
                if (f > 1f) f = 1f;

                if (f <= 0.03f)
                {
                    fire[c] = 0f;
                    fireCells.RemoveAt(i);
                    burn[c] = 1f;
                    fert[c] = Mathf.Min(1f, fert[c] + 0.3f);
                    permB[c] = Mathf.Max(permB[c], 0.3f);
                    continue;
                }
                fire[c] = f;

                Vector3 wd = wind[c];
                float wm = wd.magnitude;
                if (wm > 1e-4f) wd /= wm;
                for (int k = 0; k < 4; k++)
                {
                    int n = nbr[c * 4 + k];
                    if (n < 0 || fire[n] > 0f) continue;
                    if (rock[n] + sed[n] <= SeaLevel || burn[n] > 0.6f) continue;
                    float fuelN = Mathf.Clamp01(grass[n] + cover[n] * 1.5f);
                    float along = Mathf.Max(0f, Vector3.Dot(wd, (G.Dir[n] - G.Dir[c]).normalized));
                    float p = f * (1f - moist[n]) * fuelN * (0.1f + 0.4f * along * Mathf.Min(wm, 1.5f));
                    if ((float)fireRnd.NextDouble() < p) StartFire(n, 0.35f);
                }
            }

            if (tick % 10 == 0)
            {
                // le magma enrichit et fissure le sol, et peut allumer la végétation voisine
                Par.For(M, (s, e) =>
                {
                    for (int c = s; c < e; c++)
                    {
                        if (lava[c] <= 0.1f) continue;
                        fert[c] = Mathf.Min(1f, fert[c] + 0.02f);
                        if (permB[c] < 0.3f) permB[c] = 0.3f;
                    }
                });
                for (int c = 0; c < M; c += 1)
                {
                    if (lava[c] <= 0.1f) continue;
                    for (int k = 0; k < 4; k++)
                    {
                        int n = nbr[c * 4 + k];
                        if (n >= 0 && lava[n] <= 0.1f && burn[n] < 0.6f && grass[n] + cover[n] > 0.25f && fireRnd.NextDouble() < 0.1) StartFire(n, 0.5f);
                    }
                }
            }
        }
    }
}
