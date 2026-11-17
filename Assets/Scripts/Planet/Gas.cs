using UnityEngine;

namespace LivingPlanet
{
    // Gaz atmosphériques : O2 (%), CO2 (ppm), CH4 (ppm), SO2 (ppm). Transportés par le vent, produits/consommés par la vie,
    // le feu, les océans, le magma et la tectonique.
    public sealed partial class PlanetSim
    {
        public const float O2Base = 21f, CO2Base = 420f, CH4Base = 1.9f;
        public float[] o2, co2, ch4, so2, o2N, co2N, ch4N, so2N;
        public float[] animalBreath;

        // Âge de la planète (milliards d'années) : fixe l'état initial (tectonique, volcanisme, atmosphère, vie)
        public float planetAge = 4.5f;
        public float spinRate = 1f;                 // 1 = journée de 48 min ; plus rapide => Coriolis plus fort
        public float co2Eq = CO2Base, o2Eq = O2Base;
        public float Youth { get { return Mathf.Clamp01(1f - planetAge / 4.5f); } }
        public float Oldness { get { return Mathf.Clamp01((planetAge - 4.5f) / 4f); } }
        public float LifeLevel { get { return Mathf.Clamp01((planetAge - 0.3f) / 2.2f); } }
        // multiplicateur d'activité interne : jeune = chaude et agitée, vieille = refroidie
        public float Activity { get { return (1f + 1.2f * Youth) * (1f - 0.7f * Oldness); } }

        public void ApplyAge(float age)
        {
            planetAge = Mathf.Clamp(age, 0.3f, 10f);
            float y = Youth, o = Oldness;
            co2Eq = CO2Base * (1f + 6f * y * y) + 2600f * y * y;
            o2Eq = Mathf.Lerp(O2Base, 11f, Mathf.Clamp01(y * 1.3f)) + 1.5f * o;
            float act = Activity;
            plateSpeed = 0.064f * act;
            orogeny = 0.0004f * act;
            if (o2 != null) for (int c = 0; c < M; c++) { o2[c] = o2Eq; co2[c] = co2Eq; }
        }

        void InitGas()
        {
            o2 = new float[M]; co2 = new float[M]; ch4 = new float[M]; so2 = new float[M];
            o2N = new float[M]; co2N = new float[M]; ch4N = new float[M]; so2N = new float[M];
            animalBreath = new float[M];
            for (int c = 0; c < M; c++) { o2[c] = o2Eq; co2[c] = co2Eq; ch4[c] = CH4Base; }
        }

        public void StepGas()
        {
            var nbr = G.Nbr; var back = G.Back; var dir = G.Dir;
            // conversion de la vitesse du vent en fraction échangée avec chaque voisine
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    int b = c * 4;
                    float a = o2[c], bb = co2[c], m = ch4[c], so = so2[c];
                    float da = 0f, db = 0f, dm = 0f, ds = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        int n = nbr[b + k];
                        if (n < 0) continue;
                        Vector3 toC = (dir[c] - dir[n]);
                        float sp = Vector3.Dot(wind[n], toC) / Mathf.Max(1e-5f, toC.magnitude);
                        float f = Mathf.Clamp(sp * 0.12f, 0f, 0.2f) + 0.02f;
                        da += (o2[n] - a) * f; db += (co2[n] - bb) * f; dm += (ch4[n] - m) * f; ds += (so2[n] - so) * f;
                    }
                    a += da; bb += db; m += dm; so += ds;

                    float g = rock[c] + sed[c];
                    bool ocean = g <= SeaLevel;
                    float warm = Mathf.Clamp01((temp[c] + 5f) / 30f);
                    // Respiration animale : consommée partout, sinon elle s'accumulait sans fin au-dessus de la
                    // mer (oiseaux en vol) et se libérait d'un coup si la cellule émergeait.
                    float an = animalBreath[c];
                    if (an > 0f) { a -= an * 0.01f; bb += an * 2f; animalBreath[c] = 0f; }
                    if (!ocean)
                    {
                        float veg = Mathf.Clamp01(cover[c]);
                        a += veg * warm * 0.006f; bb -= veg * warm * 0.6f;
                        float rot = fert[c] * (water[c] > 0.3f ? 1f : 0.2f);
                        bb += rot * 0.15f * warm; m += rot * (water[c] > 0.3f ? 0.01f : 0.001f) * warm;
                        float fr = fire[c];
                        if (fr > 0f) { a -= fr * 0.03f; bb += fr * 6f; m += fr * 0.02f; }
                        if (lava[c] > 0.1f) { bb += 5f; so += 0.6f; }
                        // dégazage de la croûte aux zones de forte contrainte (failles, rifts)
                        float st = stress[c];
                        if (st > 0.5f) { bb += (st - 0.5f) * 0.4f; m += (st - 0.5f) * 0.002f; so += (st - 0.5f) * 0.005f; }
                    }
                    else
                    {
                        // l'océan absorbe le CO2 (froid surtout) et le phytoplancton produit de l'O2
                        bb -= (bb - co2Eq * 0.9f) * (0.004f + 0.004f * (1f - warm));
                        a += 0.0008f * warm;
                    }

                    // retour vers l'équilibre
                    a += (o2Eq - a) * 0.003f; bb += (co2Eq - bb) * 0.003f; m += (CH4Base - m) * 0.01f; so -= so * 0.02f;
                    o2N[c] = Mathf.Clamp(a, 5f, 35f); co2N[c] = Mathf.Clamp(bb, 100f, 20000f);
                    ch4N[c] = Mathf.Clamp(m, 0f, 500f); so2N[c] = Mathf.Max(0f, so);
                }
            });
            var t = o2; o2 = o2N; o2N = t;
            t = co2; co2 = co2N; co2N = t;
            t = ch4; ch4 = ch4N; ch4N = t;
            t = so2; so2 = so2N; so2N = t;

            // les cheminées volcaniques dégazent en continu
            for (int i = 0; i < fumaroles.Count; i++)
            {
                int c = fumaroles[i];
                co2[c] += 40f; so2[c] += 3f;
            }
        }
    }
}
