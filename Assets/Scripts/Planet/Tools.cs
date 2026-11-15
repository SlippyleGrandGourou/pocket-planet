using UnityEngine;

namespace LivingPlanet
{
    // Outils du joueur : injection de matières / gaz, catastrophes manuelles et souffle planétaire.
    // Tout passe par les mécanismes existants (dépôt de sol, magma, coulées, gaz transportés par le vent, séismes...).
    public sealed partial class PlanetSim
    {
        public static readonly string[] SolidNames =
            { "Terre", "Sable", "Gravier", "Argile", "Roche", "Charbon", "Fer", "Cuivre", "Étain", "Or", "Neige", "Eau douce", "Cendres", "Magma", "Lave", "Sel" };
        public static readonly string[] GasNames = { "Oxygène", "CO₂", "Méthane", "SO₂", "Vapeur d'eau" };
        public static readonly string[] DisasterNames =
            { "Éruption", "Séisme", "Impact météoritique", "Incendie", "Tsunami", "Glissement de terrain", "Inondation" };

        const int SolidSnow = 10, SolidWater = 11, SolidAsh = 12, SolidMagma = 13, SolidLava = 14;
        public const int ExternalSolid = 0, ExternalWater = 1, ExternalSalt = 2,
            ExternalO2 = 3, ExternalCO2 = 4, ExternalCH4 = 5, ExternalSO2 = 6;
        public float[] externalAdded = new float[7];

        public Vector3[] gustV;
        public float[] presBias;

        // vitesse de dérive de la plaque sous la cellule (0..~0,5 normalisée), tangente à la sphère
        public Vector3 PlateVelocity(int c)
        {
            if (plate == null || axis == null) return Vector3.zero;
            int p = plate[c];
            return Vector3.Cross(axis[p] * (rate[p] * speedNorm * 2f), G.Dir[c]);
        }

        // qty = épaisseur (unités de relief) au centre ; radius en cellules
        public int InjectSolid(int c, int id, float qty, int radius)
        {
            if (c < 0) return 0;
            Region(c, radius);
            int n = regionA.Count;
            for (int i = 0; i < n; i++)
            {
                int cell = regionA[i];
                float f = 1f - regionD[i] / (radius + 1f);
                float a = qty * f;
                if (id < 4)
                {
                    var comp = new float[4]; comp[id] = 1f;
                    Deposit(cell, a, comp);
                    externalAdded[ExternalSolid] += a;
                }
                else if (id < 10)
                {
                    rock[cell] += a;
                    externalAdded[ExternalSolid] += a;
                    if (id >= 5) ore[cell] = (byte)id;
                }
                else if (id == SolidSnow) { snow[cell] += a * 0.15f; externalAdded[ExternalWater] += a * 0.15f; }
                else if (id == SolidWater) { water[cell] += a * 0.15f; externalAdded[ExternalWater] += a * 0.15f; }
                else if (id == SolidAsh) ash[cell] = Mathf.Min(2f, ash[cell] + a * 0.05f);
                else if (id == SolidMagma)
                {
                    float before = magma[cell];
                    magma[cell] = Mathf.Min(1.5f, magma[cell] + a * 0.04f);
                    externalAdded[ExternalSolid] += (magma[cell] - before) * MagmaSolidScale;
                    stress[cell] = Mathf.Min(3f, stress[cell] + a * 0.01f);
                }
                else if (id == SolidLava)
                {
                    lava[cell] = 1f;
                    if (regionD[i] == 0 && lavaFlows.Count < 64)
                    {
                        lavaFlows.Add(new LavaFlow { cell = cell, steps = 0, vol = qty * 0.35f });
                        externalAdded[ExternalSolid] += qty * 0.35f;
                    }
                }
                else if (id == 15) { waterState.deposit[cell] += a * 0.15f; externalAdded[ExternalSalt] += a * 0.15f; }
                if (onDisturb != null && id != SolidSnow && id != SolidWater) onDisturb(cell);
            }
            terrainDirty = true;
            return n;
        }

        // qty en unités : O2 +0,3 %, CO2 +60 ppm, CH4 +0,5 ppm, SO2 +0,3 ppm, vapeur +0,01 d'humidité par unité (au centre)
        public int InjectGas(int c, int id, float qty, int radius)
        {
            if (c < 0) return 0;
            Region(c, radius);
            for (int i = 0; i < regionA.Count; i++)
            {
                int cell = regionA[i];
                float f = 1f - regionD[i] / (radius + 1f);
                float a = qty * f;
                switch (id)
                {
                    case 0: { float old = o2[cell]; o2[cell] = Mathf.Min(35f, old + a * 0.3f); externalAdded[ExternalO2] += o2[cell] - old; break; }
                    case 1: { float old = co2[cell]; co2[cell] = Mathf.Min(20000f, old + a * 60f); externalAdded[ExternalCO2] += co2[cell] - old; break; }
                    case 2: { float old = ch4[cell]; ch4[cell] = Mathf.Min(500f, old + a * 0.5f); externalAdded[ExternalCH4] += ch4[cell] - old; break; }
                    case 3: so2[cell] += a * 0.3f; externalAdded[ExternalSO2] += a * 0.3f; break;
                    default: { float old = hum[cell]; hum[cell] = Mathf.Min(1.5f, old + a * 0.01f); externalAdded[ExternalWater] += (hum[cell] - old) * rainK; break; }
                }
            }
            return regionA.Count;
        }

        // intensity 0..1
        public void Disaster(int kind, int c, float intensity, System.Random rnd)
        {
            if (c < 0) return;
            intensity = Mathf.Clamp01(intensity);
            switch (kind)
            {
                case 0: StartVolcano(c, 0.15f + 0.85f * intensity); break;
                case 1: Quake(c, 4f + 4f * intensity, rnd); Log("Séisme provoqué (" + Where(c) + ")", c); break;
                case 2: SpawnAsteroid(intensity < 0.15f ? AstBurn : (intensity < 0.4f ? AstAir : AstImpact), c, rnd, 0.1f + 3.4f * intensity * intensity); break;
                case 3: StartFire(c, 0.6f + 2.4f * intensity); Log("Incendie déclenché (" + Where(c) + ")", c); break;
                case 4: if (SpawnTsunami(c, 1.5f + 6f * intensity)) Log("Tsunami déclenché (" + Where(c) + ")", c); break;
                case 5: Landslide(c, 3f + 14f * intensity); Log("Éboulement provoqué (" + Where(c) + ")", c); break;
                default:
                    Region(c, 3 + (int)(8 * intensity));
                    for (int i = 0; i < regionA.Count; i++)
                    {
                        float added = 1.2f * intensity * (1f - regionD[i] / (4f + 8 * intensity));
                        water[regionA[i]] += added;
                        externalAdded[ExternalWater] += added;
                    }
                    Log("Inondation provoquée (" + Where(c) + ")", c);
                    break;
            }
        }

        // Souffle : dir = direction tangente (unitaire) ; strength*dt est appliqué progressivement tant que le clic est maintenu.
        public void Blow(int c, Vector3 dir, float amount, int radius)
        {
            if (c < 0 || dir.sqrMagnitude < 1e-6f) return;
            if (gustV == null) { gustV = new Vector3[M]; presBias = new float[M]; }
            dir.Normalize();
            Region(c, radius);
            float cellAng = G.CellSize / G.Radius * (radius + 1f);
            for (int i = 0; i < regionA.Count; i++)
            {
                int cell = regionA[i];
                float f = 1f - regionD[i] / (radius + 1f);
                f *= f;
                Vector3 push = dir * (amount * 0.6f * f);
                wind[cell] += push;
                Vector3 gv = gustV[cell] + push;
                if (gv.magnitude > 1.5f) gv = gv.normalized * 1.5f;
                gustV[cell] = gv;
                // le vent va de la haute vers la basse pression : creux devant, bosse derrière
                float o = Vector3.Dot(G.Dir[cell] - G.Dir[c], dir) / cellAng;
                presBias[cell] = Mathf.Clamp(presBias[cell] - o * amount * 0.25f * f, -1.2f, 1.2f);
                // l'air soufflé attise les feux voisins et disperse les cendres
                if (fire[cell] > 0f) fire[cell] = Mathf.Min(1.5f, fire[cell] * (1f + 0.5f * amount * f));
            }
        }

        // appelé par UpdateWind : les rafales et biais de pression persistent puis s'atténuent
        void ApplyBlowToPressure()
        {
            if (presBias == null) return;
            for (int c = 0; c < M; c++) pres[c] += presBias[c];
        }

        void ApplyBlowToWind()
        {
            if (gustV == null) return;
            for (int c = 0; c < M; c++)
            {
                wind[c] += gustV[c];
                gustV[c] *= 0.88f;
                presBias[c] *= 0.93f;
            }
        }
    }
}
