using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LivingPlanet
{
    // Rendu de l'eau : une nappe par face du cube pour les mers et les lacs, et des rubans pour les rivières,
    // trop étroites pour la grille de simulation.
    public sealed partial class PlanetView
    {
        // Épaisseur d'eau à partir de laquelle une cellule porte une nappe. Sur terre, une lame plus mince est
        // une rivière (dessinée en ruban) ou un simple ruissellement (le sol est assombri).
        const float SeaWaterMin = 0.02f, LandWaterMin = 0.3f;
        // Enfoncement de la nappe sous le terrain au-delà de la rive, pour qu'elle ne dépasse jamais du sol.
        const float ShoreSink = 0.4f;
        // Débit moyen à partir duquel un cours d'eau est dessiné, et nombre de tronçons par courbe.
        const float RiverMinFlux = 0.1f;
        const int RiverSegments = 6;
        const float RiverLift = 0.25f;

        bool ShowsWater(int c)
        {
            return S.water[c] > (S.Ground(c) < S.SeaLevel ? SeaWaterMin : LandWaterMin);
        }

        void BuildWaterFace(int f, Material material)
        {
            var F = faces[f];
            int vpf = G.VertsPerFace;
            F.waterMesh = BuildGridMesh(G.N, R, f);
            F.waterMesh.MarkDynamic();
            F.waterVerts = new Vector3[vpf];
            F.waterFlows = new Vector3[vpf];
            F.waterSlopes = new Vector3[vpf];
            F.waterColors = new Color32[vpf];
            F.waterData = new Vector4[vpf];
            F.waterLevel = new float[vpf];
            F.waterWet = new bool[vpf];
            F.shoreLevel = new float[G.N * G.N];
            for (int i = 0; i < vpf; i++) F.waterVerts[i] = G.VertDir[f * vpf + i];
            F.waterMesh.normals = F.waterVerts;
            F.waterR = AddWaterObject("Water" + f, F.waterMesh, material);

            F.riverMesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            F.riverMesh.MarkDynamic();
            F.riverR = AddWaterObject("River" + f, F.riverMesh, material);
        }

        MeshRenderer AddWaterObject(string name, Mesh mesh, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.enabled = !HideWater;
            return renderer;
        }

        // ---------------------------------------------------------------- nappes (mers et lacs)
        // La nappe couvre les cellules en eau et déborde d'une cellule sous les berges : la rive visible est alors
        // l'intersection de la surface de l'eau avec le terrain, et non le bord en escalier des cellules.
        void RefreshWaterFace(int f)
        {
            var F = faces[f];
            int vpf = G.VertsPerFace, n = G.N, n1 = n + 1, offset = f * n * n;

            // 1. Sommets touchant au moins une cellule en eau : niveau et attributs moyens de ces cellules.
            Par.For(vpf, (start, end) =>
            {
                for (int vi = start; vi < end; vi++)
                {
                    int gi = f * vpf + vi;
                    float heightSum = 0f, wave = 0f, ice = 0f, speed = 0f, sediment = 0f, turbulence = 0f, surgeHeight = 0f;
                    Vector3 flow = Vector3.zero;
                    int count = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        int c = G.VertCells[gi * 4 + k];
                        if (c < 0 || !ShowsWater(c)) continue;
                        // Moyenne des surfaces mouillées pour éviter les pics aux jonctions terre/mer. La crête
                        // d'un tsunami ne déplace pas d'eau en mer : elle s'ajoute ici à la surface.
                        float crest = S.TsunamiElevation(c);
                        heightSum += S.Ground(c) + S.water[c] + crest;
                        Vector3 cellFlow = Vector3.zero;
                        if (S.IsOcean(c) && S.current != null) cellFlow += S.current[c];
                        else for (int edge = 0; edge < 4; edge++) cellFlow += S.outW[c * 4 + edge] * G.Tan[c * 4 + edge];
                        Vector3 surge = S.TsunamiFlow(c);
                        cellFlow += surge;
                        surgeHeight += crest;
                        turbulence += Mathf.Clamp01(surge.magnitude * 0.12f) * Mathf.Clamp01(1f - S.water[c] / 12f);
                        speed += Mathf.Clamp01(cellFlow.magnitude);
                        sediment += Mathf.Clamp01(S.susp[c] / Mathf.Max(S.water[c], 0.01f));
                        cellFlow = Vector3.ProjectOnPlane(cellFlow, G.Dir[c]);
                        flow += cellFlow;
                        wave += Mathf.Clamp01(0.06f + S.wind[c].magnitude * (1f + 0.9f * S.storm[c]) * 0.36f +
                            S.storm[c] * 0.35f + cellFlow.magnitude * 0.35f);
                        ice += Mathf.Clamp01((-6f - S.temp[c]) / 6f);
                        count++;
                    }
                    F.waterWet[vi] = count > 0;
                    if (count == 0)
                    {
                        F.waterLevel[vi] = float.NaN;
                        F.waterFlows[vi] = Vector3.zero;
                        F.waterColors[vi] = new Color32(0, 0, 0, 0);
                        F.waterData[vi] = Vector4.zero;
                        continue;
                    }
                    float inv = 1f / count;
                    F.waterLevel[vi] = heightSum * inv;
                    flow = Vector3.ProjectOnPlane(flow * inv, G.VertDir[gi]);
                    F.waterFlows[vi] = flow.sqrMagnitude > 1e-8f ? flow.normalized : Vector3.zero;
                    F.waterColors[vi] = new Color32((byte)(wave * inv * 255f), (byte)(ice * inv * 255f), 0, (byte)(turbulence * inv * 255f));
                    F.waterData[vi] = new Vector4(speed * inv, sediment * inv, 0f, surgeHeight * inv);
                }
            });

            // 2. Cellules de berge : à sec, mais touchant une cellule en eau. Elles prolongent son niveau.
            F.waterTriangles.Clear();
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int local = j * n + i;
                    int a = j * n1 + i, b = a + 1, d = a + n + 2, cc = d - 1;
                    bool wet = ShowsWater(offset + local);
                    float shore = float.NaN;
                    if (!wet)
                    {
                        float sum = 0f; int count = 0;
                        if (F.waterWet[a]) { sum += F.waterLevel[a]; count++; }
                        if (F.waterWet[b]) { sum += F.waterLevel[b]; count++; }
                        if (F.waterWet[cc]) { sum += F.waterLevel[cc]; count++; }
                        if (F.waterWet[d]) { sum += F.waterLevel[d]; count++; }
                        if (count > 0) shore = sum / count;
                    }
                    F.shoreLevel[local] = shore;
                    if (!wet && float.IsNaN(shore)) continue;
                    F.waterTriangles.Add(a); F.waterTriangles.Add(b); F.waterTriangles.Add(cc);
                    F.waterTriangles.Add(b); F.waterTriangles.Add(d); F.waterTriangles.Add(cc);
                }

            // 3. Position, profondeur locale et pente de la surface en chaque sommet.
            Par.For(vpf, (start, end) =>
            {
                for (int vi = start; vi < end; vi++)
                {
                    int q = vi / n1, p = vi % n1;
                    Vector3 direction = G.VertDir[f * vpf + vi];
                    float terrain = F.verts[vi].magnitude - R;
                    float level = F.waterLevel[vi];
                    if (!F.waterWet[vi])
                    {
                        // Sommet extérieur d'une berge : niveau des cellules de berge voisines, enfoncé sous le sol.
                        float sum = 0f; int count = 0;
                        for (int dj = -1; dj <= 0; dj++)
                            for (int di = -1; di <= 0; di++)
                            {
                                int i = p + di, j = q + dj;
                                if (i < 0 || j < 0 || i >= n || j >= n) continue;
                                float shore = F.shoreLevel[j * n + i];
                                if (float.IsNaN(shore)) continue;
                                sum += shore; count++;
                            }
                        level = count > 0 ? Mathf.Min(sum / count, terrain - ShoreSink) : terrain - ShoreSink;
                    }
                    F.waterVerts[vi] = direction * (R + level + 0.02f);
                    F.waterData[vi].z = level - terrain;
                    F.waterSlopes[vi] = F.waterWet[vi] ? SurfaceSlope(F, f, p, q) : Vector3.zero;
                }
            });

            F.waterMesh.SetVertices(F.waterVerts, 0, vpf, MeshUpdateFlags.DontRecalculateBounds);
            F.waterMesh.normals = customOceanMaterial ? F.waterFlows : F.waterVerts;
            F.waterMesh.SetColors(F.waterColors);
            F.waterMesh.SetUVs(0, F.waterData);
            F.waterMesh.SetUVs(1, F.waterSlopes);
            F.waterMesh.SetTriangles(F.waterTriangles, 0, false);
            F.waterMesh.bounds = new Bounds(Vector3.zero, Vector3.one * R * 2.4f);
        }

        // Pente de la surface de l'eau au sommet (p, q) : vecteur tangent pointant vers le haut de la pente, de
        // norme égale à la pente. Elle n'utilise que des sommets en eau, pour ignorer l'enfoncement des berges.
        Vector3 SurfaceSlope(Face F, int f, int p, int q)
        {
            int n1 = G.N + 1, vpf = G.VertsPerFace, vi = q * n1 + p;
            Vector3 slope = Vector3.zero;
            for (int axis = 0; axis < 2; axis++)
            {
                int step = axis == 0 ? 1 : n1;
                int coordinate = axis == 0 ? p : q;
                int before = coordinate > 0 && F.waterWet[vi - step] ? vi - step : vi;
                int after = coordinate < G.N && F.waterWet[vi + step] ? vi + step : vi;
                if (before == after) continue;
                Vector3 along = (G.VertDir[f * vpf + after] - G.VertDir[f * vpf + before]) * R;
                float length = along.magnitude;
                if (length < 1e-4f) continue;
                slope += along * ((F.waterLevel[after] - F.waterLevel[before]) / (length * length));
            }
            return slope;
        }

        // ---------------------------------------------------------------- rivières
        readonly List<Vector3> riverVerts = new List<Vector3>(), riverFlows = new List<Vector3>(), riverSlopes = new List<Vector3>();
        readonly List<Color32> riverColors = new List<Color32>();
        readonly List<Vector4> riverData = new List<Vector4>();
        readonly List<int> riverTriangles = new List<int>();

        // Cellule dont la nappe recouvre le terrain affiché : son niveau dépasse le sol de ses quatre voisines.
        // Au fond d'un chenal d'une seule cellule, au contraire, le terrain lissé cache la nappe.
        bool Submerged(int c)
        {
            if (!ShowsWater(c)) return false;
            float surface = S.Ground(c) + S.water[c];
            for (int k = 0; k < 4; k++)
            {
                int n = G.Nbr[c * 4 + k];
                if (n >= 0 && S.Ground(n) > surface) return false;
            }
            return true;
        }

        bool IsRiver(int c)
        {
            return S.Ground(c) > S.SeaLevel && S.flux[c] >= RiverMinFlux && S.temp[c] > -2f && !Submerged(c);
        }

        // Cellule vers laquelle part l'essentiel de l'eau de c (-1 si rien ne s'écoule).
        int Downstream(int c)
        {
            int best = -1; float most = 0f;
            for (int k = 0; k < 4; k++)
            {
                float o = S.outW[c * 4 + k];
                if (o > most && G.Nbr[c * 4 + k] >= 0) { most = o; best = G.Nbr[c * 4 + k]; }
            }
            return best;
        }

        float RiverWidth(int c)
        {
            return G.CellSize * Mathf.Clamp(0.08f + 0.2f * Mathf.Sqrt(S.flux[c]), 0.1f, 0.38f);
        }

        static int FaceOf(Vector3 direction)
        {
            float ax = Mathf.Abs(direction.x), ay = Mathf.Abs(direction.y), az = Mathf.Abs(direction.z);
            if (ax >= ay && ax >= az) return direction.x > 0 ? 0 : 1;
            if (ay >= az) return direction.y > 0 ? 2 : 3;
            return direction.z > 0 ? 4 : 5;
        }

        // Rayon, dans la direction donnée, d'un maillage de face de k x k quads dont "grid" donne les sommets ;
        // (i, j) reçoit le quad traversé. Même découpage en deux triangles que BuildGridMesh : (a, b, c) et (b, d, c).
        static float SampleGrid(Vector3[] grid, int k, int f, Vector3 direction, out int i, out int j)
        {
            int k1 = k + 1;
            float dn = Vector3.Dot(direction, PlanetGrid.FaceN[f]);
            float fu = (Vector3.Dot(direction, PlanetGrid.FaceA[f]) / dn + 1f) * 0.5f * k;
            float fv = (Vector3.Dot(direction, PlanetGrid.FaceB[f]) / dn + 1f) * 0.5f * k;
            i = Mathf.Clamp((int)fu, 0, k - 1); j = Mathf.Clamp((int)fv, 0, k - 1);
            float tu = Mathf.Clamp01(fu - i), tv = Mathf.Clamp01(fv - j);
            int a = j * k1 + i;
            float ha = grid[a].magnitude, hb = grid[a + 1].magnitude, hc = grid[a + k1].magnitude, hd = grid[a + k1 + 1].magnitude;
            return tu + tv <= 1f ? ha + (hb - ha) * tu + (hc - ha) * tv : hd + (hc - hd) * (1f - tu) + (hb - hd) * (1f - tv);
        }

        // Hauteur du terrain affiché dans la direction donnée (maillage fin compris), au-dessus du rayon.
        float TerrainHeight(Vector3 direction)
        {
            int f = FaceOf(direction), i, j;
            var F = faces[f];
            return SampleGrid(MeshDetail > 1 ? F.fineVerts : F.verts, G.N * MeshDetail, f, direction, out i, out j) - R;
        }

        // La nappe (mer, lac) passe-t-elle au-dessus de ce point de ruban ? Lu sur la nappe réellement dessinée :
        // au fond d'un chenal étroit, le terrain lissé la cache et le ruban doit rester visible.
        bool UnderNappe(Vector3 direction, float height)
        {
            int f = FaceOf(direction), i, j;
            var F = faces[f];
            float level = SampleGrid(F.waterVerts, G.N, f, direction, out i, out j) - R;
            int local = j * G.N + i;
            if (!ShowsWater(f * G.N * G.N + local) && float.IsNaN(F.shoreLevel[local])) return false;
            return level > height + 0.5f;
        }

        // Chaque cellule de rivière relie le milieu de son arête amont au milieu de son arête aval par une courbe
        // passant près de son centre : les tronçons s'enchaînent sans angle et les affluents se rejoignent.
        void RefreshRiverFace(int f)
        {
            var F = faces[f];
            riverVerts.Clear(); riverFlows.Clear(); riverSlopes.Clear(); riverColors.Clear(); riverData.Clear(); riverTriangles.Clear();
            int cells = G.N * G.N, offset = f * cells;
            for (int local = 0; local < cells; local++)
            {
                int c = offset + local;
                if (!IsRiver(c)) continue;
                int d = Downstream(c);
                if (d < 0) continue;
                float width = RiverWidth(c);
                bool onward = IsRiver(d);
                // L'aval est une rivière : on s'arrête au milieu de l'arête. Sinon (lac, mer) on y plonge.
                Vector3 exit = onward ? (G.Dir[c] + G.Dir[d]).normalized : Vector3.Slerp(G.Dir[c], G.Dir[d], 0.8f);
                float exitWidth = onward ? 0.5f * (width + RiverWidth(d)) : width * 1.3f;
                bool fed = false;
                for (int k = 0; k < 4; k++)
                {
                    int u = G.Nbr[c * 4 + k];
                    if (u < 0 || !IsRiver(u) || Downstream(u) != c) continue;
                    fed = true;
                    AddRiverCurve(c, (G.Dir[u] + G.Dir[c]).normalized, 0.5f * (RiverWidth(u) + width), exit, exitWidth);
                }
                // Source : le ruban naît en pointe au centre de la cellule.
                if (!fed) AddRiverCurve(c, G.Dir[c], 0f, exit, exitWidth);
            }
            F.riverMesh.Clear();
            F.riverMesh.SetVertices(riverVerts);
            F.riverMesh.SetNormals(customOceanMaterial ? riverFlows : riverSlopes);
            F.riverMesh.SetColors(riverColors);
            F.riverMesh.SetUVs(0, riverData);
            F.riverMesh.SetUVs(1, riverSlopes);
            F.riverMesh.SetTriangles(riverTriangles, 0, false);
            F.riverMesh.bounds = new Bounds(Vector3.zero, Vector3.one * R * 2.4f);
        }

        void AddRiverCurve(int c, Vector3 from, float fromWidth, Vector3 to, float toWidth)
        {
            Vector3 center = G.Dir[c];
            float speed = Mathf.Clamp01(S.flux[c]);
            float sediment = Mathf.Clamp01(S.susp[c] / Mathf.Max(S.water[c], 0.01f));
            int first = riverVerts.Count;
            for (int s = 0; s <= RiverSegments; s++)
            {
                float t = (float)s / RiverSegments, it = 1f - t;
                Vector3 point = (from * (it * it) + center * (2f * it * t) + to * (t * t)).normalized;
                Vector3 tangent = Vector3.ProjectOnPlane((center - from) * it + (to - center) * t, point).normalized;
                Vector3 across = Vector3.Cross(point, tangent) * (0.5f * Mathf.Lerp(fromWidth, toWidth, t) / R);
                for (int side = -1; side <= 1; side++)
                {
                    Vector3 direction = (point + across * side).normalized;
                    float height = TerrainHeight(direction) + RiverLift;
                    riverVerts.Add(direction * (R + height));
                    riverFlows.Add(tangent);
                    riverSlopes.Add(Vector3.zero);
                    riverColors.Add(new Color32(20, 0, 255, 0));
                    // Profondeur négative sur les bords, et sous une nappe où le ruban ferait double emploi :
                    // le shader y fond puis efface le ruban.
                    riverData.Add(new Vector4(speed, sediment, UnderNappe(direction, height) ? -1f : side == 0 ? 4f : -0.15f, 0f));
                }
                if (s == 0) continue;
                int row = first + s * 3, previous = row - 3;
                for (int side = 0; side < 2; side++)
                {
                    riverTriangles.Add(previous + side); riverTriangles.Add(row + side); riverTriangles.Add(previous + side + 1);
                    riverTriangles.Add(previous + side + 1); riverTriangles.Add(row + side); riverTriangles.Add(row + side + 1);
                }
            }
        }
    }
}
