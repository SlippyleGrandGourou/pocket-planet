using System;
using System.Collections.Generic;
using UnityEngine;

namespace LivingPlanet
{
    public sealed class Asteroid
    {
        public Vector3 pos, vel;
        public float size, burstAlt, age;
        public int kind; // 0 = se désintègre dans l'atmosphère, 1 = explose en altitude, 2 = impact
        public Vector3 perturb;                       // accélération inconnue du prédicteur (la trajectoire estimée dérive puis converge)
        public readonly List<Vector3> path = new List<Vector3>(96);
        public int predCell = -1, notifiedCell = -1;
        public float tti, predTimer, notifyCool;      // temps estimé avant l'impact (s)
        public bool skipChecked, fragChecked;         // n'évaluer le ricochet/la fragmentation qu'une seule fois
        public float DiameterM { get { return size * 80f; } }
        public float EnergyMt { get { float d = DiameterM; return 7.5e-5f * d * d * d; } }
    }

    public struct AstFx { public Vector3 pos, normal; public int kind; public float rw, age, life; }

    // Astéroïdes / météorites : trajectoire en temps réel (x1), puis désintégration, explosion aérienne ou cratère.
    public sealed partial class PlanetSim
    {
        public const int AstBurn = 0, AstAir = 1, AstImpact = 2, AstBlast = 3;
        public readonly List<Asteroid> asteroids = new List<Asteroid>();
        public readonly List<AstFx> astFx = new List<AstFx>();
        public Action<int> onBlastPlant;
        public Action<Vector3, float> onBlastFauna;
        public bool terrainDirty;
        public int impactCount, airburstCount, burnCount;
        public float meteoriteAdded;
        public float astTimer, astNext = 48f * 60f * 2f;
        public const float AstSpeed = 300f, AstG = 18f;

        // taille du rayon de souffle en unités monde, proportionnelle à la taille d'une cellule
        float AstRw(float size) { return G.CellSize * (2.5f + 5f * size); }
        public float AstImpactRadius(Asteroid a) { return AstRw(a.size) * (a.kind == AstImpact ? 1.2f : 1.6f); }

        // gravité de la planète : mu = g * R^2, la trajectoire se courbe à l'approche
        Vector3 AstAccel(Vector3 p, Vector3 perturb)
        {
            float r2 = Mathf.Max(1f, p.sqrMagnitude);
            float R = G.Radius;
            return -p / Mathf.Sqrt(r2) * (AstG * R * R / r2) + perturb;
        }

        // délai de détection (s) : plus le corps est gros, plus il est repéré tôt
        public static float AstLead(float size) { return 20f + 40f * size; }

        public void SpawnAsteroid(int kind, int targetCell, System.Random rnd, float forcedSize = 0f, bool drift = false)
        {
            float size = kind == AstBurn ? 0.1f + 0.4f * (float)rnd.NextDouble()
                : kind == AstAir ? 0.5f + 0.7f * (float)rnd.NextDouble()
                : 0.4f + 1.1f * (float)rnd.NextDouble();
            if (forcedSize > 0f) size = forcedSize;
            float R = G.Radius;
            Vector3 n = G.Dir[targetCell];
            Vector3 t = Vector3.Cross(n, Vector3.up);
            if (t.sqrMagnitude < 1e-4f) t = Vector3.Cross(n, Vector3.right);
            t = Quaternion.AngleAxis((float)rnd.NextDouble() * 360f, n) * t.normalized;
            float th = (15f + (float)rnd.NextDouble() * 45f) * Mathf.Deg2Rad;
            Vector3 up = n * Mathf.Cos(th) + t * Mathf.Sin(th);
            float burst = kind == AstBurn ? 90f + 60f * (float)rnd.NextDouble() : 40f + 50f * (float)rnd.NextDouble();
            float endAlt = kind == AstImpact ? 0f : burst;
            Vector3 target = n * (R + Mathf.Max(0f, Ground(targetCell)) + endAlt);
            Vector3 pert = Vector3.zero;
            if (drift)
            {
                Vector3 rv = new Vector3((float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f);
                pert = Vector3.ProjectOnPlane(rv, up).normalized * (0.015f + 0.03f * (float)rnd.NextDouble());
            }
            // intégration à rebours depuis le point d'impact : l'astéroïde touchera bien la cible malgré la gravité et la perturbation
            float lead = AstLead(size);
            Vector3 p = target, v = -up * AstSpeed;
            const float h = 0.25f;
            for (float tt = 0f; tt < lead; tt += h)
            {
                p -= v * h;
                v -= AstAccel(p, pert) * h;
            }
            var a = new Asteroid { kind = kind, size = size, pos = p, vel = v, burstAlt = burst, perturb = pert };
            asteroids.Add(a);
            PredictAsteroid(a);
            AnnounceAsteroid(a, true);
        }

        // prédicteur : intègre la trajectoire connue (sans la perturbation inconnue) jusqu'à l'impact ou à l'explosion
        void PredictAsteroid(Asteroid a)
        {
            a.path.Clear();
            Vector3 p = a.pos, v = a.vel;
            float R = G.Radius, h = 0.5f, tt = 0f;
            int cell = G.CellFromDir(p.normalized);
            for (int i = 0; i < 600; i++)
            {
                if (i % 6 == 0) a.path.Add(p);
                v += AstAccel(p, Vector3.zero) * h;
                p += v * h; tt += h;
                float len = p.magnitude;
                cell = G.CellFromDir(p / len);
                float alt = len - R - Mathf.Max(0f, Ground(cell));
                if (alt <= (a.kind == AstImpact ? 0f : a.burstAlt)) break;
            }
            a.path.Add(p);
            a.predCell = cell; a.tti = tt;
        }

        void AnnounceAsteroid(Asteroid a, bool first)
        {
            string what = a.kind == AstImpact ? "Astéroïde" : (a.kind == AstAir ? "Bolide" : "Météore");
            float mt = a.EnergyMt;
            string en = mt >= 1f ? mt.ToString("0") + " Mt" : (mt * 1000f).ToString("0") + " kt";
            if (first)
                Log("☄ " + what + " détecté : ⌀ " + a.DiameterM.ToString("0") + " m, " + en + ", impact dans ~" + a.tti.ToString("0") + " s près de " + Where(a.predCell), a.predCell);
            else
                Log("☄ " + what + " : trajectoire révisée, impact estimé près de " + Where(a.predCell) + " (~" + a.tti.ToString("0") + " s)", a.predCell);
            a.notifiedCell = a.predCell; a.notifyCool = 6f;
        }


        // taille en loi de puissance : les petits météores sont fréquents, les gros impacts très rares
        public void SpawnRandomAsteroid(System.Random rnd)
        {
            int c = rnd.Next(M);
            double u = Math.Max(0.002, rnd.NextDouble());
            float size = Mathf.Min(3.5f, (float)(0.1 * Math.Pow(u, -0.8)));
            int kind = size < 0.5f ? AstBurn : (size < 1.2f ? AstAir : AstImpact);
            SpawnAsteroid(kind, c, rnd, size, true);
        }
        // dt en secondes réelles
        public void UpdateAsteroids(float dt, float timeScale, bool auto, System.Random rnd)
        {
            if (dt <= 0f) return;
            if (auto && asteroids.Count < 2)
            {
                astTimer += dt * Mathf.Min(timeScale, 100f);
                if (astTimer > astNext) { astTimer = 0f; astNext = 48f * 60f * (0.5f + 5f * (float)rnd.NextDouble()); SpawnRandomAsteroid(rnd); }
            }
            float R = G.Radius;
            for (int i = astFx.Count - 1; i >= 0; i--)
            {
                var f = astFx[i];
                f.age += dt;
                if (f.age > f.life) astFx.RemoveAt(i); else astFx[i] = f;
            }
            for (int i = asteroids.Count - 1; i >= 0; i--)
            {
                var a = asteroids[i];
                a.age += dt;
                int sub = Mathf.Max(1, Mathf.CeilToInt(dt / 0.1f));
                float h = dt / sub;
                for (int s = 0; s < sub; s++)
                {
                    a.vel += AstAccel(a.pos, a.perturb) * h;
                    a.pos += a.vel * h;
                }
                a.predTimer -= dt; a.notifyCool -= dt;
                if (a.predTimer <= 0f)
                {
                    a.predTimer = 0.5f;
                    PredictAsteroid(a);
                    if (a.notifyCool <= 0f && a.notifiedCell >= 0 &&
                        Vector3.Angle(G.Dir[a.predCell], G.Dir[a.notifiedCell]) * Mathf.Deg2Rad * R > 3f * G.CellSize && a.tti > 4f)
                        AnnounceAsteroid(a, false);
                }
                float len = a.pos.magnitude;
                Vector3 n = a.pos / len;
                int c = G.CellFromDir(n);
                float alt = len - R - Mathf.Max(0f, Ground(c));
                bool done = false;

                // Ricochet atmosphérique : un corps qui arrive trop à plat et trop vite au sommet de
                // l'atmosphère rebondit comme un galet sur l'eau au lieu de poursuivre sa chute - il repart
                // sur une nouvelle trajectoire balistique (vitesse tangentielle conservée, composante
                // radiale amortie et inversée) et ne pourra plus rebondir une seconde fois.
                if (!a.skipChecked && a.kind != AstBurn)
                {
                    float atmTop = 140f + 70f * a.size;
                    if (alt <= atmTop)
                    {
                        a.skipChecked = true;
                        float speed = a.vel.magnitude;
                        float grazeDeg = Vector3.Angle(-a.vel.normalized, n);
                        float chance = Mathf.Clamp01((grazeDeg - 70f) / 18f) * Mathf.Clamp01((speed - 80f) / 120f);
                        if (grazeDeg > 70f && speed > 80f && rnd.NextDouble() < chance * 0.75)
                        {
                            Vector3 radial = Vector3.Project(a.vel, n);
                            a.vel -= radial * 1.6f;
                            AddFx(n * (R + Mathf.Max(0f, Ground(c)) + alt), n, AstBurn, 8f + 10f * a.size, 1.5f);
                            PredictAsteroid(a);
                            AnnounceAsteroid(a, false);
                            Log("☄ Ricochet sur l'atmosphère, trajectoire modifiée (" + Where(c) + ")", c);
                            continue;
                        }
                    }
                }

                // Fragmentation sous la pression dynamique : un corps assez gros et rapide peut se disloquer
                // en plusieurs morceaux avant de toucher le sol, qui continuent chacun leur propre trajectoire.
                if (!a.fragChecked && a.kind == AstImpact && a.size > 0.9f)
                {
                    float fragAlt = Mathf.Max(55f, a.burstAlt + 20f);
                    if (alt <= fragAlt)
                    {
                        a.fragChecked = true;
                        float chance = Mathf.Clamp01((a.size - 0.9f) / 2.2f) * 0.45f;
                        if (rnd.NextDouble() < chance)
                        {
                            Fragment(a, rnd);
                            Log("☄ Le bolide se disloque en plusieurs fragments (" + Where(c) + ")", c);
                        }
                    }
                }

                if (a.kind == AstImpact) { if (alt <= 1f) { Impact(c, a, rnd); done = true; } }
                else if (alt <= a.burstAlt)
                {
                    if (a.kind == AstAir) Airburst(c, a, alt, rnd); else BurnUp(c, a, alt);
                    done = true;
                }
                if (done) asteroids.RemoveAt(i);
            }
        }

        // Un gros corps qui se disloque sous la pression atmosphérique donne 2 ou 3 fragments supplémentaires
        // (le corps d'origine rétrécit et reste l'un des morceaux) : masse globalement conservée (répartition en
        // cube pour rester cohérent avec DiameterM^3), vitesses légèrement divergentes autour de la trajectoire
        // d'origine, pour un vrai nuage de débris qui retombe en plusieurs points proches au lieu d'un seul.
        void Fragment(Asteroid a, System.Random rnd)
        {
            int n = 2 + rnd.Next(2);
            float originalVolume = a.size * a.size * a.size;
            float mainShare = 0.35f + 0.25f * (float)rnd.NextDouble();
            float fragShare = 1f - mainShare;
            var weights = new float[n];
            float wsum = 0f;
            for (int k = 0; k < n; k++) { weights[k] = 0.4f + (float)rnd.NextDouble(); wsum += weights[k]; }
            Vector3 travel = a.vel.normalized;
            Vector3 perp1 = Vector3.Cross(travel, a.pos.normalized);
            if (perp1.sqrMagnitude < 1e-6f) perp1 = Vector3.Cross(travel, Vector3.up);
            perp1.Normalize();
            Vector3 perp2 = Vector3.Cross(travel, perp1).normalized;
            for (int k = 0; k < n; k++)
            {
                float share = fragShare * weights[k] / wsum;
                float fs = Mathf.Max(0.12f, Mathf.Pow(originalVolume * share, 1f / 3f));
                Vector3 spread = (perp1 * (float)(rnd.NextDouble() * 2d - 1d) + perp2 * (float)(rnd.NextDouble() * 2d - 1d));
                if (spread.sqrMagnitude > 1e-8f) spread.Normalize();
                float spreadAngle = 0.01f + 0.035f * (float)rnd.NextDouble();
                Vector3 fvel = (travel + spread * spreadAngle).normalized * a.vel.magnitude * (0.9f + 0.1f * (float)rnd.NextDouble());
                var frag = new Asteroid
                {
                    kind = fs < 0.5f ? AstBurn : (fs < 1.2f ? AstAir : AstImpact),
                    size = fs,
                    pos = a.pos + spread * (G.CellSize * (0.3f + 0.3f * (float)rnd.NextDouble())),
                    vel = fvel,
                    burstAlt = a.burstAlt,
                    perturb = a.perturb,
                    skipChecked = true,
                    fragChecked = true
                };
                asteroids.Add(frag);
                PredictAsteroid(frag);
                AnnounceAsteroid(frag, true);
            }
            a.size = Mathf.Max(0.12f, a.size * Mathf.Pow(mainShare, 1f / 3f));
            a.kind = a.size < 0.5f ? AstBurn : (a.size < 1.2f ? AstAir : AstImpact);
            PredictAsteroid(a);
        }

        void AddFx(Vector3 pos, Vector3 normal, int kind, float rw, float life)
        {
            astFx.Add(new AstFx { pos = pos, normal = normal, kind = kind, rw = rw, age = 0f, life = life });
        }

        void DepositMeteorite(int c, Asteroid asteroid)
        {
            // Quantité relative, proportionnelle au cube de la taille.
            float amount = 0.1f * asteroid.size * asteroid.size * asteroid.size;
            AddSoil(c, amount, 0.1f, 0.2f, 0.6f, 0.1f);
            meteoriteAdded += amount;
            terrainDirty = true;
        }

        void BurnUp(int c, Asteroid a, float alt)
        {
            Vector3 n = G.Dir[c];
            AddFx(n * (G.Radius + Mathf.Max(0f, Ground(c)) + alt), n, AstBurn, 10f + 14f * a.size, 1.2f);
            DepositMeteorite(c, a);
            burnCount++;
            Log("Météore désintégré dans l'atmosphère (" + Where(c) + ")", c);
        }

        void Airburst(int c, Asteroid a, float alt, System.Random rnd)
        {
            float R = G.Radius;
            Vector3 n = G.Dir[c];
            float rw = AstRw(a.size) * 1.6f;
            AddFx(n * (R + Mathf.Max(0f, Ground(c)) + alt), n, AstAir, rw, 4f);
            float rMax = rw * 1.8f;
            float cosMax = Mathf.Cos(Mathf.Min(rMax / R, 1.5f));
            for (int i = 0; i < M; i++)
            {
                float dp = Vector3.Dot(G.Dir[i], n);
                if (dp < cosMax) continue;
                float x = Mathf.Acos(Mathf.Min(1f, dp)) * R / rw;
                if (x < 1f)
                {
                    if (onBlastPlant != null) onBlastPlant(i);
                    grass[i] *= 0.1f;
                    if (Ground(i) > SeaLevel) { water[i] += snow[i]; snow[i] = 0f; burn[i] = Mathf.Max(burn[i], 0.8f); }
                }
                if (Ground(i) > SeaLevel && x < 1.8f && rnd.NextDouble() < 0.18 * (1.8f - x)) StartFire(i, 0.6f);
                if (x < 1.5f) ash[i] = Mathf.Min(2f, ash[i] + 0.4f * (1.5f - x));
            }
            if (onBlastFauna != null) onBlastFauna(n, rw / R);
            DepositMeteorite(c, a);
            newQuakes.Add(new QuakeEvent { dir = n, mag = 5f + a.size });
            airburstCount++;
            terrainDirty = true;
            Log("EXPLOSION D'ASTÉROÏDE en altitude (" + Where(c) + ")", c);
        }

        void Impact(int c, Asteroid a, System.Random rnd)
        {
            float R = G.Radius;
            Vector3 n = G.Dir[c];
            float rw = AstRw(a.size);
            float depth = Mathf.Max(3f, rw * 0.28f);
            bool sea = Ground(c) < SeaLevel;

            // Angle d'impact : un corps qui arrive presque à la verticale creuse un cratère circulaire
            // classique. Plus la trajectoire est rasante, plus le cratère s'étire dans l'axe de vol (vraie
            // ellipse, comme les cratères d'impact oblique réels) et plus il est peu profond - l'essentiel de
            // l'énergie se dissipe latéralement au lieu de creuser droit dans le sol.
            Vector3 travelDir = a.vel.sqrMagnitude > 1e-6f ? a.vel.normalized : -n;
            float grazeDeg = Vector3.Angle(-travelDir, n);
            Vector3 along = Vector3.ProjectOnPlane(travelDir, n);
            along = along.sqrMagnitude > 1e-6f ? along.normalized : G.Tan[c * 4];
            Vector3 across = Vector3.Cross(n, along).normalized;
            float obliquity = Mathf.Clamp01((grazeDeg - 20f) / 55f);
            float elong = Mathf.Lerp(1f, 2.4f, obliquity);
            depth *= Mathf.Lerp(1f, 0.55f, obliquity);
            float seed = c * 0.0173f;

            AddFx(n * (R + Mathf.Max(0f, Ground(c))), n, AstImpact, rw * Mathf.Sqrt(elong), 5f);
            float rMax = rw * elong * 3f;
            float cosMax = Mathf.Cos(Mathf.Min(rMax / R, 1.5f));
            float excavated = 0f, rimWeight = 0f;
            // Un gros impact fracture assez la croûte pour que du magma remonte sous le cratère : au-delà d'une
            // certaine taille, une partie de l'excavation "fond" au lieu d'être simplement déblayée, et alimente
            // magma[] (voir FeedMagma, réutilisé du système sismique) ; de quoi faire déborder une coulée de lave
            // d'impact qui façonnera vraiment le terrain au fil du temps (StepLavaFlows), pas juste un trou.
            float meltFactor = Mathf.Clamp01((a.size - 0.7f) / 2.3f);
            float meltFed = 0f;

            // Part des éjectas reçue par une cellule du rebord. Pour un impact oblique, l'essentiel retombe du
            // côté "aval" de la trajectoire (dans le sens du vol) plutôt que réparti également tout autour du
            // cratère. Une seule formule pour le total (rimWeight) et pour le dépôt : le volume est conservé.
            float RimShare(int cell, float x, float irregular)
            {
                float k = (1f - Mathf.Abs(x - 1.1f) / 0.4f) * irregular;
                if (k <= 0f) return 0f;
                if (obliquity > 0.01f)
                {
                    // G.Dir est sur la sphère unité : on passe en unités monde avant de comparer à rw.
                    float downrange = Vector3.Dot(G.Dir[cell] - n, along) * R;
                    k *= 1f + 0.9f * obliquity * Mathf.Clamp(downrange / (rw * 0.6f), -0.6f, 1f);
                }
                return k;
            }
            for (int i = 0; i < M; i++)
            {
                float dp = Vector3.Dot(G.Dir[i], n);
                if (dp < cosMax) continue;
                float x = EllipticalX(G.Dir[i], along, across, dp, R, rw, elong);
                bool land = Ground(i) > SeaLevel;
                // irrégularité : évite un cratère/rebord parfaitement lisse et symétrique
                float irregular = Mathf.Clamp(1f + 0.18f * Noise.Perlin(x * 2.2f + seed, Vector3.Dot(G.Dir[i], along) * 0.02f - seed, seed * 1.7f), 0.75f, 1.3f);

                if (x < 0.85f)
                {
                    float take = (depth * (1f - (x / 0.85f) * (x / 0.85f)) + 0.1f) * irregular;
                    float fromSed = Mathf.Min(sed[i], take);
                    sed[i] -= fromSed;
                    rock[i] -= take - fromSed;
                    excavated += take * G.Area[i];
                    // le fer de l'astéroïde se dépose au fond du cratère
                    if (x < 0.6f && rnd.NextDouble() < 0.5) ore[i] = Iron;
                    rockT[i] = Granite;
                    hard[i] = 0.6f;
                    if (meltFactor > 0f && x < 0.55f)
                        meltFed += FeedMagma(i, Mathf.Lerp(0.3f, 1.1f, meltFactor) * (1f - x / 0.55f));
                }
                else if (x < 1.5f) rimWeight += RimShare(i, x, irregular);
                if (x < 1.5f)
                {
                    if (susp[i] > 0f) { DepositWater(i, susp[i], 0.5f); susp[i] = 0f; }
                    water[i] += snow[i] + gw[i] * 0.7f;
                    snow[i] = 0f;
                    gw[i] *= 0.3f;
                    if (onBlastPlant != null) onBlastPlant(i);
                    grass[i] = 0f; cover[i] = 0f;
                    if (land) { burn[i] = 1f; fert[i] = 0f; permB[i] = Mathf.Max(permB[i], 0.5f); }
                }
                // Le terrain alentour n'est pas qu'un simple cratère géométrique : au-delà du rebord, les pentes
                // déjà raides (flanc de montagne proche) s'effondrent sous le choc et alimentent des éboulements,
                // au lieu de rester figées comme si l'impact ignorait le relief existant.
                if (land && x >= 1.1f && x < 3f && Slope(i) > 0.35f &&
                    rnd.NextDouble() < 0.5 * (1f - (x - 1.1f) / 1.9f))
                    Landslide(i, 2f + 6f * (1f - (x - 1.1f) / 1.9f));
                if (land && x >= 1f && x < 2.6f && rnd.NextDouble() < 0.35 * (2.6f - x) / 1.6f) StartFire(i, 0.7f);
                if (x < 2.2f)
                {
                    ash[i] = Mathf.Min(2f, ash[i] + 1.2f * (2.2f - x) / 2.2f);
                    co2[i] += 250f * (1f - x / 2.2f); so2[i] += 8f * (1f - x / 2.2f);
                }
            }
            if (rimWeight > 0f)
            {
                for (int i = 0; i < M; i++)
                {
                    float dp = Vector3.Dot(G.Dir[i], n);
                    if (dp < cosMax) continue;
                    float x = EllipticalX(G.Dir[i], along, across, dp, R, rw, elong);
                    if (x < 0.85f || x >= 1.5f) continue;
                    float irregular = Mathf.Clamp(1f + 0.18f * Noise.Perlin(x * 2.2f + seed, Vector3.Dot(G.Dir[i], along) * 0.02f - seed, seed * 1.7f), 0.75f, 1.3f);
                    float k = RimShare(i, x, irregular);
                    if (k > 0f) AddSoil(i, excavated * k / (rimWeight * G.Area[i]), 0.1f, 0.2f, 0.6f, 0.1f);
                }
            }
            else rock[c] += excavated / G.Area[c];
            if (meltFed > 0.15f && lavaFlows.Count < 48)
            {
                // La coulée est prélevée sur le magma que l'impact vient de faire remonter, pas créée en plus.
                float erupted = Mathf.Min(magma[c], meltFed * 0.4f);
                if (erupted > 0f)
                {
                    magma[c] -= erupted;
                    lavaFlows.Add(new LavaFlow { cell = c, steps = 0, vol = erupted * MagmaSolidScale });
                    rockT[c] = Basalt;
                }
            }
            DepositMeteorite(c, a);
            if (onBlastFauna != null) onBlastFauna(n, rw * 2.4f / R);
            newQuakes.Add(new QuakeEvent { dir = n, mag = 6.5f + a.size });
            quakeCount++;
            impactCount++;
            terrainDirty = true;
            string msg = "IMPACT D'ASTÉROÏDE (" + Where(c) + ")";
            if (meltFed > 0.15f) msg += " - le magma remonte et s'écoule du cratère";
            if (sea && SpawnTsunami(c, Mathf.Min(10f, 4f + 5f * a.size))) msg += " - ALERTE TSUNAMI";
            Log(msg, c);
        }

        // Distance radiale "équivalente" normalisée par rw, mais mesurée sur une ellipse étirée d'un facteur
        // "elong" le long de l'axe de vol (along) : à elong=1 c'est exactement l'ancienne distance angulaire
        // circulaire (dp/R/rw), au-delà ça étire le cratère dans le sens de la trajectoire.
        static float EllipticalX(Vector3 dir, Vector3 along, Vector3 across, float dp, float R, float rw, float elong)
        {
            float distance = Mathf.Acos(Mathf.Min(1f, dp)) * R;
            float px = Vector3.Dot(dir, along), py = Vector3.Dot(dir, across);
            float planar = Mathf.Sqrt(px * px + py * py);
            float scale = planar > 1e-6f ? distance / planar : 0f;
            px *= scale; py *= scale;
            return Mathf.Sqrt((px * px) / (elong * elong) + py * py) / rw;
        }
    }
}
