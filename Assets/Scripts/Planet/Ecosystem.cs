using System;
using System.Collections.Generic;
using UnityEngine;

namespace LivingPlanet
{
    public struct Plant
    {
        public int cell;
        public byte type;   // 0 broadleaf, 1 conifer, 2 bush
        public byte state;  // 0 alive, 1 dead, 2 burning, 3 burnt, 4 fallen and rotting
        public float age, maxAge, size, health, jx, jy, yaw, scaleVar, timer, gh;
    }

    public sealed class PlantType
    {
        public float tMin, tLo, tHi, tMax, mMin, mOpt, mature, maxAge, seedRate, seedRadius, snagTime, scale;
    }

    public sealed class Ecosystem
    {
        public const int Broad = 0, Conifer = 1, Bush = 2, Palm = 3, Cactus = 4, Berry = 5, Reed = 6, Kelp = 7, TypeCount = 8;

        public static readonly PlantType[] Types =
        {
            new PlantType{ tMin=-2,  tLo=8,  tHi=27, tMax=38, mMin=0.30f, mOpt=0.55f, mature=12, maxAge=110, seedRate=0.35f, seedRadius=3, snagTime=6, scale=1.4f },
            new PlantType{ tMin=-16, tLo=-3, tHi=14, tMax=26, mMin=0.22f, mOpt=0.45f, mature=15, maxAge=150, seedRate=0.30f, seedRadius=3, snagTime=8, scale=1.5f },
            new PlantType{ tMin=-6,  tLo=5,  tHi=32, tMax=44, mMin=0.10f, mOpt=0.30f, mature=4,  maxAge=35,  seedRate=0.80f, seedRadius=2, snagTime=2, scale=0.95f },
            new PlantType{ tMin=12,  tLo=21, tHi=34, tMax=44, mMin=0.20f, mOpt=0.45f, mature=10, maxAge=90,  seedRate=0.40f, seedRadius=3, snagTime=6, scale=1.5f },
            new PlantType{ tMin=6,   tLo=16, tHi=40, tMax=52, mMin=0.00f, mOpt=0.10f, mature=14, maxAge=130, seedRate=0.25f, seedRadius=2, snagTime=8, scale=1.1f },
            new PlantType{ tMin=-4,  tLo=6,  tHi=24, tMax=34, mMin=0.25f, mOpt=0.50f, mature=3,  maxAge=25,  seedRate=0.90f, seedRadius=3, snagTime=2, scale=0.8f },
            new PlantType{ tMin=-6,  tLo=6,  tHi=34, tMax=44, mMin=0.00f, mOpt=0.50f, mature=2,  maxAge=12,  seedRate=1.00f, seedRadius=2, snagTime=1.5f, scale=0.8f },
            new PlantType{ tMin=-2,  tLo=6,  tHi=22, tMax=30, mMin=0.00f, mOpt=0.50f, mature=3,  maxAge=15,  seedRate=0.90f, seedRadius=3, snagTime=1, scale=1.0f },
        };

        public static bool IsTree(int t) { return t == Broad || t == Conifer || t == Palm; }

        // poids de colonisation par type (les espèces très compétitives sont freinées, les pionnières favorisées)
        static readonly float[] ColW = { 1f, 1f, 1.4f, 1f, 0.45f, 1f, 0.8f, 0.45f };
        // âge minimal de la planète (milliards d'années) avant l'apparition naturelle du type
        static readonly float[] Gate = { 3.4f, 3.2f, 2f, 3.2f, 2.8f, 2.6f, 1.8f, 0.4f };

        public readonly PlanetSim S;
        readonly PlanetGrid G;
        public readonly List<Plant> plants = new List<Plant>(100000);
        public readonly int[] cellPlant;
        readonly PlanetRandom rng;
        internal int RandomState { get { return rng.State; } set { rng.State = value; } }

        public int births, deaths, fires;
        public int[] aliveByType = new int[TypeCount];

        public Ecosystem(PlanetSim sim, int seed)
        {
            S = sim;
            G = sim.G;
            rng = new PlanetRandom(seed + 1234);
            cellPlant = new int[S.M];
            for (int i = 0; i < cellPlant.Length; i++) cellPlant[i] = -1;
        }

        float R() { return (float)rng.NextDouble(); }

        static float Trap(float x, float a, float b, float c, float d)
        {
            if (x <= a || x >= d) return 0f;
            if (x < b) return (x - a) / (b - a);
            if (x <= c) return 1f;
            return (d - x) / (d - c);
        }

        public float Suit(int type, int c)
        {
            float g = S.rock[c] + S.sed[c];
            if (type == Kelp) return SuitCore(type, c, g, 0f, 0f, 0f);
            if (g < (type == Reed ? 0.3f : 1.0f)) return 0f;
            float sl = type == Reed ? 0f : S.Slope(c);
            return SuitCore(type, c, g, sl, S.sed[c], S.snow[c]);
        }

        // ensoleillement moyen selon la latitude, réduit à l'ombre des grands arbres voisins (les arbres tolèrent mieux l'ombre)
        float LightFactor(int type, int c)
        {
            float l = Mathf.Clamp01(0.45f + 0.7f * Mathf.Cos(S.latA[c] * 0.9f) - 0.2f);
            int shade = 0;
            for (int k = 0; k < 4; k++)
            {
                int n = G.Nbr[c * 4 + k];
                if (n < 0) continue;
                int pi = cellPlant[n];
                if (pi < 0) continue;
                var q = plants[pi];
                if (q.state == 0 && q.size > 0.55f && IsTree(q.type)) shade++;
            }
            float tol = IsTree(type) ? 0.2f : 0.5f;
            return Mathf.Clamp01(l * (1f - tol * shade * 0.5f));
        }

        // facteurs communs calculés une seule fois pour tous les types
        float SuitCore(int type, int c, float g, float slope, float sedv, float snow)
        {
            var P = Types[type];
            float ts = Trap(S.temp[c], P.tMin, P.tLo, P.tHi, P.tMax);
            if (ts <= 0f) return 0f;
            float moist = Mathf.Min(1f, S.moist[c] + 0.35f * S.GwFill(c));
            if (type == Kelp)
            {
                if (g > -0.8f || g < -15f) return 0f;
                return ts * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(6f, 15f, -g)));
            }
            if (type == Reed)
            {
                float wet = Mathf.Clamp01(S.flux[c] * 5f + S.water[c] * 4f + (moist - 0.65f) * 3f);
                if (wet <= 0.05f || g > 12f || S.water[c] > 1.0f) return 0f;
                return ts * wet;
            }
            float ms = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(P.mMin * 0.6f, P.mOpt, moist));
            if (type == Cactus) ms = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.15f, 0.45f, moist));
            float soil = type == Cactus ? 1f : Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.05f, 0.7f, sedv)) * Mathf.Clamp01(0.35f + S.Fertility(c));
            float sl = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.5f, 1.0f, slope));
            float sn = 1f - Mathf.Clamp01(snow / (type == Conifer ? 12f : 5f));
            float r = ts * ms * soil * sl * sn * LightFactor(type, c);
            if (type == Palm) r *= 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(30f, 70f, g));
            return r;
        }

        // choix du type à installer : aptitude locale x poids de compétitivité x âge de la planète
        void SuitAll(int c, float[] o)
        {
            float g = S.rock[c] + S.sed[c];
            if (g < -0.8f)
            {
                for (int t = 0; t < TypeCount; t++) o[t] = 0f;
                o[Kelp] = SuitCore(Kelp, c, g, 0f, 0f, 0f) * ColW[Kelp] * (S.planetAge >= Gate[Kelp] ? 1f : 0f);
                return;
            }
            float sl = g > 0.3f ? S.Slope(c) : 0f;
            for (int t = 0; t < TypeCount; t++)
            {
                if (t == Kelp || g < (t == Reed ? 0.3f : 1.0f) || S.planetAge < Gate[t]) { o[t] = 0f; continue; }
                o[t] = SuitCore(t, c, g, t == Reed ? 0f : sl, S.sed[c], S.snow[c]) * ColW[t];
            }
        }

        static int Pick(float[] s, float r)
        {
            r *= 1f;
            for (int t = 0; t < TypeCount; t++)
            {
                if (r < s[t]) return t;
                r -= s[t];
            }
            return -1;
        }

        public void PlantInitial()
        {
            var s = new float[TypeCount];
            for (int c = 0; c < S.M; c++)
            {
                SuitAll(c, s);
                float tot = 0f, best = 0f;
                for (int t = 0; t < TypeCount; t++) { tot += s[t]; if (s[t] > best) best = s[t]; }
                if (tot <= 0.05f) continue;
                if (R() > best * (s[Kelp] > 0f ? 0.4f : 0.9f) * (0.3f + 0.7f * S.LifeLevel)) continue;
                int type = Pick(s, R() * tot);
                if (type < 0) continue;
                AddPlant(c, type, (0.05f + 0.6f * R()) * Types[type].maxAge, true);
            }
            for (int c = 0; c < S.M; c++) S.grass[c] = Mathf.Clamp01(S.moist[c] * 1.3f) * (S.rock[c] + S.sed[c] > 1f ? 1f : 0f) * Mathf.Clamp01(Suit(Bush, c) * 2f);
        }

        // ---- interactions avec la faune
        public float Browse(int c)
        {
            int pi = cellPlant[c];
            if (pi < 0) return 0f;
            var p = plants[pi];
            if (p.state != 0 || p.size < 0.35f) return 0f;
            float food;
            switch (p.type)
            {
                case Berry: food = 0.45f; break;
                case Bush: food = 0.3f; break;
                case Reed: food = 0.25f; break;
                case Cactus: food = 0.15f; break;
                case Kelp: food = 0.3f; break;
                default: return 0f;
            }
            p.health -= 0.18f;
            p.size = Mathf.Max(0.2f, p.size - 0.08f);
            plants[pi] = p;
            return food;
        }

        // retourne le type de plante (fruits/graines) disponible, -1 sinon
        public int FruitType(int c)
        {
            int pi = cellPlant[c];
            if (pi < 0) return -1;
            var p = plants[pi];
            if (p.state != 0 || p.size < 0.5f || p.type == Kelp || p.type == Reed) return -1;
            return p.type;
        }

        public void SeedAt(int c, int type)
        {
            if (cellPlant[c] >= 0 || S.lava[c] > 0.1f || S.burn[c] > 0.3f) return;
            if (Suit(type, c) < 0.35f) return;
            AddPlant(c, type, 0f, false);
        }

        // introduction manuelle : seules les cellules adaptées au type acceptent la plante (mêmes règles que les graines)
        public int Introduce(int type, int c, int count, int radius)
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
            for (int i = 0; i < count * 8 && placed < count; i++)
            {
                int cell = cells[rng.Next(cells.Count)];
                if (cellPlant[cell] >= 0 || S.lava[cell] > 0.1f || S.burn[cell] > 0.3f || Suit(type, cell) < 0.35f) continue;
                AddPlant(cell, type, 0f, false);
                placed++;
            }
            return placed;
        }

        // dégâts du souffle d'une explosion : les cibles tombent au sol
        public void Blast(int c)
        {
            int pi = cellPlant[c];
            S.grass[c] *= 0.2f;
            if (pi < 0) return;
            var p = plants[pi];
            if (p.state > 1) return;
            if (p.state == 0) deaths++;
            p.state = 4; p.timer = 0f; p.health = 1f;
            plants[pi] = p;
        }

        public void Disturb(int c)
        {
            int pi = cellPlant[c];
            S.grass[c] *= 0.3f;
            if (pi < 0) return;
            var p = plants[pi];
            if (p.state != 0) return;
            p.state = 1; p.timer = 0f;
            plants[pi] = p;
            deaths++;
        }

        void AddPlant(int c, int type, float age, bool init)
        {
            if (cellPlant[c] >= 0) return;
            var P = Types[type];
            var p = new Plant
            {
                cell = c, type = (byte)type, state = 0, age = age,
                maxAge = P.maxAge * (0.7f + 0.6f * R()),
                health = init ? 1f : 0.8f,
                jx = R() * 2f - 1f, jy = R() * 2f - 1f,
                yaw = R() * 360f, scaleVar = R(), timer = 0f, gh = S.rock[c] + S.sed[c]
            };
            p.size = Mathf.Clamp01(age / P.mature);
            cellPlant[c] = plants.Count;
            plants.Add(p);
            if (!init) births++;
        }

        void Remove(int i)
        {
            int last = plants.Count - 1;
            cellPlant[plants[i].cell] = -1;
            if (i != last)
            {
                var moved = plants[last];
                plants[i] = moved;
                cellPlant[moved.cell] = i;
            }
            plants.RemoveAt(last);
        }

        // dt in simulated years
        public void Step(float dt)
        {
            StepGrass(dt);
            StepPlants(dt);
            Colonize(dt);
            Lightning();
        }

        void StepGrass(float dt)
        {
            float rate = 1f - Mathf.Exp(-dt * 0.8f);
            Par.For(S.M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    float g = S.rock[c] + S.sed[c];
                    float target = 0f;
                    if (g > 0.8f)
                    {
                        float ts = Trap(S.temp[c], -8f, 6f, 28f, 42f);
                        float ms = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.12f, 0.5f, S.moist[c]));
                        float soil = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.1f, 0.8f, S.sed[c])) * Mathf.Clamp01(0.35f + S.Fertility(c));
                        float sl = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.45f, 0.9f, S.Slope(c)));
                        float sn = 1f - Mathf.Clamp01(S.snow[c] / 3f);
                        float wt = 1f - Mathf.Clamp01(S.water[c] * 2f);
                        target = ts * ms * soil * sl * sn * wt;
                    }
                    S.grass[c] += (target - S.grass[c]) * rate;
                    S.burn[c] *= 0.85f;
                    S.fert[c] *= 1f - dt * 0.04f;
                    if (g > 0.8f && (S.water[c] > 0.05f || S.gw[c] > 0.6f * S.GwCapacity(c))) S.fert[c] = Mathf.Min(1f, S.fert[c] + dt * 0.05f);
                    S.cover[c] = S.grass[c] * 0.35f;
                }
            });
        }

        void StepPlants(float dt)
        {
            Array.Clear(aliveByType, 0, aliveByType.Length);
            for (int i = plants.Count - 1; i >= 0; i--)
            {
                Plant p = plants[i];
                int c = p.cell;
                var P = Types[p.type];
                float g = S.rock[c] + S.sed[c];

                // terrain modifié sous la plante : racines à l'air, enterrée ou noyée -> elle tombe et pourrit
                if (p.state != 4 && p.type != Kelp)
                {
                    bool lost = g < p.gh - 1.0f || g > p.gh + 2.0f || (p.state != 2 && S.water[c] > (p.type == Reed ? 2.5f : 1.2f));
                    if (!lost && g > p.gh) p.gh = g;
                    if (lost)
                    {
                        if (p.state == 0) deaths++;
                        p.health = p.state == 3 ? 0.3f : 1f;
                        p.state = 4; p.timer = 0f;
                    }
                }

                if (p.state == 0 && S.fire[c] > 0.15f && R() < 0.6f) { p.state = 2; p.timer = 0f; fires++; }

                if (p.state == 0)
                {
                    p.age += dt;
                    float suit = Suit(p.type, c);
                    p.health = Mathf.Min(1f, p.health + dt * (suit * 1.6f - 0.55f));
                    float target = Mathf.Clamp01(p.age / P.mature) * (0.5f + 0.5f * p.health);
                    p.size = Mathf.MoveTowards(p.size, target, dt * 0.25f);

                    bool marine = p.type == Kelp;
                    bool die = p.health <= 0f || p.age > p.maxAge || S.lava[c] > 0.2f ||
                        (marine ? (g > -0.3f || g < -18f) : (S.water[c] > (p.type == Reed ? 2.5f : 1.2f) || g < 0.3f));
                    if (!die && p.age > 0.8f * p.maxAge && R() < dt * 2f * (p.age / p.maxAge) / (0.2f * p.maxAge)) die = true;
                    if (die)
                    {
                        p.state = (byte)(S.lava[c] > 0.2f ? 3 : 1);
                        p.timer = 0f;
                        deaths++;
                    }
                    else
                    {
                        aliveByType[p.type]++;
                        float cov = p.size * 0.9f;
                        if (cov > S.cover[c]) S.cover[c] = cov;
                        if (p.age > P.mature && p.health > 0.6f && suit > 0.35f)
                        {
                            float lam = P.seedRate * dt * suit * (0.4f + 0.6f * p.size);
                            if (R() < lam) TrySeed(p);
                        }
                    }
                }
                else if (p.state == 2)
                {
                    p.timer += dt;
                    if (S.fire[c] <= 0f)
                    {
                        if (p.timer < 0.04f) { p.state = 0; p.health *= 0.5f; }
                        else { p.state = 3; p.timer = 0f; deaths++; }
                    }
                    else
                    {
                        S.grass[c] *= 0.2f;
                        if (p.timer > 0.2f) { p.state = 3; p.timer = 0f; deaths++; }
                    }
                }
                else if (p.state == 4)
                {
                    p.timer += dt;
                    float rot = P.snagTime * 0.8f + 0.5f;
                    bool washed = S.water[c] > 1.5f || g < 0.3f || S.lava[c] > 0.2f;
                    if (p.timer > rot || washed)
                    {
                        if (!washed && p.type != Kelp) S.fert[c] = Mathf.Min(1f, S.fert[c] + 0.12f * p.health * (0.5f + p.size));
                        plants[i] = p;
                        Remove(i);
                        continue;
                    }
                }
                else
                {
                    p.timer += dt;
                    if (p.timer > P.snagTime || (g < 0.3f && p.type != Kelp))
                    {
                        // le tronc mort finit par tomber au sol
                        p.health = p.state == 3 ? 0.3f : 1f;
                        p.state = 4; p.timer = 0f;
                    }
                    if (S.cover[c] < 0.2f) S.cover[c] = 0.2f;
                }
                plants[i] = p;
            }
        }

        void TrySeed(Plant parent)
        {
            int c = parent.cell;
            int steps = 1 + rng.Next((int)Types[parent.type].seedRadius);
            for (int s = 0; s < steps; s++)
            {
                int best = -1; float bw = -1f;
                for (int k = 0; k < 4; k++)
                {
                    int n = G.Nbr[c * 4 + k];
                    if (n < 0) continue;
                    float w = R() * (1f + 2f * Mathf.Max(0f, Vector3.Dot(S.wind[c], G.Tan[c * 4 + k])));
                    if (w > bw) { bw = w; best = n; }
                }
                if (best < 0) return;
                c = best;
            }
            if (c == parent.cell) return;
            SeedAt(c, parent.type);
        }

        readonly float[] suitBuf = new float[TypeCount];

        void Colonize(float dt)
        {
            int samples = Mathf.Max(100, S.M / 500);
            for (int i = 0; i < samples; i++)
            {
                int c = rng.Next(S.M);
                if (cellPlant[c] >= 0 || S.burn[c] > 0.2f || S.lava[c] > 0.1f) continue;
                if (R() > 0.2f) continue;
                SuitAll(c, suitBuf);
                if (S.grass[c] < 0.3f)
                {
                    // Les buissons peuvent aussi recoloniser un sol nu lorsque le milieu leur convient.
                    for (int t = 0; t < TypeCount; t++) if (t != Kelp && t != Reed && t != Cactus && t != Bush) suitBuf[t] = 0f;
                }
                float tot = 0f;
                // dépendance négative à la fréquence : un type déjà très répandu colonise moins vite, ce qui laisse de la place aux autres
                int alive = 1;
                for (int t = 0; t < TypeCount; t++) alive += aliveByType[t];
                for (int t = 0; t < TypeCount; t++)
                {
                    suitBuf[t] *= Mathf.Clamp(1.3f - 2.2f * aliveByType[t] / alive, 0.2f, 1.3f);
                    tot += suitBuf[t];
                }
                if (tot < 0.3f) continue;
                int type = Pick(suitBuf, R() * tot);
                if (type >= 0) AddPlant(c, type, 0f, false);
            }
        }

        void Lightning()
        {
            int n = plants.Count;
            if (n == 0) return;
            for (int i = 0; i < 10; i++)
            {
                int idx = rng.Next(n);
                Plant p = plants[idx];
                if (p.state != 0 || p.size < 0.3f) continue;
                int c = p.cell;
                float dry = Mathf.Clamp01((0.35f - S.moist[c]) / 0.35f) * Mathf.Clamp01((S.temp[c] - 12f) / 15f);
                if (dry > 0f && R() < 0.05f * dry)
                {
                    p.state = 2; p.timer = 0f;
                    S.StartFire(c, 0.5f);
                    plants[idx] = p;
                    fires++;
                }
            }
        }

        public void Ignite(int cell)
        {
            S.StartFire(cell, 0.6f);
        }
    }
}
