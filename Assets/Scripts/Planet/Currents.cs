using UnityEngine;

namespace LivingPlanet
{
    // Courants marins : vent, rotation et gradients de densité, contraints par les côtes.
    public sealed partial class PlanetSim
    {
        public Vector3[] current;
        // Courant profond (thermohaline) : bien plus lent et indépendant du vent, poussé par la différence de
        // densité entre eau froide/polaire et eau chaude/équatoriale plutôt que par la friction du vent en surface.
        public Vector3[] deepCurrent;
        Vector3[] currentN, deepCurrentN;
        float[] oceanTemp;

        public void StepOcean(float timeScale = 1f)
        {
            if (current == null) { current = new Vector3[M]; currentN = new Vector3[M]; oceanTemp = new float[M]; }
            if (deepCurrent == null) { deepCurrent = new Vector3[M]; deepCurrentN = new Vector3[M]; }
            if (tick % 10 == 0) ComputeCurrents();
            if (tick % 40 == 0) ComputeDeepCurrents();
            AdvectOceanHeat();
        }

        void ComputeCurrents()
        {
            var nbr = G.Nbr;
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    if (Ground(c) >= SeaLevel || water[c] <= 0.00001f) { currentN[c] = Vector3.zero; continue; }
                    Vector3 d = G.Dir[c];
                    // Signe lié au sens de rotation de la planète (vers l'est = longitudes croissantes).
                    float coriolis = Mathf.Clamp(-2f * d.y * spinRate, -1f, 1f);
                    Vector3 driven = Quaternion.AngleAxis(45f * coriolis, d) * Vector3.ProjectOnPlane(wind[c], d) * 0.5f;
                    Vector3 gradient = DensityGradient(c);
                    Vector3 target = driven - gradient * 0.012f + Vector3.Cross(d, gradient) * (0.06f * coriolis);
                    Vector3 old = current[c];
                    Vector3 previousDir = (d - old * (G.CellSize / G.Radius)).normalized;
                    int source = G.CellFromDir(previousDir);
                    if (Ground(source) < SeaLevel && water[source] > 0.00001f)
                        old = Vector3.ProjectOnPlane(current[source], d);
                    old = Quaternion.AngleAxis(8f * coriolis, d) * old;
                    Vector3 v = Vector3.Lerp(old, Vector3.ClampMagnitude(target, 1f), 0.25f);
                    currentN[c] = SlideAlongCoast(c, Vector3.ClampMagnitude(v, 1f));
                }
            });
            var oldCurrent = current; current = currentN; currentN = oldCurrent;
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    if (Ground(c) >= SeaLevel || water[c] <= 0.00001f) { currentN[c] = Vector3.zero; continue; }
                    Vector3 sum = current[c] * 2f; float w = 2f;
                    for (int k = 0; k < 4; k++)
                    {
                        int n = nbr[c * 4 + k];
                        if (n < 0 || Ground(n) >= SeaLevel || water[n] <= 0.00001f) continue;
                        sum += Vector3.ProjectOnPlane(current[n], G.Dir[c]); w += 1f;
                    }
                    currentN[c] = SlideAlongCoast(c, sum / w);
                }
            });
            var t = current; current = currentN; currentN = t;
        }

        // l'eau ne traverse pas la terre : on retire la composante qui pointe vers une côte
        Vector3 SlideAlongCoast(int c, Vector3 v)
        {
            for (int k = 0; k < 4; k++)
            {
                int n = G.Nbr[c * 4 + k];
                if (n < 0 || (Ground(n) < SeaLevel && water[n] > 0.00001f)) continue;
                Vector3 t = G.Tan[c * 4 + k];
                float dt = Vector3.Dot(v, t);
                if (dt > 0f) v -= t * dt;
            }
            return v;
        }

        // Approximation de densité relative à partir de la température et de la salinité disponibles par cellule.
        float OceanDensity(int c)
        {
            return Salinity(c) * 800f - temp[c] * 0.2f;
        }

        Vector3 DensityGradient(int c)
        {
            float density = OceanDensity(c);
            Vector3 gradient = Vector3.zero;
            int count = 0;
            for (int k = 0; k < 4; k++)
            {
                int edge = c * 4 + k;
                int n = G.Nbr[edge];
                if (n < 0 || Ground(n) >= SeaLevel || water[n] <= 0.00001f) continue;
                gradient += G.Tan[edge] * ((OceanDensity(n) - density) * G.CellSize / Mathf.Max(G.Dist[edge], 0.0001f));
                count++;
            }
            return count > 0 ? gradient / count : Vector3.zero;
        }

        // Sans réservoir thermique profond distinct, le gradient local disponible sert de proxy thermohalin.
        void ComputeDeepCurrents()
        {
            var nbr = G.Nbr;
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    if (Ground(c) >= SeaLevel || water[c] <= 0.00001f) { deepCurrent[c] = Vector3.zero; continue; }
                    Vector3 driven = Vector3.ClampMagnitude(-DensityGradient(c) * 0.03f, 0.15f);
                    deepCurrent[c] = SlideAlongCoast(c, Vector3.Lerp(deepCurrent[c], driven, 0.2f));
                }
            });
            for (int it = 0; it < 3; it++)
            {
                Par.For(M, (s, e) =>
                {
                    for (int c = s; c < e; c++)
                    {
                        if (Ground(c) >= SeaLevel || water[c] <= 0.00001f) { deepCurrentN[c] = Vector3.zero; continue; }
                        Vector3 sum = deepCurrent[c] * 2f; float w = 2f;
                        for (int k = 0; k < 4; k++)
                        {
                            int n = nbr[c * 4 + k];
                            if (n < 0 || Ground(n) >= SeaLevel || water[n] <= 0.00001f) continue;
                            sum += Vector3.ProjectOnPlane(deepCurrent[n], G.Dir[c]); w += 1f;
                        }
                        deepCurrentN[c] = SlideAlongCoast(c, sum / w);
                    }
                });
                var t = deepCurrent; deepCurrent = deepCurrentN; deepCurrentN = t;
            }
        }

        void AdvectOceanHeat()
        {
            var nbr = G.Nbr; var back = G.Back; var tan = G.Tan;
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    float t0 = temp[c];
                    if (Ground(c) >= SeaLevel || water[c] <= 0.00001f) { oceanTemp[c] = t0; continue; }
                    float t = t0;
                    for (int k = 0; k < 4; k++)
                    {
                        int n = nbr[c * 4 + k];
                        if (n < 0 || Ground(n) >= SeaLevel || water[n] <= 0.00001f) continue;
                        float inflow = Vector3.Dot(current[n], tan[n * 4 + back[c * 4 + k]]);
                        if (inflow > 0f) t += (temp[n] - t0) * Mathf.Min(0.2f, inflow * 0.5f);
                    }
                    oceanTemp[c] = t;
                }
            });
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++) if (Ground(c) < SeaLevel && water[c] > 0.00001f) temp[c] = oceanTemp[c];
            });
        }
    }
}
