using UnityEngine;

namespace LivingPlanet
{
    // Flèches des vents, courants, écoulements et plaques, activables indépendamment.
    // Chacune est animée : elle glisse le long de sa direction en s'étirant puis en se résorbant,
    // à une vitesse liée à l'intensité du flux, pour rendre visible le mouvement (pas seulement sa direction).
    public sealed partial class PlanetView
    {
        public const int FlowWind = 0, FlowOcean = 1, FlowWater = 2, FlowTect = 3, FlowLayers = 4;
        public static readonly string[] FlowNames = { "Vents", "Courants marins", "Écoulement de l'eau", "Dérive et frontières des plaques" };
        public static readonly string[] FlowIcons = { "🌬", "🌊", "💦", "🧭" };
        public readonly bool[] FlowOn = new bool[FlowLayers];

        const int FlowN = 220;
        Bucket[][] flowB;                 // [couche][classe d'intensité]

        public static readonly Color[][] FlowCols =
        {
            new[] { new Color(0.8f, 0.9f, 1f), new Color(0.4f, 1f, 0.7f), new Color(1f, 0.9f, 0.3f) },
            new[] { new Color(0.2f, 0.5f, 1f), new Color(0.2f, 0.9f, 1f), new Color(1f, 0.55f, 0.25f) },
            new[] { new Color(0.4f, 0.6f, 1f), new Color(0.3f, 0.8f, 1f), new Color(0.9f, 0.95f, 1f) },
            new[] { new Color(1f, 0.6f, 0.3f), new Color(1f, 0.4f, 0.2f), new Color(1f, 0.2f, 0.2f) },
        };
        public static readonly Color[] BoundaryColors =
        {
            Color.clear, new Color(0.25f, 0.95f, 0.45f), new Color(0.25f, 0.7f, 1f),
            new Color(1f, 0.32f, 0.22f), new Color(0.86f, 0.4f, 1f)
        };

        void BuildFlow()
        {
            flowB = new Bucket[FlowLayers][];
            var bm = new Mesh();
            bm.vertices = new[]
            {
                new Vector3(-0.15f, 0f, -0.5f), new Vector3(-0.15f, 0f, 0.1f),
                new Vector3(0.15f, 0f, 0.1f), new Vector3(0.15f, 0f, -0.5f),
                new Vector3(-0.5f, 0f, 0.1f), new Vector3(0f, 0f, 0.5f), new Vector3(0.5f, 0f, 0.1f)
            };
            bm.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6 };
            bm.RecalculateNormals();
            bm.RecalculateBounds();
            for (int l = 0; l < FlowLayers; l++)
            {
                flowB[l] = new Bucket[3];
                for (int k = 0; k < 3; k++) flowB[l][k] = new Bucket { mesh = bm, mat = MakeUnlit(FlowCols[l][k]) };
            }
        }

        // champ de vecteurs (tangent) de la couche en cellule c ; renvoie l'intensité normalisée 0..1 ; false = pas de flux ici
        public bool FlowField(int l, int c, out Vector3 v, out float mag)
        {
            v = Vector3.zero; mag = 0f;
            float g = S.Ground(c);
            switch (l)
            {
                case FlowWind: v = S.wind[c]; mag = Mathf.Clamp01(v.magnitude / 1.2f); break;
                case FlowOcean:
                    if (g >= S.SeaLevel || S.current == null) return false;
                    v = S.current[c]; mag = Mathf.Clamp01(v.magnitude / 0.5f); break;
                case FlowWater:
                    if (g < S.SeaLevel) return false;
                    for (int k = 0; k < 4; k++) v += S.outW[c * 4 + k] * G.Tan[c * 4 + k];
                    mag = Mathf.Clamp01(S.flux[c] / 1f);
                    if (S.flux[c] < 0.06f) return false;
                    break;
                default:
                    v = S.PlateVelocity(c); mag = Mathf.Clamp01(v.magnitude / 0.5f); break;
            }
            v = Vector3.ProjectOnPlane(v, G.Dir[c]);
            return v.sqrMagnitude > 1e-10f && mag > 0.02f;
        }

        Vector3 FlowSampleDir(Vector3 camDir, float ang, int i, int layer)
        {
            Vector3 t = Vector3.Cross(camDir, Vector3.up);
            if (t.sqrMagnitude < 1e-4f) t = Vector3.Cross(camDir, Vector3.right);
            t.Normalize();
            float a = i * 2.39996323f + layer * 0.6f;
            float r = ang * Mathf.Sqrt((i + 0.5f) / FlowN);
            Vector3 side = Quaternion.AngleAxis(a * Mathf.Rad2Deg, camDir) * t;
            return (camDir * Mathf.Cos(r) + side * Mathf.Sin(r)).normalized;
        }

        void DrawFlow(Bounds bounds)
        {
            bool any = false;
            for (int l = 0; l < FlowLayers; l++) any |= FlowOn[l];
            if (!any) return;
            if (flowB == null) BuildFlow();
            Vector3 cp = lastCamPos;
            float alt = Mathf.Max(5f, cp.magnitude - R);
            Vector3 cd = cp.normalized;
            float horizon = Mathf.Acos(Mathf.Clamp01(R / cp.magnitude));
            float spread = Mathf.Clamp(horizon * 0.95f, 0.02f, 1.1f);
            float width = Mathf.Clamp(alt * 0.0035f, 0.35f, 7f);
            float speedK = 25f + alt * 0.35f;
            float t = Time.time;

            for (int l = 0; l < FlowLayers; l++)
            {
                if (!FlowOn[l]) continue;
                for (int k = 0; k < 3; k++) flowB[l][k].n = 0;
                for (int i = 0; i < FlowN; i++)
                {
                    Vector3 direction = FlowSampleDir(cd, spread, i, l);
                    int c = G.CellFromDir(direction);
                    Vector3 v; float mag;
                    if (!FlowField(l, c, out v, out mag)) continue;
                    Vector3 vn = v.normalized;
                    float baseLen = Mathf.Max(width * 2f, mag * speedK * 0.9f);
                    if (baseLen < 0.05f) continue;
                    float g = Mathf.Max(S.Ground(c), S.SeaLevel);
                    float lift = l == FlowWind ? 6f + 0.03f * alt : (l == FlowOcean ? 0.8f : (l == FlowWater ? 0.5f : 2.5f));
                    Vector3 pos0 = direction * (R + g + lift);
                    Vector3 up = direction;
                    var rot = Quaternion.LookRotation(Vector3.ProjectOnPlane(vn, up), up);
                    int cls = mag < 0.33f ? 0 : (mag < 0.66f ? 1 : 2);
                    // chaque flèche "coule" le long de sa direction : 2 échos déphasés qui glissent et
                    // s'étirent puis se résorbent, pour donner une sensation de mouvement continu de l'air/eau
                    // (vitesse perçue proportionnelle à l'intensité du flux, sans jamais dépasser sa longueur de base)
                    float cycle = 0.22f + 0.9f * mag;
                    for (int ec = 0; ec < 2; ec++)
                    {
                        float ph = Mathf.Repeat(t * cycle + i * 0.6180339887f + ec * 0.5f, 1f);
                        float grow = Mathf.Sin(ph * Mathf.PI);
                        float segLen = baseLen * (0.4f + 0.6f * grow);
                        Vector3 pos = pos0 + vn * ((ph - 0.5f) * baseLen * 0.7f);
                        flowB[l][cls].Add(Matrix4x4.TRS(pos, rot, new Vector3(width * 3f, width, segLen)));
                    }
                }
                for (int k = 0; k < 3; k++) DrawBucket(flowB[l][k], bounds, false);
            }
        }
    }
}
