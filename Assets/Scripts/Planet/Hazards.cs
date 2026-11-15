using System;
using System.Collections.Generic;
using UnityEngine;

namespace LivingPlanet
{
    public struct QuakeEvent { public Vector3 dir; public float mag; }
    public struct EventMsg { public float t; public string text; public int cell; }

    public sealed class Tsunami
    {
        public Vector3 epi;
        // ang sert désormais d'âge en secondes ; les autres champs restent pour lire les anciennes sauvegardes.
        public float ang = 0.06f, amp0, speed = 0.035f, width = 0.045f;
        public int[] coast;
        public float[] coastAng;
        public int next;
    }

    // Fissure magmatique ouverte par un séisme, le long de la faille mais à l'écart de l'épicentre : visible
    // dans l'option de vue "Magma" (elle alimente magma[] sur ses cellules), peut s'étendre cellule par cellule
    // tant qu'elle trouve des voisins actifs (convergence/rift), puis finit par déclencher une vraie éruption
    // (StartVolcano) une fois assez chargée, avant de s'éteindre. État transitoire, non sauvegardé (voir
    // PlanetSave.FieldsLocked, qui l'exclut nommément comme "aeolian").
    sealed class Fissure
    {
        public readonly List<int> cells = new List<int>(4);
        public float age;
        public float strength;
    }

    // Séismes, éboulements et tsunamis
    public sealed partial class PlanetSim
    {
        public float[] stress, scar;
        int[] mark;
        int markId;
        float quakeBoost;

        public readonly List<Tsunami> tsunamis = new List<Tsunami>();
        float[] tsunamiBase, tsunamiMomentum, tsunamiOut;
        byte[] tsunamiActive, tsunamiActiveN;
        float tsunamiAccum;
        public readonly List<QuakeEvent> newQuakes = new List<QuakeEvent>();
        public readonly List<EventMsg> events = new List<EventMsg>();
        readonly List<Fissure> fissures = new List<Fissure>(8);
        public Action<int> onDisturb;
        public int quakeCount, slideCount, tsunamiCount;
        readonly List<int> regionA = new List<int>(2048);
        readonly List<int> regionD = new List<int>(2048);
        // distance géométrique réelle (grand cercle, exprimée en unités de cellule) depuis le centre de la région :
        // contrairement à regionD (nombre de sauts sur la grille, qui dessine un losange/carré car il n'y a que 4 voisins
        // cardinaux), celle-ci permet de découper des zones rondes (cratères, ondes de choc...).
        readonly List<float> regionG = new List<float>(2048);

        void Log(string s, int cell = -1)
        {
            events.Add(new EventMsg { t = Time.realtimeSinceStartup, text = s, cell = cell });
            if (events.Count > 10) events.RemoveAt(0);
        }

        string Where(int c)
        {
            Vector3 d = G.Dir[c];
            float lat = Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f)) * Mathf.Rad2Deg;
            float lon = Mathf.Atan2(d.z, d.x) * Mathf.Rad2Deg;
            return Mathf.Abs(lat).ToString("0") + (lat >= 0 ? "°N " : "°S ") + Mathf.Abs(lon).ToString("0") + (lon >= 0 ? "°E" : "°O");
        }

        void InitStress(System.Random rnd)
        {
            for (int c = 0; c < M; c++)
            {
                float a = SeismicActivity(c);
                stress[c] = a > 0.05f ? a * (float)rnd.NextDouble() * 1.3f : 0f;
            }
        }

        void AccumulateStress(float ticks)
        {
            float k = 0.010f * ticks * Activity;
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    float a = SeismicActivity(c);
                    if (a > 0.05f && stress[c] < 3f) stress[c] += a * k;
                }
            });
        }

        float SeismicActivity(int c)
        {
            float activity = Mathf.Max(conv[c], 0.6f * rift[c]);
            for (int k = 0; k < 4; k++)
            {
                int n = G.Nbr[c * 4 + k];
                if (n < 0 || plate[n] == plate[c]) continue;
                if (PlateBoundary(c, n, out float strength) == BoundaryKind.Transform)
                    activity = Mathf.Max(activity, strength);
            }
            return Mathf.Clamp01(activity);
        }

        public void StepHazards(System.Random rnd)
        {
            quakeBoost *= 0.97f;
            if (tectonics && plate != null && rnd.NextDouble() < 0.4) TryQuake(rnd);
            if (rnd.NextDouble() < 0.2) TryLandslide(rnd);
            if (fissures.Count > 0) StepFissures();
        }

        // Puise dans la réserve de manteau solide pour amener magma[cell] vers la valeur visée, en respectant la
        // conservation de matière (même principe que le rechargement de StartVolcano) ; permet à une fissure
        // d'"allumer" ou de faire grandir une cellule sans attendre l'accumulation lente de StepMagma.
        float FeedMagma(int cell, float target)
        {
            float room = Mathf.Max(0f, target - magma[cell]);
            if (room <= 0f) return 0f;
            float draw = Mathf.Min(room, mantleSolid[cell] / MagmaSolidScale);
            mantleSolid[cell] -= draw * MagmaSolidScale;
            magma[cell] += draw;
            return draw;
        }

        // Ouvre une nouvelle fissure magmatique sur "cell" (déclenchée par un séisme, voir Quake()) : elle
        // devient immédiatement visible dans l'option de vue "Magma" et pourra ensuite s'étendre/s'alimenter
        // au fil du temps via StepFissures(), jusqu'à éventuellement entrer en éruption.
        void OpenFissure(int cell, float mag)
        {
            for (int i = 0; i < fissures.Count; i++) if (fissures[i].cells.Contains(cell)) return;
            var fi = new Fissure { strength = Mathf.Clamp01(0.35f + 0.5f * Mathf.Clamp01((mag - 4f) / 4f)) };
            fi.cells.Add(cell);
            FeedMagma(cell, 0.5f + 0.4f * fi.strength);
            scar[cell] = Mathf.Max(scar[cell], 0.4f);
            fissures.Add(fi);
        }

        // Fait vivre les fissures ouvertes par OpenFissure() : alimentation continue en magma (donc visible dans
        // la durée via l'option "Magma", pas qu'un flash ponctuel), extension cellule par cellule le long de la
        // faille tant qu'elle trouve des voisins actifs (respecte la géométrie de faille courante, qui dérive
        // avec les plaques), puis déclenchement d'une vraie éruption une fois assez chargée (réutilise
        // StartVolcano/les coulées de lave existantes), avant extinction progressive.
        const int MaxFissureCells = 10;
        void StepFissures()
        {
            for (int i = fissures.Count - 1; i >= 0; i--)
            {
                var fi = fissures[i];
                fi.age += 1f;

                float best = 0f;
                for (int k = 0; k < fi.cells.Count; k++)
                {
                    FeedMagma(fi.cells[k], 0.55f + 0.35f * fi.strength);
                    if (magma[fi.cells[k]] > best) best = magma[fi.cells[k]];
                }

                if (fi.cells.Count < MaxFissureCells && tectonics && conv != null && volRnd.NextDouble() < 0.05 * fi.strength)
                {
                    int growFrom = fi.cells[fi.cells.Count - 1];
                    int candidate = -1; float bestScore = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        int n = G.Nbr[growFrom * 4 + k];
                        if (n < 0 || fi.cells.Contains(n)) continue;
                        float activity = Mathf.Max(conv[n], rift != null ? rift[n] : 0f);
                        if (activity < 0.3f) continue;
                        float score = activity * (0.4f + mantleSolid[n] / Mathf.Max(1f, InitialMantleSolid));
                        if (score > bestScore) { bestScore = score; candidate = n; }
                    }
                    if (candidate >= 0)
                    {
                        fi.cells.Add(candidate);
                        FeedMagma(candidate, 0.5f);
                        scar[candidate] = Mathf.Max(scar[candidate], 0.4f);
                    }
                }

                if (best > 0.7f && volRnd.NextDouble() < 0.02 * fi.strength)
                {
                    int eruptAt = -1; float eruptBest = -1f;
                    for (int k = 0; k < fi.cells.Count; k++)
                    {
                        int cell = fi.cells[k];
                        bool busy = false;
                        for (int v = 0; v < volcanoes.Count; v++) if (volcanoes[v].cell == cell) { busy = true; break; }
                        if (busy) continue;
                        if (magma[cell] > eruptBest) { eruptBest = magma[cell]; eruptAt = cell; }
                    }
                    if (eruptAt >= 0)
                    {
                        StartVolcano(eruptAt);
                        Log("La fissure magmatique entre en éruption (" + Where(eruptAt) + ")", eruptAt);
                    }
                }

                fi.strength *= 0.994f;
                if (fi.strength < 0.08f || fi.age > 600f) fissures.RemoveAt(i);
            }
        }

        // ------------------------------------------------------------ éboulements
        void TryLandslide(System.Random rnd)
        {
            int c = rnd.Next(M);
            float g = Ground(c);
            if (g < 4f) return;
            float thr = 0.85f - 0.3f * moist[c] - 0.5f * quakeBoost - (snow[c] > 3f && temp[c] > 0f ? 0.12f : 0f);
            if (Slope(c) < thr || rnd.NextDouble() > 0.3) return;
            Landslide(c, 1.5f + (float)rnd.NextDouble() * 4f);
        }

        public void Landslide(int c, float volume)
        {
            float g = Ground(c), low = g;
            for (int k = 0; k < 4; k++)
            {
                int n = G.Nbr[c * 4 + k];
                if (n >= 0) low = Mathf.Min(low, Ground(n));
            }
            float m = Mathf.Min(volume, (g - low) * 0.8f + sed[c] * 0.5f);
            if (m < 0.2f) return;
            float fs = Mathf.Min(m, sed[c]);
            sed[c] -= fs;
            rock[c] -= m - fs;
            scar[c] = 1f;
            if (onDisturb != null) onDisturb(c);
            slideCount++;

            int cur = c;
            for (int step = 0; step < 60; step++)
            {
                float gc = Ground(cur);
                int best = -1; float bs = 0.2f;
                for (int k = 0; k < 4; k++)
                {
                    int n = G.Nbr[cur * 4 + k];
                    if (n < 0) continue;
                    float s = (gc - Ground(n)) / G.Dist[cur * 4 + k];
                    if (s > bs) { bs = s; best = n; }
                }
                if (best < 0) break;
                float dep = m * 0.1f;
                sed[cur] += dep; m -= dep;
                m *= G.Area[cur] / G.Area[best];
                cur = best;
                scar[cur] = Mathf.Max(scar[cur], 0.7f);
                if (onDisturb != null) onDisturb(cur);
                float ent = Mathf.Min(sed[cur], 0.25f);
                sed[cur] -= ent; m += ent;
                if (Ground(cur) < SeaLevel || m < 0.05f) break;
            }
            sed[cur] += m;
        }

        public void ForceLandslide(int c)
        {
            int best = c; float bs = Slope(c);
            Region(c, 6);
            for (int i = 0; i < regionA.Count; i++)
            {
                float s = Slope(regionA[i]);
                if (s > bs && Ground(regionA[i]) > 2f) { bs = s; best = regionA[i]; }
            }
            Landslide(best, 8f);
            Log("Éboulement près de " + Where(best), best);
        }

        // ------------------------------------------------------------ séismes
        // Gutenberg-Richter : beaucoup de petits séismes, très rarement un gros ; la magnitude est plafonnée par la tension accumulée
        void TryQuake(System.Random rnd)
        {
            int best = -1; float bs = 0.25f;
            for (int i = 0; i < 48; i++)
            {
                int c = rnd.Next(M);
                if (stress[c] > bs) { bs = stress[c]; best = c; }
            }
            if (best < 0) return;
            float u = Mathf.Max(1e-4f, (float)rnd.NextDouble());
            float mag = 3.6f - Mathf.Log10(u) / 0.75f;
            mag = Mathf.Min(mag, 4.2f + 1.5f * bs + 0.3f * (float)rnd.NextDouble());
            int epicenter = PickQuakeEpicenter(best, rnd);
            Quake(epicenter, mag, rnd);
            float release = 0.85f * Mathf.Clamp((mag - 3.5f) / 3.5f, 0.12f, 1f);
            stress[best] = Mathf.Min(stress[best], bs * (1f - release));
        }

        int PickQuakeEpicenter(int source, System.Random rnd)
        {
            float radius = Mathf.Clamp(G.N / 40f, 2f, 5f);
            RegionRound(source, radius);
            int selected = source;
            double total = 0d;
            for (int i = 0; i < regionA.Count; i++)
            {
                if (regionG[i] > radius) continue;
                float weight = 1f - regionG[i] / (radius + 1f);
                total += weight;
                if (rnd.NextDouble() * total < weight) selected = regionA[i];
            }
            return selected;
        }

        void Region(int c, int radius, bool seaOnly = false)
        {
            markId++;
            regionA.Clear(); regionD.Clear(); regionG.Clear();
            Vector3 center = G.Dir[c];
            mark[c] = markId; regionA.Add(c); regionD.Add(0); regionG.Add(0f);
            for (int h = 0; h < regionA.Count; h++)
            {
                int d = regionD[h];
                if (d >= radius) continue;
                int cur = regionA[h];
                for (int k = 0; k < 4; k++)
                {
                    int n = G.Nbr[cur * 4 + k];
                    if (n >= 0 && mark[n] != markId && (!seaOnly || IsOcean(n)))
                    {
                        mark[n] = markId; regionA.Add(n); regionD.Add(d + 1);
                        float ang = Mathf.Acos(Mathf.Clamp(Vector3.Dot(center, G.Dir[n]), -1f, 1f));
                        regionG.Add(ang * G.Radius / G.CellSize);
                    }
                }
            }
        }

        // Comme Region(), mais garantit que toutes les cellules dans le rayon géométrique "geoRadius" (en unités de
        // cellule, distance de grand cercle) sont bien collectées : on explore un peu plus loin en sauts de grille
        // (la diagonale d'un losange de rayon géométrique r nécessite jusqu'à r*sqrt(2) sauts) puis on ignore dans
        // regionG tout ce qui dépasse le rayon demandé. Résultat : des zones rondes au lieu de losanges/carrés.
        void RegionRound(int c, float geoRadius)
        {
            int hopRadius = Mathf.CeilToInt(geoRadius * 1.5f) + 1;
            Region(c, hopRadius);
        }

        public void Quake(int c, float mag, System.Random rnd)
        {
            int rr = Mathf.Clamp(Mathf.RoundToInt(1f + (mag - 3.5f) * 2.4f), 1, 16);
            RegionRound(c, rr);
            bool sea = Ground(c) < SeaLevel - 3f;
            float sign = (rift != null && conv != null && rift[c] > conv[c]) ? -1f : 1f;
            float release = 0.85f * Mathf.Clamp((mag - 3.5f) / 3.5f, 0.12f, 1f);
            float sq = Mathf.Max(0f, mag - 3.5f);
            Vector3 epicenterDir = G.Dir[c];
            Vector3 faultDirection = tectonics && plate != null
                ? Vector3.Cross(axis[plate[c]], epicenterDir)
                : G.Tan[c * 4];
            if (faultDirection.sqrMagnitude < 1e-8f) faultDirection = G.Tan[c * 4 + 1];
            faultDirection.Normalize();
            // Direction perpendiculaire à la faille (dans le plan tangent à l'épicentre) : sert à distinguer les
            // deux blocs de part et d'autre de la cassure, pour le soulèvement/affaissement en bloc ci-dessous.
            Vector3 acrossDir = Vector3.Cross(epicenterDir, faultDirection);
            if (acrossDir.sqrMagnitude < 1e-8f) acrossDir = G.Tan[c * 4 + 1];
            acrossDir.Normalize();
            float norm = Mathf.Max(G.CellSize * rr, 1e-4f);
            // Largeur de la cassure nette elle-même (fossé d'effondrement étroit tout le long de la trace de
            // faille), bien plus fine que le rayon total d'influence du séisme.
            float faultBandWidth = Mathf.Max(0.6f, rr * 0.18f);

            for (int i = 0; i < regionA.Count; i++)
            {
                int cell = regionA[i];
                float distance = regionG[i];
                if (distance > rr) continue;
                float f = 1f - Mathf.SmoothStep(0f, 1f, distance / rr);
                stress[cell] *= 1f - release * f;
                float slip = 0.1f * sq * sq * f * f;
                float side = Mathf.Clamp(Vector3.Dot(G.Dir[cell], faultDirection) * G.Radius / norm, -1f, 1f);
                float displacement = sign * side * slip * (sea ? 1.5f : 1f);

                // Bloc entier qui se soulève d'un côté de la faille et s'affaisse de l'autre (horst/graben) :
                // un vrai séisme crée des reliefs nets de part et d'autre de la cassure, pas qu'un glissement
                // latéral lisse.
                float across = Vector3.Dot(G.Dir[cell] - epicenterDir, acrossDir) * G.Radius / norm;
                float block = Mathf.Sign(across) * Mathf.Clamp01(Mathf.Abs(across)) * 0.12f * sq * sq * f * f;

                // Cassure nette exactement sur la trace de la faille (fossé d'effondrement), uniquement pour
                // les séismes assez forts : un trait visible qui s'ouvre, indépendant du côté du bloc.
                float crack = mag > 5f ? -0.22f * sq * Mathf.Exp(-(distance * distance) / (faultBandWidth * faultBandWidth)) : 0f;

                float totalChange = displacement + block + crack;
                if (Mathf.Abs(totalChange) > 0.02f)
                {
                    if (totalChange > 0f) totalChange = Mathf.Min(totalChange, mantleSolid[cell]);
                    rock[cell] += totalChange;
                    mantleSolid[cell] -= totalChange;
                    if (Mathf.Abs(totalChange) > 0.3f)
                    {
                        scar[cell] = Mathf.Max(scar[cell], Mathf.Clamp01(Mathf.Abs(totalChange) / 2f));
                        if (onDisturb != null) onDisturb(cell);
                    }
                }
                if (f > 0.3f && Ground(cell) > 3f && rnd.NextDouble() < 0.04 * (mag - 4f) && Slope(cell) > 0.4f)
                    Landslide(cell, 2f + (float)rnd.NextDouble() * 5f);
            }

            quakeCount++;
            quakeBoost = Mathf.Max(quakeBoost, (mag - 4f) / 4f);
            newQuakes.Add(new QuakeEvent { dir = G.Dir[c], mag = mag });

            string msg = "Séisme de magnitude " + mag.ToString("0.0") + " (" + Where(c) + ")";
            if (!sea && conv != null && conv[c] > 0.5f && magma[c] > 0.75f && mag > 6.3f && rnd.NextDouble() < 0.3)
            {
                StartVolcano(c);
                msg += " - éruption déclenchée";
            }
            // Chance (non négligeable même pour un petit séisme, et croissante avec la magnitude) qu'une fissure
            // magmatique s'ouvre ailleurs le long de la même faille que l'épicentre : un embranchement distinct
            // du volcan de l'épicentre. On ne retient qu'une cellule déjà convergente/en rift (système de plaques)
            // et suffisamment chargée en magma (système de magma), ce qui respecte la dérive des plaques puisque
            // conv[]/rift[]/magma[] sont recalculés à chaque pas tectonique sur la géométrie de faille courante.
            // La fissure n'entre pas en éruption immédiatement : elle est visible dans l'option "Magma" et grandit
            // au fil du temps (voir StepFissures), jusqu'à éventuellement être utilisée par le magma pour sortir.
            if (tectonics && conv != null && magma != null)
            {
                float fissureChance = Mathf.Clamp01(0.05f + 0.22f * Mathf.Clamp01((mag - 3.5f) / 3.5f));
                if (rnd.NextDouble() < fissureChance)
                {
                    int branch = -1; float bestScore = 0f;
                    for (int i = 0; i < regionA.Count; i++)
                    {
                        int cell = regionA[i];
                        if (cell == c || regionG[i] > rr) continue;
                        float activity = Mathf.Max(conv[cell], rift != null ? rift[cell] : 0f);
                        if (activity < 0.35f || magma[cell] < 0.3f) continue;
                        bool busy = false;
                        for (int v = 0; v < volcanoes.Count; v++) if (volcanoes[v].cell == cell) { busy = true; break; }
                        if (busy) continue;
                        float score = activity * magma[cell] * (1f - regionG[i] / (rr + 1f));
                        if (score > bestScore) { bestScore = score; branch = cell; }
                    }
                    if (branch >= 0)
                    {
                        OpenFissure(branch, mag);
                        msg += " - nouvelle fissure magmatique (" + Where(branch) + ")";
                    }
                }
            }
            // Seuil abaissé (6.6 -> 6.0) : les gros séismes sous-marins restent rares, mais pas au point de ne
            // quasiment jamais franchir l'ancien seuil - les tsunamis doivent redevenir un évènement que l'on
            // croise de temps en temps, pas une quasi-curiosité.
            if (sea && mag >= 6f)
            {
                if (SpawnTsunami(c, Mathf.Min(9f, (mag - 5.3f) * 3f))) msg += " - ALERTE TSUNAMI";
            }
            if (mag >= 5f) Log(msg, c);
        }

        public void ForceQuake(int c, System.Random rnd)
        {
            Quake(c, 6.6f + (float)rnd.NextDouble() * 1.2f, rnd);
        }

        // ------------------------------------------------------------ tsunamis
        public bool SpawnTsunami(int c, float amp)
        {
            if (tsunamis.Count >= 3) { Log("Tsunami impossible : trois tsunamis sont déjà actifs.", c); return false; }
            if (amp <= 0f) { Log("Tsunami impossible : amplitude insuffisante.", c); return false; }
            int radius = Mathf.Max(3, Mathf.RoundToInt(G.N * 0.025f));
            Region(c, radius * 2 + 2);
            int source = -1;
            float nearest = float.MaxValue;
            for (int i = 0; i < regionA.Count; i++)
            {
                int cell = regionA[i];
                if (IsOcean(cell) && regionG[i] < nearest)
                {
                    source = cell;
                    nearest = regionG[i];
                }
            }
            if (source < 0)
            {
                Log("Tsunami impossible : aucune mer proche.", c);
                return false;
            }
            Region(source, radius * 2 + 2, true);
            float uplift = amp * 1.8f; // exagération visuelle à l'échelle de la grille planétaire
            double needed = 0d, available = 0d;
            for (int i = 0; i < regionA.Count; i++)
            {
                int cell = regionA[i];
                if (!IsOcean(cell)) continue;
                float d = regionG[i];
                if (d < radius)
                    needed += uplift * (1f - d / radius) * G.Area[cell];
                else if (d < radius * 2f + 1f)
                    available += water[cell] * 0.2f * G.Area[cell];
            }
            if (needed <= 0d || available <= 0d)
            {
                Log("Tsunami impossible : volume d'eau insuffisant.", c);
                return false;
            }
            if (tsunamis.Count == 0)
            {
                for (int cell = 0; cell < M; cell++)
                    tsunamiBase[cell] = IsOcean(cell) ? Ground(cell) + water[cell] : SeaLevel;
                Array.Clear(tsunamiMomentum, 0, tsunamiMomentum.Length);
                Array.Clear(tsunamiActive, 0, tsunamiActive.Length);
                tsunamiAccum = 0f;
            }
            float fraction = (float)(Math.Min(needed, available) / available);
            double salt = 0d, sediment = 0d, volume = 0d;
            for (int i = 0; i < regionA.Count; i++)
            {
                int cell = regionA[i];
                float d = regionG[i];
                if (!IsOcean(cell) || d < radius || d >= radius * 2f + 1f) continue;
                tsunamiActive[cell] = 1;
                float share = 0.2f * fraction;
                float moved = water[cell] * share;
                volume += moved * G.Area[cell];
                salt += waterState.salt[cell] * share * G.Area[cell];
                sediment += susp[cell] * share * G.Area[cell];
                water[cell] -= moved;
                waterState.salt[cell] *= 1f - share;
                susp[cell] *= 1f - share;
            }
            for (int i = 0; i < regionA.Count; i++)
            {
                int cell = regionA[i];
                float d = regionG[i];
                if (!IsOcean(cell) || d >= radius) continue;
                tsunamiActive[cell] = 1;
                double share = uplift * (1f - d / radius) * G.Area[cell] / needed;
                water[cell] += (float)(volume * share / G.Area[cell]);
                waterState.salt[cell] += (float)(salt * share / G.Area[cell]);
                susp[cell] += (float)(sediment * share / G.Area[cell]);
            }
            tsunamis.Add(new Tsunami { epi = G.Dir[source], amp0 = amp, ang = 0f });
            tsunamiCount++;
            return true;
        }

        public void ForceTsunami(int c, bool announce = true)
        {
            if (SpawnTsunami(c, 7f) && announce) Log("Tsunami déclenché (" + Where(c) + ")", c);
        }

        public bool StepTsunamis(float dt)
        {
            if (tsunamis.Count == 0 || dt <= 0f) return false;
            if (tsunamiOut == null) tsunamiOut = new float[M * 4];
            if (tsunamiActiveN == null) tsunamiActiveN = new byte[M];
            tsunamiAccum += dt;
            const float step = 0.1f;
            int steps = 0;
            while (tsunamiAccum >= step && steps++ < 4)
            {
                StepTsunamiWater(step);
                tsunamiAccum -= step;
                for (int i = tsunamis.Count - 1; i >= 0; i--)
                {
                    tsunamis[i].ang += step;
                    if (tsunamis[i].ang >= 90f) tsunamis.RemoveAt(i);
                }
                if (tsunamis.Count == 0) break;
            }
            return steps > 0;
        }

        public Vector3 TsunamiFlow(int c)
        {
            if (tsunamis.Count == 0 || tsunamiActive[c] == 0) return Vector3.zero;
            Vector3 flow = Vector3.zero;
            for (int k = 0; k < 4; k++) flow += G.Tan[c * 4 + k] * tsunamiMomentum[c * 4 + k];
            return flow * 0.5f;
        }

        public float TsunamiElevation(int c)
        {
            return tsunamis.Count > 0 && tsunamiActive[c] != 0
                ? Ground(c) + water[c] - tsunamiBase[c] : 0f;
        }

        // Débit minimal (épaisseur par sous-pas) reçu d'une voisine active pour qu'une cellule terrestre rejoigne
        // l'onde : au-dessus du ruissellement d'une simple pellicule de pluie, très en dessous d'une vraie vague.
        const float TsunamiReachFlow = 0.005f;

        void StepTsunamiWater(float dt)
        {
            // L'onde n'active que les cellules qu'elle atteint, sans perturber les lacs et rivières éloignés.
            // En mer, elle se propage par son écart à la surface de référence ou par son inertie. Dès qu'une
            // terre est en jeu, cet écart n'a plus de sens (sa référence est le niveau de la mer, donc il vaut
            // l'altitude et serait toujours "non nul") : seule l'eau réellement poussée vers la cellule compte.
            Par.For(M, (start, end) =>
            {
                for (int c = start; c < end; c++)
                {
                    byte active = tsunamiActive[c];
                    if (active == 0)
                        for (int k = 0; k < 4; k++)
                        {
                            int edge = c * 4 + k, n = G.Nbr[edge];
                            if (n < 0 || tsunamiActive[n] == 0 || water[n] <= 0.001f) continue;
                            int toward = n * 4 + G.Back[edge];
                            bool reached;
                            if (IsOcean(c) && IsOcean(n))
                            {
                                float head = Ground(n) + water[n] - tsunamiBase[n];
                                reached = Mathf.Abs(head) > 0.01f || Mathf.Abs(tsunamiMomentum[toward]) > 0.01f;
                            }
                            // Débit du pas précédent, relu depuis l'inertie (sauvegardée) plutôt que depuis
                            // tsunamiOut (non sauvegardé) : le résultat reste identique après un rechargement.
                            else reached = tsunamiMomentum[toward] * dt > TsunamiReachFlow;
                            if (reached) { active = 1; break; }
                        }
                    tsunamiActiveN[c] = active;
                }
            });
            var oldActive = tsunamiActive; tsunamiActive = tsunamiActiveN; tsunamiActiveN = oldActive;
            Par.For(M, (start, end) =>
            {
                for (int c = start; c < end; c++)
                {
                    if (tsunamiActive[c] == 0)
                    {
                        int first = c * 4;
                        tsunamiOut[first] = tsunamiOut[first + 1] = tsunamiOut[first + 2] = tsunamiOut[first + 3] = 0f;
                        continue;
                    }
                    float head = Ground(c) + water[c] - tsunamiBase[c];
                    float total = 0f;
                    int b = c * 4;
                    for (int k = 0; k < 4; k++)
                    {
                        int edge = b + k, n = G.Nbr[edge];
                        if (n < 0) { tsunamiMomentum[edge] = tsunamiOut[edge] = 0f; continue; }
                        float other = Ground(n) + water[n] - tsunamiBase[n];
                        // Pression et inertie sur chaque arête ; la surface de référence retire les pentes
                        // préexistantes de l'océan sans supprimer les crêtes et creux créés par l'onde.
                        float v = tsunamiMomentum[edge] * 0.997f + (head - other) * 2f * dt;
                        if (water[c] <= 0.001f && v > 0f) v = 0f;
                        if (water[n] <= 0.001f && v < 0f) v = 0f;
                        if (water[n] <= 0.001f && head <= other && v > 0f) v = 0f;
                        tsunamiMomentum[edge] = v;
                        tsunamiOut[edge] = Mathf.Max(0f, v) * dt;
                        total += tsunamiOut[edge];
                    }
                    float scale = total > water[c] * 0.45f ? water[c] * 0.45f / total : 1f;
                    for (int k = 0; k < 4; k++)
                    {
                        tsunamiOut[b + k] *= scale;
                        if (tsunamiMomentum[b + k] > 0f) tsunamiMomentum[b + k] *= scale;
                    }
                }
            });
            Par.For(M, (start, end) =>
            {
                for (int c = start; c < end; c++)
                {
                    int b = c * 4;
                    float outgoing = tsunamiOut[b] + tsunamiOut[b + 1] + tsunamiOut[b + 2] + tsunamiOut[b + 3];
                    float fraction = water[c] > 1e-6f ? outgoing / water[c] : 0f;
                    float w = water[c] - outgoing;
                    float sp = susp[c] * (1f - fraction);
                    float salt = waterState.salt[c] * (1f - fraction);
                    float moving = outgoing;
                    for (int k = 0; k < 4; k++)
                    {
                        int n = G.Nbr[b + k];
                        if (n < 0 || water[n] <= 1e-6f) continue;
                        float incoming = tsunamiOut[n * 4 + G.Back[b + k]] * G.Area[n] / G.Area[c];
                        w += incoming;
                        moving += incoming;
                        sp += susp[n] * incoming / water[n];
                        salt += waterState.salt[n] * incoming / water[n];
                    }
                    if (moving <= 0f)
                    {
                        waterN[c] = water[c]; suspN[c] = susp[c];
                        waterState.nextSalt[c] = waterState.salt[c];
                        continue;
                    }
                    float eroded = ErodeTsunamiBed(c, moving, dt);
                    sp += eroded;
                    if (w < 0.003f && sp > 0f) { DepositWater(c, sp, 0.3f); sp = 0f; }
                    waterN[c] = Mathf.Max(0f, w);
                    suspN[c] = Mathf.Max(0f, sp);
                    waterState.nextSalt[c] = Mathf.Max(0f, salt);
                }
            });
            var oldWater = water; water = waterN; waterN = oldWater;
            var oldSusp = susp; susp = suspN; suspN = oldSusp;
            var oldSalt = waterState.salt; waterState.salt = waterState.nextSalt; waterState.nextSalt = oldSalt;
        }

        float ErodeTsunamiBed(int c, float movingWater, float dt)
        {
            if (movingWater <= 0.0001f) return 0f;
            float want = Mathf.Min(maxErode * dt, movingWater * 0.08f * erosionScale);
            float soil = ErodeSoil(c, want * ErodibilityAt(c));
            float stone = sed[c] < 0.05f
                ? Mathf.Max(0f, want - soil) * RockErodibilityAt(c) * 0.5f : 0f;
            rock[c] -= stone;
            waterErode[c] += (-(soil + stone) * ErodeViewScale - waterErode[c]) * ErodeViewRate;
            return soil + stone;
        }
    }
}
