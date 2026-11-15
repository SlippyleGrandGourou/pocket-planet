using UnityEngine;

namespace LivingPlanet
{
    // Sol en couches : socle rocheux (type + minerai) + couche meuble (épaisseur `sed` + mélange de 4 matériaux).
    // Tout est stocké par cellule, sans allocation, pour permettre plus tard de creuser / déposer.
    public sealed partial class PlanetSim
    {
        public const int Dirt = 0, Sand = 1, Gravel = 2, Clay = 3, Stone = 4, Coal = 5, Iron = 6, Copper = 7, Tin = 8, Gold = 9, Salt = 10, MatCount = 11;
        public static readonly string[] MatNames = { "Terre", "Sable", "Gravier", "Argile", "Pierre", "Charbon", "Fer", "Cuivre", "Étain", "Or", "Sel" };
        public static readonly Color[] MatColors =
        {
            new Color(0.40f, 0.29f, 0.18f), new Color(0.83f, 0.74f, 0.50f), new Color(0.52f, 0.51f, 0.49f), new Color(0.63f, 0.39f, 0.28f),
            new Color(0.55f, 0.54f, 0.53f), new Color(0.09f, 0.09f, 0.10f), new Color(0.62f, 0.32f, 0.22f), new Color(0.30f, 0.62f, 0.52f),
            new Color(0.76f, 0.79f, 0.82f), new Color(0.95f, 0.78f, 0.20f), new Color(0.92f, 0.92f, 0.87f)
        };

        // Granite/Basalte/Grès/Calcaire : roches "de base", posées une fois à la génération (InitMaterials) ou
        // par le volcanisme. Poudingue/Argilite : roches sédimentaires obtenues par lithification (voir
        // StepLithification ci-dessous) de sédiments meubles enfouis sous leur propre poids — gravier/sable
        // cimentés pour le poudingue, terre/argile compactées pour l'argilite.
        public const int Granite = 0, Basalt = 1, Sandstone = 2, Limestone = 3, Conglomerate = 4, Mudstone = 5, RockCount = 6;
        public static readonly string[] RockNames = { "Granit", "Basalte", "Grès", "Calcaire", "Poudingue", "Argilite" };
        public static readonly Color[] RockColors =
        {
            new Color(0.56f, 0.54f, 0.53f), new Color(0.24f, 0.24f, 0.27f), new Color(0.74f, 0.56f, 0.38f), new Color(0.80f, 0.78f, 0.71f),
            new Color(0.58f, 0.46f, 0.34f), new Color(0.42f, 0.38f, 0.34f)
        };
        static readonly float[] RockHard = { 0.95f, 0.8f, 0.45f, 0.55f, 0.5f, 0.3f };
        // résistance à l'eau courante : terre, sable, gravier, argile
        static readonly float[] SoilErod = { 0.9f, 1.0f, 0.3f, 0.45f };
        // granite, basalte, grès, calcaire, poudingue, argilite
        static readonly float[] RockErod = { 0.08f, 0.16f, 0.52f, 0.34f, 0.4f, 0.62f };
        // composition du sol issu de l'altération de chaque roche (terre, sable, gravier, argile)
        static readonly float[,] Weathered =
        {
            { 0.20f, 0.50f, 0.30f, 0.00f }, { 0.45f, 0.10f, 0.15f, 0.30f }, { 0.15f, 0.80f, 0.05f, 0.00f }, { 0.30f, 0.10f, 0.20f, 0.40f },
            { 0.10f, 0.35f, 0.50f, 0.05f }, { 0.35f, 0.05f, 0.05f, 0.55f }
        };

        // ---------------------------------------------------------------- lithification (sédiment -> roche)
        // Vitesse à laquelle chaque matériau meuble se compacte/cimente en roche une fois suffisamment enfoui
        // (terre, sable, gravier, argile) : les grains fins (argile, terre) perdent leur porosité et se tassent
        // plus vite que le sable/gravier, qui ont besoin d'une vraie cimentation minérale, plus lente.
        static readonly float[] LithifyRateFactor = { 1.2f, 0.7f, 0.5f, 1.5f };
        // roche produite par la lithification selon le matériau dominant du sédiment enfoui (terre/argile ->
        // argilite ; sable -> grès ; gravier -> poudingue)
        static readonly int[] LithifyProduct = { Mudstone, Sandstone, Conglomerate, Mudstone };
        // épaisseur de sédiment au-delà de laquelle le poids des couches supérieures commence à comprimer la base
        public float LithifyDepth = 4f;
        public float LithifyRate = 0.0012f;
        // compaction : la roche obtenue occupe un peu moins de volume que le sédiment meuble d'origine (perte de
        // porosité), ce qui fait très légèrement s'affaisser les bassins sédimentaires au fil du temps, comme en réalité.
        public float LithifyCompaction = 0.88f;

        public float[] mix, mixN, fert;
        public byte[] rockT, rockTN, ore, oreN;

        void InitTerrainArrays()
        {
            mix = new float[M * 4]; mixN = new float[M * 4]; fert = new float[M];
            rockT = new byte[M]; rockTN = new byte[M]; ore = new byte[M]; oreN = new byte[M];
        }

        void InitMaterials(System.Random rnd)
        {
            Vector3 O() { return new Vector3((float)rnd.NextDouble() * 90f - 45f, (float)rnd.NextDouble() * 90f - 45f, (float)rnd.NextDouble() * 90f - 45f); }
            Vector3 oR = O(), oO = O(), oK = O(), oS = O(), oM = O();
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    Vector3 d = G.Dir[c];
                    float g = rock[c] + sed[c];
                    float rn = Noise.Perlin(d.x * 3f + oR.x, d.y * 3f + oR.y, d.z * 3f + oR.z);
                    int rt;
                    if (g < -4f) rt = Basalt;
                    else if (tect[c] > 0.55f || g > 140f || rn > 0.45f) rt = Granite;
                    else rt = rn < -0.05f ? Sandstone : Limestone;
                    rockT[c] = (byte)rt;
                    hard[c] = Mathf.Clamp01(RockHard[rt] * (0.9f + 0.1f * Noise.Perlin(d.x * 9f + oO.x, d.y * 9f + oO.y, d.z * 9f + oO.z)) * (0.9f + 0.1f * hard[c]));

                    // minerais : amas denses mais rares, liés au type de roche
                    int o = 0;
                    float n1 = Noise.Perlin(d.x * 16f + oK.x, d.y * 16f + oK.y, d.z * 16f + oK.z);
                    if (n1 > 0.42f)
                    {
                        float n2 = Noise.Perlin(d.x * 5f + oS.x, d.y * 5f + oS.y, d.z * 5f + oS.z);
                        switch (rt)
                        {
                            case Granite: o = n2 > 0.35f ? Gold : (n2 > -0.1f ? Tin : Copper); if (o == Gold && n1 < 0.55f) o = Tin; break;
                            case Basalt: o = n2 > 0f ? Copper : Iron; break;
                            case Sandstone: o = n2 > 0f ? Coal : Iron; break;
                            default: o = n2 > 0.1f ? Iron : Coal; break;
                        }
                    }
                    ore[c] = (byte)o;

                    // sol initial : majoritairement de la terre, sable sur les côtes, gravier en pente, argile en creux
                    float coast = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.5f, 6f, g));
                    float alt = Mathf.Clamp01((g - 60f) / 120f);
                    float dn = 0.5f + 0.5f * Noise.Perlin(d.x * 4f + oM.x, d.y * 4f + oM.y, d.z * 4f + oM.z);
                    float sand = 0.08f + 0.75f * coast + 0.25f * (1f - dn) * (1f - alt);
                    float grav = 0.05f + 0.5f * alt;
                    float clay = 0.08f + 0.25f * dn * (1f - alt) * (1f - coast);
                    float dirt = 0.8f * (1f - coast) * (1f - 0.7f * alt);
                    float sum = dirt + sand + grav + clay;
                    int b = c * 4;
                    mix[b] = dirt / sum; mix[b + 1] = sand / sum; mix[b + 2] = grav / sum; mix[b + 3] = clay / sum;
                    fert[c] = 0f;
                }
            });
        }

        // fertilité du sol 0..1 selon le mélange (terre/argile fertiles, sable/gravier pauvres) et l'humus
        public float Fertility(int c)
        {
            int b = c * 4;
            float f = mix[b] + mix[b + 1] * 0.4f + mix[b + 2] * 0.12f + mix[b + 3] * 0.75f;
            return Mathf.Clamp01(f * (0.9f + 0.5f * fert[c]) * 1.1f);
        }

        // matériau dominant en surface (roche / minerai si le sol est mince)
        public int SurfaceMaterial(int c)
        {
            if (waterState != null && waterState.deposit[c] > 0.05f) return Salt;
            if (sed[c] < 0.15f) return ore[c] != 0 ? ore[c] : Stone;
            int b = c * 4, best = 0;
            for (int i = 1; i < 4; i++) if (mix[b + i] > mix[b + best]) best = i;
            return best;
        }

        // ---------------------------------------------------------------- opérations sur le sol (par cellule, sans accès aux voisines)
        float ErodibilityAt(int c)
        {
            int b = c * 4;
            return mix[b] * SoilErod[0] + mix[b + 1] * SoilErod[1] + mix[b + 2] * SoilErod[2] + mix[b + 3] * SoilErod[3];
        }

        float RockErodibilityAt(int c)
        {
            return RockErod[rockT[c]] * (1f - 0.7f * hard[c]);
        }

        // retire de la couche meuble ; les matériaux les plus érodables partent en premier
        float ErodeSoil(int c, float amt)
        {
            float s = sed[c];
            float take = Mathf.Min(amt, s);
            if (take <= 1e-7f) return 0f;
            int b = c * 4;
            float er = Mathf.Max(ErodibilityAt(c), 1e-4f);
            float ns = s - take;
            if (ns > 1e-3f)
            {
                float sum = 0f;
                for (int i = 0; i < 4; i++)
                {
                    float a = Mathf.Max(0f, mix[b + i] * s - take * mix[b + i] * SoilErod[i] / er);
                    mixN[b + i] = a; sum += a;
                }
                if (sum > 1e-6f) for (int i = 0; i < 4; i++) mix[b + i] = mixN[b + i] / sum;
            }
            sed[c] = ns;
            return take;
        }

        // dépose du sol avec une composition donnée
        void AddSoil(int c, float amt, float m0, float m1, float m2, float m3)
        {
            if (amt <= 0f) return;
            int b = c * 4;
            float s = sed[c], t = s + amt;
            mix[b] = (mix[b] * s + m0 * amt) / t;
            mix[b + 1] = (mix[b + 1] * s + m1 * amt) / t;
            mix[b + 2] = (mix[b + 2] * s + m2 * amt) / t;
            mix[b + 3] = (mix[b + 3] * s + m3 * amt) / t;
            sed[c] = t;
        }

        // dépôt par l'eau : courant fort = gravier/sable, courant faible = limon/argile (coarse 0..1)
        void DepositWater(int c, float amt, float coarse)
        {
            AddSoil(c, amt, 0.35f * (1f - coarse), 0.1f + 0.35f * coarse, 0.55f * coarse, 0.55f * (1f - coarse));
            fert[c] = Mathf.Min(1f, fert[c] + amt * 0.25f * (1f - coarse));
        }

        // altération du socle en sol
        void WeatherRock(int c, float amt)
        {
            int rt = rockT[c];
            rock[c] -= amt;
            AddSoil(c, amt, Weathered[rt, 0], Weathered[rt, 1], Weathered[rt, 2], Weathered[rt, 3]);
        }

        // ---------------------------------------------------------------- lithification (le contraire de l'altération)
        // Le sédiment meuble, une fois enfoui sous une épaisseur suffisante (poids des couches au-dessus), se
        // compacte et se cimente progressivement en roche sédimentaire solide : exactement le cycle sable -> grès
        // -> (uplift) -> érosion -> sable que l'on veut pouvoir observer. Comme `sed`/`mix` ne représentent qu'un
        // empilement agrégé (pas de vraies couches datées), on applique le processus à la tranche la plus
        // profonde de l'empilement actuel, approximée par l'épaisseur totale au-delà de `LithifyDepth` : plus le
        // dépôt est épais, plus sa base est comprimée et plus la conversion est rapide.
        public void StepLithification()
        {
            Par.For(M, (s, e) =>
            {
                for (int c = s; c < e; c++)
                {
                    float excess = sed[c] - LithifyDepth;
                    if (excess <= 1e-4f) continue;

                    int b = c * 4;
                    int dominant = 0;
                    for (int i = 1; i < 4; i++) if (mix[b + i] > mix[b + dominant]) dominant = i;
                    float rateFactor = mix[b] * LithifyRateFactor[0] + mix[b + 1] * LithifyRateFactor[1] +
                        mix[b + 2] * LithifyRateFactor[2] + mix[b + 3] * LithifyRateFactor[3];
                    float amt = Mathf.Min(excess, excess * LithifyRate * rateFactor);
                    if (amt <= 1e-7f) continue;

                    sed[c] -= amt;
                    float produced = amt * LithifyCompaction;
                    int newType = LithifyProduct[dominant];
                    // La roche n'est pas stratifiée dans ce modèle (une seule couche agrégée par cellule) : le
                    // type affiché/utilisé par cellule reste celui de la couche la plus volumineuse, donc on ne
                    // bascule vers le nouveau type que si l'apport fraîchement lithifié dépasse la roche déjà en
                    // place (sinon une fine pellicule d'argilite sur un socle de granite ne "repeindrait" pas
                    // toute la cellule en argilite).
                    if (produced > rock[c]) { rockT[c] = (byte)newType; hard[c] = RockHard[newType]; }
                    else hard[c] = Mathf.Clamp01((hard[c] * rock[c] + RockHard[newType] * produced) / Mathf.Max(1e-5f, rock[c] + produced));
                    rock[c] += produced;
                }
            });
        }

        // ---------------------------------------------------------------- API de modification du terrain (creuser / déposer)
        // Creuse `amount` d'épaisseur et accumule les matériaux obtenus dans `yield` (taille MatCount). Retourne l'épaisseur retirée.
        public float Dig(int c, float amount, float[] yield)
        {
            float salt = Mathf.Min(amount, waterState.deposit[c]);
            waterState.deposit[c] -= salt;
            if (yield != null) yield[Salt] += salt;
            float t = Mathf.Min(amount - salt, sed[c]);
            int b = c * 4;
            if (yield != null) for (int i = 0; i < 4; i++) yield[i] += t * mix[b + i];
            sed[c] -= t;
            float r = amount - salt - t;
            if (r > 0f)
            {
                rock[c] -= r;
                if (yield != null)
                {
                    if (ore[c] != 0) { yield[ore[c]] += r * 0.35f; yield[Stone] += r * 0.65f; }
                    else yield[Stone] += r;
                }
            }
            return amount;
        }

        // Dépose `amount` de matériaux bruts ; `comp` = proportions (terre, sable, gravier, argile), null = terre.
        public void Deposit(int c, float amount, float[] comp)
        {
            if (comp == null) AddSoil(c, amount, 1f, 0f, 0f, 0f);
            else
            {
                float s = comp[0] + comp[1] + comp[2] + comp[3];
                if (s <= 1e-6f) return;
                AddSoil(c, amount, comp[0] / s, comp[1] / s, comp[2] / s, comp[3] / s);
            }
        }
    }
}
