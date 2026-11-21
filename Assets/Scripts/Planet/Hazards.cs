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
        // Front d'onde : rayon angulaire atteint par la crête (rad), amplitude à la source, vitesse (rad/s)
        // et longueur de la traîne (rad).
        public float ang, amp0, speed, width;
        // Terres que la crête peut mouiller. Pour chacune : sa cellule, la distance (rad) à laquelle le front
        // l'atteint et l'épaisseur d'eau qui lui est prêtée en ce moment.
        public int[] coast;
        public float[] coastAng;
        public float[] held;
        // Cellules de mer profonde qui prêtent cette eau, à parts égales.
        public int[] donor;
        public int next; // inutilisé : reste pour lire les anciennes sauvegardes
        // Distance par mer (rad) de l'épicentre à chaque cellule, négative hors d'atteinte. Non sauvegardée
        // (voir PlanetSave.FieldsLocked) : elle se recalcule depuis l'épicentre.
        public float[] reach;
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
        // Un tsunami est un front qui s'éloigne de son épicentre. En mer il ne déplace pas d'eau : sa crête est
        // une hauteur calculée, ajoutée à la surface par le rendu. Sur les côtes basses il prête de l'eau de
        // mer aux terres le temps de son passage, puis la reprend.
        const float TsunamiSpeed = 0.02f;      // avance du front, en radians par seconde
        const float TsunamiRise = 0.02f;       // largeur de la montée de la crête, en radians
        const float TsunamiTail = 0.06f;       // longueur de la traîne derrière la crête, en radians
        const float TsunamiRange = 0.12f;      // portée en radians par unité d'amplitude : l'onde s'y éteint
        const float TsunamiCrest = 0.63f;      // hauteur de la crête à la source, par unité d'amplitude
        const float TsunamiPush = 3f;          // poussée transmise au rendu (écume) par unité de hauteur
        const float TsunamiInland = 45f;       // distance maximale inondée depuis le rivage
        const float TsunamiDonorDepth = 8f;    // profondeur à partir de laquelle la mer prête son eau
        const int TsunamiDonors = 1024;        // nombre maximal de cellules de mer qui prêtent leur eau
        // Rapport maximal entre le plus court chemin de cellule en cellule et la distance à vol d'oiseau en
        // mer ouverte (FlowValidation.TsunamiRoundFront le vérifie) : en dessous, le front reste un cercle.
        const float TsunamiDetour = 1.2f;

        // craterRadius : rayon du cratère qu'un impact vient de creuser sous la mer, 0 pour un séisme.
        public bool SpawnTsunami(int c, float amp, float craterRadius = 0f)
        {
            if (tsunamis.Count >= 3) { Log("Tsunami impossible : trois tsunamis sont déjà actifs.", c); return false; }
            if (amp <= 0f) { Log("Tsunami impossible : amplitude insuffisante.", c); return false; }
            Region(c, Mathf.Max(3, Mathf.RoundToInt(G.N * 0.025f)) * 2 + 2);
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
            // Le front naît avec sa crête sur l'épicentre.
            var wave = new Tsunami { epi = G.Dir[source], amp0 = amp, ang = TsunamiRise, speed = TsunamiSpeed, width = TsunamiTail };
            wave.reach = SeaReach(wave);
            if (craterRadius > 0f) FillSeaHollow(wave, source, craterRadius);
            FindFloodable(wave);
            tsunamis.Add(wave);
            tsunamiCount++;
            return true;
        }

        public void ForceTsunami(int c, bool announce = true)
        {
            if (SpawnTsunami(c, 7f) && announce) Log("Tsunami déclenché (" + Where(c) + ")", c);
        }

        static float Arc(Vector3 a, Vector3 b)
        {
            return 2f * Mathf.Asin(Mathf.Min(1f, (a - b).magnitude * 0.5f));
        }

        // Une des quatre voisines en diagonale de c, si l'eau peut y passer. Les côtés 0 et 1 d'une cellule
        // sont opposés, comme les côtés 2 et 3 (voir PlanetGrid) : une diagonale touche un côté de chaque paire.
        int SeaDiagonal(int c, int k)
        {
            int a = G.Nbr[c * 4 + k / 2], b = G.Nbr[c * 4 + 2 + k % 2];
            if (a < 0 || b < 0 || (!IsOcean(a) && !IsOcean(b))) return -1;
            for (int i = 0; i < 4; i++)
            {
                int n = G.Nbr[a * 4 + i];
                if (n < 0 || n == c) continue;
                for (int j = 0; j < 4; j++) if (G.Nbr[b * 4 + j] == n) return n;
            }
            return -1;
        }

        // Distance par mer (en radians) de l'épicentre à chaque cellule de mer à portée, négative ailleurs.
        // C'est le plus court chemin de cellule de mer en cellule de mer : en mer ouverte il vaut la distance
        // à vol d'oiseau, derrière une terre il s'allonge, et l'onde y arrive plus tard ou pas du tout.
        float[] SeaReach(Tsunami wave)
        {
            int source = G.CellFromDir(wave.epi);
            float limit = TsunamiRange * wave.amp0 * TsunamiDetour;
            var path = new float[M];
            for (int c = 0; c < M; c++) path[c] = -1f;
            var queue = new Queue<int>();
            path[source] = 0f;
            queue.Enqueue(source);
            while (queue.Count > 0)
            {
                int c = queue.Dequeue();
                for (int k = 0; k < 8; k++)
                {
                    int n = k < 4 ? G.Nbr[c * 4 + k] : SeaDiagonal(c, k - 4);
                    if (n < 0 || !IsOcean(n)) continue;
                    // Entre deux cellules voisines, la corde vaut l'arc.
                    float d = path[c] + (G.Dir[c] - G.Dir[n]).magnitude;
                    if (d > limit || (path[n] >= 0f && d >= path[n] - 1e-6f)) continue;
                    path[n] = d;
                    queue.Enqueue(n);
                }
            }
            for (int c = 0; c < M; c++)
                if (path[c] >= 0f) path[c] = Mathf.Max(Arc(wave.epi, G.Dir[c]), path[c] / TsunamiDetour);
            return path;
        }

        // Terres que la crête peut mouiller : de proche en proche depuis le rivage, tant que le sol reste sous
        // la crête attendue à cet endroit et à moins de TsunamiInland du rivage. Un relief plus haut que la
        // crête arrête donc l'inondation. L'eau leur est prêtée par l'ensemble de la mer profonde à portée de
        // l'onde : chaque cellule n'en cède qu'une fraction infime, sans creuser de trou.
        void FindFloodable(Tsunami wave)
        {
            var cells = new List<int>();
            var arrivals = new List<float>();
            var hops = new List<int>();
            float cellArc = G.CellSize / G.Radius;
            int maxHops = Mathf.Max(1, Mathf.RoundToInt(TsunamiInland / G.CellSize));
            void Spread(int cell, float arrival, int hop)
            {
                for (int k = 0; k < 4; k++)
                {
                    int n = G.Nbr[cell * 4 + k];
                    if (n < 0 || mark[n] == markId || IsOcean(n)) continue;
                    if (Ground(n) >= SeaLevel + wave.amp0 * TsunamiCrest * TsunamiFade(wave.amp0, arrival)) continue;
                    mark[n] = markId;
                    cells.Add(n); arrivals.Add(arrival); hops.Add(hop);
                }
            }
            var lenders = new List<int>();
            markId++;
            for (int c = 0; c < M; c++)
            {
                if (wave.reach[c] < 0f) continue;
                Spread(c, wave.reach[c] + cellArc, 1);
                if (water[c] >= TsunamiDonorDepth) lenders.Add(c);
            }
            for (int i = 0; i < cells.Count; i++)
                if (hops[i] < maxHops) Spread(cells[i], arrivals[i] + cellArc, hops[i] + 1);
            wave.coast = cells.ToArray();
            wave.coastAng = arrivals.ToArray();
            wave.held = new float[cells.Count];
            int stride = Mathf.Max(1, lenders.Count / TsunamiDonors);
            wave.donor = new int[lenders.Count / stride];
            for (int i = 0; i < wave.donor.Length; i++) wave.donor[i] = lenders[i * stride];
        }

        // Un impact en mer creuse le fond sous l'eau et laisse un creux dans la surface, sur le rayon du cratère.
        // La mer profonde à portée de l'onde le comble aussitôt jusqu'au niveau de la mer qui l'entoure, en cédant
        // partout la même mince épaisseur : c'est ce déplacement qui lance l'onde, et la mer ne garde pas un trou
        // jusqu'aux prochains pas d'hydrologie.
        void FillSeaHollow(Tsunami wave, int source, float craterRadius)
        {
            float radius = craterRadius / G.CellSize;
            Region(source, Mathf.CeilToInt(radius * 1.5f) + 3, true);
            // Niveau de la mer intacte : la couronne de deux cellules juste au-delà du cratère.
            double around = 0d, aroundArea = 0d;
            for (int i = 0; i < regionA.Count; i++)
            {
                int c = regionA[i];
                if (regionG[i] < radius || regionG[i] >= radius + 2f) continue;
                around += (Ground(c) + water[c]) * G.Area[c];
                aroundArea += G.Area[c];
            }
            if (aroundArea <= 0d) return;
            float level = (float)(around / aroundArea);
            var hollow = new List<int>();
            double need = 0d, poolArea = 0d;
            markId++;
            for (int i = 0; i < regionA.Count; i++)
            {
                int c = regionA[i];
                mark[c] = markId;
                if (regionG[i] >= radius || Ground(c) + water[c] >= level) continue;
                hollow.Add(c);
                need += (level - Ground(c) - water[c]) * G.Area[c];
            }
            var lenders = new List<int>();
            for (int c = 0; c < M; c++)
            {
                if (wave.reach[c] < 0f || mark[c] == markId || water[c] < TsunamiDonorDepth) continue;
                lenders.Add(c);
                poolArea += G.Area[c];
            }
            if (need <= 0d || poolArea <= 0d) return;
            var salt = waterState.salt;
            float given = Mathf.Min((float)(need / poolArea), TsunamiDonorDepth * 0.2f);
            double lent = 0d, lentSalt = 0d, lentLoad = 0d;
            for (int i = 0; i < lenders.Count; i++)
            {
                int sea = lenders[i];
                float share = given / water[sea];
                lent += given * G.Area[sea];
                lentSalt += salt[sea] * share * G.Area[sea];
                lentLoad += susp[sea] * share * G.Area[sea];
                water[sea] -= given; salt[sea] *= 1f - share; susp[sea] *= 1f - share;
            }
            for (int i = 0; i < hollow.Count; i++)
            {
                int c = hollow[i];
                float got = (float)((level - Ground(c) - water[c]) * lent / need);
                salt[c] += (float)(lentSalt * got / lent);
                susp[c] += (float)(lentLoad * got / lent);
                water[c] += got;
            }
        }

        // Part de sa hauteur initiale que la crête garde quand le front a parcouru "front" radians : elle
        // baisse vite au début, puis s'annule en douceur à la portée.
        static float TsunamiFade(float amp, float front)
        {
            float left = Mathf.Max(0f, 1f - front / (TsunamiRange * amp));
            return left * left;
        }

        // Hauteur de la crête à la distance "dist" de l'épicentre : nulle devant le front, montée raide, longue
        // traîne. Elle faiblit avec l'avance du front : l'onde s'éteint partout en même temps, sans coupure.
        static float TsunamiHeight(Tsunami wave, float dist)
        {
            float behind = wave.ang - dist;
            if (dist < 0f || behind <= 0f) return 0f;
            float shape = behind < TsunamiRise ? behind / TsunamiRise : 1f - (behind - TsunamiRise) / wave.width;
            return Mathf.Max(0f, shape) * TsunamiFade(wave.amp0, wave.ang) * wave.amp0 * TsunamiCrest;
        }

        public bool StepTsunamis(float dt)
        {
            if (tsunamis.Count == 0 || dt <= 0f) return false;
            for (int i = tsunamis.Count - 1; i >= 0; i--)
            {
                var wave = tsunamis[i];
                if (wave.reach == null) wave.reach = SeaReach(wave);
                wave.ang += wave.speed * dt;
                StepTsunamiShore(wave, dt);
                // À la portée la crête est nulle partout : toute l'eau prêtée vient d'être reprise.
                if (wave.ang >= TsunamiRange * wave.amp0) tsunamis.RemoveAt(i);
            }
            return true;
        }

        // Poussée de l'onde vue par le rendu : elle s'éloigne de l'épicentre et grandit avec la crête.
        public Vector3 TsunamiFlow(int c)
        {
            Vector3 flow = Vector3.zero;
            for (int i = 0; i < tsunamis.Count; i++)
            {
                var wave = tsunamis[i];
                float height = wave.reach != null ? TsunamiHeight(wave, wave.reach[c]) : 0f;
                if (height > 0f) flow += Vector3.ProjectOnPlane(G.Dir[c] - wave.epi, G.Dir[c]).normalized * (height * TsunamiPush);
            }
            return flow;
        }

        public float TsunamiElevation(int c)
        {
            float height = 0f;
            for (int i = 0; i < tsunamis.Count; i++)
                if (tsunamis[i].reach != null) height += TsunamiHeight(tsunamis[i], tsunamis[i].reach[c]);
            return height;
        }

        // Lame d'eau que la crête fait passer sur une terre inondable : ce dont elle dépasse son sol, sans
        // jamais excéder sa propre hauteur (une cuvette à sec sous le niveau de la mer n'est pas remplie).
        float ShoreTarget(Tsunami wave, int i)
        {
            float crest = TsunamiHeight(wave, wave.coastAng[i]);
            return Mathf.Clamp(SeaLevel + crest - Ground(wave.coast[i]), 0f, crest);
        }

        // Chaque terre inondable vise sa lame d'eau. L'écart avec l'eau déjà prêtée est pris à la mer profonde
        // ou lui est rendu, avec sa part de sel et de sédiment en suspension : quand la crête est passée, la
        // cible retombe à zéro et le retrait se fait par le même chemin.
        void StepTsunamiShore(Tsunami wave, float dt)
        {
            if (wave.coast == null || wave.held == null || wave.donor == null || wave.donor.Length == 0) return;
            var salt = waterState.salt;
            double wanted = 0d, back = 0d, backSalt = 0d, backLoad = 0d;
            for (int i = 0; i < wave.coast.Length; i++)
            {
                int land = wave.coast[i];
                float target = ShoreTarget(wave, i), change = target - wave.held[i];
                if (change >= 0f) continue;
                // On ne reprend jamais plus que ce qui se trouve sur la cellule : si l'hydrologie l'a déjà
                // drainée, le reste du prêt est abandonné.
                float moved = Mathf.Min(-change, water[land]);
                wave.held[i] = target;
                if (moved <= 0f) continue;
                float share = moved / water[land];
                back += moved * G.Area[land];
                backSalt += salt[land] * share * G.Area[land];
                backLoad += susp[land] * share * G.Area[land];
                water[land] -= moved; salt[land] *= 1f - share; susp[land] *= 1f - share;
                ScourShore(land, moved, dt);
            }
            // Les emprunts se comptent après les retraits : le sol qu'un retrait vient d'éroder change la cible.
            for (int i = 0; i < wave.coast.Length; i++)
                wanted += Mathf.Max(0f, ShoreTarget(wave, i) - wave.held[i]) * G.Area[wave.coast[i]];
            if (wanted <= 0d && back <= 0d) return;

            double poolArea = 0d;
            for (int i = 0; i < wave.donor.Length; i++) poolArea += G.Area[wave.donor[i]];
            // Chaque cellule de mer prête et récupère la même épaisseur, jamais plus d'un cinquième de la
            // profondeur exigée d'elle à la fois : la mer retrouve ainsi exactement sa surface.
            float lending = Mathf.Min((float)(wanted / poolArea), TsunamiDonorDepth * 0.2f);
            float returned = (float)(back / poolArea), returnedSalt = (float)(backSalt / poolArea), returnedLoad = (float)(backLoad / poolArea);
            double lent = 0d, lentSalt = 0d, lentLoad = 0d;
            for (int i = 0; i < wave.donor.Length; i++)
            {
                int sea = wave.donor[i];
                float given = Mathf.Min(lending, water[sea]);
                float share = given > 0f ? given / water[sea] : 0f;
                lent += given * G.Area[sea];
                lentSalt += salt[sea] * share * G.Area[sea];
                lentLoad += susp[sea] * share * G.Area[sea];
                water[sea] += returned - given;
                salt[sea] += returnedSalt - salt[sea] * share;
                susp[sea] += returnedLoad - susp[sea] * share;
            }
            if (lent <= 0d) return;

            float granted = (float)(lent / wanted);
            for (int i = 0; i < wave.coast.Length; i++)
            {
                int land = wave.coast[i];
                float got = (ShoreTarget(wave, i) - wave.held[i]) * granted;
                if (got <= 0f) continue;
                water[land] += got;
                salt[land] += (float)(lentSalt * got / lent);
                susp[land] += (float)(lentLoad * got / lent);
                wave.held[i] += got;
                ScourShore(land, got, dt);
            }
        }

        // L'eau qui passe sur une terre arrache un peu de son sol ; ce qu'elle porte encore se dépose quand
        // la cellule s'assèche.
        void ScourShore(int land, float movingWater, float dt)
        {
            susp[land] += ErodeTsunamiBed(land, movingWater, dt);
            if (water[land] < 0.003f && susp[land] > 0f) { DepositWater(land, susp[land], 0.3f); susp[land] = 0f; }
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
