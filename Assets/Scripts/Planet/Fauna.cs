using System;
using System.Collections.Generic;
using UnityEngine;

namespace LivingPlanet
{
    public struct Animal
    {
        public Vector3 dir, head;
        public float energy, water, age, cd, think, boost, preg;
        public int cell;
        public bool dead, male, sleep;
    }

    public sealed class Pop
    {
        public Animal[] a;
        public int n;
        public int cap;
    }

    // Faune : besoins (faim, soif, sommeil), reproduction, mort naturelle. Les durées sont en jours de jeu.
    //
    // Réseau trophique (chaque espèce est mangée par au moins une autre ou recyclée par les charognards) :
    //   flore (herbe, baies, algues) -> lapins, cerfs, poissons (algues) ; fruits -> oiseaux, renards
    //   plancton -> krill -> poissons, baleines ; insectes (végétation) -> oiseaux, renards
    //   lapins -> loups, renards, rapaces ; cerfs -> loups ; oiseaux -> rapaces ; poissons -> requins, rapaces
    //   toute mort -> charogne -> loups, renards, rapaces, requins, puis fertilité du sol / nutriments marins -> flore, plancton
    public sealed class Fauna
    {
        public const int Rabbit = 0, Deer = 1, Wolf = 2, Bird = 3, Fish = 4, Shark = 5, Fox = 6, Raptor = 7, Whale = 8, Count = 9;
        public static readonly string[] Names = { "Lapins", "Cerfs", "Loups", "Oiseaux", "Poissons", "Requins", "Renards", "Rapaces", "Baleines" };
        static readonly float[] Speed = { 4.2f, 5.6f, 9f, 11f, 5f, 8.5f, 7.5f, 12f, 4.5f };
        static readonly int[] Cap = { 800, 250, 60, 300, 600, 40, 100, 50, 24 };
        static readonly int[] Start = { 250, 70, 12, 100, 200, 8, 22, 10, 6 };
        static readonly float[] Hunger = { 0.45f, 0.35f, 0.25f, 0.5f, 0.4f, 0.2f, 0.35f, 0.3f, 0.12f };
        static readonly float[] Thirst = { 0.7f, 0.6f, 0.5f, 0.6f, 0f, 0f, 0.55f, 0.5f, 0f };
        static readonly float[] Life = { 30f, 60f, 70f, 45f, 40f, 100f, 50f, 60f, 120f };
        static readonly float[] Mature = { 4f, 8f, 9f, 5f, 5f, 14f, 6f, 8f, 16f };
        static readonly float[] Gest = { 1.2f, 3f, 2.5f, 1.5f, 1.2f, 4f, 2f, 3f, 6f };
        static readonly int[] Litter = { 3, 1, 2, 2, 3, 1, 2, 1, 1 };
        // masse relative : charogne laissée par une mort
        static readonly float[] Size = { 0.2f, 0.5f, 0.5f, 0.1f, 0.15f, 0.6f, 0.25f, 0.2f, 1f };
        // âge minimal de la planète (milliards d'années) pour que l'espèce apparaisse naturellement
        static readonly float[] Gate = { 3.6f, 3.8f, 4f, 3.6f, 1.2f, 3.2f, 4f, 4f, 4.2f };
        static readonly float[] Sense = { 0f, 0f, 60f, 0f, 0f, 60f, 45f, 90f, 0f };
        const float BreathPerDay = 37.5f;

        struct Prey
        {
            public int sp; public float gain, catchP;
            public Prey(int s, float g, float c) { sp = s; gain = g; catchP = c; }
        }

        static readonly Prey[][] Diet =
        {
            null, null,
            new[] { new Prey(Deer, 0.55f, 0.35f), new Prey(Rabbit, 0.35f, 0.4f) },
            null, null,
            new[] { new Prey(Fish, 0.5f, 0.4f) },
            new[] { new Prey(Rabbit, 0.45f, 0.45f) },
            new[] { new Prey(Bird, 0.5f, 0.3f), new Prey(Rabbit, 0.45f, 0.3f), new Prey(Fish, 0.35f, 0.25f) },
            null
        };

        static readonly int[][] Threats =
        {
            new[] { Wolf, Fox, Raptor }, new[] { Wolf }, null, new[] { Raptor }, new[] { Shark, Raptor }, null, new[] { Wolf }, null, null
        };

        static bool IsFlyer(int sp) { return sp == Bird || sp == Raptor; }
        static bool IsAquatic(int sp) { return sp == Fish || sp == Shark || sp == Whale; }
        static bool IsScavenger(int sp) { return sp == Wolf || sp == Fox || sp == Raptor || sp == Shark; }
        static bool IsHunter(int sp) { return Diet[sp] != null; }

        public readonly Pop[] pops = new Pop[Count];
        public readonly EcoFields F;
        public Vector3 sunDir = Vector3.right;
        readonly PlanetGrid G;
        readonly PlanetSim S;
        readonly Ecosystem E;
        readonly float R;
        readonly PlanetRandom rng = new PlanetRandom(1234);
        internal int RandomState { get { return rng.State; } set { rng.State = value; } }
        float fieldAcc;
        float Rf() { return (float)rng.NextDouble(); }

        public Fauna(PlanetWorld w)
        {
            G = w.Grid; S = w.Sim; E = w.Eco; R = G.Radius;
            F = new EcoFields(S, E);
            for (int i = 0; i < Count; i++)
                pops[i] = new Pop { a = new Animal[Cap[i] + 16], cap = Cap[i] };
        }

        public bool Available(int sp) { return S.planetAge >= Gate[sp]; }

        public void Init()
        {
            float lvl = 0.35f + 0.65f * S.LifeLevel;
            for (int sp = 0; sp < Count; sp++)
            {
                pops[sp].n = 0;
                if (!Available(sp)) continue;
                int n = Mathf.RoundToInt(Start[sp] * lvl);
                for (int i = 0; i < n; i++) SpawnRandom(sp, 0.6f + 0.4f * Rf(), true);
            }
        }

        public int Total { get { int t = 0; for (int i = 0; i < Count; i++) t += pops[i].n; return t; } }

        // ------------------------------------------------------------ règles
        bool Valid(int sp, int c)
        {
            float g = S.Ground(c) - S.SeaLevel;
            switch (sp)
            {
                case Bird:
                case Raptor: return true;
                case Fish:
                case Shark: return g < -1.2f && S.water[c] > 1.2f && S.temp[c] > -1.5f;
                case Whale: return g < -5f && S.water[c] > 5f && S.temp[c] > -1.5f;
                default: return g > 0.5f && g < 220f && S.water[c] < 0.5f && S.lava[c] < 0.1f && S.burn[c] < 0.5f;
            }
        }

        // contrainte environnementale : froid/chaleur extrêmes, gaz toxiques (SO2), manque d'oxygène ; augmente la dépense d'énergie
        float Stress(int sp, int c)
        {
            float t = S.temp[c];
            float st = IsAquatic(sp) ? Mathf.Clamp01((t - 30f) / 6f) : Mathf.Clamp01((-18f - t) / 20f) + Mathf.Clamp01((t - 38f) / 12f);
            st += Mathf.Clamp01(S.so2[c] / 25f);
            float o = S.o2[c];
            if (o < 15f) st += Mathf.Clamp01((15f - o) / 6f);
            return st;
        }

        // introduction manuelle : mêmes règles de validité et plafonds de population que la reproduction naturelle
        public int Introduce(int sp, int c, int count, int radius)
        {
            if (c < 0) return 0;
            var cells = new List<int> { c };
            var seen = new HashSet<int> { c };
            for (int h = 0, last = 0; h < radius; h++)
            {
                int end = cells.Count;
                for (int i = last; i < end; i++)
                    for (int k = 0; k < 4; k++)
                    {
                        int n = G.Nbr[cells[i] * 4 + k];
                        if (n >= 0 && seen.Add(n)) cells.Add(n);
                    }
                last = end;
            }
            int placed = 0;
            for (int i = 0; i < count * 8 && placed < count && pops[sp].n < pops[sp].cap; i++)
            {
                int cell = cells[rng.Next(cells.Count)];
                if (!Valid(sp, cell)) continue;
                Spawn(sp, cell, 0.8f, Mature[sp] + Rf() * Life[sp] * 0.3f);
                placed++;
            }
            return placed;
        }

        // une espèce n'apparaît (au départ ou par migration) que là où elle trouve de quoi se nourrir
        bool Habitat(int sp, int c)
        {
            switch (sp)
            {
                case Rabbit:
                case Deer: return S.grass[c] >= 0.3f;
                case Wolf:
                case Raptor:
                case Shark: return F.carrion[c] > 0.12f || HasLocalPrey(sp, c);
                case Fox: return F.carrion[c] > 0.12f || F.insects[c] > 0.2f ||
                    (F.fruit[c] > 0f && E.FruitType(c) == Ecosystem.Berry) || HasLocalPrey(sp, c);
                case Bird: return F.insects[c] > 0.2f || (F.fruit[c] > 0f && E.FruitType(c) >= 0);
                case Fish: return F.krill[c] > 0.2f || F.plankton[c] > 0.3f;
                case Whale: return F.krill[c] > 0.3f;
            }
            return true;
        }

        bool HasLocalPrey(int sp, int c)
        {
            float minimumDot = Mathf.Cos(Mathf.Min(Mathf.PI, Sense[sp] / R));
            Vector3 position = G.Dir[c];
            var diet = Diet[sp];
            for (int d = 0; d < diet.Length; d++)
            {
                var prey = pops[diet[d].sp];
                for (int i = 0; i < prey.n; i++)
                {
                    if (prey.a[i].dead || Vector3.Dot(position, prey.a[i].dir) <= minimumDot) continue;
                    if (sp == Raptor && diet[d].sp == Fish && S.Ground(prey.a[i].cell) < S.SeaLevel - 8f) continue;
                    return true;
                }
            }
            return false;
        }

        bool SpawnRandom(int sp, float energy, bool adult)
        {
            for (int t = 0; t < 120; t++)
            {
                int c = rng.Next(S.M);
                if (!Valid(sp, c) || !Habitat(sp, c)) continue;
                Spawn(sp, c, energy, adult ? Mature[sp] + Rf() * Life[sp] * 0.4f : 0f);
                return true;
            }
            return false;
        }

        void Spawn(int sp, int c, float energy, float age)
        {
            var p = pops[sp];
            if (p.n >= p.cap) return;
            Vector3 d = G.Dir[c];
            Vector3 h = Vector3.Cross(d, Mathf.Abs(d.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            h = Quaternion.AngleAxis(Rf() * 360f, d) * h;
            p.a[p.n++] = new Animal
            {
                dir = d, head = h.normalized, energy = energy, water = 0.7f + 0.3f * Rf(), cell = c,
                age = age, cd = Rf() * 1.5f, think = Rf(), male = Rf() < 0.5f
            };
        }

        int Walk(int c, int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                int n = G.Nbr[c * 4 + rng.Next(4)];
                if (n >= 0) c = n;
            }
            return c;
        }

        Vector3 Toward(Vector3 from, Vector3 to)
        {
            Vector3 h = Vector3.ProjectOnPlane(to - from, from);
            return h.sqrMagnitude < 1e-10f ? Vector3.zero : h.normalized;
        }

        Vector3 Jitter(Vector3 head, Vector3 up, float maxDeg)
        {
            return Quaternion.AngleAxis((Rf() * 2f - 1f) * maxDeg, up) * head;
        }

        float PlantFood(int c)
        {
            int pi = E.cellPlant[c];
            if (pi < 0) return 0f;
            var p = E.plants[pi];
            if (p.state != 0 || p.size < 0.35f) return 0f;
            return (p.type == Ecosystem.Bush || p.type == Ecosystem.Berry || p.type == Ecosystem.Reed || p.type == Ecosystem.Cactus) ? 0.6f : 0f;
        }

        bool IsKelp(int c)
        {
            int pi = E.cellPlant[c];
            return pi >= 0 && E.plants[pi].type == Ecosystem.Kelp;
        }

        bool IsWater(int c)
        {
            return S.FreshWater(c);
        }

        bool NearWater(int c)
        {
            if (IsWater(c)) return true;
            for (int k = 0; k < 4; k++)
            {
                int n = G.Nbr[c * 4 + k];
                if (n >= 0 && IsWater(n)) return true;
            }
            return false;
        }

        // l'explosion d'un astéroïde tue les animaux dans un rayon angulaire (radians)
        public void KillNear(Vector3 dir, float ang)
        {
            float cs = Mathf.Cos(Mathf.Min(ang, 1.5f));
            for (int sp = 0; sp < Count; sp++)
            {
                var p = pops[sp];
                for (int i = 0; i < p.n; i++)
                    if (!p.a[i].dead && Vector3.Dot(p.a[i].dir, dir) > cs) Die(sp, ref p.a[i]);
            }
        }

        // toute mort alimente la charogne (puis le sol ou les nutriments marins)
        void Die(int sp, ref Animal an)
        {
            an.dead = true;
            F.AddCarrion(an.cell, Size[sp] * 0.5f);
        }

        // ------------------------------------------------------------ simulation
        // dt : secondes de mouvement simulées, days : jours de jeu écoulés (besoins, âge)
        public void Step(float dt, float days)
        {
            if (dt <= 0f) return;
            fieldAcc += days;
            if (fieldAcc >= 0.1f) { F.Step(fieldAcc); fieldAcc = 0f; }
            for (int sp = 0; sp < Count; sp++)
            {
                var p = pops[sp];
                int n = p.n;
                for (int i = 0; i < n; i++)
                {
                    if (p.a[i].dead) continue;
                    if (!IsAquatic(sp)) S.animalBreath[p.a[i].cell] += days * BreathPerDay;
                    Update(sp, p, i, dt, days);
                }
            }
            for (int sp = 0; sp < Count; sp++)
            {
                var p = pops[sp];
                for (int i = p.n - 1; i >= 0; i--)
                    if (p.a[i].dead) { p.a[i] = p.a[p.n - 1]; p.n--; }
            }
        }

        bool WantsSleep(int sp, ref Animal an)
        {
            if (sp == Whale) return false;
            if (an.energy < 0.25f || an.water < 0.25f) return false;
            float sd = Vector3.Dot(an.dir, sunDir);
            return (sp == Wolf || sp == Shark || sp == Fox) ? sd > 0.6f : sd < -0.1f;
        }

        void Update(int sp, Pop pop, int i, float dt, float days)
        {
            ref Animal an = ref pop.a[i];
            an.sleep = WantsSleep(sp, ref an);
            an.energy -= (Hunger[sp] * (an.sleep ? 0.4f : 1f) + 0.3f * Stress(sp, an.cell)) * days;
            if (Thirst[sp] > 0f) an.water -= Thirst[sp] * days * (an.sleep ? 0.5f : 1f);
            else an.water = 1f;
            an.age += days;
            an.cd -= days;
            if (an.preg > 0f)
            {
                an.preg -= days;
                if (an.preg <= 0f) GiveBirth(sp, pop, ref an);
            }
            if (an.energy <= 0f || an.water <= 0f || an.age > Life[sp] * (0.85f + 0.3f * ((i * 7919) % 100) / 100f)) { Die(sp, ref an); return; }

            if (an.sleep) { an.boost = 0f; return; }
            an.think -= dt;
            if (an.boost > 0f) an.boost -= dt;

            float step = Speed[sp] * (an.boost > 0f ? 1.6f : 1f) * dt / R;
            Vector3 nd = (an.dir + an.head * step).normalized;
            int nc = G.CellFromDir(nd);
            if (IsFlyer(sp) || Valid(sp, nc))
            {
                an.dir = nd; an.cell = nc;
                an.head = Vector3.ProjectOnPlane(an.head, nd).normalized;
            }
            else
            {
                an.head = Quaternion.AngleAxis(90f + Rf() * 180f, an.dir) * an.head;
                an.think = 0f;
            }

            if (an.think > 0f) return;
            an.think = IsHunter(sp) ? 0.3f : 0.7f + Rf() * 0.5f;
            Think(sp, pop, ref an);
        }

        void GiveBirth(int sp, Pop pop, ref Animal an)
        {
            for (int k = 0; k < Litter[sp]; k++)
            {
                int cc = Walk(an.cell, 1 + rng.Next(2));
                if (Valid(sp, cc)) Spawn(sp, cc, 0.7f, 0f);
            }
            an.cd = 1.5f;
        }

        void Think(int sp, Pop pop, ref Animal an)
        {
            int c = an.cell;
            if (!IsFlyer(sp) && !Valid(sp, c)) { Die(sp, ref an); return; }

            if (Threats[sp] != null && Flee(sp, ref an)) return;

            bool thirsty = Thirst[sp] > 0f && an.water < 0.55f;
            if (thirsty && SeekWater(sp, ref an)) return;

            if (IsScavenger(sp) && Scavenge(sp, ref an)) return;

            switch (sp)
            {
                case Rabbit:
                case Deer: if (EatHerbivore(sp, ref an)) return; break;
                case Wolf:
                case Shark:
                case Raptor: if (Hunt(sp, ref an)) return; break;
                case Fox: if (Hunt(sp, ref an) || EatForager(ref an, true)) return; break;
                case Bird: if (EatForager(ref an, false)) return; break;
                case Fish: if (EatFish(ref an)) return; break;
                case Whale: if (EatWhale(ref an)) return; break;
            }

            if (!an.male && an.preg <= 0f && an.cd <= 0f && an.age > Mature[sp] && an.energy > 0.65f && an.water > 0.4f && pop.n < pop.cap)
            {
                if (Mate(sp, pop, ref an)) return;
            }
            an.head = Jitter(an.head, an.dir, IsFlyer(sp) ? 50f : 45f);
        }

        bool Flee(int sp, ref Animal an)
        {
            var th = Threats[sp];
            for (int t = 0; t < th.Length; t++)
            {
                int ts = th[t];
                if (ts == Raptor && sp == Fish && S.Ground(an.cell) < S.SeaLevel - 8f) continue;
                float cosFlee = Mathf.Cos((ts == Raptor ? 25f : 35f) / R);
                var threat = pops[ts];
                for (int w = 0; w < threat.n; w++)
                {
                    if (threat.a[w].dead || threat.a[w].sleep) continue;
                    if (Vector3.Dot(threat.a[w].dir, an.dir) > cosFlee)
                    {
                        Vector3 away = Toward(an.dir, an.dir * 2f - threat.a[w].dir);
                        if (away != Vector3.zero) an.head = away;
                        an.boost = 2f;
                        an.think = 0.3f;
                        return true;
                    }
                }
            }
            return false;
        }

        // boit si de l'eau est à portée, sinon part à sa recherche (retourne true si en déplacement)
        bool SeekWater(int sp, ref Animal an)
        {
            if (NearWater(an.cell)) { an.water = 1f; return false; }
            int c = an.cell;
            int best = -1; float low = S.rock[c] + S.sed[c];
            for (int k = 0; k < 6; k++)
            {
                int t = Walk(c, 2 + rng.Next(6));
                if (!IsFlyer(sp) && !Valid(sp, t) && !NearWater(t)) continue;
                if (NearWater(t)) { best = t; break; }
                float g = S.rock[t] + S.sed[t];
                if (g < low) { low = g; best = t; }
            }
            if (best < 0) return false;
            Vector3 h = Toward(an.dir, G.Dir[best]);
            if (h == Vector3.zero) return false;
            an.head = h;
            return true;
        }

        bool EatHerbivore(int sp, ref Animal an)
        {
            int c = an.cell;
            if (an.energy < 0.95f)
            {
                if (S.grass[c] > 0.3f) { S.grass[c] -= 0.25f; an.energy += 0.3f; }
                else if (sp == Deer) { float f = E.Browse(c); if (f > 0f) an.energy += f; }
            }
            if (an.energy < 0.7f)
            {
                int best = -1; float bs = 0.3f;
                for (int k = 0; k < 5; k++)
                {
                    int t = Walk(c, 1 + rng.Next(4));
                    if (!Valid(sp, t)) continue;
                    float s = S.grass[t] + (sp == Deer ? PlantFood(t) : 0f);
                    if (s > bs) { bs = s; best = t; }
                }
                if (best >= 0)
                {
                    Vector3 h = Toward(an.dir, G.Dir[best]);
                    if (h != Vector3.zero) { an.head = h; return true; }
                }
            }
            return false;
        }

        // charognards : mangent sur place la charogne de la cellule, sinon la cherchent alentour
        bool Scavenge(int sp, ref Animal an)
        {
            if (an.energy >= 0.8f) return false;
            int c = an.cell;
            if (F.carrion[c] > 0.12f)
            {
                float take = Mathf.Min(F.carrion[c], 0.35f);
                F.carrion[c] -= take;
                an.energy = Mathf.Min(1f, an.energy + take * 1.2f);
                return true;
            }
            for (int k = 0; k < 4; k++)
            {
                int t = Walk(c, 1 + rng.Next(5));
                if (F.carrion[t] < 0.2f || F.IsOcean(t) != (sp == Shark)) continue;
                Vector3 h = Toward(an.dir, G.Dir[t]);
                if (h != Vector3.zero) { an.head = h; return true; }
            }
            return false;
        }

        // prédation générique : table de régime (gain d'énergie, probabilité de capture), proie la plus proche
        bool Hunt(int sp, ref Animal an)
        {
            if (an.energy >= 0.75f) return false;
            var diet = Diet[sp];
            float bestDot = Mathf.Cos(Sense[sp] / R);
            Pop bp = null; int bi = -1; int bd = -1;
            for (int di = 0; di < diet.Length; di++)
            {
                var prey = pops[diet[di].sp];
                bool shallowOnly = sp == Raptor && diet[di].sp == Fish;
                for (int j = 0; j < prey.n; j++)
                {
                    if (prey.a[j].dead) continue;
                    float d = Vector3.Dot(prey.a[j].dir, an.dir);
                    if (d <= bestDot) continue;
                    if (shallowOnly && S.Ground(prey.a[j].cell) < S.SeaLevel - 8f) continue;
                    bestDot = d; bp = prey; bi = j; bd = di;
                }
            }
            if (bp == null) return false;
            if (bestDot > Mathf.Cos(5f / R))
            {
                if (Rf() < diet[bd].catchP)
                {
                    bp.a[bi].dead = true;
                    F.AddCarrion(bp.a[bi].cell, Size[diet[bd].sp] * 0.3f);
                    an.energy = Mathf.Min(1f, an.energy + diet[bd].gain);
                }
                else
                {
                    bp.a[bi].boost = 1.5f;
                    an.energy -= 0.03f;
                    an.think = 0.6f;
                }
                return true;
            }
            Vector3 h = Toward(an.dir, bp.a[bi].dir);
            if (h != Vector3.zero) an.head = h;
            an.boost = 1f;
            an.energy -= 0.004f;
            return true;
        }

        // oiseaux et renards : fruits, insectes ; les renards mangent aussi des baies
        bool EatForager(ref Animal an, bool fox)
        {
            int c = an.cell;
            if (an.energy < 0.95f)
            {
                int ft = E.FruitType(c);
                if (ft >= 0 && (!fox || ft == Ecosystem.Berry) && F.fruit[c] > 0f && Rf() < 0.5f)
                {
                    float gain = fox ? 0.2f : 0.3f;
                    float taken = F.TakeFruit(c, Mathf.Min(0.12f, (1f - an.energy) * 0.12f / gain));
                    an.energy += gain * taken / 0.12f;
                    if (taken > 0f && Rf() < 0.35f) E.SeedAt(Walk(c, 1 + rng.Next(3)), ft);
                }
                else if (F.insects[c] > 0.2f)
                {
                    an.energy = Mathf.Min(1f, an.energy + (fox ? 0.15f : 0.3f) * Mathf.Min(1f, F.insects[c] / 0.5f));
                    F.insects[c] -= 0.06f;
                }
            }
            if (an.energy < 0.7f)
            {
                for (int k = 0; k < 5; k++)
                {
                    int t = Walk(c, 1 + rng.Next(6));
                    int ft = E.FruitType(t);
                    if ((ft < 0 || (fox && ft != Ecosystem.Berry) || F.fruit[t] <= 0f) && F.insects[t] < 0.3f) continue;
                    Vector3 h = Toward(an.dir, G.Dir[t]);
                    if (h != Vector3.zero) { an.head = h; return true; }
                }
            }
            return false;
        }

        // poissons : krill, plancton, algues (broutées)
        bool EatFish(ref Animal an)
        {
            int c = an.cell;
            if (an.energy < 0.95f)
            {
                if (F.krill[c] > 0.15f)
                {
                    an.energy = Mathf.Min(1f, an.energy + 0.35f * Mathf.Min(1f, F.krill[c] / 0.5f));
                    F.krill[c] -= 0.07f;
                }
                else if (IsKelp(c) && an.energy < 0.6f) { float f = E.Browse(c); if (f > 0f) an.energy += f; }
                else if (F.plankton[c] > 0.2f)
                {
                    an.energy = Mathf.Min(1f, an.energy + 0.12f);
                    F.plankton[c] -= 0.05f;
                }
            }
            if (an.energy < 0.7f)
            {
                for (int k = 0; k < 5; k++)
                {
                    int t = Walk(c, 1 + rng.Next(4));
                    if (!Valid(Fish, t)) continue;
                    float s = F.krill[t] + (IsKelp(t) ? 0.3f : 0f) + 0.3f * F.plankton[t];
                    if (s < 0.25f) continue;
                    Vector3 h = Toward(an.dir, G.Dir[t]);
                    if (h != Vector3.zero) { an.head = h; return true; }
                }
            }
            return false;
        }

        // baleines : filtrent le krill
        bool EatWhale(ref Animal an)
        {
            int c = an.cell;
            if (an.energy < 0.95f && F.krill[c] > 0.2f)
            {
                an.energy = Mathf.Min(1f, an.energy + 0.45f * Mathf.Min(1f, F.krill[c] / 0.6f));
                F.krill[c] -= 0.15f;
            }
            if (an.energy < 0.7f)
            {
                for (int k = 0; k < 6; k++)
                {
                    int t = Walk(c, 2 + rng.Next(7));
                    if (!Valid(Whale, t) || F.krill[t] < 0.3f) continue;
                    Vector3 h = Toward(an.dir, G.Dir[t]);
                    if (h != Vector3.zero) { an.head = h; return true; }
                }
            }
            return false;
        }

        // une femelle prête cherche un mâle mature de son espèce
        bool Mate(int sp, Pop pop, ref Animal an)
        {
            float bestDot = Mathf.Cos(70f / R);
            int bi = -1;
            for (int j = 0; j < pop.n; j++)
            {
                if (!pop.a[j].male || pop.a[j].dead || pop.a[j].age < Mature[sp] || pop.a[j].energy < 0.4f) continue;
                float d = Vector3.Dot(pop.a[j].dir, an.dir);
                if (d > bestDot) { bestDot = d; bi = j; }
            }
            if (bi < 0) return false;
            if (bestDot > Mathf.Cos(4f / R))
            {
                an.preg = Gest[sp];
                an.energy -= 0.1f;
                return true;
            }
            Vector3 h = Toward(an.dir, pop.a[bi].dir);
            if (h == Vector3.zero) return false;
            an.head = h;
            return true;
        }
    }
}
