using System;
using UnityEngine;

namespace LivingPlanet
{
    // Plaques tectoniques : chaque plaque tourne autour d'un pôle d'Euler, transporte son relief,
    // crée des montagnes aux zones de convergence, des rifts/nouvelle croûte aux zones de divergence.
    public sealed partial class PlanetSim
    {
        public const int PlateCount = 14;
        public enum BoundaryKind { None, Divergence, Subduction, Collision, Transform }

        public bool tectonics = true;
        public float plateSpeed = 0.064f;   // échelle de dérive en cellules par année
        public float orogeny = 0.0004f;    // soulèvement par tick à la convergence maximale
        public float riftSink = 0.00008f;  // affaissement des rifts continentaux
        public float riftCrust = -14f;      // altitude de la nouvelle croûte océanique

        public byte[] plate;
        public float[] conv, rift;
        float[] convN, riftN, rockN, sedN, hardN;
        // Taux de décroissance par cellule pour la propagation conv[]/rift[] (largeur irrégulière des chaînes de
        // montagnes). Précalculé une seule fois (position fixe sur la grille) : évaluer un bruit de Perlin à
        // chaque itération de propagation (des millions de fois par pas tectonique) provoquait des freezes
        // perceptibles en vitesse rapide.
        // boundaryDecay (plus doux) sert à l'élargissement de conv[] seul (collines/contreforts qui portent
        // loin) ; riftDecay (plus marqué, portée resserrée d'origine) sert à rift[] pour que les failles et
        // fossés d'effondrement ne s'élargissent pas en même temps que les contreforts.
        float[] boundaryDecay;
        float[] riftDecay;
        // Marge de hauteur du soulèvement (voir StepGeology), précalculée pour la même raison : évaluée à chaque
        // cellule à CHAQUE pas (pas seulement tous les TectEvery pas), un bruit de Perlin recalculé en direct
        // y serait encore plus coûteux que pour boundaryDecay.
        public float[] orogenyHeightVariance;
        Vector3[] warp, seeds, axis;
        float[] rate, acc;
        bool[] activePlates = new bool[PlateCount];
        bool[] ready;
        Quaternion[] rotations;
        float cellAngle, speedNorm;
        int tectPhase;
        const int TectEvery = 8;

        static Vector3 RandomUnit(System.Random r)
        {
            while (true)
            {
                var v = new Vector3((float)r.NextDouble() * 2f - 1f, (float)r.NextDouble() * 2f - 1f, (float)r.NextDouble() * 2f - 1f);
                float m = v.sqrMagnitude;
                if (m > 0.05f && m <= 1f) return v / Mathf.Sqrt(m);
            }
        }

        static uint MixHash(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352d;
            x ^= x >> 15;
            x *= 0x846ca68b;
            return x ^ (x >> 16);
        }

        // Hash spatial pur (statique, ne dérive pas dans le temps) donnant une valeur déterministe dans [0,1)
        // pour une cellule et un usage (`salt`) donnés — sert de base aux variantes "figées par lieu".
        static float CellHashUnit(int c, uint salt)
        {
            return MixHash((uint)c ^ salt) / (float)uint.MaxValue;
        }

        // Variance aléatoire déterministe (±10%) appliquée aux angles de pente/repos, pour éviter une inclinaison
        // strictement identique partout et obtenir des reliefs moins artificiels. `salt` différencie les usages.
        // Elle dérive lentement dans le temps (le sol/relief "vieillit") par paliers de SlopeDriftYears années,
        // interpolés en douceur pour éviter tout saut brusque d'inclinaison d'une cellule à l'autre instant.
        const float SlopeDriftYears = 150f;

        static float SlopeVariance(int c, uint salt, int yearNumber)
        {
            float t = yearNumber / SlopeDriftYears;
            int e0 = Mathf.FloorToInt(t);
            float frac = Mathf.SmoothStep(0f, 1f, t - e0);
            float v0 = SlopeHashUnit(c, salt, e0);
            float v1 = SlopeHashUnit(c, salt, e0 + 1);
            return 0.9f + Mathf.Lerp(v0, v1, frac) * 0.2f;
        }

        static float SlopeHashUnit(int c, uint salt, int epoch)
        {
            return CellHashUnit(c, salt ^ (uint)(epoch * 2654435761L));
        }

        static float EpochValue(int plateId, int epoch, int salt)
        {
            uint key = (uint)(plateId * 73856093 ^ epoch * 19349663 ^ salt * 83492791);
            return (MixHash(key) & 0x00ffffff) / 16777215f;
        }

        static Vector3 EpochAxis(int plateId, int epoch)
        {
            Vector3 axis = new Vector3(
                EpochValue(plateId, epoch, 11) * 2f - 1f,
                EpochValue(plateId, epoch, 23) * 2f - 1f,
                EpochValue(plateId, epoch, 37) * 2f - 1f);
            return axis.sqrMagnitude > 1e-5f ? axis.normalized : Vector3.up;
        }

        void InitPlates(System.Random rnd)
        {
            plate = new byte[M];
            conv = new float[M]; rift = new float[M]; convN = new float[M]; riftN = new float[M];
            rockN = new float[M]; sedN = new float[M]; hardN = new float[M];
            boundaryDecay = new float[M];
            riftDecay = new float[M];
            orogenyHeightVariance = new float[M];
            warp = new Vector3[M];
            seeds = new Vector3[PlateCount]; axis = new Vector3[PlateCount];
            rate = new float[PlateCount]; acc = new float[PlateCount];
            activePlates = new bool[PlateCount];
            ready = new bool[PlateCount]; rotations = new Quaternion[PlateCount];
            cellAngle = Mathf.PI * 0.5f / G.N;

            float maxRate = 0f;
            for (int p = 0; p < PlateCount; p++)
            {
                seeds[p] = RandomUnit(rnd);
                axis[p] = RandomUnit(rnd);
                activePlates[p] = true;
                rate[p] = cellAngle * (0.3f + 0.7f * (float)rnd.NextDouble()) * plateSpeed;
                acc[p] = 0f;
                if (rate[p] > maxRate) maxRate = rate[p];
            }
            speedNorm = 1f / (2f * maxRate + 1e-6f);

            Vector3 o1 = RandomUnit(rnd) * 37f, o2 = RandomUnit(rnd) * 41f, o3 = RandomUnit(rnd) * 29f;
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    Vector3 d = G.Dir[c];
                    warp[c] = d + 0.3f * new Vector3(
                        Noise.Perlin(d.x * 2.4f + o1.x, d.y * 2.4f + o1.y, d.z * 2.4f + o1.z),
                        Noise.Perlin(d.x * 2.4f + o2.x, d.y * 2.4f + o2.y, d.z * 2.4f + o2.z),
                        Noise.Perlin(d.x * 2.4f + o3.x, d.y * 2.4f + o3.y, d.z * 2.4f + o3.z));
                    boundaryDecay[c] = 0.93f + 0.035f * Noise.Perlin(d.x * 9f + 41.3f, d.y * 9f - 17.9f, d.z * 9f + 6.4f);
                    riftDecay[c] = 0.88f + 0.05f * Noise.Perlin(d.x * 9f + 41.3f, d.y * 9f - 17.9f, d.z * 9f + 6.4f);
                    orogenyHeightVariance[c] = Mathf.Clamp(1f + 0.25f * Noise.Perlin(
                        d.x * 13f + 71.2f, d.y * 13f - 29.4f, d.z * 13f + 18.6f), 0.6f, 1.5f);
                }
            });
            AssignPlates();
            ComputeActivity();
            InitStress(rnd);
        }

        public void MigrateTectonicTimescale()
        {
            plateSpeed *= 0.02f;
            if (rate == null || acc == null) return;
            speedNorm *= 50f;
            for (int p = 0; p < PlateCount; p++)
            {
                rate[p] *= 0.02f;
                acc[p] *= 0.02f;
            }
        }

        public void MigratePlateDriftSpeed()
        {
            plateSpeed *= 2f;
            if (rate == null) return;
            speedNorm *= 0.5f;
            for (int p = 0; p < rate.Length; p++) rate[p] *= 2f;
        }

        public void MigrateVisiblePlateDrift()
        {
            for (int i = 0; i < 2; i++) MigratePlateDriftSpeed();
        }

        public void MigrateActivePlates()
        {
            activePlates = new bool[PlateCount];
            if (plate == null) return;
            for (int c = 0; c < plate.Length; c++) activePlates[plate[c]] = true;
        }

        void AssignPlates()
        {
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    Vector3 w = warp[c];
                    float best = float.MinValue; int bp = 0;
                    for (int p = 0; p < PlateCount; p++)
                    {
                        if (!activePlates[p]) continue;
                        float d = w.x * seeds[p].x + w.y * seeds[p].y + w.z * seeds[p].z;
                        if (d > best) { best = d; bp = p; }
                    }

                    plate[c] = (byte)bp;
                }
            });
        }

        public BoundaryKind PlateBoundary(int c, int n, out float strength)
        {
            strength = 0f;
            if (plate == null || axis == null || rate == null || c < 0 || n < 0 || c >= M || n >= M || plate[c] == plate[n])
                return BoundaryKind.None;
            Vector3 position = G.Dir[c];
            Vector3 between = G.Dir[n] - position;
            if (between.sqrMagnitude < 1e-12f) return BoundaryKind.None;
            Vector3 relative = Vector3.Cross(axis[plate[c]] * rate[plate[c]] - axis[plate[n]] * rate[plate[n]], position);
            float closing = Vector3.Dot(relative, between.normalized) * speedNorm;
            Vector3 along = Vector3.Cross(position, between).normalized;
            float shear = Vector3.Dot(relative, along) * speedNorm;
            strength = Mathf.Sqrt(closing * closing + shear * shear);
            if (strength < 1e-6f) return BoundaryKind.None;
            if (Mathf.Abs(shear) > Mathf.Abs(closing)) return BoundaryKind.Transform;
            if (closing < 0f) return BoundaryKind.Divergence;
            return Ground(c) >= SeaLevel && Ground(n) >= SeaLevel ? BoundaryKind.Collision : BoundaryKind.Subduction;
        }

        void ComputeActivity()
        {
            var nbr = G.Nbr;
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    float cv = 0f, rf = 0f;
                    int pc = plate[c];
                    Vector3 x = G.Dir[c];
                    for (int k = 0; k < 4; k++)
                    {
                        int n = nbr[c * 4 + k];
                        if (n < 0) continue;
                        int q = plate[n];
                        if (q == pc) continue;
                        Vector3 vp = Vector3.Cross(axis[pc] * rate[pc], x);
                        Vector3 vq = Vector3.Cross(axis[q] * rate[q], x);
                        Vector3 dir = (G.Dir[n] - x).normalized;
                        Vector3 relative = vp - vq;
                        float cl = Vector3.Dot(relative, dir) * speedNorm;
                        float shear = Vector3.Dot(relative, Vector3.Cross(x, dir).normalized) * speedNorm;
                        if (Mathf.Abs(shear) > Mathf.Abs(cl)) continue;
                        if (cl > cv) cv = cl;
                        if (-cl > rf) rf = -cl;
                    }
                    conv[c] = cv; rift[c] = rf;
                }
            });

            // Portée élargie et décroissance adoucie (vs. 0.8 avant) pour que conv[]/rift[] forment un vrai
            // dégradé de collines/contreforts sur une large bande autour des frontières de plaques, et pas
            // seulement sur 2-3 cellules juste à la limite (voir aussi le terme linéaire ajouté à l'orogenèse
            // dans PlanetSim.StepGeology, qui exploite cette queue de dégradé au lieu de l'écraser).
            // Un taux de décroissance identique partout, propagé cellule par cellule sur la grille, forme des
            // "dents" parfaitement régulières et régulièrement espacées le long de la frontière (chaque cellule
            // de bordure agit comme une source ponctuelle identique). Un bruit spatial stable fait varier ce
            // taux (largeur de la chaîne irrégulière d'un endroit à l'autre, crêtes qui se chevauchent).
            // La portée de rift[] reste resserrée (comme avant) : seule celle de conv[] est élargie pour les
            // collines/contreforts — sans cette séparation, élargir la propagation agrandissait aussi les
            // failles/fossés d'effondrement bien au-delà de leur largeur voulue.
            int itersRift = Mathf.Max(4, G.N / 14);
            int itersConv = Mathf.Max(10, G.N / 6);
            for (int it = 0; it < itersRift; it++)
            {
                Par.For(M, (s, e) =>
                {
                    for (int c = s; c < e; c++)
                    {
                        float a = conv[c], b = rift[c];
                        for (int k = 0; k < 4; k++)
                        {
                            int n = nbr[c * 4 + k];
                            if (n < 0) continue;
                            float decay = boundaryDecay[n];
                            float x = conv[n] * decay, y = rift[n] * riftDecay[n];
                            if (x > a) a = x;
                            if (y > b) b = y;
                        }
                        convN[c] = a; riftN[c] = b;
                    }
                });
                var t1 = conv; conv = convN; convN = t1;
                var t2 = rift; rift = riftN; riftN = t2;
            }
            // Passes supplémentaires : ne prolongent plus que conv[] (le dégradé de collines), rift[] n'évolue
            // plus au-delà de sa portée resserrée ci-dessus.
            for (int it = itersRift; it < itersConv; it++)
            {
                Par.For(M, (s, e) =>
                {
                    for (int c = s; c < e; c++)
                    {
                        float a = conv[c];
                        for (int k = 0; k < 4; k++)
                        {
                            int n = nbr[c * 4 + k];
                            if (n < 0) continue;
                            float x = conv[n] * boundaryDecay[n];
                            if (x > a) a = x;
                        }
                        convN[c] = a;
                    }
                });
                var t1 = conv; conv = convN; convN = t1;
            }
            // Passe de lissage (moyenne, pas maximum) : arrondit les dents issues de la propagation cellule par
            // cellule en une enveloppe continue, pour que les contreforts se fondent/chevauchent au lieu de
            // rester des pics espacés régulièrement d'une cellule de grille.
            for (int it = 0; it < 3; it++)
            {
                Par.For(M, (s, e) =>
                {
                    for (int c = s; c < e; c++)
                    {
                        float a = conv[c], b = rift[c]; int cnt = 1;
                        for (int k = 0; k < 4; k++)
                        {
                            int n = nbr[c * 4 + k];
                            if (n < 0) continue;
                            a += conv[n]; b += rift[n]; cnt++;
                        }
                        convN[c] = a / cnt; riftN[c] = b / cnt;
                    }
                });
                var t1 = conv; conv = convN; convN = t1;
                var t2 = rift; rift = riftN; riftN = t2;
            }
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++) { conv[c] = Mathf.Clamp01(conv[c]); rift[c] = Mathf.Clamp01(rift[c]); }
            });
        }

        void AdvectCrust(float dt)
        {
            var nbr = G.Nbr;
            // Étend la contrainte dans chaque plaque, sans élargir les zones de magma ou de rift.
            Array.Copy(conv, convN, M);
            var shortening = convN;
            var shorteningNext = riftN;
            // Les deux premières cases de thOut mémorisent la source de contrainte pendant sa propagation.
            int anchorSlot = 0;
            for (int c = 0; c < M; c++) thOut[c * 4] = c;
            int reach = Mathf.Max(4, G.N / 32);
            for (int it = 0; it < reach; it++)
            {
                int sourceSlot = anchorSlot, destinationSlot = 1 - anchorSlot;
                Par.For(M, (s, e) =>
                {
                    for (int c = s; c < e; c++)
                    {
                        float pressure = shortening[c];
                        float anchor = thOut[c * 4 + sourceSlot];
                        for (int k = 0; k < 4; k++)
                        {
                            int n = nbr[c * 4 + k];
                            if (n < 0 || plate[n] != plate[c] || rift[n] >= shortening[n]) continue;
                            float candidate = shortening[n] * 0.84f;
                            if (candidate > pressure)
                            {
                                pressure = candidate;
                                anchor = thOut[n * 4 + sourceSlot];
                            }
                        }
                        shorteningNext[c] = pressure;
                        thOut[c * 4 + destinationSlot] = anchor;
                    }
                });
                var previous = shortening; shortening = shorteningNext; shorteningNext = previous;
                anchorSlot = destinationSlot;
            }
            float radiusSquared = G.Radius * G.Radius;
            float noiseScale = G.N * 0.15f;
            float cliffRelaxation = 0.2f * (1f - Mathf.Exp(-20f * dt));
            // La dérive accélérée ne doit pas produire une compression instantanée illimitée.
            float compressionSpeed = Mathf.Min(plateSpeed, 0.032f * Activity);
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    if (shortening[c] <= 0.02f || shortening[c] <= rift[c]) { rockN[c] = 0f; continue; }
                    Vector3 anchorDir = G.Dir[(int)thOut[c * 4 + anchorSlot]];
                    float noise = Noise.Perlin(anchorDir.x * noiseScale + 23.7f,
                        anchorDir.y * noiseScale - 11.3f, anchorDir.z * noiseScale + 7.1f);
                    rockN[c] = Mathf.Clamp01(0.5f + noise * 1.1f);
                }
            });
            // Approximation de l'épaisseur mobile, pas une couche de croûte explicitement simulée.
            float baseHeight = CrustDatum - MaxHeight;
            int slopeYear = YearNumber;
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    int b = c * 4, p = plate[c];
                    float ground = Ground(c);
                    float mobile = Mathf.Max(0f, rock[c] - magmaInflation[c] - baseHeight) + sed[c];
                    float total = 0f;
                    Vector3 pressureGradient = Vector3.zero;
                    if (shortening[c] > 0.02f)
                    {
                        for (int k = 0; k < 4; k++)
                        {
                            int n = nbr[b + k];
                            if (n >= 0 && plate[n] == p)
                                pressureGradient += G.Tan[b + k] * (shortening[n] - shortening[c]);
                        }
                        if (pressureGradient.sqrMagnitude > 1e-10f) pressureGradient.Normalize();
                    }
                    for (int k = 0; k < 4; k++)
                    {
                        int n = nbr[b + k];
                        Vector3 angularVelocity = axis[p] * rate[p];
                        if (n >= 0 && plate[n] != p)
                            angularVelocity = (angularVelocity + axis[plate[n]] * rate[plate[n]]) * 0.5f;
                        float fraction = n >= 0
                            ? Mathf.Max(0f, Vector3.Dot(angularVelocity, G.EdgeFlux[b + k]) *
                                dt * radiusSquared / G.Area[c])
                            : 0f;
                        if (n >= 0)
                        {
                            if (plate[n] == p && shortening[n] > shortening[c] && shortening[n] > rift[n])
                            {
                                float compression = shortening[n] - shortening[c];
                                float riftFactor = 1f - Mathf.Clamp01(Mathf.Max(rift[c], rift[n]) / shortening[n]);
                                fraction += 1.35f * compressionSpeed * dt * compression * riftFactor;
                            }
                            if (plate[n] == p && shortening[c] > rift[c] && shortening[n] > rift[n] &&
                                rockN[n] > rockN[c] && pressureGradient.sqrMagnitude > 0.5f)
                            {
                                float across = 1f - Mathf.Abs(Vector3.Dot(G.Tan[b + k], pressureGradient));
                                float pressure = Mathf.Min(shortening[c], shortening[n]);
                                fraction += 0.04f * compressionSpeed * dt * pressure * (rockN[n] - rockN[c]) * across;
                            }
                            if (mobile > 1e-8f)
                            {
                                // Étale aussi les anciens reliefs, même après le déplacement de la faille.
                                float neighborGround = Ground(n);
                                float inland = Mathf.SmoothStep(0f, 1f,
                                    Mathf.Clamp01((Mathf.Min(ground, neighborGround) - SeaLevel - 2f) / 8f));
                                float repose = Mathf.Lerp(1f, Mathf.Lerp(0.45f, 0.7f, hard[c]), inland) * SlopeVariance(c, 0x51A9D3u, slopeYear);
                                float excess = ground - neighborGround - repose * G.Dist[b + k];
                                if (excess > 0f)
                                {
                                    float cliff = Mathf.Max(0f, excess - 0.5f * G.Dist[b + k]);
                                    fraction += (0.5f * dt * excess + cliffRelaxation * cliff) / mobile;
                                }
                            }
                        }
                        thOut[b + k] = fraction;
                        total += fraction;
                    }
                    if (total > 0.9f)
                    {
                        float scale = 0.9f / total;
                        for (int k = 0; k < 4; k++) thOut[b + k] *= scale;
                    }
                }
            });

            double mantleBefore = MantleVolume();
            TransportCrust(baseHeight, true);
            SubductOceanFloor(mantleBefore - MantleVolume());
            CirculateMantle(dt);

            // Un second passage stabilise les fortes falaises sans déplacer la matière hors du bilan.
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    int b = c * 4;
                    float ground = Ground(c);
                    float mobile = Mathf.Max(0f, rock[c] - magmaInflation[c] - baseHeight) + sed[c];
                    float total = 0f;
                    for (int k = 0; k < 4; k++)
                    {
                        int n = nbr[b + k];
                        float fraction = 0f;
                        if (n >= 0 && mobile > 1e-8f)
                        {
                            float neighborGround = Ground(n);
                            float inland = Mathf.SmoothStep(0f, 1f,
                                Mathf.Clamp01((Mathf.Min(ground, neighborGround) - SeaLevel - 2f) / 8f));
                            float repose = Mathf.Lerp(1f, Mathf.Lerp(0.45f, 0.7f, hard[c]), inland) * SlopeVariance(c, 0x51A9D3u, slopeYear);
                            float excess = ground - neighborGround - repose * G.Dist[b + k];
                            fraction = 0.6f * cliffRelaxation * Mathf.Max(0f, excess) / mobile;
                        }
                        thOut[b + k] = fraction;
                        total += fraction;
                    }
                    if (total > 0.9f)
                    {
                        float scale = 0.9f / total;
                        for (int k = 0; k < 4; k++) thOut[b + k] *= scale;
                    }
                }
            });
            TransportCrust(baseHeight, false);
        }

        // Altitude de référence des planchers tectoniques (base de la croûte mobile, fond des rifts). Fixe, et
        // surtout pas le niveau de la mer courant : une fosse qui se creuse attire l'eau, fait baisser la mer,
        // et un plancher accroché à la mer descendrait avec elle, sans limite.
        public const float CrustDatum = 0f;
        // Temps caractéristique de brassage du manteau solide entre cellules (années simulées).
        const float MantleMixingYears = 1f;
        // Profondeur maximale d'une fosse de subduction sous la référence, la même que pour les rifts.
        const float MaxTrenchDepth = 110f;

        double MantleVolume()
        {
            double total = 0d;
            for (int c = 0; c < M; c++) total += mantleSolid[c] * (double)G.Area[c];
            return total;
        }

        // Les plaques fonctionnent comme un tapis roulant : le volume de croûte créé aux dorsales pendant ce pas
        // replonge dans le manteau là où le fond océanique converge (subduction). Sans cette fermeture du
        // bilan, la croûte neuve épaissirait indéfiniment le fond des océans et ferait monter la mer.
        // Le prélèvement est proportionnel à la convergence et s'annule en approchant de la profondeur maximale
        // d'une fosse, qui ne peut donc pas se creuser sans fin.
        void SubductOceanFloor(double createdVolume)
        {
            if (createdVolume <= 0d) return;
            float trenchFloor = CrustDatum - MaxTrenchDepth;
            double weightTotal = 0d;
            for (int c = 0; c < M; c++) weightTotal += SubductionWeight(c, trenchFloor) * (double)G.Area[c];
            if (weightTotal <= 0d) return;
            float perWeight = (float)(createdVolume / weightTotal);
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    float weight = SubductionWeight(c, trenchFloor);
                    if (weight <= 0f) continue;
                    float subducted = Mathf.Min(weight * perWeight, Ground(c) - trenchFloor);
                    rock[c] -= subducted;
                    mantleSolid[c] += subducted;
                }
            });
        }

        // Fond océanique (plus bas que la croûte océanique neuve) en zone de convergence.
        float SubductionWeight(int c, float trenchFloor)
        {
            float ground = Ground(c);
            if (conv[c] <= 0.05f || ground >= riftCrust) return 0f;
            return conv[c] * Mathf.Clamp01((ground - trenchFloor) / 20f);
        }

        // Convection du manteau : le réservoir de chaque cellule revient vers la moyenne planétaire. Sans cela,
        // les dorsales épuisent le manteau sous elles pendant qu'il s'accumule sous les chaînes de montagnes.
        // La moyenne est pondérée par l'aire, donc le volume total est conservé.
        void CirculateMantle(float dt)
        {
            double total = 0d, area = 0d;
            for (int c = 0; c < M; c++)
            {
                total += mantleSolid[c] * (double)G.Area[c];
                area += G.Area[c];
            }
            float mean = (float)(total / area);
            float blend = 1f - Mathf.Exp(-dt / MantleMixingYears);
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++) mantleSolid[c] += (mean - mantleSolid[c]) * blend;
            });
        }

        // `spreading` : passage d'advection des plaques. Aux frontières divergentes, les deux plaques emportent
        // la croûte chacune de leur côté ; ce qui part est alors remplacé par de la roche montée du manteau
        // (expansion des fonds océaniques), sans dépasser l'altitude de la croûte océanique neuve. Sans ce
        // remplacement, chaque dorsale devenait une fosse sans fond qui vidait les océans.
        void TransportCrust(float baseHeight, bool spreading)
        {
            var nbr = G.Nbr; var back = G.Back;
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    int b = c * 4;
                    float outgoing = thOut[b] + thOut[b + 1] + thOut[b + 2] + thOut[b + 3];
                    float crust = Mathf.Max(0f, rock[c] - magmaInflation[c] - baseHeight);
                    float remainingRock = crust * (1f - outgoing);
                    float remainingSed = sed[c] * (1f - outgoing);
                    float rockIn = 0f, sediment = remainingSed;
                    float hardness = remainingRock * hard[c], rockWeight = remainingRock;
                    float m0 = remainingSed * mix[b], m1 = remainingSed * mix[b + 1];
                    float m2 = remainingSed * mix[b + 2], m3 = remainingSed * mix[b + 3];
                    for (int k = 0; k < 4; k++)
                    {
                        int n = nbr[b + k];
                        if (n < 0) continue;
                        float fraction = thOut[n * 4 + back[b + k]] * G.Area[n] / G.Area[c];
                        if (fraction <= 0f) continue;
                        float incomingRock = Mathf.Max(0f, rock[n] - magmaInflation[n] - baseHeight) * fraction;
                        float incomingSed = sed[n] * fraction;
                        rockIn += incomingRock;
                        rockWeight += incomingRock;
                        hardness += incomingRock * hard[n];
                        sediment += incomingSed;
                        int nb = n * 4;
                        m0 += incomingSed * mix[nb]; m1 += incomingSed * mix[nb + 1];
                        m2 += incomingSed * mix[nb + 2]; m3 += incomingSed * mix[nb + 3];
                    }
                    rockN[c] = rock[c] - crust * outgoing + rockIn;
                    sedN[c] = sediment;
                    if (spreading && rift[c] > 0.05f)
                    {
                        float lost = crust * outgoing - rockIn;
                        float room = riftCrust - (rockN[c] + sediment);
                        float created = Mathf.Min(Mathf.Min(lost, room), mantleSolid[c]);
                        if (created > 0f) { rockN[c] += created; mantleSolid[c] -= created; }
                    }
                    hardN[c] = rockWeight > 1e-8f ? hardness / rockWeight : hard[c];
                    if (sediment > 1e-8f)
                    {
                        mixN[b] = m0 / sediment; mixN[b + 1] = m1 / sediment;
                        mixN[b + 2] = m2 / sediment; mixN[b + 3] = m3 / sediment;
                    }
                    else
                    {
                        mixN[b] = mix[b]; mixN[b + 1] = mix[b + 1];
                        mixN[b + 2] = mix[b + 2]; mixN[b + 3] = mix[b + 3];
                    }
                }
            });
            var r = rock; rock = rockN; rockN = r;
            var s1 = sed; sed = sedN; sedN = s1;
            var h = hard; hard = hardN; hardN = h;
            var m = mix; mix = mixN; mixN = m;
        }

        // La fréquence dépend du temps simulé, pas de la vitesse demandée.
        public void StepTectonics(float timeScale = 1f)
        {
            int interval = TectEvery;
            if (++tectPhase < interval) return;
            tectPhase = 0;
            if (!tectonics || plate == null)
            {
                StepMagma(1f);
                return;
            }
            float dt = interval / (float)TicksPerYear;
            int motionEpoch = YearNumber / 800;
            float driftBlend = 1f - Mathf.Exp(-dt / 400f);
            for (int p = 0; p < PlateCount; p++)
            {
                if (!activePlates[p]) continue;
                axis[p] = Vector3.Slerp(axis[p], EpochAxis(p, motionEpoch), driftBlend).normalized;
                float targetRate = cellAngle * (0.3f + 0.7f * EpochValue(p, motionEpoch, 53)) * plateSpeed;
                rate[p] = Mathf.Lerp(rate[p], targetRate, driftBlend);
            }

            Array.Clear(ready, 0, ready.Length);
            bool any = false;
            for (int p = 0; p < PlateCount; p++)
            {
                if (!activePlates[p]) continue;
                acc[p] += rate[p] * dt;
                if (acc[p] >= cellAngle)
                {
                    ready[p] = true;
                    rotations[p] = Quaternion.AngleAxis(-acc[p] * Mathf.Rad2Deg, axis[p]);
                    acc[p] = 0f;
                    any = true;
                }
            }

            AdvectCrust(dt);
            if (any)
            {
                // Au-delà des étiquettes (type de roche, minerai), toute la colonne géologique doit voyager
                // bodily avec sa plaque lors d'un pas de rotation complet : sans ça, un relief construit près
                // d'une frontière convergente (montagnes) restait figé à l'ancien emplacement de la faille au
                // lieu de suivre le flanc de la plaque qui s'en éloigne.
                int layers = SubductionLayerCount;
                Par.For(M, (s, e) =>
                {
                    for (int c = s; c < e; c++)
                    {
                        int p = plate[c];
                        int sc = ready[p] ? G.CellFromDir(rotations[p] * G.Dir[c]) : c;
                        if (ready[p] && plate[sc] != p)
                        {
                            // Juste à la frontière, l'échantillon inverse peut tomber d'un cran de l'autre côté
                            // (la frontière elle-même a aussi légèrement bougé) : au lieu d'abandonner et de
                            // garder c (ce qui figeait précisément le relief construit près des failles), on
                            // cherche la cellule de la même plaque la plus proche parmi les voisines directes.
                            int fallback = -1;
                            for (int k = 0; k < 4; k++)
                            {
                                int nb = G.Nbr[sc * 4 + k];
                                if (nb >= 0 && plate[nb] == p) { fallback = nb; break; }
                            }
                            sc = fallback >= 0 ? fallback : sc;
                        }
                        rockTN[c] = rockT[sc]; oreN[c] = ore[sc];
                        // Roche, sédiment et composition restent sur place : AdvectCrust les a déjà transportés
                        // à la vitesse de la plaque, en conservant le volume. Les recopier ici en plus déplaçait
                        // le relief près de trois fois plus vite que la plaque et perdait de la matière à chaque
                        // saut (69 % du sédiment en 170 ans dans VisibleDriftOver170Years).
                        rockN[c] = rock[c]; sedN[c] = sed[c]; hardN[c] = hard[sc];
                        magmaN[c] = magma[sc];
                        mantleSolidN[c] = mantleSolid[sc]; mantleResidualN[c] = mantleResidual[sc];
                        int b = c * 4, sb = sc * 4;
                        mixN[b] = mix[b]; mixN[b + 1] = mix[b + 1]; mixN[b + 2] = mix[b + 2]; mixN[b + 3] = mix[b + 3];
                        int lc = c * layers, lsc = sc * layers;
                        for (int k = 0; k < layers; k++) subductionLayersN[lc + k] = subductionLayers[lsc + k];
                    }
                });
                var rt = rockT; rockT = rockTN; rockTN = rt;
                var oo = ore; ore = oreN; oreN = oo;
                var r = rock; rock = rockN; rockN = r;
                var s1 = sed; sed = sedN; sedN = s1;
                var h = hard; hard = hardN; hardN = h;
                var m = mix; mix = mixN; mixN = m;
                var mg = magma; magma = magmaN; magmaN = mg;
                var ms = mantleSolid; mantleSolid = mantleSolidN; mantleSolidN = ms;
                var mr = mantleResidual; mantleResidual = mantleResidualN; mantleResidualN = mr;
                var sl = subductionLayers; subductionLayers = subductionLayersN; subductionLayersN = sl;
            }

            for (int p = 0; p < PlateCount; p++)
                if (activePlates[p])
                    seeds[p] = Quaternion.AngleAxis(rate[p] * dt * Mathf.Rad2Deg, axis[p]) * seeds[p];
            int plateEpoch = tick / (TicksPerYear * 250);
            if (plateEpoch > 0 && (tick - interval) / (TicksPerYear * 250) < plateEpoch)
                EvolvePlateBoundaries(plateEpoch);
            AssignPlates();
            ComputeActivity();
            StepMagma(interval / (float)TectEvery);
            AccumulateStress(interval);
        }

        void EvolvePlateBoundaries(int epoch)
        {
            var sizes = new int[PlateCount];
            for (int c = 0; c < M; c++) sizes[plate[c]]++;

            int free = -1, activeCount = 0;
            for (int p = 0; p < PlateCount; p++)
            {
                if (sizes[p] == 0) { activePlates[p] = false; if (free < 0) free = p; }
                else activeCount++;
            }

            int splitCell = -1;
            float splitScore = 0f;
            if (free >= 0)
            {
                for (int c = 0; c < M; c++)
                {
                    int p = plate[c];
                    float distance = Vector3.Angle(seeds[p], G.Dir[c]) * Mathf.Deg2Rad;
                    if (sizes[p] < M / PlateCount || distance < 2f * cellAngle) continue;
                    float score = rift[c] * distance;
                    if (rift[c] > 0.08f && score > splitScore) { splitScore = score; splitCell = c; }
                }
            }
            if (splitCell >= 0)
            {
                int parent = plate[splitCell];
                Vector3 position = G.Dir[splitCell];
                Vector3 tangent = Vector3.ProjectOnPlane(position - seeds[parent], position);
                if (tangent.sqrMagnitude < 1e-5f) tangent = Vector3.Cross(position, axis[parent]);
                if (tangent.sqrMagnitude < 1e-5f)
                    tangent = Vector3.Cross(position, Mathf.Abs(position.y) < 0.9f ? Vector3.up : Vector3.right);
                tangent.Normalize();
                seeds[free] = position;
                Vector3 omega = axis[parent] * rate[parent] + Vector3.Cross(position, tangent) * (cellAngle * plateSpeed * 0.5f);
                axis[free] = omega.sqrMagnitude > 1e-12f ? omega.normalized : EpochAxis(free, epoch);
                rate[free] = omega.magnitude;
                acc[free] = 0f;
                activePlates[free] = true;
                return;
            }

            if (activeCount <= 2) return;
            var contacts = new int[PlateCount * PlateCount];
            var difference = new float[PlateCount * PlateCount];
            for (int c = 0; c < M; c++)
            {
                int p = plate[c];
                for (int k = 0; k < 4; k++)
                {
                    int n = G.Nbr[c * 4 + k];
                    if (n < 0 || n <= c || plate[n] == p) continue;
                    int q = plate[n], pair = Mathf.Min(p, q) * PlateCount + Mathf.Max(p, q);
                    Vector3 delta = axis[p] * rate[p] - axis[q] * rate[q];
                    difference[pair] += Vector3.Cross(delta, G.Dir[c]).magnitude;
                    contacts[pair]++;
                }
            }
            int first = -1, second = -1;
            float closest = 0.25f * cellAngle * plateSpeed;
            for (int p = 0; p < PlateCount; p++)
                for (int q = p + 1; q < PlateCount; q++)
                {
                    int pair = p * PlateCount + q;
                    if (contacts[pair] < 2) continue;
                    float relative = difference[pair] / contacts[pair];
                    if (relative < closest) { closest = relative; first = p; second = q; }
                }
            if (first < 0) return;
            Vector3 merged = (axis[first] * rate[first] * sizes[first] + axis[second] * rate[second] * sizes[second]) /
                (sizes[first] + sizes[second]);
            if (merged.sqrMagnitude > 1e-12f) axis[first] = merged.normalized;
            rate[first] = merged.magnitude;
            activePlates[second] = false;
            acc[second] = 0f;
        }
    }
}
    