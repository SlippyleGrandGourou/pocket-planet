using UnityEngine;

namespace LivingPlanet
{
    // Cube-sphere: 6 faces of N x N cells. Cell index = face*N*N + j*N + i.
    // Neighbour slots: 0:-u 1:+u 2:-v 3:+v (symmetrical graph, -1 = none).
    public sealed class PlanetGrid
    {
        public static readonly Vector3[] FaceN =
        {
            new Vector3(1, 0, 0), new Vector3(-1, 0, 0), new Vector3(0, 1, 0),
            new Vector3(0, -1, 0), new Vector3(0, 0, 1), new Vector3(0, 0, -1)
        };
        public static readonly Vector3[] FaceA =
        {
            new Vector3(0, 0, -1), new Vector3(0, 0, 1), new Vector3(1, 0, 0),
            new Vector3(1, 0, 0), new Vector3(1, 0, 0), new Vector3(-1, 0, 0)
        };
        public static readonly Vector3[] FaceB =
        {
            new Vector3(0, 1, 0), new Vector3(0, 1, 0), new Vector3(0, 0, -1),
            new Vector3(0, 0, 1), new Vector3(0, 1, 0), new Vector3(0, 1, 0)
        };

        static readonly int[] DI = { -1, 1, 0, 0 };
        static readonly int[] DJ = { 0, 0, -1, 1 };

        public readonly int N;
        public readonly int CellCount;
        public readonly float Radius;
        public readonly float CellSize;
        public readonly Vector3[] Dir;
        public readonly int[] Nbr;
        public readonly int[] Back;
        public readonly Vector3[] Tan;
        public readonly float[] Dist;
        public readonly float[] Area;
        public readonly Vector3[] EdgeFlux;
        public readonly Vector3[] VertDir;
        public readonly int[] VertCells;

        public int VertsPerFace { get { return (N + 1) * (N + 1); } }

        public PlanetGrid(int n, float radius)
        {
            N = n;
            Radius = radius;
            CellSize = radius * Mathf.PI * 0.5f / n;
            CellCount = 6 * n * n;
            Dir = new Vector3[CellCount];
            Nbr = new int[CellCount * 4];
            Back = new int[CellCount * 4];
            Tan = new Vector3[CellCount * 4];
            Dist = new float[CellCount * 4];
            Area = new float[CellCount];
            EdgeFlux = new Vector3[CellCount * 4];

            for (int f = 0; f < 6; f++)
                for (int j = 0; j < n; j++)
                    for (int i = 0; i < n; i++)
                        Dir[CellIndex(f, i, j)] = FacePoint(f, 2f * (i + 0.5f) / n - 1f, 2f * (j + 0.5f) / n - 1f);

            var raw = new int[CellCount * 4];
            for (int f = 0; f < 6; f++)
                for (int j = 0; j < n; j++)
                    for (int i = 0; i < n; i++)
                    {
                        int c = CellIndex(f, i, j);
                        for (int k = 0; k < 4; k++)
                        {
                            int nb = CellAt(f, i + DI[k], j + DJ[k]);
                            raw[c * 4 + k] = nb == c ? -1 : nb;
                        }
                    }

            // keep only reciprocal edges so flow/advection conserve mass
            for (int c = 0; c < CellCount; c++)
                for (int k = 0; k < 4; k++)
                {
                    int nb = raw[c * 4 + k];
                    Nbr[c * 4 + k] = -1;
                    Back[c * 4 + k] = -1;
                    if (nb < 0) continue;
                    for (int kk = 0; kk < 4; kk++)
                        if (raw[nb * 4 + kk] == c) { Nbr[c * 4 + k] = nb; Back[c * 4 + k] = kk; break; }
                }

            for (int c = 0; c < CellCount; c++)
                for (int k = 0; k < 4; k++)
                {
                    int nb = Nbr[c * 4 + k];
                    if (nb < 0) { Dist[c * 4 + k] = CellSize; continue; }
                    Vector3 d = Dir[c];
                    Vector3 t = Dir[nb] - d;
                    t -= d * Vector3.Dot(t, d);
                    Tan[c * 4 + k] = t.sqrMagnitude > 1e-12f ? t.normalized : Vector3.zero;
                    Dist[c * 4 + k] = Radius * Mathf.Acos(Mathf.Clamp(Vector3.Dot(d, Dir[nb]), -1f, 1f));
                }

            int vpf = VertsPerFace;
            VertDir = new Vector3[6 * vpf];
            VertCells = new int[6 * vpf * 4];
            for (int f = 0; f < 6; f++)
                for (int q = 0; q <= n; q++)
                    for (int p = 0; p <= n; p++)
                    {
                        int vi = f * vpf + q * (n + 1) + p;
                        VertDir[vi] = FacePoint(f, 2f * p / n - 1f, 2f * q / n - 1f);
                        VertCells[vi * 4 + 0] = CellAt(f, p - 1, q - 1);
                        VertCells[vi * 4 + 1] = CellAt(f, p, q - 1);
                        VertCells[vi * 4 + 2] = CellAt(f, p - 1, q);
                        VertCells[vi * 4 + 3] = CellAt(f, p, q);
                    }

            for (int f = 0; f < 6; f++)
                for (int j = 0; j < n; j++)
                    for (int i = 0; i < n; i++)
                    {
                        int c = CellIndex(f, i, j);
                        double u0 = 2.0 * i / n - 1.0, u1 = 2.0 * (i + 1) / n - 1.0;
                        double v0 = 2.0 * j / n - 1.0, v1 = 2.0 * (j + 1) / n - 1.0;
                        Area[c] = (float)((SolidAngle(u1, v1) - SolidAngle(u0, v1) -
                            SolidAngle(u1, v0) + SolidAngle(u0, v0)) * radius * radius);
                        int vi = f * vpf + j * (n + 1) + i;
                        Vector3 bottomLeft = VertDir[vi], bottomRight = VertDir[vi + 1];
                        Vector3 topLeft = VertDir[vi + n + 1], topRight = VertDir[vi + n + 2];
                        SetEdgeFlux(c, 0, bottomLeft, topLeft);
                        SetEdgeFlux(c, 1, bottomRight, topRight);
                        SetEdgeFlux(c, 2, bottomLeft, bottomRight);
                        SetEdgeFlux(c, 3, topLeft, topRight);
                    }
        }

        static double SolidAngle(double u, double v)
        {
            return System.Math.Atan2(u * v, System.Math.Sqrt(1.0 + u * u + v * v));
        }

        void SetEdgeFlux(int c, int k, Vector3 a, Vector3 b)
        {
            int edge = c * 4 + k;
            int n = Nbr[edge];
            if (n < 0) return;
            float sign = Vector3.Dot(Vector3.Cross(a, b), Dir[n] - Dir[c]) >= 0f ? 1f : -1f;
            // Flux intégré sur l'arête pour une rotation autour d'un axe unitaire.
            EdgeFlux[edge] = (a - b) * sign;
        }

        public static Vector3 FacePoint(int f, float u, float v)
        {
            return (FaceN[f] + u * FaceA[f] + v * FaceB[f]).normalized;
        }

        public int CellIndex(int f, int i, int j) { return f * N * N + j * N + i; }

        public int CellAt(int f, int i, int j)
        {
            bool oi = i < 0 || i >= N, oj = j < 0 || j >= N;
            if (!oi && !oj) return CellIndex(f, i, j);
            if (oi && oj) return -1;
            float u = 2f * (i + 0.5f) / N - 1f;
            float v = 2f * (j + 0.5f) / N - 1f;
            return CellFromDir(FaceN[f] + u * FaceA[f] + v * FaceB[f]);
        }

        public int CellFromDir(Vector3 d)
        {
            float ax = Mathf.Abs(d.x), ay = Mathf.Abs(d.y), az = Mathf.Abs(d.z);
            int f;
            if (ax >= ay && ax >= az) f = d.x > 0 ? 0 : 1;
            else if (ay >= az) f = d.y > 0 ? 2 : 3;
            else f = d.z > 0 ? 4 : 5;
            float dn = Vector3.Dot(d, FaceN[f]);
            float u = Vector3.Dot(d, FaceA[f]) / dn;
            float v = Vector3.Dot(d, FaceB[f]) / dn;
            int i = Mathf.Clamp(Mathf.FloorToInt((u + 1f) * 0.5f * N), 0, N - 1);
            int j = Mathf.Clamp(Mathf.FloorToInt((v + 1f) * 0.5f * N), 0, N - 1);
            return CellIndex(f, i, j);
        }
    }
}
