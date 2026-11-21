using UnityEngine;

namespace LivingPlanet
{
    // Couverture nuageuse 0..1 : condensation selon l'humidité relative, la pression (subsidence sous un anticyclone,
    // ascendance dans une dépression), le relief face au vent et les fronts. Elle réagit lentement : les nuages se forment
    // et se dissipent en plusieurs jours, et le beau temps reste possible même au-dessus de l'océan.
    public sealed partial class PlanetSim
    {
        public float[] cloud;

        public void StepClouds()
        {
            if (cloud == null) cloud = new float[M];
            var nbr = G.Nbr; var tan = G.Tan; var dist = G.Dist;
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    float sat = Mathf.Max(Sat(temp[c]), 0.05f);
                    // Un air saturé ne suffit pas à couvrir le ciel : au-delà de 80 % d'humidité, ce sont les ascendances
                    // (dépressions, fronts, relief) qui décident, sinon les mers froides restent sous une bande grise continue.
                    float rh = Mathf.Min(hum[c] / sat, 0.8f);
                    float g = Mathf.Max(rock[c] + sed[c], 0f);
                    Vector3 w = wind[c];
                    float lift = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        int n = nbr[c * 4 + k];
                        if (n < 0) continue;
                        float dd = Vector3.Dot(w, tan[c * 4 + k]);
                        if (dd > 0f) lift += dd * (Mathf.Max(rock[n] + sed[n], 0f) - g) / dist[c * 4 + k];
                    }
                    lift = Mathf.Clamp(lift * 6f, 0f, 0.5f);
                    float pr = pres != null ? pres[c] : 0f;
                    float rhEff = rh + 0.05f * Mathf.Clamp01(-pr) - 0.12f * Mathf.Clamp01(pr) + 0.5f * lift + 0.8f * (frontN[c] - 0.2f);
                    float tgt = Mathf.Clamp01((rhEff - 0.78f) / 0.22f);
                    tgt = Mathf.Max(tgt, Mathf.Clamp01(rainNow[c] * 60f));
                    tgt = Mathf.Max(tgt, Mathf.Min(1f, ash[c]));
                    float cl = cloud[c];
                    cloud[c] = cl + (tgt - cl) * (tgt > cl ? 0.15f : 0.1f);
                }
            });
        }
    }
}
