using System.Collections.Generic;
using UnityEngine;

namespace LivingPlanet
{
    // Éruptions : explosion initiale (cratère, projections qui bâtissent un cône), coulées de lave qui descendent les pentes,
    // panache de cendres emporté par le vent (voir StepWeather) et effondrement final (caldeira) pour les grosses éruptions.
    public sealed partial class PlanetSim
    {
        struct LavaFlow { public int cell, steps; public float vol; }

        readonly List<LavaFlow> lavaFlows = new List<LavaFlow>(64);
        readonly PlanetRandom volRnd = new PlanetRandom(9137);
        internal int VolcanoRandomState { get { return volRnd.State; } set { volRnd.State = value; } }

        public void MaterialIndices(out double geological, out double waterTotal, out double salt)
        {
            double geologicalArea;
            MaterialIndices(out geological, out waterTotal, out salt, out geologicalArea);
        }

        public void MaterialIndices(out double geological, out double waterTotal, out double salt, out double geologicalArea)
        {
            double waterArea, saltArea;
            MaterialIndices(out geological, out waterTotal, out salt, out geologicalArea, out waterArea, out saltArea);
        }

        public void MaterialIndices(out double geological, out double waterTotal, out double salt, out double geologicalArea,
            out double waterArea, out double saltArea)
        {
            geological = 0d; waterTotal = 0d; salt = 0d; geologicalArea = 0d; waterArea = 0d; saltArea = 0d;
            // Normalisation : le poids moyen d'une cellule vaut un.
            double areaScale = M / (4d * System.Math.PI * G.Radius * G.Radius);
            for (int c = 0; c < M; c++)
            {
                double column = (double)rock[c] - magmaInflation[c] + sed[c] + susp[c] + mantleSolid[c] + mantleResidual[c] + magma[c] * MagmaSolidScale;
                for (int layer = 0; layer < SubductionLayerCount; layer++)
                    column += subductionLayers[c * SubductionLayerCount + layer];
                geological += column;
                geologicalArea += column * G.Area[c] * areaScale;
                double waterColumn = water[c] + gw[c] + snow[c] + glacier[c] + seaIce[c] * 0.2f +
                    hum[c] * rainK + (evap == null ? 0f : evap[c]);
                double saltColumn = waterState.salt[c] + waterState.deposit[c];
                waterTotal += waterColumn;
                salt += saltColumn;
                waterArea += waterColumn * G.Area[c] * areaScale;
                saltArea += saltColumn * G.Area[c] * areaScale;
            }
            for (int i = 0; i < lavaFlows.Count; i++)
            {
                geological += lavaFlows[i].vol;
                geologicalArea += lavaFlows[i].vol * G.Area[lavaFlows[i].cell] * areaScale;
            }
            for (int i = 0; i < icebergs.Count; i++)
            {
                waterTotal += icebergs[i].size * 6f * (4d * System.Math.PI * G.Radius * G.Radius / M) / G.Area[G.CellFromDir(icebergs[i].dir)];
                waterArea += icebergs[i].size * 6f;
            }
        }

        public void StartVolcano(int cell)
        {
            if (cell < 0 || cell >= M) return;
            float r = (float)volRnd.NextDouble();
            float p = 0.15f + 0.85f * r * r * Mathf.Clamp(0.6f + magma[cell], 0.6f, 1.6f);
            StartVolcano(cell, Mathf.Clamp(p, 0.15f, 1f));
        }

        // power 0..1 : intensité (quantité de magma + explosivité)
        public void StartVolcano(int cell, float power)
        {
            if (cell < 0 || cell >= M) return;
            for (int i = 0; i < volcanoes.Count; i++)
                if (volcanoes[i].cell == cell) return;
            power = Mathf.Clamp01(power);
            float recharge = Mathf.Min(Mathf.Max(0f, 0.9f - magma[cell]), mantleSolid[cell] / MagmaSolidScale);
            mantleSolid[cell] -= recharge * MagmaSolidScale;
            magma[cell] += recharge;
            if (magma[cell] < 0.04f)
            {
                Log("Éruption impossible : réservoir magmatique épuisé (" + Where(cell) + ")", cell);
                return;
            }
            int total = 70 + (int)(230f * power);
            volcanoes.Add(new Volcano { cell = cell, left = total, total = total, age = 0, power = power, env = 0f });
            newQuakes.Add(new QuakeEvent { dir = G.Dir[cell], mag = 4f + 2.5f * power });
            float explosivity = VolcanoExplosivity(cell);
            string kind = explosivity > 0.65f ? "stratovolcan" : (explosivity < 0.35f ? "volcan-bouclier" : "cône de scories");
            Log("Éruption de " + kind + " (" + Where(cell) + ")", cell);
        }

        float VolcanoExplosivity(int c)
        {
            float subduction = tectonics && conv != null ? conv[c] : 0f;
            float mantle = (tectonics && rift != null ? rift[c] : 0f) + plumeInf[c];
            float explosive = Mathf.Clamp01(0.2f + 0.8f * subduction / (subduction + mantle + 0.15f));
            return explosive * Mathf.Lerp(1f, 0.3f, Mathf.Clamp01((SeaLevel - Ground(c)) / 25f));
        }

        float VolcanoEnvelope(float prog)
        {
            return prog < 0.12f ? prog / 0.12f : Mathf.Exp(-(prog - 0.12f) * 2.2f);
        }

        void StepVolcanoes()
        {
            for (int i = volcanoes.Count - 1; i >= 0; i--)
            {
                var v = volcanoes[i];
                int vc = v.cell;
                float prog = v.age / (float)v.total;
                float env = VolcanoEnvelope(prog);
                float pw = v.power;
                float explosive = VolcanoExplosivity(vc);
                v.env = env;
                terrainDirty = true;

                if (v.age == 0 && pw * explosive > 0.25f) Blast(vc, pw * explosive);

                float draw = Mathf.Min(magma[vc], 0.012f * (0.4f + pw) * env);
                magma[vc] -= draw;
                float erupted = draw * MagmaSolidScale;
                float tephra = erupted * explosive * 0.55f;
                if (tephra > 0f)
                {
                    int rad = 1 + (int)(3f * pw * explosive);
                    RegionRound(vc, rad);
                    float wsum = 0f;
                    for (int j = 1; j < regionA.Count; j++) { if (regionG[j] > rad) continue; float f = 1f - regionG[j] / (rad + 1f); wsum += f * f; }
                    if (wsum > 0f)
                        for (int j = 1; j < regionA.Count; j++)
                        {
                            if (regionG[j] > rad) continue;
                            float dn = regionG[j] / (rad + 1f);
                            float w = (1f - dn) * (1f - dn) / wsum;
                            int destination = regionA[j];
                            DepositTephra(destination, tephra * w * G.Area[vc] / G.Area[destination], dn);
                        }
                    else rock[vc] += tephra;
                }

                float lavaVolume = erupted - tephra;
                float edifice = lavaVolume * Mathf.Lerp(0.6f, 0.85f, explosive);
                DepositVolcanicEdifice(vc, edifice, explosive, pw);
                rockT[vc] = Basalt; ore[vc] = 0;
                lava[vc] = 1f;

                float flow = lavaVolume - edifice;
                if (flow > 0f && lavaFlows.Count < 48)
                    lavaFlows.Add(new LavaFlow { cell = vc, steps = 0, vol = flow });
                else DepositVolcanicEdifice(vc, flow, explosive, pw);

                v.age++;
                v.left--;

                if (v.left <= 0 || magma[vc] < 0.04f)
                {
                    if (pw * explosive > 0.55f) Collapse(vc, pw * explosive);
                    volcanoes.RemoveAt(i);
                }
                else volcanoes[i] = v;
            }
            StepLavaFlows();
        }

        void DepositVolcanicEdifice(int center, float amount, float explosive, float power)
        {
            if (amount <= 0f) return;
            Vector3 position = G.Dir[center];
            Vector3 east = Vector3.Cross(Vector3.up, position);
            if (east.sqrMagnitude < 1e-6f) east = Vector3.Cross(Vector3.right, position);
            east.Normalize();
            Vector3 north = Vector3.Cross(position, east);
            float angle = EpochValue(center, 0, 211) * 2f * Mathf.PI;
            Vector3 along = east * Mathf.Cos(angle) + north * Mathf.Sin(angle);
            Vector3 across = Vector3.Cross(position, along);
            float radius = Mathf.Lerp(5f, 3.2f, explosive) * Mathf.Lerp(0.8f, 1f, power);
            RegionRound(center, radius * 1.5f);
            double weightSum = 0d;
            for (int j = 0; j < regionA.Count; j++)
            {
                int c = regionA[j];
                float weight = VolcanicFootprintWeight(G.Dir[c], along, across, regionG[j], radius, angle);
                weightSum += weight * G.Area[c];
            }
            if (weightSum <= 0d)
                throw new System.InvalidOperationException("Empreinte volcanique vide.");
            double volume = amount * (double)G.Area[center];
            for (int j = 0; j < regionA.Count; j++)
            {
                int c = regionA[j];
                float weight = VolcanicFootprintWeight(G.Dir[c], along, across, regionG[j], radius, angle);
                if (weight <= 0f) continue;
                float deposit = (float)(volume * weight / weightSum);
                rock[c] += deposit;
                if (deposit >= 0.03f) { rockT[c] = Basalt; ore[c] = 0; }
            }
        }

        static float VolcanicFootprintWeight(Vector3 direction, Vector3 along, Vector3 across, float distance, float radius, float angle)
        {
            float x = Vector3.Dot(direction, along), y = Vector3.Dot(direction, across);
            float length = Mathf.Sqrt(x * x + y * y);
            float scale = length > 1e-6f ? distance / length : 0f;
            x *= scale; y *= scale;
            float noise = Noise.Perlin(x * 0.55f + angle, y * 0.55f - angle, angle * 0.7f);
            float ellipticalDistance = Mathf.Sqrt(x * x / 1.44f + y * y / 0.64f);
            float edge = Mathf.Max(0f, 1f - ellipticalDistance / (radius * Mathf.Clamp(1f + 0.22f * noise, 0.75f, 1.25f)));
            float azimuth = Mathf.Atan2(y, x);
            float branch = 0.5f + 0.5f * Mathf.Cos(3f * azimuth + angle + 0.6f * Mathf.Sin(2f * azimuth - angle));
            float foothill = Mathf.Exp(-Mathf.Pow((ellipticalDistance / radius - 0.62f) / 0.22f, 2f));
            float flank = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(distance / (radius * 0.3f)));
            // Évent creux : sans ça, le dépôt (maximal au centre via "edge") remplit le cratère/la caldeira à
            // chaque pas de l'éruption aussi vite qu'il se creuse, qui finit donc toujours comblé. On réduit
            // fortement le dépôt tout près du sommet (même zone que la transition "flank" ci-dessus) pour que
            // la matière s'accumule plutôt sur les flancs/le rebord, laissant un évent visible en permanence.
            float hollow = Mathf.SmoothStep(0.12f, 1f, Mathf.Clamp01(distance / (radius * 0.3f)));
            return hollow * edge * (edge * Mathf.Lerp(1f, 0.35f + 1.3f * branch * branch, flank) +
                0.55f * foothill * branch * branch) * Mathf.Clamp(1f + 0.35f * noise, 0.65f, 1.35f);
        }

        // Le matériau excavé est redistribué entre le rebord et la couverture d'éjectas.
        void Blast(int vc, float pw)
        {
            int cr = 1 + (int)(2f * pw);
            int rad = cr + 2 + (int)(3f * pw);
            float depth = 1f + 5f * pw * pw;
            // Onde de choc visible (anneau qui s'étend puis s'efface, voir DrawAsteroids/PlanetViewFx), réutilisant
            // le même système d'effet que les explosions d'astéroïdes pour une explosion volcanique cohérente.
            Vector3 bn = G.Dir[vc];
            AddFx(bn * (G.Radius + Mathf.Max(0f, Ground(vc))), bn, AstBlast, G.CellSize * (rad + 2f), 1.6f + 1.3f * pw);
            RegionRound(vc, rad);
            float excavated = 0f, wsum = 0f, rimSum = 0f;
            for (int j = 0; j < regionA.Count; j++)
            {
                int c = regionA[j]; float d = regionG[j];
                if (d > rad) continue;
                if (d <= cr)
                {
                    float x = d / (cr + 1f);
                    float cut = depth * (1f - x * x);
                    float take = Mathf.Min(cut, Mathf.Max(0f, Ground(c) - (SeaLevel - MaxHeight)));
                    float fromSed = Mathf.Min(sed[c], take);
                    sed[c] -= fromSed; rock[c] -= take - fromSed;
                    excavated += take * G.Area[c];
                    scar[c] = 1f; lava[c] = 1f; ash[c] = Mathf.Min(2f, ash[c] + 0.8f);
                    if (onDisturb != null) onDisturb(c);
                    if (onBlastPlant != null) onBlastPlant(c);
                }
                else
                {
                    float f = 1f - (d - cr) / (rad - cr + 1f);
                    wsum += f * f;
                    if (d <= cr + 1f) rimSum += 1f;
                }
            }
            if (wsum <= 0f) { rock[vc] += excavated / G.Area[vc]; return; }
            float rim = rimSum > 0f ? excavated * 0.25f : 0f;
            float blanket = excavated - rim;
            for (int j = 0; j < regionA.Count; j++)
            {
                int c = regionA[j]; float d = regionG[j];
                if (d > rad || d <= cr) continue;
                float f = 1f - (d - cr) / (rad - cr + 1f);
                float a = blanket * f * f / (wsum * G.Area[c]);
                // le rebord du cratère se soulève (bande géométrique juste après cr) ; le reste forme des couches de tephra, de blocs et de cendres
                if (d <= cr + 1f) rock[c] += rim / (rimSum * G.Area[c]);
                DepositTephra(c, a, 1f - f);
                if (onBlastPlant != null && f > 0.5f) onBlastPlant(c);
                if (onBlastFauna != null && f > 0.55f && (j & 3) == 0) onBlastFauna(G.Dir[c], 0.03f);
                ash[c] = Mathf.Min(2f, ash[c] + 0.5f * f);
            }
            rockT[vc] = Basalt; ore[vc] = 0;
            terrainDirty = true;
            if (pw > 0.5f) Log("Cratère d'explosion de " + (cr * 2 + 1) + " cellules (" + Where(vc) + ")", vc);
        }

        void DepositTephra(int c, float amt, float dn)
        {
            if (amt <= 0f) return;
            float dirt = 0.15f + 0.45f * dn, sand = 0.25f, grav = 0.05f + 0.5f * (1f - dn), clay = 0.1f;
            float s = dirt + sand + grav + clay;
            AddSoil(c, amt, dirt / s, sand / s, grav / s, clay / s);
            if (rock[c] + sed[c] > SeaLevel) fert[c] = Mathf.Min(1f, fert[c] + amt * 0.6f * dn);
            scar[c] = Mathf.Max(scar[c], 0.5f);
            if (onDisturb != null && amt > 0.02f) onDisturb(c);
        }

        // la chambre magmatique vidée : le terrain s'affaisse en une caldeira
        void Collapse(int vc, float pw)
        {
            int r = 1 + (int)(3f * pw);
            RegionRound(vc, r);
            float depth = 1.5f * pw;
            for (int j = 0; j < regionA.Count; j++)
            {
                if (regionG[j] > r) continue;
                float f = 1f - regionG[j] / (r + 1f);
                int c = regionA[j];
                float collapsed = depth * f * f;
                rock[c] -= collapsed;
                mantleSolid[c] += collapsed;
                scar[c] = 1f;
                if (onDisturb != null) onDisturb(c);
            }
            terrainDirty = true;
        }

        void StepLavaFlows()
        {
            for (int i = lavaFlows.Count - 1; i >= 0; i--)
            {
                var f = lavaFlows[i];
                bool alive = true;
                for (int m = 0; m < 2 && alive; m++)
                {
                    int cur = f.cell;
                    float dep = f.vol * 0.22f;
                    rock[cur] += dep; f.vol -= dep;
                    lava[cur] = 1f;
                    rockT[cur] = Basalt; ore[cur] = 0;
                    float buried = sed[cur] * 0.3f;
                    sed[cur] -= buried; rock[cur] += buried;
                    scar[cur] = Mathf.Max(scar[cur], 0.6f);
                    if (onDisturb != null) onDisturb(cur);

                    float gc = Ground(cur);
                    int best = -1, second = -1; float bg = gc, sg = gc;
                    for (int k = 0; k < 4; k++)
                    {
                        int n = G.Nbr[cur * 4 + k];
                        if (n < 0) continue;
                        float gn = Ground(n);
                        if (gn < bg) { second = best; sg = bg; bg = gn; best = n; }
                        else if (gn < sg) { second = n; sg = gn; }
                    }
                    if (best < 0 || f.vol < 0.03f || ++f.steps > 90) { rock[cur] += Mathf.Max(0f, f.vol); alive = false; break; }
                    f.cell = (second >= 0 && volRnd.NextDouble() < 0.2) ? second : best;
                    f.vol *= G.Area[cur] / G.Area[f.cell];
                    if (Ground(f.cell) < SeaLevel)
                    {
                        // la lave se fige au contact de l'eau et produit de la vapeur
                        float solidified = f.vol * 0.6f;
                        rock[f.cell] += solidified;
                        rockT[f.cell] = Basalt; ore[f.cell] = 0;
                        f.vol -= solidified;
                        if (rainK > 0f)
                        {
                            float steam = Mathf.Min(water[f.cell], Mathf.Min(Mathf.Max(0f, 1.2f - hum[f.cell]), 0.2f) * rainK);
                            water[f.cell] -= steam;
                            hum[f.cell] += steam / rainK;
                        }
                        lava[f.cell] = 0.6f;
                    }
                }
                if (alive) lavaFlows[i] = f; else lavaFlows.RemoveAt(i);
            }
        }
    }
}
