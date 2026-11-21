using System;
using System.Collections.Generic;
using UnityEngine;

namespace LivingPlanet
{
    // Continentalité : part de terres émergées dans un rayon d'une vingtaine de degrés autour d'un point, de 0 en
    // plein océan à 1 au cœur d'un grand continent. C'est elle qui place les centres d'action de la pression
    // (dépression thermique d'été et anticyclone d'hiver sur les continents, l'inverse sur les océans).
    // Les terres sont comptées par blocs de cellules : lisser directement la grille fine sur une telle distance
    // demanderait des centaines de passes à chaque mise à jour du vent.
    // Rien à sauvegarder : la valeur d'une cellule ne dépend que des terres actuelles des blocs à sa portée, et
    // elle est recalculée dès que l'un d'eux change (une côte qui avance ou recule). Une partie rechargée retrouve
    // donc exactement les mêmes valeurs.
    public sealed class Continentality
    {
        const float Reach = 0.35f;
        const int MaxSide = 12;

        readonly PlanetGrid grid;
        readonly int side, perFace;
        readonly float cosReach;
        readonly Vector3[] blockDir;
        readonly float[] blockArea, blockLand, counted, around;
        readonly bool[] stale;
        bool filled;
        readonly int[] nearStart, near;

        // Part de terres sur toute la planète.
        public float Mean { get; private set; }

        public Continentality(PlanetGrid grid)
        {
            this.grid = grid;
            side = Mathf.Min(grid.N, MaxSide);
            perFace = side * side;
            cosReach = Mathf.Cos(Reach);
            int blocks = 6 * perFace;
            blockDir = new Vector3[blocks];
            blockArea = new float[blocks];
            blockLand = new float[blocks];
            counted = new float[blocks];
            stale = new bool[blocks];
            around = new float[grid.CellCount];
            for (int c = 0; c < grid.CellCount; c++)
            {
                int b = Block(c);
                blockDir[b] += grid.Dir[c] * grid.Area[c];
                blockArea[b] += grid.Area[c];
            }
            for (int b = 0; b < blocks; b++) blockDir[b].Normalize();

            // Un bloc compte pour une cellule dès que son centre est à portée : la liste des blocs voisins d'un bloc
            // doit donc couvrir la portée augmentée de l'écart entre une cellule et le centre de son propre bloc.
            float closest = 1f;
            for (int c = 0; c < grid.CellCount; c++) closest = Mathf.Min(closest, Vector3.Dot(grid.Dir[c], blockDir[Block(c)]));
            float cosList = Mathf.Cos(Mathf.Min(Mathf.PI, Reach + Mathf.Acos(Mathf.Clamp(closest, -1f, 1f))));
            var list = new List<int>();
            nearStart = new int[blocks + 1];
            for (int a = 0; a < blocks; a++)
            {
                nearStart[a] = list.Count;
                for (int b = 0; b < blocks; b++)
                    if (Vector3.Dot(blockDir[a], blockDir[b]) >= cosList) list.Add(b);
            }
            nearStart[blocks] = list.Count;
            near = list.ToArray();
        }

        int Block(int c)
        {
            int n = grid.N, inFace = c % (n * n);
            return c / (n * n) * perFace + (inFace / n * side / n) * side + inFace % n * side / n;
        }

        // Recompte les terres émergées de chaque bloc, puis met à jour les cellules à portée des blocs qui ont changé.
        public void Refresh(PlanetSim sim)
        {
            Array.Clear(counted, 0, counted.Length);
            double land = 0, all = 0;
            for (int c = 0; c < grid.CellCount; c++)
            {
                all += grid.Area[c];
                if (sim.rock[c] + sim.sed[c] <= sim.SeaLevel) continue;
                counted[Block(c)] += grid.Area[c];
                land += grid.Area[c];
            }
            Mean = (float)(land / all);

            bool any = !filled;
            for (int b = 0; b < stale.Length; b++) stale[b] = !filled;
            for (int b = 0; b < counted.Length; b++)
            {
                if (counted[b] == blockLand[b]) continue;
                blockLand[b] = counted[b];
                any = true;
                for (int k = nearStart[b]; k < nearStart[b + 1]; k++) stale[near[k]] = true;
            }
            if (!any) return;
            filled = true;
            Par.For(grid.CellCount, (s, e) =>
            {
                for (int c = s; c < e; c++)
                    if (stale[Block(c)]) around[c] = Around(c);
            });
        }

        // Part de terres autour de la cellule.
        public float At(int c) { return around[c]; }

        // Les blocs proches pèsent plus que ceux en limite de portée.
        float Around(int c)
        {
            Vector3 d = grid.Dir[c];
            int b = Block(c);
            float land = 0f, all = 0f;
            for (int k = nearStart[b]; k < nearStart[b + 1]; k++)
            {
                int o = near[k];
                float w = Vector3.Dot(d, blockDir[o]) - cosReach;
                if (w <= 0f) continue;
                w *= w;
                land += w * blockLand[o];
                all += w * blockArea[o];
            }
            return all > 0f ? land / all : 0f;
        }
    }
}
