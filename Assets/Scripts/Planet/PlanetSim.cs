using System;
using System.Collections.Generic;
using UnityEngine;

namespace LivingPlanet
{
    public sealed class WaterState
    {
        public float[] salt, deposit, nextSalt, outSalt, airOutScale;

        public WaterState(int count)
        {
            salt = new float[count];
            deposit = new float[count];
            nextSalt = new float[count];
            outSalt = new float[count * 4];
            airOutScale = new float[count];
        }
    }

    public sealed class AeolianState
    {
        public int[] target, next, head;
        public float[] amount, composition;

        public AeolianState(int count)
        {
            target = new int[count];
            next = new int[count];
            head = new int[count];
            amount = new float[count];
            composition = new float[count * 4];
        }
    }

    public sealed partial class PlanetSim
    {
        public const int TicksPerYear = 150;
        public const float OceanSalinity = 0.035f;
        public const float DrinkableSalinity = 0.001f;
        public WaterState waterState;
        public AeolianState aeolian;

        public readonly PlanetGrid G;
        public readonly int M;

        // state
        public float[] rock, sed, water, susp, snow, hum, temp, moist, precipAvg, flux, grass, burn, lava;
        public float[] tect, hard, cover, rainNow, frontN, waterN, suspN, humN, outTot, outW, outS, thOut;
        public Vector3[] wind;
        // Vent d'altitude (courant-jet / circulation générale) : dominé par l'équilibre géostrophique (Coriolis),
        // sans le ralentissement par friction qui s'applique au vent de surface — plus rapide, moins dévié vers
        // les basses pressions locales. Utilisé pour tout ce qui se déroule en haute altitude (cendres volcaniques...).
        public Vector3[] windHigh;
        // Sédiments enfoncés : empilement de couches qui s'enfoncent progressivement (plus c'est profond, plus le
        // transfert est lent : pression/métamorphisme croissants) jusqu'à fusionner dans le magma. LayerCount fixe
        // (plutôt qu'un nombre de couches variable par cellule) pour garder un tableau plat simple à simuler et à
        // sauvegarder ; passé de 3 à 6 pour une bien meilleure granularité de l'enfouissement.
        public const int SubductionLayerCount = 6;
        static readonly float[] LayerTransitFactor = { 1f, 0.82f, 0.66f, 0.52f, 0.4f, 0.3f };
        public float[] subductionLayers;
        float[] subductionLayersN;

        public int tick;
        public float SeaLevel = 0f;
        public float OceanFraction = 0.64f;
        public float MaxHeight = 320f * 2f / 3f; // plafond réduit aux 2/3 de l'ancienne valeur (320)
        public float MountainAmp = 230f;
        public float Lapse = 0.2f;
        public float AxialTilt = 23f * Mathf.Deg2Rad;
        public float[] latA;

        // hydrology / erosion (per tick)
        public float kFlow = 0.32f;
        public float rainK = 3f;
        public float snowK = 3f;
        public float meltRate = 0.02f;
        public float infil = 0.004f;
        public float evapK = 0.01f;
        public float[] evap;   // eau évaporée par cellule au dernier pas : elle retourne dans l'air (cycle de l'eau)
        float[] channelNoise;  // irrégularité spatiale de la canalisation hydraulique, précalculée (voir constructeur)
        public float Kc = 1.0f;
        // Relevé (x2.1) pour que l'érosion fluviale/du relief reste nettement visible au fil d'une partie, et pas
        // seulement sur des milliers de ticks.
        public float Ke = 0.0055f;
        public float Kd = 0.3f;
        public float maxErode = 0.65f;
        public float kThermal = 0.00015f;
        public float uplift = 0.003f;
        public float weathering = 0.0038f;
        public float maxSoil = 3f;
        public float erosionScale = 1f;
        // Érosion côtière par les courants (houle/marée) : attaque le littoral et le fond peu profond, remet de la
        // matière en suspension pour qu'elle soit transportée/redéposée ailleurs par les courants (voir StepHydro).
        // Part du débit d'une arête conservée d'un pas au suivant (élan de l'eau) : faible pour une lame mince,
        // proche de un pour une section mouillée d'au moins DeepFlowDepth.
        const float ShallowMomentum = 0.65f;
        const float DeepMomentum = 0.98f;
        const float DeepFlowDepth = 2f;
        public float CoastalErode = 0.11f;
        public float CoastalErodeDepth = 8f;

        // Couches visuelles "érosion" (voir ColorCell ViewMode 10/11) : moyenne mobile du bilan net érosion (-) /
        // dépôt (+) par l'eau (StepHydro) et par le vent (StepWindErosion), en épaisseur par tick. ErodeViewScale
        // amplifie des variations par tick minuscules pour qu'elles restent lisibles une fois lissées ;
        // ErodeViewRate fixe la vitesse de la moyenne (même esprit que precipRate pour precipAvg).
        public const float ErodeViewScale = 6f;
        public const float ErodeViewRate = 0.04f;
        public float[] waterErode;  // < 0 érosion nette récente, > 0 dépôt net récent (plage indicative, non bornée)
        public float[] windErode;

        // weather
        public float advection = 0.3f;
        public float moistScale = 80f;
        public float precipRate = 0.01f;

        public struct Volcano { public int cell, left, total, age; public float power, env; }
        public List<Volcano> volcanoes = new List<Volcano>();

        public PlanetSim(PlanetGrid grid)
        {
            G = grid;
            M = grid.CellCount;
            waterState = new WaterState(M);
            aeolian = new AeolianState(M);
            rock = new float[M]; sed = new float[M]; water = new float[M]; susp = new float[M];
            snow = new float[M]; hum = new float[M]; temp = new float[M]; moist = new float[M];
            precipAvg = new float[M]; flux = new float[M]; grass = new float[M]; burn = new float[M];
            lava = new float[M]; tect = new float[M]; hard = new float[M]; cover = new float[M];
            rainNow = new float[M]; frontN = new float[M]; waterN = new float[M]; suspN = new float[M];
            humN = new float[M]; outTot = new float[M];
            waterErode = new float[M]; windErode = new float[M];
            outW = new float[M * 4]; outS = new float[M * 4]; thOut = new float[M * 4];
            tsunamiBase = new float[M]; tsunamiMomentum = new float[M * 4]; tsunamiActive = new byte[M];
            subductionLayers = new float[M * SubductionLayerCount];
            subductionLayersN = new float[M * SubductionLayerCount];
            wind = new Vector3[M];
            windHigh = new Vector3[M];
            latA = new float[M];
            stress = new float[M]; scar = new float[M]; mark = new int[M];
            InitIce();
            InitTerrainArrays(); InitGroundwater();
            InitWeather();
            var dirs = G.Dir; var la = latA;
            Par.For(M, (s, e) => { for (int c = s; c < e; c++) la[c] = Mathf.Asin(Mathf.Clamp(dirs[c].y, -1f, 1f)); });
            channelNoise = new float[M];
            InitChannelNoise();
        }

        // Bruit d'irrégularité du chenal hydraulique (voir StepHydro) : position fixe sur la grille, donc
        // précalculé une seule fois plutôt que ré-évalué à chaque cellule à CHAQUE pas de simulation
        // (contrairement à la tectonique, l'hydrologie tourne tous les ticks, pas tous les TectEvery) —
        // sinon le coût du bruit de Perlin provoquait des microgels sensibles en vitesse rapide.
        // Il dépend de la table de bruit globale : Generate le recalcule donc après Noise.Seed, sinon une
        // même graine donnait des chenaux différents selon la planète (ou l'aperçu de menu) qui précédait.
        void InitChannelNoise()
        {
            var dirs = G.Dir;
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    Vector3 d = dirs[c];
                    channelNoise[c] = Mathf.Clamp(1f + 0.55f * Noise.Perlin(d.x * 37f + 5.1f, d.y * 37f - 3.3f, d.z * 37f + 9.7f), 0.45f, 1.6f);
                }
            });
        }

        public float Phase { get { return (tick % TicksPerYear) / (float)TicksPerYear; } }
        public int YearNumber { get { return tick / TicksPerYear; } }
        public float Declination { get { return AxialTilt * Mathf.Sin(2f * Mathf.PI * Phase); } }

        public float Ground(int c) { return rock[c] + sed[c]; }

        public void InitializeWater()
        {
            waterState = new WaterState(M);
            for (int c = 0; c < M; c++)
            {
                float depth = Mathf.Max(0f, SeaLevel - Ground(c));
                if (depth <= 0f) continue;
                water[c] += depth;
                waterState.salt[c] = depth * OceanSalinity;
            }
        }

        public float Salinity(int c)
        {
            return water[c] > 0.00001f ? waterState.salt[c] / water[c] : 0f;
        }

        public bool IsOcean(int c) { return Ground(c) < SeaLevel && water[c] > 0.02f; }

        // Niveau moyen des surfaces marines réellement mouillées, pondéré par leur aire. Les changements
        // de côte ne créent ni ne détruisent d'eau : StepHydro assure son déplacement.
        public void StepCoastline()
        {
            double area = 0d, surface = 0d;
            for (int c = 0; c < M; c++)
            {
                if (!IsOcean(c)) continue;
                double a = G.Area[c];
                area += a;
                surface += (Ground(c) + water[c]) * a;
            }
            if (area > 0d) SeaLevel = (float)(surface / area);
        }

        public bool FreshWater(int c)
        {
            return water[c] > 0.1f && Salinity(c) <= DrinkableSalinity;
        }

        public float Slope(int c)
        {
            float g = rock[c] + sed[c], s = 0;
            int b = c * 4;
            for (int k = 0; k < 4; k++)
            {
                int n = G.Nbr[b + k];
                if (n < 0) continue;
                float d = Mathf.Abs(g - rock[n] - sed[n]) / G.Dist[b + k];
                if (d > s) s = d;
            }
            return s;
        }

        public static float Sat(float t)
        {
            float x = Mathf.Clamp01((t + 15f) / 50f);
            return 0.15f + 0.85f * x * x;
        }

        public float TempTarget(int c, float decl, float g)
        {
            float insol = Mathf.Max(0f, Mathf.Cos(latA[c] - decl));
            float t = -32f + 66f * Mathf.Pow(insol, 0.8f);
            if (g < SeaLevel) t = t * 0.8f + 4f;
            // Le refroidissement suit l'altitude au-dessus de la mer, pas l'altitude absolue : sinon toute
            // variation du niveau de la mer réchauffe ou refroidit artificiellement l'ensemble des terres.
            else t -= Lapse * (g - SeaLevel);
            t -= 7f * Mathf.Min(1f, ash[c]);
            if (co2 != null)
            {
                // effet de serre (CO2, CH4) et refroidissement par les aérosols soufrés ; chaleur interne des planètes jeunes
                t += 2.2f * Mathf.Clamp(Mathf.Log(Mathf.Max(100f, co2[c]) / CO2Base), -1.2f, 3.2f);
                t += 0.5f * Mathf.Min(6f, ch4[c] - CH4Base);
                t -= 2f * Mathf.Min(2f, so2[c]);
            }
            t += 3f * Youth;
            return t;
        }

        // ------------------------------------------------------------------ generation
        public void Generate(int seed)
        {
            Noise.Seed(seed);
            InitChannelNoise();
            var rnd = new System.Random(seed * 31 + 7);
            Vector3 O(float s) { return new Vector3((float)rnd.NextDouble() * 80f - 40f, (float)rnd.NextDouble() * 80f - 40f, (float)rnd.NextDouble() * 80f - 40f) * s; }
            Vector3 o1 = O(1), o2 = O(1), o3 = O(1), o4 = O(1), o5 = O(1);
            var raw = new float[M];
            var ridge = new float[M];
            if (tectonics) InitPlates(rnd);
            InitMagma(rnd);

            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    Vector3 d = G.Dir[c];
                    Vector3 w = d + 0.22f * new Vector3(
                        Noise.Perlin(d.x * 2f + o2.x, d.y * 2f + o2.y, d.z * 2f + o2.z),
                        Noise.Perlin(d.x * 2f + o3.x, d.y * 2f + o3.y, d.z * 2f + o3.z),
                        Noise.Perlin(d.x * 2f + o4.x, d.y * 2f + o4.y, d.z * 2f + o4.z));
                    raw[c] = Noise.Fbm(w * 1.25f + o1, 5) + 0.25f * Noise.Fbm(d * 5f + o5, 4);
                    float tc = Noise.Fbm(d * 1.8f + o3, 3) * 2.2f + 0.35f;
                    tect[c] = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.3f, 0.85f, tc));
                    ridge[c] = Noise.Ridged(d * 2.6f + o4, 5);
                    hard[c] = 0.45f + 0.55f * Mathf.Clamp01(Noise.Fbm(d * 6f + o5, 3) * 1.5f + 0.5f);
                }
            });

            var sorted = (float[])raw.Clone();
            Array.Sort(sorted);
            float seaRaw = sorted[Mathf.Clamp((int)(OceanFraction * M), 0, M - 1)];

            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    float h = raw[c] - seaRaw;
                    float height;
                    if (h > 0f)
                    {
                        float coast = Mathf.SmoothStep(0f, 1f, h / 0.12f);
                        float mt = tectonics ? Mathf.Clamp01(conv[c] * 1.4f + 0.15f * tect[c]) : tect[c];
                        height = h * 38f + Mathf.Pow(ridge[c], 1.3f) * mt * MountainAmp * (1f + 0.3f * Youth) * (1f - 0.45f * Oldness) * coast + 0.6f;
                    }
                    else
                    {
                        // Fonds plus profonds en moyenne (480 au lieu de 350), mais plafonnés à -110 : la
                        // profondeur maximale qu'une faille peut atteindre en jeu (voir depthTarget dans
                        // StepGeology, Lerp(15,110)) — les fosses générées au départ ne dépassent donc jamais
                        // ce qu'une faille créerait elle-même pendant la simulation.
                        height = Mathf.Max(h * 480f, -110f) - 0.3f;
                        if (tectonics) height += Mathf.Min(rift[c] * 14f, -height * 0.65f);
                    }
                    height = Mathf.Min(height, MaxHeight);
                    float soil = height > 0 ? 1.2f : 0.5f;
                    sed[c] = soil;
                    rock[c] = height - soil;
                    water[c] = 0; susp[c] = 0; snow[c] = 0; flux[c] = 0; grass[c] = 0; burn[c] = 0; lava[c] = 0;
                    temp[c] = TempTarget(c, 0f, height);
                    hum[c] = Sat(temp[c]) * 0.5f;
                    moist[c] = 0.4f;
                    precipAvg[c] = 0f;
                    wind[c] = Vector3.zero;
                }
            });
            DrainBasins();
            InitMaterials(rnd);
            Array.Clear(outW, 0, outW.Length);
            InitializeWater();
            tick = 0;
            volcanoes.Clear(); lavaFlows.Clear();
            UpdateWind();
        }

        public void CalibrateMoisture()
        {
            var vals = new List<float>(M / 2);
            for (int c = 0; c < M; c++) if (Ground(c) > 0) vals.Add(precipAvg[c]);
            if (vals.Count == 0) return;
            vals.Sort();
            float p80 = vals[(int)(vals.Count * 0.8f)];
            moistScale = 0.75f / Mathf.Max(p80, 1e-5f);
        }

        // ------------------------------------------------------------------ weather
        void UpdateWind()
        {
            float t = tick * 0.004f;
            float decl0 = Declination;
            if (pres == null) pres = new float[M];
            StepPressure();
            int pn = pressDir.Length;
            var pDir = pressDir; var pAmp = pressAmp; var pSgn = pressSgn; var pRad = pressRad;
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    Vector3 d = G.Dir[c];
                    float g0 = rock[c] + sed[c];
                    float lat = Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f));
                    // Est = sens de rotation de la planète = longitudes croissantes (voir PlanetView.SunDir).
                    Vector3 east = new Vector3(-d.z, 0f, d.x);
                    if (east.sqrMagnitude < 1e-6f) east = Vector3.right;
                    east.Normalize();
                    Vector3 north = Vector3.up - d * d.y;
                    north = north.sqrMagnitude < 1e-6f ? Vector3.forward : north.normalized;

                    // circulation générale : alizés, vents d'ouest, vents polaires (cellules de Hadley / Ferrel / polaire)
                    float lon = Mathf.Atan2(d.z, d.x);
                    float jetLat = lat - 0.12f * Mathf.Cos(lat) * Mathf.Sin(3f * lon + t * 0.3f + 0.4f * Mathf.Sin(2f * lat));
                    float tl = Mathf.Abs(jetLat) / (Mathf.PI * 0.5f);
                    float u = tl < 0.667f ? -Mathf.Cos(3f * jetLat) : Mathf.Lerp(1f, -0.35f, Mathf.SmoothStep(0f, 1f, (tl - 0.667f) / 0.333f));
                    float v = -Mathf.Sin(6f * jetLat) * 0.35f;
                    float n1 = Noise.Perlin(d.x * 2.5f + t, d.y * 2.5f, d.z * 2.5f);
                    float n2 = Noise.Perlin(d.x * 2.5f, d.y * 2.5f + 7.1f, d.z * 2.5f + t);
                    Vector3 w = (east * (u + 0.5f * n1) + north * (v + 0.5f * n2)) * 0.3f;
                    float pr = -Mathf.Cos(6f * lat);
                    if (g0 > SeaLevel) { float bt = -32f + 66f * Mathf.Pow(Mathf.Max(0f, Mathf.Cos(lat - decl0)), 0.8f); pr -= Mathf.Clamp((temp[c] - bt) * 0.03f, -0.6f, 0.6f); }

                    // dépressions et anticyclones : la force de Coriolis les fait tourner (anti-horaire au nord, horaire au sud pour une dépression)
                    float front = 0f;
                    for (int i = 0; i < pn; i++)
                    {
                        float dt = Vector3.Dot(d, pDir[i]);
                        float cr = Mathf.Cos(pRad[i] * 2.6f);
                        if (dt < cr) continue;
                        float th = Mathf.Acos(Mathf.Min(1f, dt));
                        float x = th / pRad[i];
                        float fall = Mathf.Exp(-x * x);
                        float prof = x * Mathf.Exp(1f - x * x);
                        pr -= pSgn[i] * pAmp[i] * fall * 1.1f;
                        front += pSgn[i] > 0f ? pAmp[i] * fall : -0.5f * pAmp[i] * fall;
                    }
                    wind[c] = w;
                    windHigh[c] = w * 1.6f;
                    pres[c] = pr;
                    // thOut est réutilisé temporairement avant les calculs d'érosion.
                    thOut[c * 4] = Noise.Perlin(d.x * 4f + t * 0.23f, d.y * 4f + t * 0.17f, d.z * 4f - t * 0.19f);
                    float nz = Mathf.Clamp01(Noise.Fbm(d * 3.2f + new Vector3(t * 3f, 0f, t * 2f), 2) * 1.2f);
                    frontN[c] = Mathf.Clamp01(0.15f + 0.75f * front + 0.3f * nz);
                }
            });
            ApplyBlowToPressure();
            WindFromPressure();
            WindEddies();
            ApplyBlowToWind();
            if (volcanoes.Count > 0) VolcanoWind();
        }

        public float[] pres;

        void WindEddies()
        {
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    int b = c * 4;
                    Vector3 gradient = Vector3.zero;
                    for (int k = 0; k < 4; k++)
                    {
                        int n = G.Nbr[b + k];
                        if (n < 0) continue;
                        gradient += G.Tan[b + k] * ((thOut[n * 4] - thOut[b]) / Mathf.Max(G.Dist[b + k], 1e-4f));
                    }
                    Vector3 eddy = Vector3.Cross(G.Dir[c], gradient) * (0.5f * G.Radius);
                    wind[c] = Vector3.ClampMagnitude(wind[c] + eddy * 0.075f, 1.5f);
                    windHigh[c] = Vector3.ClampMagnitude(windHigh[c] + eddy * 0.12f, 2.5f);
                }
            });
        }

        // vent géostrophique (Coriolis) + écoulement vers les basses pressions près de l'équateur et par friction
        void WindFromPressure()
        {
            var nbr = G.Nbr; var tan = G.Tan; var dist = G.Dist;
            float R = G.CellSize * Mathf.Sqrt(M / 6f) * 2f / Mathf.PI;
            float kR = R / 6f;
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    int b = c * 4;
                    Vector3 gr = Vector3.zero;
                    for (int k = 0; k < 4; k++)
                    {
                        int n = nbr[b + k];
                        if (n < 0) continue;
                        gr += tan[b + k] * ((pres[n] - pres[c]) / dist[b + k]);
                    }
                    gr *= 0.5f * kR;
                    Vector3 d = G.Dir[c];
                    float sl = d.y;
                    float cf = Mathf.Clamp01(Mathf.Abs(sl) * 3.5f * spinRate);
                    Vector3 wg = Vector3.Cross(gr, d) * (sl >= 0f ? 1f : -1f);
                    wind[c] += wg * cf - gr * (0.45f * (1f - cf) + 0.22f);
                    // en altitude, l'écoulement est quasi purement géostrophique (pas de friction de surface) :
                    // il suit les isobares sans être dévié vers les basses pressions.
                    windHigh[c] += wg;
                }
            });
        }

        public void StepClimate()
        {
            if (tick % 5 == 0) UpdateWind();
            float decl = Declination;
            float itczLat = decl * 0.8f;
            float adv = advection;
            float rate = precipRate;
            float mScale = moistScale;
            var nbr = G.Nbr; var back = G.Back; var tan = G.Tan; var dist = G.Dist;

            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    float outgoing = 0f;
                    int b = c * 4;
                    for (int k = 0; k < 4; k++)
                        if (nbr[b + k] >= 0)
                            outgoing += Mathf.Max(0f, Vector3.Dot(wind[c], tan[b + k])) * adv;
                    waterState.airOutScale[c] = outgoing > 1f ? 1f / outgoing : 1f;
                }
            });

            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    int b = c * 4;
                    float g = rock[c] + sed[c];
                    bool ocean = g < SeaLevel;

                    float tgt = TempTarget(c, decl, g);
                    temp[c] += (tgt - temp[c]) * (ocean ? 0.01f : 0.05f);
                    float t = temp[c];
                    float sat = Sat(t);

                    Vector3 wc = wind[c];
                    float outFrac = 0f, inflow = 0f, slopeAlong = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        int n = nbr[b + k];
                        if (n < 0) continue;
                        float dd = Vector3.Dot(wc, tan[b + k]);
                        if (dd > 0f)
                        {
                            outFrac += dd * adv * waterState.airOutScale[c];
                            float gn = rock[n] + sed[n];
                            slopeAlong += dd * (Mathf.Max(gn, 0f) - Mathf.Max(g, 0f)) / dist[b + k];
                        }
                        float di = Vector3.Dot(wind[n], tan[n * 4 + back[b + k]]);
                        if (di > 0f) inflow += hum[n] * di * adv * waterState.airOutScale[n] * G.Area[n] / G.Area[c];
                    }
                    float h = hum[c] * (1f - outFrac) + inflow;
                    if (evap != null) h += evap[c] / rainK;

                    float condensation = 0f;
                    if (ocean)
                    {
                        float ice = Mathf.Clamp01((t + 15f) / 10f) * (1f - Mathf.Clamp01(seaIce[c] / SeaIceCap));
                        float exchange = (sat - h) * 0.05f * ice;
                        if (exchange > 0f)
                        {
                            float evaporated = Mathf.Min(exchange * rainK, water[c]);
                            water[c] -= evaporated;
                            h += evaporated / rainK;
                        }
                        else { condensation = -exchange; h += exchange; }
                    }
                    else
                    {
                        float target = sat * 0.7f * moist[c];
                        if (h < target)
                        {
                            float evaporated = Mathf.Min((target - h) * 0.012f * rainK, gw[c]);
                            gw[c] -= evaporated;
                            h += evaporated / rainK;
                        }
                    }

                    float lift = Mathf.Clamp(slopeAlong * 6f, 0f, 0.5f);
                    float dl = (latA[c] - itczLat) / 0.17f;
                    float conv = 0.25f * Mathf.Exp(-dl * dl);
                    float effSat = sat * Mathf.Max(0.2f, 1f - lift - conv - 0.35f * frontN[c]);
                    float precip = 0f;
                    if (h > effSat) { precip = (h - effSat) * 0.25f; h -= precip; }
                    precip += condensation;
                    if (h > 1.2f) { precip += h - 1.2f; h = 1.2f; }
                    if (h < 0f) h = 0f;

                    humN[c] = h;
                    rainNow[c] = precip;
                    precipAvg[c] += (precip - precipAvg[c]) * rate;

                    float mt = ocean ? 1f : Mathf.Clamp01(precipAvg[c] * mScale + 0.5f * Mathf.Clamp01(water[c] * 2f) + 0.4f * Mathf.Clamp01(flux[c] * 3f));
                    moist[c] += (mt - moist[c]) * 0.02f;
                }
            });
            var tmp = hum; hum = humN; humN = tmp;
        }

        // ------------------------------------------------------------------ hydrology + erosion
        public void StepHydro()
        {
            var nbr = G.Nbr; var back = G.Back; var dist = G.Dist;
            float kf = kFlow;
            bool tsunamiRunning = tsunamis.Count > 0;
            if (evap == null) evap = new float[M];

            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    int b = c * 4;
                    float w = water[c];
                    if (w <= 1e-6f)
                    {
                        outTot[c] = 0f;
                        outW[b] = outW[b + 1] = outW[b + 2] = outW[b + 3] = 0f;
                        outS[b] = outS[b + 1] = outS[b + 2] = outS[b + 3] = 0f;
                        waterState.outSalt[b] = waterState.outSalt[b + 1] = waterState.outSalt[b + 2] = waterState.outSalt[b + 3] = 0f;
                        continue;
                    }
                    float S = rock[c] + sed[c] + w;
                    float total = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        int n = nbr[b + k];
                        float o = 0f;
                        if (n >= 0)
                        {
                            float dz = S - (rock[n] + sed[n] + water[n]);
                            o = dz * kf * G.CellSize / Mathf.Max(dist[b + k], 1e-4f);
                            // Plus le terrain (hors colonne d'eau) descend vite vers ce voisin, plus l'écoulement s'y
                            // concentre préférentiellement : cela fait émerger de vrais chenaux au lieu d'une nappe
                            // diffuse qui s'étale également dans toutes les directions en pente. Un bruit spatial
                            // stable (pas de dérive dans le temps) casse la régularité du motif : sans lui, un
                            // relief symétrique (dôme, mur de faille) canalise exactement pareil dans les 4
                            // directions de la grille, ce qui crée des cannelures parfaitement régulières au lieu
                            // de chenaux qui se croisent/fusionnent comme un vrai réseau hydrographique.
                            float terrainSlope = Mathf.Max(0f, rock[c] + sed[c] - rock[n] - sed[n]) / Mathf.Max(dist[b + k], 1e-4f);
                            o *= 1f + Mathf.Min(5f, terrainSlope * 3f * channelNoise[c]);
                            // Garde-fou de stabilité : un mur de faille très raide combiné à la canalisation peut
                            // faire dépasser au flux explicite la différence de niveau elle-même en un seul pas,
                            // ce qui ne converge plus vers l'équilibre mais s'emballe (pic d'eau géant au fond des
                            // failles). On ne transfère jamais plus de la moitié de l'écart de niveau par pas.
                            if (dz > 0f) o = Mathf.Min(o, dz * 0.5f);
                            bool oceanPair = rock[c] + sed[c] < SeaLevel && rock[n] + sed[n] < SeaLevel;
                            if (current != null && oceanPair && water[n] > 0f)
                                o += Mathf.Max(0f, Vector3.Dot(current[c], G.Tan[b + k])) * Mathf.Min(w, 1f) * 0.02f;
                            // L'onde possède sa propre inertie : additionner les deux débits inertiels
                            // sur une même arête amplifie les oscillations en avance rapide.
                            bool waveEdge = tsunamiRunning && (tsunamiActive[c] != 0 || tsunamiActive[n] != 0);
                            // Le frottement du fond freine d'autant moins le courant que la section mouillée est
                            // profonde : une lame mince s'arrête dès que la pente s'annule, un chenal profond garde son
                            // élan et débite donc bien plus pour le même écart de niveau. Sans cela, un fleuve ou un
                            // détroit profond n'évacuait pas plus qu'un filet d'eau, et l'eau s'entassait en amont
                            // jusqu'à noyer les terres.
                            float section = S - Mathf.Max(rock[c] + sed[c], rock[n] + sed[n]);
                            float momentum = Mathf.Lerp(ShallowMomentum, DeepMomentum, Mathf.Clamp01(section / DeepFlowDepth));
                            o = dz > 0f || oceanPair || section > 0f
                                ? Mathf.Max(0f, (waveEdge ? 0f : momentum) * outW[b + k] + 0.35f * o) : 0f;
                        }
                        outW[b + k] = o;
                        total += o;
                    }
                    float lim = w * 0.9f;
                    float sc = total > lim ? lim / total : 1f;
                    float sf = susp[c] / w;
                    float sedimentOut = 0f, currentWeight = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        outW[b + k] *= sc;
                        outS[b + k] = outW[b + k] * sf;
                        sedimentOut += outS[b + k];
                        waterState.outSalt[b + k] = outW[b + k] * waterState.salt[c] / w;
                        int n = nbr[b + k];
                        if (current != null && rock[c] + sed[c] < SeaLevel && n >= 0 &&
                            rock[n] + sed[n] < SeaLevel && water[n] > 1e-6f)
                            currentWeight += Mathf.Max(0f, Vector3.Dot(current[c], G.Tan[b + k]));
                    }
                    if (currentWeight > 1e-6f)
                    {
                        float carried = Mathf.Min(Mathf.Max(0f, susp[c] - sedimentOut),
                            susp[c] * Mathf.Min(0.25f, current[c].magnitude * 0.12f));
                        for (int k = 0; k < 4; k++)
                        {
                            int n = nbr[b + k];
                            if (n < 0 || rock[n] + sed[n] >= SeaLevel || water[n] <= 1e-6f) continue;
                            outS[b + k] += carried * Mathf.Max(0f, Vector3.Dot(current[c], G.Tan[b + k])) / currentWeight;
                        }
                    }
                    outTot[c] = total * sc;
                }
            });

            float Kc_ = Kc, Ke_ = Ke * erosionScale, Kd_ = Kd;
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    int b = c * 4;
                    float w = water[c] - outTot[c];
                    float sp = susp[c] - (outS[b] + outS[b + 1] + outS[b + 2] + outS[b + 3]);
                    float salt = waterState.salt[c] - (waterState.outSalt[b] + waterState.outSalt[b + 1] + waterState.outSalt[b + 2] + waterState.outSalt[b + 3]);
                    for (int k = 0; k < 4; k++)
                    {
                        int n = nbr[b + k];
                        if (n < 0) continue;
                        int idx = n * 4 + back[b + k];
                        float areaRatio = G.Area[n] / G.Area[c];
                        w += outW[idx] * areaRatio;
                        sp += outS[idx] * areaRatio;
                        salt += waterState.outSalt[idx] * areaRatio;
                    }

                    float g = rock[c] + sed[c];
                    float t = temp[c];
                    float p = rainNow[c];
                    float sn = snow[c];
                    if (g > SeaLevel)
                    {
                        if (t > 0f) w += p * rainK; else sn += p * snowK;
                        if (t > 0f && sn > 0f)
                        {
                            float m = Mathf.Min(sn, t * meltRate);
                            sn -= m; w += m;
                        }
                        if (sn > 25f) { w += sn - 25f; sn = 25f; }
                    }
                    else w += p * rainK;
                    snow[c] = sn;

                    float q = outTot[c];
                    // l'évaporation dépend de la chaleur, du vent et de la sécheresse de l'air ; l'eau gèle sous 0°C
                    float dry = Mathf.Clamp01(1f - hum[c] / Mathf.Max(Sat(t), 0.05f));
                    float ev = g > SeaLevel
                        ? Mathf.Min(w, Mathf.Min(Mathf.Max(0f, Sat(t) - hum[c]) * rainK,
                            evapK * Mathf.Max(0f, t) / 20f * (1f + 0.5f * wind[c].magnitude) * dry))
                        : 0f;
                    evap[c] = ev;
                    w = Mathf.Max(0f, w - ev);
                    if (t < -2f && w > 0f && g > SeaLevel)
                    {
                        float fr = Mathf.Min(w, Mathf.Min(25f - snow[c], 0.05f * Mathf.Min(1f, (-2f - t) / 10f + 0.2f)));
                        w -= fr; snow[c] += fr;
                    }

                    float dissolve = Mathf.Min(waterState.deposit[c], Mathf.Max(0f, 0.26f * w - salt));
                    salt += dissolve;
                    waterState.deposit[c] -= dissolve;
                    if (g > SeaLevel && w > 0f)
                    {
                        float washed = Mathf.Min(waterState.deposit[c], Mathf.Max(0f, 0.26f * w - salt) * 0.05f);
                        salt += washed;
                        waterState.deposit[c] -= washed;
                    }
                    float crystallized = Mathf.Max(0f, salt - 0.26f * w);
                    salt -= crystallized;
                    waterState.deposit[c] += crystallized;
                    waterState.nextSalt[c] = Mathf.Max(0f, salt);

                    // Bilan net érosion (-) / dépôt (+) d'épaisseur de terrain pour cette cellule ce pas-ci,
                    // lissé ci-dessous (voir ErodeViewRate) pour alimenter la couche visuelle "érosion de l'eau".
                    float netWaterChange = 0f;
                    if (g <= SeaLevel)
                    {
                        float speed = current == null ? 0f : current[c].magnitude;
                        float settling = Mathf.Lerp(0.15f, 0.008f, Mathf.Clamp01(speed / 0.4f));
                        float dep = Mathf.Max(0f, sp) * settling;
                        if (dep > 0f) { DepositWater(c, dep, 0.3f); sp -= dep; netWaterChange += dep; }

                        // Érosion côtière/sous-marine : l'énergie de la houle/du courant décroît vite avec la
                        // profondeur (base de houle), donc seule la frange littorale et le fond peu profond sont
                        // vraiment attaqués — ce qui redessine au fil du temps le contour des côtes et des îles.
                        // Le matériau arraché part en suspension pour être redéposé ailleurs par les courants.
                        float depth = SeaLevel - g;
                        float waveEnergy = speed * speed * Mathf.Exp(-depth / CoastalErodeDepth);
                        if (waveEnergy > 1e-5f)
                        {
                            float want = Mathf.Min(waveEnergy * CoastalErode, maxErode);
                            float fromSed = ErodeSoil(c, want * ErodibilityAt(c));
                            float fromRock = sed[c] < 0.05f
                                ? Mathf.Max(0f, want - fromSed) * RockErodibilityAt(c) * 0.5f
                                : 0f;
                            rock[c] -= fromRock;
                            sp += fromSed + fromRock;
                            netWaterChange -= fromSed + fromRock;
                        }
                    }
                    else
                    {
                        // la capacité inclut le débit moyen : les couloirs creusés attirent l'eau et s'approfondissent
                        int material = c * 4;
                        float grain = mix[material] * 0.25f + mix[material + 1] * 0.65f +
                            mix[material + 2] + mix[material + 3] * 0.15f;
                        float cap = Kc_ * (q + 0.6f * flux[c]);
                        float prot = 1f - 0.8f * cover[c];
                        float coarse = Mathf.Clamp01(q * 4f + grain * 0.35f);
                        if (sp < cap)
                        {
                            float want = Mathf.Min((cap - sp) * Ke_ * prot, maxErode);
                            float fromSed = ErodeSoil(c, want * ErodibilityAt(c) * 1.1f);
                            float fromRock = sed[c] < 0.05f
                                ? Mathf.Max(0f, want - fromSed) * RockErodibilityAt(c) * 0.65f
                                : 0f;
                            rock[c] -= fromRock;
                            sp += fromSed + fromRock;
                            netWaterChange -= fromSed + fromRock;
                        }
                        else
                        {
                            // Le dépôt probabiliste fait varier la distance parcourue selon le débit et la granulométrie.
                            float random = TickHashUnit(c, tick, 0xD3905E7u);
                            float settling = Kd_ * Mathf.Lerp(0.35f, 1.65f, random) *
                                (0.25f + 0.75f * Mathf.Clamp01(grain)) / (0.4f + q + flux[c]);
                            float dep = (sp - cap) * Mathf.Clamp(settling, 0.02f, 0.6f);
                            DepositWater(c, dep, coarse);
                            sp -= dep;
                            netWaterChange += dep;
                        }
                        if (w < 0.003f && sp > 0f) { DepositWater(c, sp, coarse); netWaterChange += sp; sp = 0f; }
                    }
                    waterErode[c] += (netWaterChange * ErodeViewScale - waterErode[c]) * ErodeViewRate;
                    waterN[c] = w;
                    suspN[c] = sp;
                    flux[c] += (q - flux[c]) * 0.03f;
                }
            });
            var t1 = water; water = waterN; waterN = t1;
            var t2 = susp; susp = suspN; suspN = t2;
            var t3 = waterState.salt; waterState.salt = waterState.nextSalt; waterState.nextSalt = t3;
        }

        // ------------------------------------------------------------------ mass wasting, uplift, weathering, volcanoes
        void TransferMantleRock(int c, float change)
        {
            float previous = rock[c];
            float next = previous + change;
            double delta = (double)next - previous;
            double mantle = (double)mantleSolid[c] + mantleResidual[c] - delta;
            if (mantle < 0d) return;
            rock[c] = next;
            mantleSolid[c] = (float)mantle;
            // Conserve la fraction trop petite pour être représentée dans le réservoir principal.
            mantleResidual[c] = (float)(mantle - mantleSolid[c]);
        }

        public void StepGeology()
        {
            var nbr = G.Nbr; var back = G.Back; var dist = G.Dist;
            float kt = kThermal;
            int slopeYear = YearNumber;

            Array.Copy(sed, waterN, M);
            Array.Copy(mix, mixN, M * 4);
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    int b = c * 4;
                    float g = rock[c] + sed[c];
                    float talus = Mathf.Lerp(2.0f, 0.7f, Mathf.Clamp01(sed[c] / 1.5f)) * SlopeVariance(c, 0x7A17ADu, slopeYear);
                    float soilF = 0.3f + 0.7f * Mathf.Clamp01(sed[c]);
                    for (int k = 0; k < 4; k++)
                    {
                        int n = nbr[b + k];
                        float o = 0f;
                        if (n >= 0)
                        {
                            float dz = g - rock[n] - sed[n];
                            float lim = talus * dist[b + k];
                            if (dz > lim) o = (dz - lim) * kt * soilF;
                        }
                        thOut[b + k] = o;
                    }
                }
            });

            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    int b = c * 4;
                    float outgoing = thOut[b] + thOut[b + 1] + thOut[b + 2] + thOut[b + 3];
                    float soilRemoved = Mathf.Min(outgoing, waterN[c]);
                    float remaining = waterN[c] - soilRemoved;
                    float m0 = 0f, m1 = 0f, m2 = 0f, m3 = 0f, inSum = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        int n = nbr[b + k];
                        if (n < 0) continue;
                        float a = thOut[n * 4 + back[b + k]] * G.Area[n] / G.Area[c];
                        if (a <= 0f) continue;
                        int nb = n * 4;
                        float sourceOut = thOut[nb] + thOut[nb + 1] + thOut[nb + 2] + thOut[nb + 3];
                        float soilFraction = sourceOut > 0f ? Mathf.Clamp01(waterN[n] / sourceOut) : 0f;
                        float soil = a * soilFraction, stone = a - soil;
                        inSum += a;
                        m0 += soil * mixN[nb] + stone * 0.1f;
                        m1 += soil * mixN[nb + 1] + stone * 0.2f;
                        m2 += soil * mixN[nb + 2] + stone * 0.6f;
                        m3 += soil * mixN[nb + 3] + stone * 0.1f;
                    }
                    rock[c] -= outgoing - soilRemoved;
                    float newSediment = remaining + inSum;
                    if (newSediment > 1e-9f)
                    {
                        mix[b] = (remaining * mixN[b] + m0) / newSediment;
                        mix[b + 1] = (remaining * mixN[b + 1] + m1) / newSediment;
                        mix[b + 2] = (remaining * mixN[b + 2] + m2) / newSediment;
                        mix[b + 3] = (remaining * mixN[b + 3] + m3) / newSediment;
                    }
                    sed[c] = newSediment;

                    if (rock[c] + sed[c] > SeaLevel)
                    {
                        float tc = tect[c];
                        if (!tectonics)
                        {
                            float rise = Mathf.Min(mantleSolid[c], uplift * tc * tc);
                            TransferMantleRock(c, rise);
                        }
                        float wr = weathering * (0.2f + moist[c]) * Mathf.Clamp01((temp[c] + 8f) / 30f) * Mathf.Clamp01(1f - sed[c] / maxSoil) * (1.3f - hard[c]);
                        WeatherRock(c, wr);
                    }
                    if (tectonics)
                    {
                        float g2 = rock[c] + sed[c];
                        float cv = conv[c];
                        rockN[c] = 0f;
                        if (cv > 0.02f)
                        {
                            // Terme mixte linéaire + quadratique, à dominante linéaire (0.55/0.45, vs 0.35/0.65
                            // avant) : les cellules à convergence modérée, plus loin de la frontière de plaque
                            // (queue du dégradé élargi dans ComputeActivity), produisent un soulèvement plus
                            // marqué — de vraies collines/contreforts qui montent progressivement en se
                            // rapprochant de la chaîne, au lieu d'être écrasées à presque zéro par le carré.
                            float cvBlend = 0.55f * cv + 0.45f * cv * cv;
                            // Marge aléatoire (mais stable dans l'espace) sur l'intensité du soulèvement : toutes
                            // les chaînes de montagnes n'ont plus exactement la même hauteur relative, certains
                            // segments culminent plus haut, d'autres restent plus bas, comme un vrai relief.
                            float heightVar = orogenyHeightVariance[c];
                            float up = orogeny * cvBlend * heightVar * Mathf.Clamp01((MaxHeight - g2) / 90f);
                            float change = g2 > SeaLevel ? up * (0.65f + 0.7f * tect[c]) :
                                (cv > 0.6f ? -0.5f * up * Mathf.Clamp01((g2 + 60f) / 30f) : 0.6f * up);
                            change = Mathf.Min(change, mantleSolid[c]);
                            if (g2 > SeaLevel && change > 0f)
                                // Près du plafond, le soulèvement est différé : il sera soit ajouté au sommet,
                                // soit redéposé au hasard sur une pente voisine plus basse (passe dédiée plus bas).
                                rockN[c] = change;
                            else
                                TransferMantleRock(c, change);

                            if (g2 <= SeaLevel && cv > 0.4f)
                            {
                                int layer = c * SubductionLayerCount;
                                float buried = Mathf.Min(Mathf.Max(0f, sed[c]), 0.0002f * cv * cv);
                                sed[c] -= buried;
                                float carry = 0f;
                                for (int k = 0; k < SubductionLayerCount; k++)
                                {
                                    float amt = subductionLayers[layer + k];
                                    float transit = 0.002f * cv * LayerTransitFactor[k];
                                    bool last = k == SubductionLayerCount - 1;
                                    float outFlow = last ? Mathf.Min(amt * transit, Mathf.Max(0f, 1.5f - magma[c]) * MagmaSolidScale) : amt * transit;
                                    float inFlow = carry + (k == 0 ? buried : 0f);
                                    subductionLayers[layer + k] = amt - outFlow + inFlow;
                                    carry = outFlow;
                                }
                                magma[c] += carry / MagmaSolidScale;
                            }
                        }
                        float rf = rift[c];
                        if (rf > 0.02f && g2 > SeaLevel)
                        {
                            // Chaque segment de faille a sa propre profondeur plancher (variable par cellule,
                            // entre 15 et 110 sous le niveau de la mer) : l'affaissement ralentit puis s'arrête
                            // en l'approchant, au lieu de creuser indéfiniment jusqu'au plancher mantellique —
                            // les failles n'ont donc plus toutes la même profondeur uniforme.
                            float depthTarget = CrustDatum - Mathf.Lerp(15f, 110f, CellHashUnit(c, 0x9E3779B9u));
                            float sinking = riftSink * rf * rf * Mathf.Clamp01((g2 - depthTarget) / 20f);
                            TransferMantleRock(c, -sinking);
                        }
                        else if (rf > 0.05f && g2 <= SeaLevel)
                        {
                            float rise = Mathf.Min(magma[c] * MagmaSolidScale,
                                (0.02f / 150f) * rf * magma[c] * Mathf.Clamp01((riftCrust + 8f - g2) / 8f));
                            rock[c] += rise;
                            magma[c] -= rise / MagmaSolidScale;
                        }
                    }
                    if (lava[c] > 0f) lava[c] = Mathf.Max(0f, lava[c] - 0.01f);
                    if (scar[c] > 0.01f) scar[c] *= 0.985f; else scar[c] = 0f;
                    if (rock[c] + sed[c] > MaxHeight + 30f)
                    {
                        float ex = rock[c] + sed[c] - (MaxHeight + 30f);
                        TransferMantleRock(c, -ex * 0.5f);
                    }
                }
            });

            if (tectonics)
            {
                // Le soulèvement différé (rockN) est réparti entre le sommet et une ou plusieurs pentes voisines
                // plus basses, choisies au hasard (un côté, l'autre, ou les deux à la fois) ; la part redéposée
                // sur les pentes croît avec la hauteur relative au plafond MaxHeight, sur toute la plage de
                // croissance. Cela évite l'empilement indéfini au sommet et fait apparaître des contreforts et
                // vallées réalistes dès les premiers reliefs, pas seulement juste avant le plafond.
                Array.Clear(thOut, 0, M * 4);
                Par.For(M, (s, e) =>
                {
                    for (int c = s; c < e; c++)
                    {
                        // Le mantle a pu être réduit depuis le calcul initial (ex. affaissement de rift sur
                        // la même cellule) : on reclamp pour ne jamais vider le réservoir sous zéro.
                        float change = Mathf.Min(rockN[c], mantleSolid[c]);
                        if (change <= 0f) continue;
                        float g2 = Ground(c);
                        // La part de matière redéposée sur une pente (plutôt qu'ajoutée au sommet) croît avec la
                        // hauteur relative au plafond sur TOUTE la plage de croissance (pas seulement tout en
                        // haut) : contreforts discrets pour les jeunes reliefs, dépôts de plus en plus marqués
                        // sur les pentes à mesure que le sommet approche MaxHeight — orogenèse complète.
                        float heightFrac = Mathf.Clamp01((g2 - SeaLevel) / Mathf.Max(1f, MaxHeight - SeaLevel));
                        float near = heightFrac * heightFrac;
                        int b = c * 4, count = 0;
                        for (int k = 0; k < 4; k++)
                        {
                            int n = nbr[b + k];
                            if (n < 0 || Ground(n) >= g2) continue;
                            uint rk = (uint)n * 374761393u ^ (uint)(k + 1) * 3266489917u ^ (uint)tick * 2246822519u;
                            if ((MixHash(rk) & 0x00ffffffu) / 16777215f < 0.5f) { thOut[b + k] = 1f; count++; }
                        }
                        if (count > 0 && near > 0f)
                        {
                            float spill = change * near;
                            float share = spill / count;
                            for (int k = 0; k < 4; k++) if (thOut[b + k] > 0f) thOut[b + k] = share;
                            mantleSolid[c] -= spill;
                            change -= spill;
                        }
                        // Rien à redéposer : les marques posées ci-dessus ne doivent pas être lues comme une
                        // quantité de sédiment par la passe suivante.
                        else if (count > 0)
                            for (int k = 0; k < 4; k++) thOut[b + k] = 0f;
                        if (change > 0f) TransferMantleRock(c, change);
                    }
                });
                Par.For(M, (s, e) =>
                {
                    for (int c = s; c < e; c++)
                    {
                        int b = c * 4;
                        float inbound = 0f;
                        for (int k = 0; k < 4; k++)
                        {
                            int n = nbr[b + k];
                            if (n >= 0) inbound += thOut[n * 4 + back[b + k]] * G.Area[n] / G.Area[c];
                        }
                        if (inbound > 0f) sed[c] += inbound;
                    }
                });
            }

            StepVolcanoes();
        }

        public void MaybeRandomEruption(System.Random rnd)
        {
            // Les points chauds ont leur propre chemin d'éruption, indépendant du niveau d'activité tectonique
            // global (voir MaybeHotspotEruption) : sans cela, ils étaient noyés dans le budget d'éruptions
            // aléatoires des zones de subduction/rift et ne produisaient presque jamais de relief visible.
            MaybeHotspotEruption(rnd);
            if (rnd.NextDouble() > (tectonics ? Activity / 150.0 : 1.0 / 400.0) || volcanoes.Count > 5) return;
            int best = -1; float bt = -1f;
            for (int i = 0; i < 80; i++)
            {
                int c = rnd.Next(M);
                float g = Ground(c);
                if (tectonics)
                {
                    if (magma[c] < 0.85f) continue;
                    float score = magma[c] + 0.15f * (float)rnd.NextDouble();
                    if (score > bt) { bt = score; best = c; }
                }
                else
                {
                    if (g < 2f) continue;
                    if (tect[c] > bt) { bt = tect[c]; best = c; }
                }
            }
            if (best >= 0) StartVolcano(best);
        }

        // Chemin d'éruption dédié aux points chauds : panaches du manteau fixes, indépendants de la convergence
        // des plaques, donc alimentés et déclenchés régulièrement même quand Activity (intensité tectonique
        // globale) est faible. Comme la cellule du point chaud ne bouge jamais, des éruptions répétées y bâtissent
        // réellement un édifice volcanique au fil du temps (îles/boucliers type Hawaï), plafonné à 2 volcans de
        // point chaud actifs à la fois pour ne pas saturer la scène.
        void MaybeHotspotEruption(System.Random rnd)
        {
            int activeHotspots = 0;
            for (int i = 0; i < volcanoes.Count; i++) if (plumeInf[volcanoes[i].cell] > 0.2f) activeHotspots++;
            if (activeHotspots >= 2 || rnd.NextDouble() > 0.02) return;
            int hotspot = FindHotspotEruption(rnd);
            if (hotspot >= 0) StartVolcano(hotspot);
        }

        int FindHotspotEruption(System.Random rnd)
        {
            int selected = -1;
            double total = 0d;
            for (int c = 0; c < M; c++)
            {
                if (plumeInf[c] < 0.2f || magma[c] < 0.5f) continue;
                bool active = false;
                for (int i = 0; i < volcanoes.Count; i++)
                    if (volcanoes[i].cell == c) { active = true; break; }
                if (active) continue;
                total += plumeInf[c] * magma[c] * G.Area[c];
                if (rnd.NextDouble() * total < plumeInf[c] * magma[c] * G.Area[c]) selected = c;
            }
            return selected;
        }
    }
}
