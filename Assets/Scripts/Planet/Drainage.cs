using System.Collections.Generic;
using UnityEngine;

namespace LivingPlanet
{
    // Drainage du relief généré. Le bruit qui dessine les continents laisse des cuvettes fermées, parfois de
    // grands bassins dont le fond est sous le niveau de la mer et le seuil très au-dessus : la pluie les
    // remplit jusqu'à ce seuil et noie les terres alentour, là où de vraies rivières auraient depuis longtemps
    // comblé les creux et entaillé les seuils. On fait donc ce travail une fois, à la génération.
    public sealed partial class PlanetSim
    {
        // Épaisseur d'alluvions déposée au plus dans une cuvette, en part du plafond du relief : les creux moins
        // profonds disparaissent.
        const float BasinFillShare = 0.02f;
        // Profondeur de lac laissée au plus dans un bassin plus profond, après comblement : son seuil est
        // entaillé (vallée de l'exutoire) jusqu'à cette hauteur au-dessus du fond.
        const float LakeDepthShare = 0.015f;
        // Pente minimale laissée sur les plaines comblées et dans les vallées entaillées, pour qu'elles s'écoulent.
        const float BasinDrainSlope = 0.02f;
        // Profondeur du détroit ouvert vers une mer intérieure (bassin dont le fond reste sous le niveau de la mer).
        const float StraitDepth = 2f;

        // Cellules de l'océan : la plus vaste étendue d'un seul tenant sous le niveau de la mer. Les autres
        // étendues sont des bassins intérieurs, isolés par des terres.
        bool[] OpenSea()
        {
            var seen = new bool[M];
            var region = new List<int>();
            var largest = new List<int>();
            for (int start = 0; start < M; start++)
            {
                if (seen[start] || Ground(start) >= SeaLevel) continue;
                region.Clear();
                region.Add(start); seen[start] = true;
                for (int i = 0; i < region.Count; i++)
                    for (int k = 0; k < 4; k++)
                    {
                        int n = G.Nbr[region[i] * 4 + k];
                        if (n < 0 || seen[n] || Ground(n) >= SeaLevel) continue;
                        seen[n] = true; region.Add(n);
                    }
                if (region.Count > largest.Count) { var swap = largest; largest = region; region = swap; }
            }
            var sea = new bool[M];
            for (int i = 0; i < largest.Count; i++) sea[largest[i]] = true;
            if (largest.Count == 0)
            {
                // Planète sans mer : tout s'écoule vers son point le plus bas.
                int lowest = 0;
                for (int c = 1; c < M; c++) if (Ground(c) < Ground(lowest)) lowest = c;
                sea[lowest] = true;
            }
            return sea;
        }

        // Niveau que l'eau atteindrait dans chaque cellule avant de pouvoir rejoindre la mer (inondation par
        // priorité depuis la mer) : il dépasse le sol exactement dans les cuvettes fermées. "downstream" reçoit la
        // cellule par laquelle chacune se déverse, "order" les cellules dans l'ordre où l'inondation les atteint.
        float[] SpillLevels(int[] downstream, List<int> order)
        {
            var level = new float[M];
            var seen = OpenSea();
            var heap = new CellHeap(M);
            for (int c = 0; c < M; c++)
            {
                if (downstream != null) downstream[c] = -1;
                if (!seen[c]) continue;
                level[c] = Ground(c); heap.Push(level[c], c);
            }
            while (heap.Count > 0)
            {
                int c = heap.Pop();
                if (order != null) order.Add(c);
                for (int k = 0; k < 4; k++)
                {
                    int n = G.Nbr[c * 4 + k];
                    if (n < 0 || seen[n]) continue;
                    seen[n] = true;
                    level[n] = Mathf.Max(Ground(n), level[c] + BasinDrainSlope);
                    if (downstream != null) downstream[n] = c;
                    heap.Push(level[n], n);
                }
            }
            return level;
        }

        void DrainBasins()
        {
            float fill = MaxHeight * BasinFillShare, lake = MaxHeight * LakeDepthShare;
            var downstream = new int[M];
            var order = new List<int>(M);
            float[] level = SpillLevels(downstream, order);
            // Entaille des seuils : tout fond de bassin trop profond pour être comblé obtient un exutoire
            // descendant jusqu'à la mer, le long du chemin que l'eau aurait pris en débordant.
            for (int i = 0; i < order.Count; i++)
            {
                int c = order[i];
                // Un bassin dont le fond reste sous la mer est relié à l'océan par un détroit, pas par une gorge.
                float target = Mathf.Max(Ground(c) + fill + lake, SeaLevel - StraitDepth);
                if (level[c] <= target) continue;
                for (int p = downstream[c]; p >= 0 && Ground(p) > target; p = downstream[p])
                {
                    rock[p] = target - sed[p];
                    target -= BasinDrainSlope;
                }
            }
            // Comblement : les creux restants reçoivent des alluvions, jusqu'à leur seuil ou jusqu'à "fill".
            level = SpillLevels(null, null);
            for (int c = 0; c < M; c++)
            {
                float g = Ground(c);
                if (level[c] > g) sed[c] += Mathf.Min(level[c] - g, fill);
            }
        }

        // Tas binaire minimal (clé = niveau, valeur = cellule), suffisant pour l'inondation par priorité.
        sealed class CellHeap
        {
            readonly float[] keys;
            readonly int[] cells;
            public int Count;

            public CellHeap(int capacity) { keys = new float[capacity]; cells = new int[capacity]; }

            public void Push(float key, int cell)
            {
                int i = Count++;
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (keys[parent] <= key) break;
                    keys[i] = keys[parent]; cells[i] = cells[parent];
                    i = parent;
                }
                keys[i] = key; cells[i] = cell;
            }

            public int Pop()
            {
                int top = cells[0];
                float key = keys[--Count];
                int cell = cells[Count];
                int i = 0;
                while (true)
                {
                    int child = i * 2 + 1;
                    if (child >= Count) break;
                    if (child + 1 < Count && keys[child + 1] < keys[child]) child++;
                    if (keys[child] >= key) break;
                    keys[i] = keys[child]; cells[i] = cells[child];
                    i = child;
                }
                keys[i] = key; cells[i] = cell;
                return top;
            }
        }
    }
}
