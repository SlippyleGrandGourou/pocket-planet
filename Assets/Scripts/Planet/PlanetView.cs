using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LivingPlanet
{
    public sealed partial class PlanetView
    {
        sealed class Face
        {
            public Mesh mesh;
            public Vector3[] verts, norms;
            public Texture2D tex;
            public Color32[] col;
            // Maillage effectivement affiché : identique à "mesh" quand MeshDetail == 1 (coût inchangé), ou une
            // version subdivisée purement visuelle quand MeshDetail > 1 (voir RefreshDetailMesh).
            public Mesh renderMesh;
            public Vector3[] fineVerts, fineNorms;
            public Mesh waterMesh, riverMesh;
            public MeshRenderer waterR, riverR;
            public Vector3[] waterVerts;
            public Vector3[] waterFlows, waterSlopes;
            public Color32[] waterColors;
            public Vector4[] waterData;
            // Niveau de l'eau par sommet (NaN hors de l'eau), sommets touchant une cellule en eau, et niveau
            // prolongé sous les cellules de berge (NaN ailleurs) : voir RefreshWaterFace.
            public float[] waterLevel, shoreLevel;
            public bool[] waterWet;
            public readonly List<int> waterTriangles = new List<int>();
            public bool dirty = true;
        }

        sealed class Bucket
        {
            public Mesh mesh;
            public Material mat;
            public Matrix4x4[] m = new Matrix4x4[1024];
            public int n;
            public void Add(Matrix4x4 x)
            {
                if (n == m.Length) Array.Resize(ref m, n * 2);
                m[n++] = x;
            }
        }

        const int TRUNK = 0, BROAD_G1 = 1, BROAD_G2 = 2, BROAD_Y = 3, BROAD_O = 4, CONIF = 5, CONIF_S = 6,
                  BUSH = 7, BUSH_DRY = 8, BUSH_WIN = 9, SNAG = 10, BURNT = 11, FLAME = 12,
                  PALM_T = 13, PALM_L = 14, CACTUS = 15, BERRY = 16, BERRY_F = 17, REED = 18, KELP = 19, BUCKETS = 20;

        // Facteur de subdivision purement visuel du maillage de terrain (découplé de la grille de simulation) :
        // chaque quad de la grille de simulation est affiché comme MeshDetail x MeshDetail quads plus fins, dont
        // la hauteur est interpolée entre les sommets déjà calculés (+ un léger relief procédural), sans ajouter
        // la moindre cellule ni le moindre calcul physique. 1 = désactivé (comportement d'origine, coût nul).
        public const int MeshDetail = 2;

        readonly PlanetGrid G;
        readonly PlanetSim S;
        readonly Ecosystem E;
        readonly Fauna Fa;
        readonly float R;
        // Distance (depuis la caméra) des éléments du ciel (étoiles/soleil/lune), qui suivent toujours la caméra pour
        // paraître infiniment loin. Elle doit rester au-delà de l'altitude de zoom arrière maximale permise pour ce
        // rayon de planète ; sinon ces éléments, plus proches de la caméra que le sol, "traversent" la planète.
        readonly float SkyDist;

        public int ViewMode;
        public float DrawDistance = 420f;
        public bool ShowPlants = true;
        public bool ShowAtmosphere = true;
        public bool HideWater = false;
        public float DaysPerYear = PlanetWorld.DaysPerYear;
        float dayAngle = 1.0f, visDecl, skyCloud;
        bool declInit;
        bool hadTsunami;
        int lastTick = -1;
        Vector3 lastCamPos;

        GameObject root;
        readonly Face[] faces = new Face[6];
        Bucket[] buckets;
        Light sun;
        Camera cam;
        Transform starsT, sunDiscT, moonT;
        float moonAngle = 2.4f;
        public const float MoonPeriodDays = 3f;
        public float vDays, EclipseAmount;
        MeshRenderer moonR;
        public Vector3 MoonDir = Vector3.left;
        MeshRenderer starsR, sunDiscR;
        Material waterMaterial;
        float waterVisualTime;
        bool customOceanMaterial;
        ParticleSystem precip;
        ParticleSystemRenderer precipR;
        Transform precipT;
        bool precipSnow;
        float precipSmooth;
        int nextFace, nextWaterFace;
        float faceTimer, waterFaceTimer, plantTimer;
        public Vector3 SunDir = Vector3.right;
        public float Atmo, DayFactor;

        // atmosphère / nuages / rivières / faune / tsunami
        readonly float AtmoThickness;
        // Part de l'intensité du ciel conservée quand la planète est vue de l'espace.
        const float OrbitAtmoGain = 0.12f;
        bool atmoShader;
        GameObject atmoIn, atmoOut, atmoFallback;
        float atmoShellRadius;
        float cloudTimer;
        Bucket icebergB;
        Bucket[] faunaB;
        System.Random vr = new System.Random(5);

        public PlanetView(PlanetWorld w, Camera camera)
        {
            G = w.Grid; S = w.Sim; E = w.Eco; Fa = w.Fauna; R = G.Radius; cam = camera; HeightScale = Mathf.Clamp(R / 1200f, 0.6f, 2f); AtmoThickness = 300f * HeightScale;
            // Le point de la planète le plus éloigné que la caméra puisse voir est son horizon au zoom arrière
            // maximal : étoiles, soleil et lune doivent rester au-delà, sinon ils se dessinent devant le bord
            // du disque planétaire (c'était le cas dès un rayon de 2000).
            float farthestCamera = R + w.MaxCamAlt;
            float farthestHorizon = Mathf.Sqrt(farthestCamera * farthestCamera - R * R);
            SkyDist = Mathf.Max(5000f, farthestHorizon + 1500f);
        }

        public void MarkDirty() { for (int f = 0; f < 6; f++) faces[f].dirty = true; }

        // Remplace l'ancien mode "eau transparente" (qui laissait voir le fond sous l'eau profonde) : l'eau
        // garde désormais toujours son rendu opaque (voir Ocean.shader), et cette option se contente de
        // masquer entièrement son rendu sans toucher à la simulation (Sim.water[] continue d'exister/s'écouler).
        public void SetWaterHidden(bool hidden)
        {
            HideWater = hidden;
            for (int f = 0; f < 6; f++)
            {
                if (faces[f] == null || faces[f].waterR == null) continue;
                faces[f].waterR.enabled = !hidden;
                faces[f].riverR.enabled = !hidden;
            }
        }

        public float SaveDayAngle() { return dayAngle; }
        public void RestoreClock(float days, float angle)
        {
            vDays = days;
            dayAngle = angle;
            visDecl = S.AxialTilt * Mathf.Sin(2f * Mathf.PI * days / DaysPerYear);
            declInit = true;
        }

        // ---------------------------------------------------------------- materials
        static Shader LitShader()
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) sh = Shader.Find("Standard");
            return sh;
        }

        static Material MakeLit(Color c, float smooth, bool instancing)
        {
            var m = new Material(LitShader());
            m.SetColor("_BaseColor", c);
            m.SetColor("_Color", c);
            m.SetFloat("_Smoothness", smooth);
            m.SetFloat("_Metallic", 0f);
            m.enableInstancing = instancing;
            return m;
        }

        static void MakeTransparent(Material m, bool twoSided)
        {
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            if (twoSided) m.SetFloat("_Cull", 0f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
            m.SetShaderPassEnabled("ShadowCaster", false);
        }

        static Material MakeUnlit(Color c)
        {
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            var m = new Material(sh);
            m.SetColor("_BaseColor", c);
            m.SetColor("_Color", c);
            m.enableInstancing = true;
            return m;
        }

        // ---------------------------------------------------------------- mesh builders
        static Mesh BuildGridMesh(int res, float radius, int onlyFace)
        {
            int n1 = res + 1;
            int faceCount = onlyFace >= 0 ? 1 : 6;
            var verts = new Vector3[faceCount * n1 * n1];
            var uvs = new Vector2[verts.Length];
            var tris = new int[faceCount * res * res * 6];
            int vo = 0, to = 0;
            for (int fi = 0; fi < faceCount; fi++)
            {
                int f = onlyFace >= 0 ? onlyFace : fi;
                for (int j = 0; j <= res; j++)
                    for (int i = 0; i <= res; i++)
                    {
                        verts[vo + j * n1 + i] = PlanetGrid.FacePoint(f, 2f * i / res - 1f, 2f * j / res - 1f) * radius;
                        uvs[vo + j * n1 + i] = new Vector2((float)i / res, (float)j / res);
                    }
                for (int j = 0; j < res; j++)
                    for (int i = 0; i < res; i++)
                    {
                        int a = vo + j * n1 + i, b = a + 1, c = a + n1, d = c + 1;
                        tris[to++] = a; tris[to++] = b; tris[to++] = c;
                        tris[to++] = b; tris[to++] = d; tris[to++] = c;
                    }
                vo += n1 * n1;
            }
            var mesh = new Mesh();
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris;
            if (onlyFace < 0) mesh.RecalculateNormals();
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * radius * 2.4f);
            return mesh;
        }

        sealed class MeshBuilder
        {
            readonly List<Vector3> v = new List<Vector3>();
            readonly List<int> t = new List<int>();

            void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 center)
            {
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), (a + b + c) / 3f - center) < 0f) { var x = b; b = c; c = x; }
                int i = v.Count;
                v.Add(a); v.Add(b); v.Add(c);
                t.Add(i); t.Add(i + 1); t.Add(i + 2);
            }

            public void Cylinder(float y0, float r0, float r1, float h, int sides)
            {
                for (int i = 0; i < sides; i++)
                {
                    float a0 = i * Mathf.PI * 2f / sides, a1 = (i + 1) * Mathf.PI * 2f / sides;
                    Vector3 b0 = new Vector3(Mathf.Cos(a0) * r0, y0, Mathf.Sin(a0) * r0);
                    Vector3 b1 = new Vector3(Mathf.Cos(a1) * r0, y0, Mathf.Sin(a1) * r0);
                    Vector3 t0 = new Vector3(Mathf.Cos(a0) * r1, y0 + h, Mathf.Sin(a0) * r1);
                    Vector3 t1 = new Vector3(Mathf.Cos(a1) * r1, y0 + h, Mathf.Sin(a1) * r1);
                    Vector3 ctr = new Vector3(0, y0 + h * 0.5f, 0);
                    Tri(b0, b1, t0, ctr);
                    Tri(b1, t1, t0, ctr);
                }
            }

            public void Cone(float y0, float r, float h, int sides)
            {
                Vector3 apex = new Vector3(0, y0 + h, 0);
                Vector3 ctr = new Vector3(0, y0 + h * 0.3f, 0);
                for (int i = 0; i < sides; i++)
                {
                    float a0 = i * Mathf.PI * 2f / sides, a1 = (i + 1) * Mathf.PI * 2f / sides;
                    Vector3 b0 = new Vector3(Mathf.Cos(a0) * r, y0, Mathf.Sin(a0) * r);
                    Vector3 b1 = new Vector3(Mathf.Cos(a1) * r, y0, Mathf.Sin(a1) * r);
                    Tri(b0, b1, apex, ctr);
                    Tri(b0, b1, new Vector3(0, y0, 0), new Vector3(0, y0 + h, 0));
                }
            }

            public void Sphere(Vector3 center, float r, float yScale, int lat, int lon)
            {
                for (int i = 0; i < lat; i++)
                {
                    float p0 = Mathf.PI * i / lat, p1 = Mathf.PI * (i + 1) / lat;
                    for (int j = 0; j < lon; j++)
                    {
                        float a0 = Mathf.PI * 2f * j / lon, a1 = Mathf.PI * 2f * (j + 1) / lon;
                        Vector3 P(float p, float a) { return center + new Vector3(Mathf.Sin(p) * Mathf.Cos(a) * r, Mathf.Cos(p) * r * yScale, Mathf.Sin(p) * Mathf.Sin(a) * r); }
                        Tri(P(p0, a0), P(p0, a1), P(p1, a0), center);
                        Tri(P(p0, a1), P(p1, a1), P(p1, a0), center);
                    }
                }
            }

            public void Box(Vector3 c, Vector3 s)
            {
                Vector3 h = s * 0.5f;
                for (int ax = 0; ax < 3; ax++)
                    for (int sg = -1; sg <= 1; sg += 2)
                    {
                        int a1 = (ax + 1) % 3, a2 = (ax + 2) % 3;
                        Vector3 n = Vector3.zero; n[ax] = sg * h[ax];
                        Vector3 u = Vector3.zero; u[a1] = h[a1];
                        Vector3 w = Vector3.zero; w[a2] = h[a2];
                        Vector3 p0 = c + n - u - w, p1 = c + n + u - w, p2 = c + n + u + w, p3 = c + n - u + w;
                        Tri(p0, p1, p2, c); Tri(p0, p2, p3, c);
                    }
            }

            public Mesh Build()
            {
                var m = new Mesh();
                m.SetVertices(v);
                m.SetTriangles(t, 0);
                m.RecalculateNormals();
                m.RecalculateBounds();
                return m;
            }
        }

        // ---------------------------------------------------------------- build
        public void Build()
        {
            root = new GameObject("PlanetView");
            int N = G.N;

            for (int f = 0; f < 6; f++)
            {
                var F = new Face();
                F.mesh = BuildGridMesh(N, 1f, f);
                F.mesh.bounds = new Bounds(Vector3.zero, Vector3.one * R * 2.4f);
                int vpf = G.VertsPerFace;
                F.verts = new Vector3[vpf];
                F.norms = new Vector3[vpf];
                F.col = new Color32[N * N];
                F.tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                if (MeshDetail > 1)
                {
                    F.renderMesh = BuildGridMesh(N * MeshDetail, 1f, f);
                    F.renderMesh.bounds = F.mesh.bounds;
                    int rvpf = (N * MeshDetail + 1) * (N * MeshDetail + 1);
                    F.fineVerts = new Vector3[rvpf];
                    F.fineNorms = new Vector3[rvpf];
                }
                else F.renderMesh = F.mesh;
                faces[f] = F;

                var go = new GameObject("Terrain" + f);
                go.transform.SetParent(root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = F.renderMesh;
                var mat = MakeLit(Color.white, 0.04f, false);
                mat.SetTexture("_BaseMap", F.tex);
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
            }

            Material om;
            var osh = Resources.Load<Shader>("LivingPlanet/Ocean");
            customOceanMaterial = osh != null && osh.isSupported;
            if (customOceanMaterial) om = new Material(osh);
            else
            {
                om = MakeLit(new Color(0.04f, 0.2f, 0.4f, 1f), 0.9f, false);
            }
            waterMaterial = om;

            for (int f = 0; f < 6; f++) BuildWaterFace(f, om);

            BuildAtmosphere();
            BuildClouds();
            BuildPlantBuckets();
            BuildFauna();
            BuildSky();
            BuildPrecip();
            SetupLight();

            for (int f = 0; f < 6; f++) RefreshFace(f);
        }

        void BuildAtmosphere()
        {
            var sh = Resources.Load<Shader>("LivingPlanet/Atmosphere");
            if (sh == null) sh = Shader.Find("LivingPlanet/Atmosphere");
            atmoShader = sh != null && sh.isSupported;
            float ra = R + AtmoThickness;
            if (!atmoShader)
            {
                // repli : coque simple translucide
                var fb = atmoFallback = new GameObject("Atmosphere");
                fb.transform.SetParent(root.transform, false);
                var mesh0 = BuildGridMesh(48, ra, -1);
                fb.AddComponent<MeshFilter>().sharedMesh = mesh0;
                var am = MakeUnlit(new Color(0.35f, 0.6f, 1f, 0.12f));
                MakeTransparent(am, false);
                var amr = fb.AddComponent<MeshRenderer>();
                amr.sharedMaterial = am;
                amr.shadowCastingMode = ShadowCastingMode.Off;
                return;
            }
            atmoShellRadius = ra * 1.04f;
            var mesh = BuildGridMesh(64, atmoShellRadius, -1);
            GameObject Make(string name, float cull)
            {
                var go = new GameObject(name);
                go.transform.SetParent(root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var m = new Material(sh);
                m.SetFloat("_Cull", cull);
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = m;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                return go;
            }
            atmoIn = Make("AtmoSky", 1f);
            atmoOut = Make("AtmoHalo", 2f);
            Shader.SetGlobalVector("_LP_Planet", new Vector4(R, ra, 45f, 0f));
        }

        void BuildPlantBuckets()
        {
            var trunk = new MeshBuilder(); trunk.Cylinder(0f, 0.17f, 0.1f, 1.5f, 6);
            var broad = new MeshBuilder(); broad.Sphere(new Vector3(0, 2.0f, 0), 1.0f, 0.85f, 5, 7);
            var conif = new MeshBuilder();
            conif.Cone(0.6f, 1.05f, 1.3f, 7); conif.Cone(1.4f, 0.8f, 1.2f, 7); conif.Cone(2.15f, 0.55f, 1.05f, 7);
            var bush = new MeshBuilder(); bush.Sphere(new Vector3(0, 0.4f, 0), 0.65f, 0.75f, 4, 6);
            Mesh mT = trunk.Build(), mB = broad.Build(), mC = conif.Build(), mS = bush.Build();

            Material L(float r, float g, float b) { return MakeLit(new Color(r, g, b), 0.05f, true); }
            var flame = L(1f, 0.4f, 0.05f);
            flame.EnableKeyword("_EMISSION");
            flame.SetColor("_EmissionColor", new Color(2.5f, 0.8f, 0.1f));

            buckets = new Bucket[BUCKETS];
            Func<Mesh, Material, Bucket> mk = (m, mat) => new Bucket { mesh = m, mat = mat };
            buckets[TRUNK] = mk(mT, L(0.33f, 0.21f, 0.12f));
            buckets[BROAD_G1] = mk(mB, L(0.2f, 0.48f, 0.14f));
            buckets[BROAD_G2] = mk(mB, L(0.3f, 0.55f, 0.18f));
            buckets[BROAD_Y] = mk(mB, L(0.66f, 0.65f, 0.18f));
            buckets[BROAD_O] = mk(mB, L(0.78f, 0.36f, 0.1f));
            buckets[CONIF] = mk(mC, L(0.07f, 0.28f, 0.13f));
            buckets[CONIF_S] = mk(mC, L(0.86f, 0.92f, 0.94f));
            buckets[BUSH] = mk(mS, L(0.34f, 0.5f, 0.2f));
            buckets[BUSH_DRY] = mk(mS, L(0.62f, 0.52f, 0.28f));
            buckets[BUSH_WIN] = mk(mS, L(0.4f, 0.3f, 0.2f));
            buckets[SNAG] = mk(mT, L(0.42f, 0.4f, 0.37f));
            buckets[BURNT] = mk(mT, L(0.05f, 0.045f, 0.045f));
            buckets[FLAME] = mk(mB, flame);

            var palmT = new MeshBuilder(); palmT.Cylinder(0f, 0.14f, 0.08f, 2.6f, 5);
            var palmL = new MeshBuilder();
            for (int i = 0; i < 4; i++)
            {
                float a = i * Mathf.PI * 0.5f;
                palmL.Box(new Vector3(Mathf.Cos(a) * 0.8f, 2.6f, Mathf.Sin(a) * 0.8f), new Vector3(Mathf.Abs(Mathf.Cos(a)) * 1.4f + 0.3f, 0.12f, Mathf.Abs(Mathf.Sin(a)) * 1.4f + 0.3f));
            }
            var cactus = new MeshBuilder(); cactus.Box(new Vector3(0, 0.6f, 0), new Vector3(0.4f, 1.2f, 0.4f)); cactus.Box(new Vector3(0.35f, 0.7f, 0), new Vector3(0.3f, 0.12f, 0.12f)); cactus.Box(new Vector3(0.5f, 0.9f, 0), new Vector3(0.12f, 0.45f, 0.12f));
            var berry = new MeshBuilder(); berry.Sphere(new Vector3(0, 0.35f, 0), 0.55f, 0.7f, 4, 6);
            var fruit = new MeshBuilder();
            for (int i = 0; i < 4; i++) { float a = i * 1.7f; fruit.Box(new Vector3(Mathf.Cos(a) * 0.45f, 0.45f + 0.1f * i, Mathf.Sin(a) * 0.45f), Vector3.one * 0.14f); }
            var reed = new MeshBuilder();
            for (int i = 0; i < 5; i++) { float a = i * 1.3f; reed.Box(new Vector3(Mathf.Cos(a) * 0.25f, 0.55f, Mathf.Sin(a) * 0.25f), new Vector3(0.06f, 1.1f, 0.06f)); }
            var kelp = new MeshBuilder();
            for (int i = 0; i < 3; i++) { float a = i * 2.1f; kelp.Box(new Vector3(Mathf.Cos(a) * 0.2f, 0.9f, Mathf.Sin(a) * 0.2f), new Vector3(0.1f, 1.8f, 0.1f)); }
            buckets[PALM_T] = mk(palmT.Build(), L(0.45f, 0.32f, 0.18f));
            buckets[PALM_L] = mk(palmL.Build(), L(0.25f, 0.55f, 0.15f));
            buckets[CACTUS] = mk(cactus.Build(), L(0.25f, 0.5f, 0.25f));
            buckets[BERRY] = mk(berry.Build(), L(0.24f, 0.4f, 0.2f));
            buckets[BERRY_F] = mk(fruit.Build(), L(0.8f, 0.1f, 0.2f));
            buckets[REED] = mk(reed.Build(), L(0.55f, 0.62f, 0.25f));
            buckets[KELP] = mk(kelp.Build(), L(0.12f, 0.45f, 0.25f));
        }

        void BuildFauna()
        {
            faunaB = new Bucket[Fauna.Count];
            Material L(float r, float g, float b) { return MakeLit(new Color(r, g, b), 0.1f, true); }
            MeshBuilder B() { return new MeshBuilder(); }
            var rb = B(); rb.Box(new Vector3(0, 0.3f, 0), new Vector3(0.3f, 0.3f, 0.5f)); rb.Box(new Vector3(0, 0.45f, 0.3f), new Vector3(0.2f, 0.2f, 0.2f)); rb.Box(new Vector3(0, 0.7f, 0.28f), new Vector3(0.08f, 0.3f, 0.06f));
            var db = B(); db.Box(new Vector3(0, 0.9f, 0), new Vector3(0.5f, 0.5f, 1.2f)); db.Box(new Vector3(0, 1.4f, 0.7f), new Vector3(0.3f, 0.5f, 0.3f)); db.Box(new Vector3(0, 0.35f, 0), new Vector3(0.4f, 0.7f, 0.9f)); db.Box(new Vector3(0, 1.8f, 0.7f), new Vector3(0.7f, 0.1f, 0.1f));
            var wb = B(); wb.Box(new Vector3(0, 0.7f, 0), new Vector3(0.4f, 0.45f, 1.1f)); wb.Box(new Vector3(0, 0.85f, 0.7f), new Vector3(0.3f, 0.3f, 0.35f)); wb.Box(new Vector3(0, 0.3f, 0), new Vector3(0.3f, 0.6f, 0.8f));
            var bb = B(); bb.Box(new Vector3(0, 0, 0), new Vector3(0.3f, 0.3f, 0.6f)); bb.Box(new Vector3(0, 0, 0), new Vector3(1.8f, 0.06f, 0.4f));
            var fb = B(); fb.Box(new Vector3(0, 0, 0), new Vector3(0.18f, 0.3f, 0.7f)); fb.Box(new Vector3(0, 0, -0.45f), new Vector3(0.05f, 0.4f, 0.2f));
            var sb = B(); sb.Box(new Vector3(0, 0, 0), new Vector3(0.8f, 0.7f, 2.6f)); sb.Box(new Vector3(0, 0.6f, -0.1f), new Vector3(0.08f, 0.6f, 0.6f)); sb.Box(new Vector3(0, 0, -1.5f), new Vector3(0.1f, 1f, 0.4f));
            var fxb = B(); fxb.Box(new Vector3(0, 0.45f, 0), new Vector3(0.3f, 0.32f, 0.8f)); fxb.Box(new Vector3(0, 0.55f, 0.5f), new Vector3(0.22f, 0.22f, 0.3f)); fxb.Box(new Vector3(0, 0.45f, -0.55f), new Vector3(0.12f, 0.12f, 0.5f)); fxb.Box(new Vector3(0, 0.2f, 0), new Vector3(0.22f, 0.4f, 0.6f));
            var rpb = B(); rpb.Box(new Vector3(0, 0, 0), new Vector3(0.35f, 0.35f, 0.8f)); rpb.Box(new Vector3(0, 0, 0), new Vector3(3f, 0.07f, 0.6f)); rpb.Box(new Vector3(0, 0, -0.55f), new Vector3(0.3f, 0.05f, 0.4f));
            var whb = B(); whb.Box(new Vector3(0, 0, 0), new Vector3(1.6f, 1.4f, 5.5f)); whb.Box(new Vector3(0, 0, -3f), new Vector3(2.2f, 0.2f, 0.9f)); whb.Box(new Vector3(0, -0.5f, 0.5f), new Vector3(0.2f, 0.1f, 1.6f));
            Bucket K(MeshBuilder m, Material mat) { return new Bucket { mesh = m.Build(), mat = mat }; }
            faunaB[Fauna.Fox] = K(fxb, L(0.85f, 0.4f, 0.12f));
            faunaB[Fauna.Raptor] = K(rpb, L(0.35f, 0.25f, 0.15f));
            faunaB[Fauna.Whale] = K(whb, L(0.15f, 0.25f, 0.4f));
            faunaB[Fauna.Rabbit] = K(rb, L(0.82f, 0.78f, 0.7f));
            faunaB[Fauna.Deer] = K(db, L(0.55f, 0.35f, 0.18f));
            faunaB[Fauna.Wolf] = K(wb, L(0.32f, 0.32f, 0.36f));
            faunaB[Fauna.Bird] = K(bb, L(0.9f, 0.25f, 0.2f));
            faunaB[Fauna.Fish] = K(fb, L(1f, 0.6f, 0.15f));
            faunaB[Fauna.Shark] = K(sb, L(0.4f, 0.47f, 0.55f));
            // Icebergs à la dérive : un bloc anguleux simple (quelques boîtes qui dépassent), dont la majeure
            // partie "immergée" reste sous la ligne de flottaison comme un vrai iceberg.
            var icb = B(); icb.Box(new Vector3(0, -0.6f, 0), new Vector3(2.2f, 1.6f, 2.4f)); icb.Box(new Vector3(0.4f, 0.35f, -0.2f), new Vector3(1.1f, 0.9f, 1.3f)); icb.Box(new Vector3(-0.5f, 0.15f, 0.4f), new Vector3(0.7f, 0.5f, 0.8f));
            icebergB = new Bucket { mesh = icb.Build(), mat = L(0.82f, 0.92f, 0.98f) };
            icebergB.mat.enableInstancing = true;
        }

        void BuildSky()
        {
            var rnd = new System.Random(99);
            int count = 2500;
            var pts = new Vector3[count];
            var idx = new int[count];
            for (int i = 0; i < count; i++)
            {
                Vector3 d;
                do { d = new Vector3((float)rnd.NextDouble() * 2 - 1, (float)rnd.NextDouble() * 2 - 1, (float)rnd.NextDouble() * 2 - 1); }
                while (d.sqrMagnitude > 1f || d.sqrMagnitude < 0.05f);
                pts[i] = d.normalized * SkyDist;
                idx[i] = i;
            }
            var m = new Mesh();
            m.indexFormat = IndexFormat.UInt32;
            m.vertices = pts;
            m.SetIndices(idx, MeshTopology.Points, 0);
            m.bounds = new Bounds(Vector3.zero, Vector3.one * (SkyDist * 2.4f));
            var go = new GameObject("Stars");
            go.AddComponent<MeshFilter>().sharedMesh = m;
            starsR = go.AddComponent<MeshRenderer>();
            starsR.sharedMaterial = MakeUnlit(Color.white);
            starsR.shadowCastingMode = ShadowCastingMode.Off;
            starsT = go.transform;

            var sunGo = new GameObject("SunDisc");
            var sb = new MeshBuilder();
            sb.Sphere(Vector3.zero, 1f, 1f, 8, 12);
            sunGo.AddComponent<MeshFilter>().sharedMesh = sb.Build();
            sunDiscR = sunGo.AddComponent<MeshRenderer>();
            var sr = sunDiscR;
            sr.sharedMaterial = MakeUnlit(new Color(1f, 0.95f, 0.75f));
            sr.shadowCastingMode = ShadowCastingMode.Off;
            // Taille proportionnelle à la distance : le disque garde la même taille apparente quel que soit le rayon.
            sunGo.transform.localScale = Vector3.one * (110f * (SkyDist - 500f) / 4500f);
            sunDiscT = sunGo.transform;

            // la lune est éclairée par la lumière du soleil : ses phases viennent de leur position relative
            var moonGo = new GameObject("Moon");
            var mm = new MeshBuilder();
            mm.Sphere(Vector3.zero, 1f, 1f, 8, 14);
            moonGo.AddComponent<MeshFilter>().sharedMesh = mm.Build();
            var mr = moonGo.AddComponent<MeshRenderer>();
            moonR = mr;
            mr.sharedMaterial = MakeLit(new Color(0.78f, 0.78f, 0.8f), 0f, false);
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            moonGo.transform.localScale = Vector3.one * (60f * (SkyDist - 700f) / 4300f);
            moonT = moonGo.transform;
        }

        void BuildPrecip()
        {
            var go = new GameObject("Precipitation");
            precipT = go.transform;
            precip = go.AddComponent<ParticleSystem>();
            precip.Stop();
            var main = precip.main;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = 1.3f;
            main.startSpeed = 30f;
            main.startSize = 0.06f;
            main.startColor = new Color(0.75f, 0.85f, 1f, 0.55f);
            main.maxParticles = 8000;
            main.gravityModifier = 0f;
            var em = precip.emission;
            em.rateOverTime = 0f;
            var sh = precip.shape;
            sh.shapeType = ParticleSystemShapeType.Box;
            sh.scale = new Vector3(70f, 70f, 1f);
            precipR = go.GetComponent<ParticleSystemRenderer>();
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            var mat = new Material(shader);
            mat.SetColor("_BaseColor", Color.white);
            MakeTransparent(mat, true);
            // Après les nuages (Transparent+10), qui écrivent leur profondeur : sinon ils recouvrent la pluie
            // qui tombe devant eux.
            mat.renderQueue = (int)RenderQueue.Transparent + 20;
            precipR.sharedMaterial = mat;
            precipR.renderMode = ParticleSystemRenderMode.Stretch;
            precipR.lengthScale = 2.5f;
            precipR.velocityScale = 0.02f;
            precipR.shadowCastingMode = ShadowCastingMode.Off;
            precip.Play();
        }

        void SetupLight()
        {
            sun = null;
            foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional) { sun = l; break; }
            if (sun == null)
            {
                var go = new GameObject("Sun");
                sun = go.AddComponent<Light>();
                sun.type = LightType.Directional;
            }
            sun.color = new Color(1f, 0.96f, 0.88f);
            sun.intensity = 1.5f;
            sun.shadows = LightShadows.Soft;
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;

            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.nearClipPlane = 0.5f;
            // Valeur de départ ; recalculé chaque frame dans Tick() à partir de la distance caméra réelle
            // (voir plus bas), pour ne jamais dépendre d'une hypothèse figée sur le zoom arrière maximal.
            cam.farClipPlane = Mathf.Max(7000f, SkyDist + 500f);
        }

        // ---------------------------------------------------------------- terrain refresh
        float Hs(int c) { return S.rock[c] + S.sed[c]; }

        Vector3 CellNormal(int c)
        {
            int b = c * 4;
            Vector3 pc = G.Dir[c] * (R + Hs(c));
            int a = G.Nbr[b], bb = G.Nbr[b + 1], cc = G.Nbr[b + 2], dd = G.Nbr[b + 3];
            Vector3 pa = a >= 0 ? G.Dir[a] * (R + Hs(a)) : pc;
            Vector3 pb = bb >= 0 ? G.Dir[bb] * (R + Hs(bb)) : pc;
            Vector3 pcc = cc >= 0 ? G.Dir[cc] * (R + Hs(cc)) : pc;
            Vector3 pd = dd >= 0 ? G.Dir[dd] * (R + Hs(dd)) : pc;
            Vector3 n = Vector3.Cross(pb - pa, pd - pcc);
            if (n.sqrMagnitude < 1e-12f) return G.Dir[c];
            if (Vector3.Dot(n, G.Dir[c]) < 0f) n = -n;
            return n.normalized;
        }

        void RefreshFace(int f, bool refreshWater = true)
        {
            var F = faces[f];
            int vpf = G.VertsPerFace;
            int cells = G.N * G.N;
            Par.For(vpf, (s, e) =>
            {
                for (int vi = s; vi < e; vi++)
                {
                    int gi = f * vpf + vi;
                    float h = 0f; Vector3 ns = Vector3.zero; int cnt = 0;
                    for (int q = 0; q < 4; q++)
                    {
                        int c = G.VertCells[gi * 4 + q];
                        if (c < 0) continue;
                        h += Hs(c);
                        ns += CellNormal(c);
                        cnt++;
                    }
                    if (cnt == 0) { F.verts[vi] = G.VertDir[gi] * R; F.norms[vi] = G.VertDir[gi]; continue; }
                    F.verts[vi] = G.VertDir[gi] * (R + h / cnt);
                    F.norms[vi] = ns.normalized;
                }
            });
            F.mesh.SetVertices(F.verts, 0, vpf, MeshUpdateFlags.DontRecalculateBounds);
            F.mesh.SetNormals(F.norms, 0, vpf, MeshUpdateFlags.DontRecalculateBounds);

            Par.For(cells, (s, e) =>
            {
                for (int i = s; i < e; i++)
                {
                    int c = f * cells + i;
                    F.col[i] = ColorCell(c);
                }
            });
            F.tex.SetPixels32(F.col);
            F.tex.Apply(false);
            if (MeshDetail > 1) RefreshDetailMesh(f);
            if (refreshWater) RefreshWaterFace(f);
            RefreshRiverFace(f);
            F.dirty = false;
        }

        // Produit un maillage plus fin que la grille de simulation, affiché à la place de "F.mesh" : la hauteur
        // de chaque sommet supplémentaire est interpolée bilinéairement entre les 4 sommets de base encadrants
        // (déjà calculés ci-dessus), additionnée d'un léger relief procédural déterministe (fonction du point sur
        // la sphère, donc stable d'un rafraîchissement à l'autre). Aucune nouvelle donnée de simulation requise.
        void RefreshDetailMesh(int f)
        {
            var F = faces[f];
            int N = G.N, n1 = N + 1, K = N * MeshDetail, k1 = K + 1;
            float detailAmp = G.CellSize * 0.18f;
            Par.For(k1 * k1, (s, e) =>
            {
                for (int idx = s; idx < e; idx++)
                {
                    int q = idx / k1, p = idx % k1;
                    float fu = (float)p / MeshDetail, fv = (float)q / MeshDetail;
                    int i0 = Mathf.Min((int)fu, N - 1);
                    int j0 = Mathf.Min((int)fv, N - 1);
                    float tu = fu - i0, tv = fv - j0;
                    int b00 = j0 * n1 + i0, b10 = b00 + 1, b01 = b00 + n1, b11 = b01 + 1;
                    Vector3 v00 = F.verts[b00], v10 = F.verts[b10], v01 = F.verts[b01], v11 = F.verts[b11];
                    float h = Mathf.Lerp(Mathf.Lerp(v00.magnitude, v10.magnitude, tu), Mathf.Lerp(v01.magnitude, v11.magnitude, tu), tv);
                    Vector3 dir = PlanetGrid.FacePoint(f, 2f * p / K - 1f, 2f * q / K - 1f);
                    float detail = (ValueHash(dir * 211.3f) - 0.5f) * detailAmp;
                    F.fineVerts[idx] = dir * (h + detail);
                    Vector3 n00 = F.norms[b00], n10 = F.norms[b10], n01 = F.norms[b01], n11 = F.norms[b11];
                    Vector3 n = Vector3.Lerp(Vector3.Lerp(n00, n10, tu), Vector3.Lerp(n01, n11, tu), tv);
                    F.fineNorms[idx] = n.sqrMagnitude > 1e-8f ? n.normalized : dir;
                }
            });
            F.renderMesh.SetVertices(F.fineVerts, 0, F.fineVerts.Length, MeshUpdateFlags.DontRecalculateBounds);
            F.renderMesh.SetNormals(F.fineNorms, 0, F.fineNorms.Length, MeshUpdateFlags.DontRecalculateBounds);
        }

        // Bruit de valeur bon marché (hash déterministe, pas de texture) : même esprit que le bruit utilisé côté
        // shader pour l'eau, utilisé ici pour un micro-relief stable et sans aucun état supplémentaire à stocker.
        static float ValueHash(Vector3 p)
        {
            float h = Vector3.Dot(p, new Vector3(127.1f, 311.7f, 74.7f));
            float s = Mathf.Sin(h) * 43758.5453f;
            return s - Mathf.Floor(s);
        }

        static Color32 To32(Color c, float a = 1f)
        {
            return new Color32((byte)(Mathf.Clamp01(c.r) * 255f), (byte)(Mathf.Clamp01(c.g) * 255f), (byte)(Mathf.Clamp01(c.b) * 255f), (byte)(Mathf.Clamp01(a) * 255f));
        }

        static Color Ramp(float t, Color a, Color b, Color c)
        {
            return t < 0.5f ? Color.Lerp(a, b, t * 2f) : Color.Lerp(b, c, t * 2f - 1f);
        }

        public Color32 ColorCell(int c)
        {
            float g = S.rock[c] + S.sed[c];
            float t = S.temp[c];

            if (ViewMode == 5)
            {
                float m = Mathf.Clamp01(S.magma[c] / 1.2f);
                Color mc = Ramp(m, new Color(0.25f, 0.25f, 0.27f), new Color(0.9f, 0.3f, 0.05f), new Color(1f, 0.95f, 0.3f));
                if (g < S.SeaLevel) mc *= 0.55f;
                return To32(mc);
            }
            if (ViewMode == 4)
            {
                int pl = S.plate[c];
                Color pc = Color.HSVToRGB((pl * 0.381966f) % 1f, 0.45f, 0.75f);
                pc = Color.Lerp(pc, new Color(1f, 0.15f, 0.1f), Mathf.Clamp01(S.conv[c] * 1.5f));
                pc = Color.Lerp(pc, new Color(0.1f, 0.3f, 1f), Mathf.Clamp01(S.rift[c] * 1.5f));
                if (g < S.SeaLevel) pc *= 0.6f;
                float strongest = 0f;
                PlanetSim.BoundaryKind boundary = PlanetSim.BoundaryKind.None;
                for (int k = 0; k < 4; k++)
                {
                    float strength;
                    var kind = S.PlateBoundary(c, G.Nbr[c * 4 + k], out strength);
                    if (strength > strongest) { strongest = strength; boundary = kind; }
                }
                if (boundary != PlanetSim.BoundaryKind.None) pc = Color.Lerp(pc, BoundaryColors[(int)boundary], 0.9f);
                return To32(pc);
            }
            if (ViewMode == 6)
            {
                int sm = S.SurfaceMaterial(c);
                Color mc = PlanetSim.MatColors[sm];
                if (g < S.SeaLevel) mc *= 0.6f;
                return To32(mc);
            }
            if (ViewMode == 7)
            {
                // O2 (vert=bas), CO2 (rouge), SO2/CH4 (jaune/violet) : composition de l'air
                float o = Mathf.Clamp01((S.o2[c] - 15f) / 10f);
                float co = Mathf.Clamp01((S.co2[c] - 400f) / 400f);
                float so = Mathf.Clamp01(S.so2[c] / 15f);
                Color gc = new Color(0.1f + co, 0.15f + 0.6f * o + so, 0.2f + 0.7f * Mathf.Clamp01(S.ch4[c] / 10f));
                if (g < S.SeaLevel) gc *= 0.75f;
                return To32(gc);
            }
            if (ViewMode == 8)
            {
                Color nc = Color.Lerp(new Color(0.55f, 0.45f, 0.3f), new Color(0.1f, 0.4f, 0.95f), S.GwFill(c));
                nc = Color.Lerp(nc, new Color(0.9f, 0.9f, 0.2f), Mathf.Clamp01(S.Perm(c)) * 0.35f);
                if (g < S.SeaLevel) nc = new Color(0.05f, 0.1f, 0.25f);
                return To32(nc);
            }
            if (ViewMode == 9)
                return To32(Ramp(Mathf.InverseLerp(-1.6f, 1.6f, S.pres[c]), new Color(0.15f, 0.25f, 0.95f), new Color(0.85f, 0.85f, 0.85f), new Color(0.95f, 0.15f, 0.1f)));
            if (ViewMode == 10)
            {
                // Rouge = érosion nette récente (l'eau arrache de la matière), bleu = dépôt net récent (sédimentation) ;
                // gris neutre là où il ne se passe rien (voir waterErode, moyenne mobile en épaisseur/tick dans StepHydro).
                Color wc = Ramp(Mathf.InverseLerp(-0.3f, 0.3f, S.waterErode[c]), new Color(0.85f, 0.2f, 0.15f), new Color(0.55f, 0.52f, 0.48f), new Color(0.15f, 0.45f, 0.9f));
                if (g < S.SeaLevel) wc *= 0.6f;
                return To32(wc);
            }
            if (ViewMode == 11)
            {
                // Orange = déflation éolienne nette récente, jaune pâle = accumulation (dunes) ; gris ailleurs
                // (voir windErode, moyenne mobile en épaisseur/tick dans StepWindErosion).
                Color wc = Ramp(Mathf.InverseLerp(-0.3f, 0.3f, S.windErode[c]), new Color(0.9f, 0.45f, 0.1f), new Color(0.55f, 0.52f, 0.48f), new Color(0.95f, 0.88f, 0.55f));
                if (g < S.SeaLevel) wc *= 0.6f;
                return To32(wc);
            }
            if (ViewMode == 1)
                return To32(Ramp(Mathf.InverseLerp(-35f, 40f, t), new Color(0.2f, 0.3f, 1f), new Color(0.9f, 0.9f, 0.3f), new Color(1f, 0.15f, 0.1f)));
            if (ViewMode == 2)
                return To32(Ramp(S.moist[c], new Color(0.8f, 0.6f, 0.3f), new Color(0.3f, 0.8f, 0.3f), new Color(0.1f, 0.3f, 0.9f)));
            if (ViewMode == 3)
            {
                float hh = Mathf.InverseLerp(-60f, S.MaxHeight, g);
                return To32(Ramp(hh, new Color(0f, 0f, 0.4f), new Color(0.3f, 0.7f, 0.3f), Color.white));
            }

            if (S.IsOcean(c))
            {
                float depth = Mathf.Clamp01((S.SeaLevel - g) / 35f);
                Color col = Color.Lerp(new Color(0.78f, 0.72f, 0.52f), new Color(0.2f, 0.17f, 0.12f), Mathf.Sqrt(depth));
                if (t < -8f) col = Color.Lerp(col, new Color(0.9f, 0.95f, 1f), Mathf.Clamp01((-8f - t) / 6f));
                // Banquise persistante : une vraie couverture de glace de mer qui s'épaissit/s'amincit au fil
                // du temps (StepIce), affichée distinctement du simple "bleuissement" de surface ci-dessus.
                float seaIceCover = S.seaIce != null ? Mathf.Clamp01(S.seaIce[c] / 2.5f) : 0f;
                if (seaIceCover > 0f) col = Color.Lerp(col, new Color(0.88f, 0.94f, 0.98f), seaIceCover);
                return To32(col);
            }

            float slope = S.Slope(c);
            float sedF = Mathf.Clamp01(S.sed[c] / 0.6f);
            float moist = S.moist[c];
            float grass = S.grass[c];

            Color rockC = Color.Lerp(PlanetSim.RockColors[S.rockT[c]] * 0.85f, PlanetSim.RockColors[S.rockT[c]], S.hard[c]);
            if (S.ore[c] != 0 && S.sed[c] < 0.35f) rockC = Color.Lerp(rockC, PlanetSim.MatColors[S.ore[c]], 0.55f * (1f - S.sed[c] / 0.35f));
            Color dry = new Color(0.66f, 0.6f, 0.32f), lush = new Color(0.17f, 0.42f, 0.12f), tundra = new Color(0.46f, 0.5f, 0.42f);
            Color gcol = Color.Lerp(dry, lush, Mathf.Clamp01(moist * 1.4f));
            float fertility = S.Fertility(c);
            Color poor = new Color(0.10f, 0.23f, 0.09f), normal = new Color(0.25f, 0.43f, 0.15f), rich = new Color(0.53f, 0.70f, 0.30f);
            Color soilGreen = fertility < 0.5f ? Color.Lerp(poor, normal, fertility * 2f) : Color.Lerp(normal, rich, (fertility - 0.5f) * 2f);
            gcol = Color.Lerp(gcol, soilGreen, 0.5f);
            gcol = Color.Lerp(gcol, tundra, Mathf.Clamp01((5f - t) / 15f));
            int mb = c * 4;
            Color bare = PlanetSim.MatColors[0] * S.mix[mb] + PlanetSim.MatColors[1] * S.mix[mb + 1] + PlanetSim.MatColors[2] * S.mix[mb + 2] + PlanetSim.MatColors[3] * S.mix[mb + 3];
            bare.a = 1f;
            bare = Color.Lerp(bare, soilGreen, 0.45f * (S.mix[mb] + S.mix[mb + 3]));
            Color baseC = Color.Lerp(bare, gcol, grass);
            float expose = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.5f, 1.0f, slope));
            // Strates géologiques sur les parois raides (failles, falaises) : sans ça, une paroi exposée n'est
            // qu'une seule teinte de roche plate. Des bandes suivant l'altitude (légèrement déphasées par
            // cellule pour ne pas dessiner des cercles parfaitement horizontaux sur toute la planète) imitent
            // des couches sédimentaires empilées, visibles dès qu'assez de roche nue est exposée.
            if (expose > 0.02f)
            {
                Vector3 d = G.Dir[c];
                float phase = Noise.Perlin(d.x * 3.1f + 7.3f, d.y * 3.1f - 2.1f, d.z * 3.1f + 5.9f) * 6f;
                float band = Mathf.Sin(g * 0.55f + phase) * 0.5f + 0.5f;
                float band2 = Mathf.Sin(g * 1.7f + phase * 1.6f) * 0.5f + 0.5f;
                Color strata = Color.Lerp(rockC * 0.72f, rockC * 1.18f, band);
                strata = Color.Lerp(strata, rockC * 0.9f, band2 * 0.3f);
                rockC = Color.Lerp(rockC, strata, expose);
            }
            baseC = Color.Lerp(rockC, baseC, sedF * (1f - expose));
            float coastalHeight = g - S.SeaLevel;
            if (coastalHeight >= 0f && coastalHeight < 2f)
                baseC = Color.Lerp(baseC, new Color(0.8f, 0.75f, 0.55f), 1f - coastalHeight / 2f);
            baseC = Color.Lerp(baseC, PlanetSim.MatColors[PlanetSim.Salt], Mathf.Clamp01(S.waterState.deposit[c] * 2f));

            float sn = Mathf.Clamp01(S.snow[c] / 1.5f);
            baseC = Color.Lerp(baseC, new Color(0.96f, 0.97f, 1f), sn);
            // Glace de glacier : plus dense et plus bleutée que la neige saisonnière, et surtout persistante
            // (ne fond pas entièrement chaque année) puisqu'alimentée par le compactage de l'excédent de neige.
            float glacierCover = S.glacier != null ? Mathf.Clamp01(S.glacier[c] / 15f) : 0f;
            if (glacierCover > 0f) baseC = Color.Lerp(baseC, new Color(0.82f, 0.9f, 0.98f), glacierCover);
            baseC = Color.Lerp(baseC, new Color(0.07f, 0.06f, 0.06f), Mathf.Clamp01(S.burn[c]));
            baseC = Color.Lerp(baseC, baseC * 0.78f, S.GwFill(c) * 0.6f * (1f - sn));
            if (S.fire[c] > 0f) baseC = Color.Lerp(baseC, new Color(1f, 0.4f, 0.05f), Mathf.Clamp01(S.fire[c] * 1.5f));
            if (S.scar[c] > 0.05f) baseC = Color.Lerp(baseC, new Color(0.45f, 0.32f, 0.2f), Mathf.Clamp01(S.scar[c]) * 0.85f);

            float wt = Mathf.Max(Mathf.SmoothStep(0.08f, 0.4f, S.flux[c]), Mathf.Clamp01(S.water[c] * 2f));
            if (wt > 0f)
            {
                // l'eau est un fluide rendu à part : le terrain mouillé ne fait que s'assombrir
                baseC = t < -2f ? Color.Lerp(baseC, new Color(0.8f, 0.9f, 1f), wt * 0.9f) : Color.Lerp(baseC, baseC * 0.6f, wt * 0.7f);
            }
            if (S.lava[c] > 0f)
            {
                // Magma visqueux : un cœur en fusion vif (jaune-blanc près de l'évent, orange en s'éloignant), qui se fige
                // en croûte basaltique sombre craquelée à mesure qu'il refroidit (lava décroît lentement avec le temps,
                // voir StepVolcanoes) — seules des fissures encore incandescentes restent visibles sur la croûte.
                float lv = S.lava[c];
                Color molten = Color.Lerp(new Color(0.95f, 0.3f, 0.02f), new Color(1f, 0.92f, 0.45f), Mathf.Clamp01((lv - 0.7f) / 0.3f));
                float crack = ValueHash(G.Dir[c] * 83.7f);
                Color crust = new Color(0.07f, 0.055f, 0.05f);
                bool glowingCrack = crack < lv * lv;
                Color cooling = glowingCrack ? molten : crust;
                baseC = Color.Lerp(baseC, lv > 0.55f ? molten : cooling, Mathf.Clamp01(lv * 1.3f));
            }
            return To32(baseC);
        }

        // ---------------------------------------------------------------- per frame
        public void Tick(float dt, float simDt, Vector3 camPos, Vector3 camFwd)
        {
            if (TimeScale > 0f) waterVisualTime += dt;
            if (customOceanMaterial) waterMaterial.SetFloat("_LP_WaterTime", waterVisualTime);
            lastCamPos = camPos;
            // Le plan proche doit grandir avec l'altitude : sinon le ratio proche/lointain devient énorme sur
            // les grandes planètes vues de loin, et le z-buffer perd en précision au point de laisser voir
            // à travers la sphère près de l'horizon (artefact de "z-fighting" classique).
            float altitude = Mathf.Max(0f, camPos.magnitude - R);
            cam.nearClipPlane = Mathf.Clamp(altitude * 0.0035f, 0.12f, 60f);
            // Le plan éloigné est recalculé à partir de la distance caméra réelle (et non d'un zoom arrière
            // maximal supposé) : il doit toujours couvrir l'horizon vu d'ici, sinon le limbe de la planète est
            // tronqué et on voit les étoiles/le ciel à travers (ou le soleil se fait couper avant l'horizon).
            float camDist = camPos.magnitude;
            float horizonNow = camDist > R ? Mathf.Sqrt(camDist * camDist - R * R) : R * 2f;
            cam.farClipPlane = Mathf.Max(7000f, horizonNow + 1000f, SkyDist + 500f);
            // le jour est lié au temps simulé : le soleil décrit une spirale continue (pas de zigzag)
            float dA = Mathf.Min(simDt * DaysPerYear * 2f * Mathf.PI, dt * 1.5f);
            dayAngle += dA;
            float tgtDays = (S.tick + 0.5f) / PlanetSim.TicksPerYear * DaysPerYear;
            if (!declInit) { vDays = tgtDays; }
            vDays += dA / (2f * Mathf.PI);
            vDays += (tgtDays - vDays) * Mathf.Min(1f, dt * 0.5f);
            if (!declInit) { visDecl = S.Declination; declInit = true; }
            visDecl = S.AxialTilt * Mathf.Sin(2f * Mathf.PI * vDays / DaysPerYear);
            if (S.tick != lastTick) { lastTick = S.tick; MarkDirty(); }
            bool hasTsunami = S.tsunamis.Count > 0;
            if (hasTsunami || hadTsunami) MarkDirty();
            hadTsunami = hasTsunami;
            faceTimer += dt;
            if (faceTimer > 0.12f)
            {
                faceTimer = 0f;
                for (int k = 0; k < 6; k++)
                {
                    int f = (nextFace + k) % 6;
                    if (!faces[f].dirty) continue;
                    RefreshFace(f, !hasTsunami || TimeScale <= 0f);
                    nextFace = (f + 1) % 6;
                    break;
                }
            }
            if (hasTsunami && TimeScale > 0f)
            {
                waterFaceTimer += dt;
                if (waterFaceTimer >= 0.07f)
                {
                    waterFaceTimer = 0f;
                    for (int i = 0; i < 2; i++)
                    {
                        RefreshWaterFace(nextWaterFace);
                        nextWaterFace = (nextWaterFace + 1) % 6;
                    }
                }
            }
            else waterFaceTimer = 0f;
            plantTimer += dt;
            if (plantTimer > 0.25f)
            {
                plantTimer = 0f;
                RebuildPlants(camPos);
            }
            cloudTimer += dt;
            if (cloudTimer > 0.15f) { UpdateClouds(camPos, cloudTimer); cloudTimer = 0f; }
            TickBolts(dt);
            UpdateSky(camPos);
            UpdatePrecip(dt, camPos, camFwd);
        }

        public void RefreshAll()
        {
            for (int f = 0; f < 6; f++) RefreshFace(f);
        }

        void UpdateSky(Vector3 camPos)
        {
            float decl = visDecl;
            float cd = Mathf.Cos(decl);
            // La planète tourne vers l'est (longitudes croissantes) : vu du sol, le soleil se lève à l'est et
            // parcourt le ciel vers l'ouest, donc sa longitude décroît quand dayAngle augmente.
            SunDir = new Vector3(cd * Mathf.Cos(dayAngle), Mathf.Sin(decl), -cd * Mathf.Sin(dayAngle)).normalized;
            sun.transform.rotation = Quaternion.LookRotation(-SunDir);

            float alt = camPos.magnitude - R;
            Atmo = 1f - Mathf.Clamp01(alt / AtmoThickness);
            Atmo *= Atmo;
            if (!ShowAtmosphere) Atmo = 0f;
            float sunUp = Vector3.Dot(camPos.normalized, SunDir);
            DayFactor = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.15f, 0.25f, sunUp));
            float twilight = Mathf.Clamp01(1f - Mathf.Abs(sunUp) / 0.22f);

            // couleur du soleil : orange/rouge près de l'horizon (comme l'atmosphère le fait sur le ciel)
            float low = Mathf.Clamp01(1f - Mathf.Clamp01(sunUp * 3f));
            Color sunCol = Color.Lerp(new Color(1f, 0.97f, 0.9f), new Color(1f, 0.45f, 0.18f), low * Atmo);
            sun.color = sunCol;
            sun.intensity = Mathf.Lerp(1.5f, 0.9f, low * Atmo);
            sunDiscR.sharedMaterial.SetColor("_BaseColor", Color.Lerp(new Color(1f, 0.95f, 0.75f), new Color(1f, 0.5f, 0.2f), low));

            Color daySky = new Color(0.42f, 0.66f, 0.95f);
            Color dusk = new Color(0.95f, 0.5f, 0.28f);
            Color night = new Color(0.01f, 0.012f, 0.035f);
            Color sky = Color.Lerp(night, Color.Lerp(daySky, dusk, twilight * 0.7f), DayFactor);
            Color bg = Color.Lerp(Color.black, sky, Atmo);
            // avec le shader, le ciel est rendu par l'atmosphère : le fond reste noir
            cam.backgroundColor = atmoShader || !ShowAtmosphere ? Color.black : bg;
            RenderSettings.fogColor = bg;
            RenderSettings.fogDensity = 0.0011f * Atmo;
            float amb = Mathf.Lerp(0.14f, Mathf.Lerp(0.07f, 0.32f, DayFactor), Atmo);
            // clair de lune : proportionnel à la phase, absent pendant une éclipse ou sous l'horizon
            float moonUp = Mathf.Clamp01(Vector3.Dot(camPos.normalized, MoonDir) * 4f + 0.3f);
            float phaseLit = (1f - Vector3.Dot(MoonDir, SunDir)) * 0.5f;
            amb += 0.07f * phaseLit * moonUp * (1f - DayFactor) * (1f - EclipseAmount) * Atmo;
            RenderSettings.ambientLight = new Color(amb * 0.9f, amb, amb * 1.2f);

            if (atmoShader)
            {
                Shader.SetGlobalVector("_LP_SunDir", new Vector4(SunDir.x, SunDir.y, SunDir.z, 0f));
                int cc = G.CellFromDir(camPos);
                float cloudHere = S.cloud[cc];
                for (int k = 0; k < 4; k++) { int n = G.Nbr[cc * 4 + k]; if (n >= 0) cloudHere += S.cloud[n]; }
                cloudHere = Mathf.Clamp01((cloudHere / 5f - 0.2f) / 0.8f) * Atmo;
                skyCloud = Mathf.Lerp(skyCloud, cloudHere, 0.02f);
                Shader.SetGlobalFloat("_LP_Cloud", skyCloud);
                // L'intensité du ciel est réglée pour être vue du sol. Vue du dessus, la même lumière diffusée
                // s'ajoute à l'image du terrain et le noyait sous un voile blanc-bleu : on la réduit
                // progressivement à mesure que la caméra monte et sort de l'atmosphère.
                float leaving = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.25f * AtmoThickness, 1.5f * AtmoThickness, alt));
                Shader.SetGlobalFloat("_LP_AtmoGain", Mathf.Lerp(1f, OrbitAtmoGain, leaving));
                // "Dedans" se juge par rapport à la coque réellement dessinée, pas au sommet de l'atmosphère :
                // entre les deux, la caméra est dans la coque et sa face externe, seule affichée, est invisible.
                bool inside = camPos.magnitude < atmoShellRadius;
                atmoIn.SetActive(ShowAtmosphere && inside);
                atmoOut.SetActive(ShowAtmosphere && !inside);
            }
            else if (atmoFallback != null) atmoFallback.SetActive(ShowAtmosphere);

            starsT.position = camPos;
            sunDiscT.position = camPos + SunDir * (SkyDist - 500f);
            float lamS = 2f * Mathf.PI * vDays / DaysPerYear;
            // la voûte céleste tourne autour de l'axe des pôles (jour sidéral = jour solaire + dérive annuelle)
            starsT.rotation = Quaternion.AngleAxis((dayAngle + lamS) * Mathf.Rad2Deg, Vector3.up);
            float phi = 2f * Mathf.PI * vDays / MoonPeriodDays + Mathf.PI;
            // orbite lunaire inclinée de 5° dont la ligne des nœuds est alignée avec 2 des 4 pleines lunes de l'année
            float decM = S.AxialTilt * Mathf.Sin(lamS + phi) + 5f * Mathf.Deg2Rad * Mathf.Sin(lamS + phi);
            float cm = Mathf.Cos(decM), raM = dayAngle + phi;
            MoonDir = new Vector3(cm * Mathf.Cos(raM), Mathf.Sin(decM), -cm * Mathf.Sin(raM)).normalized;
            moonT.position = camPos + MoonDir * (SkyDist - 700f);
            float sep = Vector3.Angle(MoonDir, -SunDir);
            EclipseAmount = Mathf.MoveTowards(EclipseAmount, 1f - Mathf.Clamp01(sep / 4f), 0.1f);
            moonR.sharedMaterial.SetColor("_BaseColor", Color.Lerp(new Color(0.78f, 0.78f, 0.8f), new Color(0.38f, 0.07f, 0.04f), EclipseAmount));
            starsR.enabled = Atmo * DayFactor < 0.45f;
        }

        void UpdatePrecip(float dt, Vector3 camPos, Vector3 camFwd)
        {
            int c = G.CellFromDir(camPos);
            float alt = camPos.magnitude - R - Mathf.Max(0f, S.Ground(c));
            float p = S.rainNow[c];
            for (int k = 0; k < 4; k++) { int n = G.Nbr[c * 4 + k]; if (n >= 0) p += S.rainNow[n]; }
            p *= 0.2f;
            float target = alt < 90f ? Mathf.Clamp01(p / 0.02f) : 0f;
            precipSmooth = Mathf.MoveTowards(precipSmooth, target, dt * 0.5f);

            Vector3 up = camPos.normalized;
            Vector3 f = Vector3.ProjectOnPlane(camFwd, up);
            if (f.sqrMagnitude < 1e-4f) f = Vector3.ProjectOnPlane(Vector3.forward, up);
            precipT.position = camPos + up * 28f;
            precipT.rotation = Quaternion.LookRotation(-up, f.normalized);

            bool snow = S.temp[c] < 0.5f;
            var main = precip.main;
            if (snow != precipSnow)
            {
                precipSnow = snow;
                main.startSpeed = snow ? 4f : 30f;
                main.startSize = snow ? 0.14f : 0.06f;
                main.startLifetime = snow ? 9f : 1.3f;
                main.startColor = snow ? new Color(1f, 1f, 1f, 0.9f) : new Color(0.75f, 0.85f, 1f, 0.55f);
                precipR.renderMode = snow ? ParticleSystemRenderMode.Billboard : ParticleSystemRenderMode.Stretch;
            }
            var em = precip.emission;
            em.rateOverTime = precipSmooth * (snow ? 1200f : 3000f);
        }

        // ---------------------------------------------------------------- plants
        void RebuildPlants(Vector3 camPos)
        {
            for (int i = 0; i < buckets.Length; i++) buckets[i].n = 0;
            if (!ShowPlants) return;
            float alt = camPos.magnitude - R;
            float maxD = DrawDistance + alt * 0.5f;
            float maxD2 = maxD * maxD;
            var plants = E.plants;
            float cs = G.CellSize * 0.35f;
            for (int i = 0; i < plants.Count; i++)
            {
                Plant p = plants[i];
                int c = p.cell;
                Vector3 d = G.Dir[c];
                float gh = S.rock[c] + S.sed[c];
                Vector3 pos = d * (R + gh - 0.12f) + (G.Tan[c * 4 + 1] * p.jx + G.Tan[c * 4 + 3] * p.jy) * cs;
                if ((pos - camPos).sqrMagnitude > maxD2) continue;

                var P = Ecosystem.Types[p.type];
                float sc = P.scale * Mathf.Max(0.12f, p.size) * (0.85f + 0.3f * p.scaleVar);
                Quaternion rot = Quaternion.FromToRotation(Vector3.up, d) * Quaternion.Euler(0f, p.yaw, 0f);
                Matrix4x4 m = Matrix4x4.TRS(pos, rot, new Vector3(sc, sc, sc));
                float t = S.temp[c];
                int ty = p.type;
                bool tree = Ecosystem.IsTree(ty);

                if (p.state == 0)
                {
                    switch (ty)
                    {
                        case Ecosystem.Broad:
                            buckets[TRUNK].Add(m);
                            if (t >= 12f) buckets[p.scaleVar > 0.5f ? BROAD_G1 : BROAD_G2].Add(m);
                            else if (t >= 6f) buckets[BROAD_Y].Add(m);
                            else if (t >= 0f) buckets[BROAD_O].Add(m);
                            break;
                        case Ecosystem.Conifer:
                            buckets[TRUNK].Add(m);
                            buckets[S.snow[c] > 0.5f ? CONIF_S : CONIF].Add(m);
                            break;
                        case Ecosystem.Palm: buckets[PALM_T].Add(m); buckets[PALM_L].Add(m); break;
                        case Ecosystem.Cactus: buckets[CACTUS].Add(m); break;
                        case Ecosystem.Berry:
                            buckets[BERRY].Add(m);
                            if (p.size > 0.6f) buckets[BERRY_F].Add(m);
                            break;
                        case Ecosystem.Reed: buckets[REED].Add(m); break;
                        case Ecosystem.Kelp: buckets[KELP].Add(m); break;
                        default:
                            if (t < 0f) buckets[BUSH_WIN].Add(m);
                            else if (S.moist[c] < 0.25f) buckets[BUSH_DRY].Add(m);
                            else buckets[BUSH].Add(m);
                            break;
                    }
                }
                else if (p.state == 1)
                {
                    if (ty == Ecosystem.Kelp) continue;
                    buckets[tree ? SNAG : BUSH_WIN].Add(m);
                }
                else if (p.state == 2)
                {
                    if (tree) buckets[BURNT].Add(m);
                }
                else if (p.state == 4)
                {
                    if (ty == Ecosystem.Kelp) continue;
                    float rotK = 1f - 0.6f * Mathf.Clamp01(p.timer / (P.snagTime * 0.8f + 0.5f));
                    Matrix4x4 fm = Matrix4x4.TRS(pos - d * 0.1f, rot * Quaternion.Euler(82f, 0f, 0f), new Vector3(sc, sc, sc) * rotK);
                    buckets[tree ? (p.health < 0.5f ? BURNT : SNAG) : BUSH_WIN].Add(fm);
                }
                else if (tree) buckets[BURNT].Add(m);
            }
        }

        static void DrawBucket(Bucket bk, Bounds bounds, bool shadows)
        {
            for (int start = 0; start < bk.n; start += 1023)
            {
                int count = Mathf.Min(1023, bk.n - start);
                var rp = new RenderParams(bk.mat)
                {
                    shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off,
                    receiveShadows = shadows,
                    worldBounds = bounds
                };
                Graphics.RenderMeshInstanced(rp, bk.mesh, 0, bk.m, count, start);
            }
        }

        void BuildFaunaInstances(Vector3 camPos)
        {
            for (int s = 0; s < Fauna.Count; s++) faunaB[s].n = 0;
            if (Fa == null) return;
            float alt = camPos.magnitude - R;
            float maxD = 450f + alt * 0.6f;
            float maxD2 = maxD * maxD;
            float t = Time.time;
            for (int s = 0; s < Fauna.Count; s++)
            {
                var pop = Fa.pops[s];
                float scale = s == Fauna.Deer || s == Fauna.Shark || s == Fauna.Fox ? 1.5f : (s == Fauna.Whale ? 2.2f : 1.8f);
                for (int i = 0; i < pop.n; i++)
                {
                    ref Animal an = ref pop.a[i];
                    int c = an.cell;
                    float g = S.rock[c] + S.sed[c];
                    float lift;
                    switch (s)
                    {
                        case Fauna.Bird:
                        case Fauna.Raptor: lift = an.sleep ? Mathf.Max(0.05f, g + 0.3f) : Mathf.Max(0f, g) + (s == Fauna.Raptor ? 14f : 9f) + Mathf.Sin(t * 2f + i) * 0.8f; break;
                        case Fauna.Fish:
                        case Fauna.Shark: lift = Mathf.Max(g + 0.6f, -2.5f); break;
                        case Fauna.Whale: lift = Mathf.Max(g + 0.6f, -1.2f); break;
                        default: lift = g + 0.05f + (s == Fauna.Rabbit && an.boost > 0f ? Mathf.Abs(Mathf.Sin(t * 12f + i)) * 0.4f : 0f); break;
                    }
                    Vector3 pos = an.dir * (R + lift);
                    if ((pos - camPos).sqrMagnitude > maxD2) continue;
                    Quaternion rot = Quaternion.LookRotation(an.head, an.dir);
                    faunaB[s].Add(Matrix4x4.TRS(pos, rot, Vector3.one * scale));
                }
            }
        }

        Bucket flameB, flameHot;
        Bucket smokeB, emberB;

        // flammes animées, 4 niveaux : braises, petit feu, feu, brasier (plus de langues, plus hautes, cœur jaune)
        void BuildFlames(Vector3 camPos)
        {
            if (flameB == null)
            {
                flameB = new Bucket { mesh = buckets[FLAME].mesh, mat = buckets[FLAME].mat };
                flameHot = new Bucket { mesh = buckets[FLAME].mesh, mat = new Material(buckets[FLAME].mat) };
            }
            flameB.n = 0; flameHot.n = 0;
            if (!ShowPlants) return;
            var fc = S.fireCells;
            float t = Time.time;
            float maxD = DrawDistance * 2f + (camPos.magnitude - R);
            float maxD2 = maxD * maxD;
            float cs = G.CellSize;
            float baseS = Mathf.Min(2.4f, cs * 0.3f);
            flameB.mat.SetColor("_EmissionColor", new Color(2.5f, 0.45f + 0.35f * Mathf.Sin(t * 14f), 0.05f));
            flameHot.mat.SetColor("_EmissionColor", new Color(3f, 2.2f + 0.6f * Mathf.Sin(t * 17f), 0.6f));
            flameHot.mat.SetColor("_BaseColor", new Color(1f, 0.85f, 0.3f));
            for (int i = 0; i < fc.Count; i++)
            {
                int c = fc[i];
                Vector3 d = G.Dir[c];
                float gh = Mathf.Max(0f, S.rock[c] + S.sed[c]);
                Vector3 pos = d * (R + gh);
                if ((pos - camPos).sqrMagnitude > maxD2) continue;
                float f = S.fire[c];
                int level = f < 0.2f ? 0 : (f < 0.5f ? 1 : (f < 0.8f ? 2 : 3));
                int tongues = 1 + level * 2;
                float spread = cs * (0.12f + 0.1f * level);
                float height = 0.5f + 0.7f * level;
                Quaternion up = Quaternion.FromToRotation(Vector3.up, d);
                for (int k = 0; k < tongues; k++)
                {
                    float ph = c * 0.37f + k * 2.1f;
                    float pulse = 0.65f + 0.35f * Mathf.Sin(t * (8f + k * 3f) + ph);
                    Vector3 off = (G.Tan[c * 4 + 1] * Mathf.Sin(ph * 3f) + G.Tan[c * 4 + 3] * Mathf.Cos(ph * 5f)) * spread;
                    float s = baseS * (0.3f + 0.7f * f) * pulse;
                    var sca = new Vector3(s * 0.6f, s * height * (1.1f + 0.7f * Mathf.Sin(t * 11f + ph)), s * 0.6f);
                    Quaternion rq = up * Quaternion.Euler(0f, ph * 40f + t * 90f, 0f);
                    flameB.Add(Matrix4x4.TRS(pos + off, rq, sca));
                    if (level >= 2 && k < 2 + level - 2)
                        flameHot.Add(Matrix4x4.TRS(pos + off, rq, sca * 0.55f));
                }
            }
        }
        // panaches de fumée/cendres (montent, se dispersent avec le vent, s'estompent) et bombes volcaniques
        // (matériaux les plus gros projetés, trajectoire en arc) pour visualiser où l'essentiel des éjectas retombe.
        void BuildVolcanoFX(Vector3 camPos)
        {
            if (smokeB == null)
            {
                var sm = new MeshBuilder(); sm.Sphere(Vector3.zero, 1f, 1f, 4, 8);
                smokeB = new Bucket { mesh = sm.Build(), mat = MakeUnlit(new Color(0.3f, 0.28f, 0.27f, 0.5f)) };
                MakeTransparent(smokeB.mat, true);
                var eb = new MeshBuilder(); eb.Box(Vector3.zero, Vector3.one);
                emberB = new Bucket { mesh = eb.Build(), mat = MakeLit(new Color(1f, 0.4f, 0.05f), 0.1f, true) };
                emberB.mat.SetColor("_EmissionColor", new Color(3f, 0.8f, 0.05f));
            }
            smokeB.n = 0; emberB.n = 0;
            var vs = S.volcanoes;
            if (vs == null || vs.Count == 0) return;
            float t = Time.time;
            float cs = G.CellSize;
            float maxD = DrawDistance * 2f + (camPos.magnitude - R);
            float maxD2 = maxD * maxD;
            for (int i = 0; i < vs.Count; i++)
            {
                var v = vs[i];
                if (v.env < 0.03f) continue;
                int c = v.cell;
                if (S.Ground(c) < S.SeaLevel - 30f) continue;
                Vector3 d = G.Dir[c];
                float gh = Mathf.Max(0f, S.rock[c] + S.sed[c]);
                Vector3 pos = d * (R + gh);
                if ((pos - camPos).sqrMagnitude > maxD2) continue;
                float pw = v.power, env = v.env;
                Vector3 up = G.Tan[c * 4 + 1], side = G.Tan[c * 4 + 3];
                Vector3 wind = S.wind != null ? S.wind[c] : Vector3.zero;
                Vector3 windAloft = S.windHigh != null ? S.windHigh[c] : wind;
                Quaternion rot = Quaternion.FromToRotation(Vector3.up, d);

                // panache : des bouffées qui montent, grossissent, dérivent avec le vent puis s'évanouissent
                int puffs = 4 + (int)(6f * pw);
                for (int k = 0; k < puffs; k++)
                {
                    float ph = Mathf.Repeat(t * 0.25f + k / (float)puffs, 1f);
                    float h = ph * (7f + 14f * pw) * cs;
                    Vector3 driftDir = up * (float)System.Math.Sin(k * 1.7f) + side * (float)System.Math.Cos(k * 1.7f);
                    Vector3 blendedWind = Vector3.Lerp(wind, windAloft, ph); // plus la bouffée monte, plus elle suit le vent d'altitude
                    Vector3 driftPos = pos + d * h + driftDir * (cs * 0.4f * ph) + blendedWind * (ph * ph * cs * 6f);
                    float sc = cs * (0.6f + 2.2f * ph) * (0.5f + 0.8f * pw);
                    smokeB.Add(Matrix4x4.TRS(driftPos, rot, Vector3.one * sc));
                }

                // bombes volcaniques : les blocs les plus gros, lancés en arc, retombent vers le rayon d'éjectas réel (voir Blast())
                int bombs = pw > 0.3f ? 3 + (int)(5f * pw) : 0;
                float range = (2f + 7f * pw) * cs;
                for (int k = 0; k < bombs; k++)
                {
                    float ph = Mathf.Repeat(t * 1.1f + k * 0.618f, 1f);
                    float ang = k * 2.39996f;
                    Vector3 outDir = up * Mathf.Cos(ang) + side * Mathf.Sin(ang);
                    float dist = range * ph;
                    float arc = Mathf.Sin(ph * Mathf.PI) * range * 0.4f;
                    Vector3 bp = pos + outDir * dist + d * arc;
                    float sc = cs * 0.16f * (0.6f + 0.6f * pw) * (1f - 0.3f * ph);
                    emberB.Add(Matrix4x4.TRS(bp, rot, Vector3.one * sc));
                }
            }
        }

        public void DrawPlants()
        {
            if (buckets == null) return;
            var bounds = new Bounds(Vector3.zero, Vector3.one * R * 4f);
            for (int b = 0; b < buckets.Length; b++) DrawBucket(buckets[b], bounds, true);
            BuildFlames(lastCamPos);
            DrawBucket(flameB, bounds, false);
            DrawBucket(flameHot, bounds, false);
            DrawClouds(bounds);
            DrawAsteroids(bounds);
            DrawFlow(bounds);
            BuildVolcanoFX(lastCamPos);
            DrawBucket(smokeB, bounds, false);
            DrawBucket(emberB, bounds, false);
            if (icebergB != null) { BuildIcebergInstances(); DrawBucket(icebergB, bounds, true); }
            if (faunaB != null)
            {
                BuildFaunaInstances(lastCamPos);
                for (int s = 0; s < faunaB.Length; s++) DrawBucket(faunaB[s], bounds, true);
            }
        }

        void BuildIcebergInstances()
        {
            icebergB.n = 0;
            var list = S.icebergs;
            for (int i = 0; i < list.Count; i++)
            {
                var ib = list[i];
                int c = G.CellFromDir(ib.dir);
                float waterLevel = R + Mathf.Max(S.SeaLevel, S.Ground(c) + S.water[c]);
                Vector3 pos = ib.dir * waterLevel;
                Vector3 up = ib.dir;
                Vector3 fwd = Vector3.Cross(up, Vector3.right);
                if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.Cross(up, Vector3.forward);
                float scale = 1.2f + ib.size * 6f;
                icebergB.Add(Matrix4x4.TRS(pos, Quaternion.LookRotation(fwd.normalized, up), Vector3.one * scale));
            }
        }

        public int VisiblePlants
        {
            get { int s = 0; if (buckets != null) for (int i = 0; i < buckets.Length; i++) s += buckets[i].n; return s; }
        }

        // Détruire un GameObject ne libère ni ses maillages, ni ses matériaux, ni ses textures : tout ce que la
        // vue a créé par `new` doit être détruit explicitement, sinon chaque régénération de planète laisse
        // l'ancienne en mémoire. Tous ces objets sont créés par la vue (aucun asset du projet, aucune primitive
        // intégrée), donc les détruire est sans risque ; les shaders, eux, sont partagés et ne sont pas touchés.
        public void Destroy()
        {
            if (flashLight != null) flashLight.enabled = false;
            var owned = new HashSet<UnityEngine.Object>();
            for (int f = 0; f < faces.Length; f++)
            {
                var F = faces[f];
                if (F == null) continue;
                Own(owned, F.mesh); Own(owned, F.renderMesh); Own(owned, F.waterMesh); Own(owned, F.riverMesh); Own(owned, F.tex);
            }
            Own(owned, waterMaterial);
            Own(owned, buckets); Own(owned, faunaB); Own(owned, cloudB);
            if (flowB != null) for (int l = 0; l < flowB.Length; l++) Own(owned, flowB[l]);
            Own(owned, new[] { icebergB, flameB, flameHot, smokeB, emberB, boltB,
                astRock, astGlow, astTrail, astBall, astRing, astPath, astZone });

            var objects = new[]
            {
                root,
                starsT != null ? starsT.gameObject : null,
                sunDiscT != null ? sunDiscT.gameObject : null,
                moonT != null ? moonT.gameObject : null,
                precipT != null ? precipT.gameObject : null
            };
            foreach (var go in objects)
            {
                if (go == null) continue;
                foreach (var filter in go.GetComponentsInChildren<MeshFilter>(true)) Own(owned, filter.sharedMesh);
                foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
                    foreach (var material in renderer.sharedMaterials) Own(owned, material);
                UnityEngine.Object.Destroy(go);
            }
            foreach (var item in owned) UnityEngine.Object.Destroy(item);
        }

        static void Own(HashSet<UnityEngine.Object> owned, UnityEngine.Object item)
        {
            if (item != null) owned.Add(item);
        }

        static void Own(HashSet<UnityEngine.Object> owned, Bucket[] items)
        {
            if (items == null) return;
            foreach (var bucket in items)
            {
                if (bucket == null) continue;
                Own(owned, bucket.mesh); Own(owned, bucket.mat);
            }
        }
    }
}
