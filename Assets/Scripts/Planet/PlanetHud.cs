using System;
using UnityEngine;

namespace LivingPlanet
{
    // Interface : bouton "Options" en haut à droite (couches, réglages, graphiques des systèmes, informations)
    public sealed partial class PlanetWorld
    {
        // Échantillonnage sur l'horloge de simulation : 48 points par jour, mémoire = 1 année (12 jours)
        const int SamplesPerDay = 48;
        const int GraphCap = SamplesPerDay * 12;
        // Historique long terme : un point tous les jours simulés,
        // donc 2000 points ≈ 2000 jours. Bien plus grossier que le suivi court terme, pour rester léger.
        const int LongCap = 2000;
        const int LongEvery = SamplesPerDay;
        static readonly int[] GraphWin = { SamplesPerDay, SamplesPerDay * 3, SamplesPerDay * 12 };
        static readonly string[] GraphPeriods = { "Dernière journée", "Dernière saison", "Dernière année" };
        int graphPeriod = 1;
        float sampleAcc;
        int longCounter;

        sealed class Series
        {
            public string name; public Color col; public float[] v = new float[GraphCap]; public int n, head; public string unit;
            public float[] lv = new float[LongCap]; public int ln, lhead;
            public void Add(float x)
            {
                if (n < GraphCap) { v[(head + n) % GraphCap] = x; n++; }
                else { v[head] = x; head = (head + 1) % GraphCap; }
            }
            public float At(int i) { return v[(head + i) % GraphCap]; }
            public float Last { get { return n > 0 ? At(n - 1) : 0f; } }
            public void AddLong(float x)
            {
                if (ln < LongCap) { lv[(lhead + ln) % LongCap] = x; ln++; }
                else { lv[lhead] = x; lhead = (lhead + 1) % LongCap; }
            }
            public float LongAt(int i) { return lv[(lhead + i) % LongCap]; }
        }

        static readonly string[] PlantNames = { "Feuillus", "Conifères", "Buissons", "Palmiers", "Cactus", "Baies", "Roseaux", "Algues" };
        static readonly string[] GraphTabs = { "Climat", "Végétation", "Faune", "Ressources", "Géologie" };
        static readonly Color[] SeriesCols =
        {
            new Color(1f, 0.45f, 0.35f), new Color(0.4f, 0.8f, 1f), new Color(0.9f, 0.9f, 0.9f), new Color(0.5f, 0.6f, 1f),
            new Color(0.5f, 0.9f, 0.4f), new Color(1f, 0.85f, 0.3f), new Color(0.9f, 0.5f, 0.9f), new Color(0.3f, 0.9f, 0.8f)
        };

        Series[] sClimate, sVeg, sFauna, sGeo, sRes;
        readonly float[] resBuf = new float[4];
        int graphTab;
        Rect layerRect, mapRect, mapPanelRect;
        int lastQuakes, lastSlides;
        Texture2D mapTexture;
        PlanetGrid mapGrid;
        int[] mapCells;
        Color32[] mapPixels;
        float mapRefreshAt;
        int mapViewMode = -1, mapLayerState = -1;
        const int MapWidth = 256, MapHeight = 144;
        float materialInfoRefresh;
        string materialInfo;

        void ResetStats() { sClimate = sVeg = sFauna = sGeo = sRes = null; sampleAcc = 0f; longCounter = 0; materialInfo = null; }

        // avance l'horloge des graphiques (secondes simulées) et enregistre les échantillons dus
        void TickStats(float simSeconds)
        {
            sampleAcc += simSeconds;
            float interval = dayMinutes * 60f / SamplesPerDay;
            int guard = 0;
            while (sampleAcc >= interval && guard++ < 8) { sampleAcc -= interval; RecordStats(); }
            if (sampleAcc > interval) sampleAcc = 0f;
        }

        static Series[] MakeSeries(string[] names, string[] units, int offset = 0)
        {
            var r = new Series[names.Length];
            for (int i = 0; i < r.Length; i++)
                r[i] = new Series { name = names[i], col = SeriesCols[(i + offset) % SeriesCols.Length], unit = units != null ? units[i] : "" };
            return r;
        }

        void RecordStats()
        {
            if (Sim == null || Eco == null || Fauna == null) return;
            if (sClimate == null)
            {
                sClimate = MakeSeries(new[] { "Température", "Humidité de l'air", "Nuages", "Précipitations" }, new[] { "°C", "", "%", "" });
                sVeg = MakeSeries(PlantNames, null);
                var fn = new string[Fauna.Count];
                for (int i = 0; i < fn.Length; i++) fn[i] = global::LivingPlanet.Fauna.Names[i];
                sFauna = MakeSeries(fn, null);
                sGeo = MakeSeries(new[] { "Séismes", "Éboulements", "Volcans actifs", "Magma moyen" }, null);
                sRes = MakeSeries(new[] { "Plancton", "Krill", "Insectes", "Charogne" }, null);
                lastQuakes = Sim.quakeCount; lastSlides = Sim.slideCount;
            }
            int M = Sim.M;
            double t = 0, h = 0, cl = 0, r = 0, mg = 0;
            int cnt = 0;
            for (int c = 0; c < M; c += 3)
            {
                t += Sim.temp[c]; h += Sim.hum[c]; cl += Sim.cloud[c]; r += Sim.rainNow[c]; mg += Sim.magma[c]; cnt++;
            }
            sClimate[0].Add((float)(t / cnt));
            sClimate[1].Add((float)(h / cnt));
            sClimate[2].Add((float)(cl / cnt * 100.0));
            sClimate[3].Add((float)(r / cnt));
            for (int i = 0; i < sVeg.Length && i < Eco.aliveByType.Length; i++) sVeg[i].Add(Eco.aliveByType[i]);
            for (int i = 0; i < sFauna.Length; i++) sFauna[i].Add(Fauna.pops[i].n);
            sGeo[0].Add(Sim.quakeCount - lastQuakes); lastQuakes = Sim.quakeCount;
            sGeo[1].Add(Sim.slideCount - lastSlides); lastSlides = Sim.slideCount;
            sGeo[2].Add(Sim.volcanoes.Count);
            sGeo[3].Add((float)(mg / cnt));
            Fauna.F.Means(resBuf);
            for (int i = 0; i < 4; i++) sRes[i].Add(resBuf[i]);

            // Historique long terme : un point par jour, dans une fenêtre bornée.
            if (++longCounter >= LongEvery)
            {
                longCounter = 0;
                RecordLong(sClimate); RecordLong(sVeg); RecordLong(sFauna); RecordLong(sGeo); RecordLong(sRes);
            }
        }

        static void RecordLong(Series[] ss)
        {
            for (int i = 0; i < ss.Length; i++) ss[i].AddLong(ss[i].Last);
        }

        static void FillRect(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        static Vector3 MapDirection(float u, float v)
        {
            float longitude = (u - 0.5f) * 2f * Mathf.PI;
            float latitude = (0.5f - v) * Mathf.PI;
            float cosLatitude = Mathf.Cos(latitude);
            return new Vector3(cosLatitude * Mathf.Cos(longitude), Mathf.Sin(latitude), cosLatitude * Mathf.Sin(longitude));
        }

        Color MapSurfaceColor(int c)
        {
            float height = Sim.Ground(c) - Sim.SeaLevel;
            if (height < 0f && Sim.water[c] > 0.05f)
                return Color.Lerp(new Color(0.10f, 0.48f, 0.52f), new Color(0.03f, 0.15f, 0.34f),
                    Mathf.Clamp01(Sim.Salinity(c) / PlanetSim.OceanSalinity));

            Color col = view.ColorCell(c);
            // Le basalte sombre émergé ne doit pas se confondre avec l'océan sur la carte.
            if (col.b > col.r && col.b > col.g && col.b < 0.35f)
                col = Color.Lerp(col, new Color(0.42f, 0.36f, 0.28f), 0.55f);
            if (height >= 0f && Sim.water[c] > 0.5f)
                col = Color.Lerp(col, new Color(0.14f, 0.42f, 0.62f), Mathf.Clamp01((Sim.water[c] - 0.5f) / 2f) * 0.2f);
            return col;
        }

        void RefreshMap()
        {
            if (mapTexture == null || mapGrid != Grid)
            {
                if (mapTexture != null) UnityEngine.Object.Destroy(mapTexture);
                mapTexture = new Texture2D(MapWidth, MapHeight, TextureFormat.RGBA32, false);
                mapTexture.wrapMode = TextureWrapMode.Repeat;
                mapTexture.filterMode = FilterMode.Point;
                mapGrid = Grid;
                mapCells = new int[MapWidth * MapHeight];
                mapPixels = new Color32[mapCells.Length];
                for (int y = 0; y < MapHeight; y++)
                    for (int x = 0; x < MapWidth; x++)
                        mapCells[y * MapWidth + x] = Grid.CellFromDir(MapDirection((x + 0.5f) / MapWidth, 1f - (y + 0.5f) / MapHeight));
            }
            for (int i = 0; i < mapCells.Length; i++)
            {
                int c = mapCells[i];
                Color col;
                if (view.ViewMode != 0)
                    col = view.ColorCell(c);
                else
                    col = MapSurfaceColor(c);
                if (view.ShowPlants && Sim.Ground(c) >= Sim.SeaLevel && Eco.cellPlant[c] >= 0)
                    col = Color.Lerp(col, new Color(0.1f, 0.7f, 0.18f), 0.5f);
                if (view.ShowClouds)
                    col = Color.Lerp(col, Color.white, Mathf.Clamp01((Sim.cloud[c] - 0.18f) / 0.82f) * 0.5f);
                mapPixels[i] = col;
            }
            if ((view.ViewMode == 4 || view.FlowOn[PlanetView.FlowTect]) && Sim.plate != null)
            {
                for (int y = 0; y < MapHeight; y++)
                    for (int x = 0; x < MapWidth; x++)
                    {
                        int index = y * MapWidth + x;
                        int c = mapCells[index];
                        int right = mapCells[y * MapWidth + (x + 1) % MapWidth];
                        int up = y + 1 < MapHeight ? mapCells[index + MapWidth] : c;
                        float a, b;
                        var ka = Sim.PlateBoundary(c, right, out a);
                        var kb = Sim.PlateBoundary(c, up, out b);
                        var kind = a >= b ? ka : kb;
                        if (kind != PlanetSim.BoundaryKind.None)
                            mapPixels[index] = PlanetView.BoundaryColors[(int)kind];
                    }
            }
            for (int l = 0; l < PlanetView.FlowLayers; l++)
            {
                if (!view.FlowOn[l]) continue;
                for (int y = 8 + l * 2; y < MapHeight - 4; y += 16)
                    for (int x = 8 + l * 2; x < MapWidth; x += 16)
                    {
                        int c = mapCells[y * MapWidth + x];
                        Vector3 flow; float magnitude;
                        if (!view.FlowField(l, c, out flow, out magnitude)) continue;
                        Vector3 d = Grid.Dir[c];
                        float lon = Mathf.Atan2(d.z, d.x);
                        Vector3 east = new Vector3(-Mathf.Sin(lon), 0f, Mathf.Cos(lon));
                        Vector3 north = Vector3.Cross(east, d).normalized;
                        Vector2 dir = new Vector2(Vector3.Dot(flow, east), Vector3.Dot(flow, north)).normalized;
                        int cls = magnitude < 0.33f ? 0 : (magnitude < 0.66f ? 1 : 2);
                        Color32 color = PlanetView.FlowCols[l][cls];
                        Vector2 end = new Vector2(x, y) + dir * (3f + magnitude * 3f);
                        MapLine(new Vector2(x, y), end, color);
                        Vector2 side = new Vector2(-dir.y, dir.x);
                        MapLine(end, end - dir * 2f + side * 2f, color);
                        MapLine(end, end - dir * 2f - side * 2f, color);
                    }
            }
            mapTexture.SetPixels32(mapPixels);
            mapTexture.Apply(false, false);
            mapRefreshAt = Time.unscaledTime + 2f;
            mapViewMode = view.ViewMode;
            mapLayerState = MapLayerState();
        }

        int MapLayerState()
        {
            int state = (view.ShowPlants ? 1 : 0) | (view.ShowClouds ? 2 : 0);
            for (int i = 0; i < PlanetView.FlowLayers; i++)
                if (view.FlowOn[i]) state |= 1 << (i + 2);
            return state;
        }

        void MapLine(Vector2 from, Vector2 to, Color32 color)
        {
            int steps = Mathf.CeilToInt(Mathf.Max(Mathf.Abs(to.x - from.x), Mathf.Abs(to.y - from.y)));
            for (int i = 0; i <= steps; i++)
            {
                Vector2 p = Vector2.Lerp(from, to, steps == 0 ? 0f : (float)i / steps);
                int x = (Mathf.RoundToInt(p.x) + MapWidth) % MapWidth;
                int y = Mathf.RoundToInt(p.y);
                if (y >= 0 && y < MapHeight) mapPixels[y * MapWidth + x] = color;
            }
        }

        void DrawMiniMapToggles()
        {
            Rect row = new Rect(mapRect.x, mapRect.y - ToggleRowHeight, mapRect.width, ToggleRowHeight - 6f);
            float cw = row.width / 2f;
            Vector2 mouse = Event.current.mousePosition;
            string hoverName = null;
            for (int i = 0; i < 2; i++)
            {
                Rect cr = new Rect(row.x + i * cw + 2f, row.y, cw - 4f, row.height);
                bool on = i == 0 ? lensOn : view.HideWater;
                var prevColor = GUI.color;
                GUI.color = on ? new Color(0.55f, 1f, 0.65f) : new Color(1f, 1f, 1f, 0.88f);
                bool clicked = GUI.Button(cr, MiniToggleIcons[i], iconBtn);
                GUI.color = prevColor;
                if (on) FillRect(new Rect(cr.x + 4f, cr.yMax - 3f, cr.width - 8f, 2f), new Color(0.55f, 1f, 0.65f, 0.95f));
                if (cr.Contains(mouse)) hoverName = MiniToggleNames[i];
                if (!clicked) continue;
                if (i == 0) lensOn = !lensOn;
                else view.SetWaterHidden(!view.HideWater);
            }
            if (hoverName != null)
            {
                Vector2 size = tooltip.CalcSize(new GUIContent(hoverName));
                Rect tip = new Rect(mouse.x - size.x / 2f - 5f, mouse.y - size.y - 22f, size.x + 10f, size.y + 4f);
                tip.x = Mathf.Clamp(tip.x, 4f, Screen.width - tip.width - 4f);
                tip.y = Mathf.Max(4f, tip.y);
                FillRect(tip, new Color(0f, 0f, 0f, 0.85f));
                GUI.Label(tip, hoverName, tooltip);
            }
        }

        void DrawMiniMap()
        {
            mapRect = new Rect(mapPanelRect.x + 6f, mapPanelRect.yMax - MapHeight - 6f, MapWidth, MapHeight);
            DrawMiniMapToggles();
            if (Event.current.type == EventType.Repaint && (mapTexture == null || mapGrid != Grid || mapViewMode != view.ViewMode || mapLayerState != MapLayerState() || Time.unscaledTime >= mapRefreshAt))
                RefreshMap();

            Vector3 position = camPos.normalized;
            float longitudeOffset = Mathf.Atan2(position.z, position.x) / (2f * Mathf.PI);
            FillRect(new Rect(mapRect.x - 2f, mapRect.y - 2f, mapRect.width + 4f, mapRect.height + 4f), new Color(1f, 1f, 1f, 0.22f));
            if (mapTexture != null)
                GUI.DrawTextureWithTexCoords(mapRect, mapTexture, new Rect(longitudeOffset, 0f, 1f, 1f));
            if (view.ViewMode == 4 || view.FlowOn[PlanetView.FlowTect])
            {
                Rect legend = new Rect(mapRect.x + 3f, mapRect.y + 3f, 144f, 71f);
                FillRect(legend, new Color(0f, 0f, 0f, 0.75f));
                string[] names = { "Divergence", "Subduction*", "Collision*", "Coulissage" };
                for (int i = 0; i < names.Length; i++)
                {
                    FillRect(new Rect(legend.x + 4f, legend.y + 4f + i * 13f, 8f, 8f), PlanetView.BoundaryColors[i + 1]);
                    GUI.Label(new Rect(legend.x + 15f, legend.y + i * 13f, 125f, 14f), names[i], tooltip);
                }
                GUI.Label(new Rect(legend.x + 4f, legend.y + 52f, 138f, 14f), "* selon le relief", tooltip);
            }

            float latitude = Mathf.Asin(Mathf.Clamp(position.y, -1f, 1f));
            Vector2 marker = new Vector2(mapRect.center.x, Mathf.Clamp(mapRect.y + (0.5f - latitude / Mathf.PI) * mapRect.height, mapRect.y + 12f, mapRect.yMax - 12f));
            Vector3 north = Vector3.ProjectOnPlane(Vector3.up, position);
            if (north.sqrMagnitude < 1e-6f) north = Vector3.ProjectOnPlane(Vector3.forward, position);
            north.Normalize();
            Vector3 east = Vector3.Cross(position, north).normalized;
            Vector3 heading = Vector3.ProjectOnPlane(camRot * Vector3.forward, position);
            if (heading.sqrMagnitude < 1e-6f) heading = Vector3.ProjectOnPlane(camRot * Vector3.up, position);
            float angle = Mathf.Atan2(Vector3.Dot(heading, east), Vector3.Dot(heading, north)) * Mathf.Rad2Deg;
            Matrix4x4 oldMatrix = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, marker);
            GUI.Label(new Rect(marker.x - 8f, marker.y - 8f, 16f, 16f), "▲", tooltip);
            GUI.matrix = oldMatrix;

            Rect resetBtnRect = new Rect(mapRect.xMax - 22f, mapRect.y + 2f, 20f, 20f);
            FillRect(resetBtnRect, new Color(0f, 0f, 0f, 0.55f));
            if (GUI.Button(resetBtnRect, new GUIContent("N↑", "Remettre le nord en haut (vue actuelle)")))
                StraightenCameraNorth();

            if (!pauseMenu && Event.current.type == EventType.MouseDown && Event.current.button == 0 &&
                mapRect.Contains(Event.current.mousePosition) && !resetBtnRect.Contains(Event.current.mousePosition))
            {
                Vector2 mouse = Event.current.mousePosition;
                float u = Mathf.Repeat((mouse.x - mapRect.x) / mapRect.width + longitudeOffset, 1f);
                float v = (mouse.y - mapRect.y) / mapRect.height;
                Vector3 target = MapDirection(u, v);
                int cell = Grid.CellFromDir(target);
                ExitFirstPerson();
                flyFromPos = camPos;
                flyFromRot = camRot;
                flyToPos = target * (radius + Mathf.Max(0f, Sim.Ground(cell)) + 240f);
                Vector3 flyUp = Vector3.ProjectOnPlane(Vector3.up, target);
                if (flyUp.sqrMagnitude < 1e-3f) flyUp = Vector3.ProjectOnPlane(Vector3.forward, target);
                flyToRot = Quaternion.LookRotation(-target, flyUp.normalized);
                flyT = 0f;
                Event.current.Use();
            }
        }

        void OnDestroy()
        {
            Application.wantsToQuit -= WantsToQuit;
            if (view != null) { view.Destroy(); view = null; }
            if (mapTexture != null) UnityEngine.Object.Destroy(mapTexture);
            if (previewTexture != null) UnityEngine.Object.Destroy(previewTexture);
        }

        // shared : toutes les courbes partagent la même échelle (effectifs) ; sinon chacune est normalisée (unités différentes)
        // Les points de la fenêtre choisie sont moyennés par colonne de pixels pour rester lisibles quel que soit l'historique.
        // longTerm : utilise l'historique grossier "depuis le début" (un point par jour) au lieu de la fenêtre jour/saison/année.
        void DrawGraph(Rect r, Series[] ss, bool shared, bool longTerm = false)
        {
            FillRect(r, new Color(0f, 0f, 0f, 0.45f));
            var pr = new Rect(r.x + 34f, r.y + 6f, r.width - 40f, r.height - 22f);
            int win = longTerm ? LongCap : GraphWin[graphPeriod];
            int avail = 0;
            for (int s = 0; s < ss.Length; s++) avail = Mathf.Max(avail, longTerm ? ss[s].ln : ss[s].n);
            int cnt = Mathf.Min(win, avail);
            if (cnt < 2) { GUI.Label(new Rect(r.x + 8f, r.y + 8f, r.width, 24f), "Collecte des données...", style); return; }
            int bins = Mathf.Max(2, Mathf.Min(cnt, Mathf.FloorToInt(pr.width / 3f)));
            float colW = pr.width / bins;
            var bv = new float[bins];

            // échelle partagée : maximum des moyennes affichées
            float gmax = 1e-6f;
            for (int pass = 0; pass < 2; pass++)
            {
                for (int s = 0; s < ss.Length; s++)
                {
                    var sr = ss[s];
                    int srCount = longTerm ? sr.ln : sr.n;
                    int m = Mathf.Min(cnt, srCount);
                    if (m < 2) continue;
                    int off = srCount - m;
                    for (int b = 0; b < bins; b++)
                    {
                        int i0 = off + b * m / bins, i1 = Mathf.Max(i0 + 1, off + (b + 1) * m / bins);
                        float sum = 0f;
                        for (int i = i0; i < i1; i++) sum += longTerm ? sr.LongAt(i) : sr.At(i);
                        bv[b] = sum / (i1 - i0);
                    }
                    if (pass == 0) { if (shared) for (int b = 0; b < bins; b++) gmax = Mathf.Max(gmax, bv[b]); continue; }

                    float lo = 0f, hi = gmax;
                    if (!shared)
                    {
                        lo = float.MaxValue; hi = float.MinValue;
                        for (int b = 0; b < bins; b++) { lo = Mathf.Min(lo, bv[b]); hi = Mathf.Max(hi, bv[b]); }
                        float pad = Mathf.Max((hi - lo) * 0.08f, 1e-4f);
                        lo -= pad; hi += pad;
                    }
                    else hi *= 1.08f;
                    float prevY = 0f;
                    for (int b = 0; b < bins; b++)
                    {
                        float y = pr.yMax - Mathf.Clamp01((bv[b] - lo) / (hi - lo)) * (pr.height - 2f) - 2f;
                        float x = pr.x + b * colW;
                        float y0 = b == 0 ? y : Mathf.Min(y, prevY);
                        float hgt = b == 0 ? 2f : Mathf.Abs(y - prevY) + 2f;
                        FillRect(new Rect(x, y0, Mathf.Max(2f, colW + 0.5f), hgt), sr.col);
                        prevY = y;
                    }
                }
            }

            // axes : repères horizontaux, valeur max (échelle partagée) et durée couverte
            for (int g = 0; g <= 2; g++) FillRect(new Rect(pr.x, pr.y + g * (pr.height - 2f) / 2f, pr.width, 1f), new Color(1f, 1f, 1f, 0.12f));
            var small = new GUIStyle(style) { fontSize = 11 };
            if (shared) GUI.Label(new Rect(r.x + 2f, pr.y - 4f, 34f, 16f), gmax >= 100f ? gmax.ToString("0") : gmax.ToString("0.#"), small);
            float spanDays = longTerm ? cnt * (LongEvery / (float)SamplesPerDay) : cnt / (float)SamplesPerDay;
            string span = spanDays >= 1f ? "−" + spanDays.ToString("0.#") + " j" : "−" + (spanDays * 24f).ToString("0") + " h";
            GUI.Label(new Rect(pr.x, r.yMax - 18f, 100f, 16f), span, small);
            GUI.Label(new Rect(pr.xMax - 70f, r.yMax - 18f, 70f, 16f), "maintenant", small);
        }

        void DrawLegend(Rect r, Series[] ss)
        {
            float colW = r.width / 2f;
            for (int i = 0; i < ss.Length; i++)
            {
                float x = r.x + (i % 2) * colW, y = r.y + (i / 2) * 22f;
                FillRect(new Rect(x, y + 6f, 10f, 10f), ss[i].col);
                string v = ss[i].Last.ToString(ss[i].Last >= 100f ? "0" : "0.##");
                GUI.Label(new Rect(x + 14f, y, colW - 14f, 22f), ss[i].name + " : " + v + (string.IsNullOrEmpty(ss[i].unit) ? "" : " " + ss[i].unit), style);
            }
        }

        // Panneau "Couches" : une grille d'icônes (vues, calques, écoulements) entièrement visible, sans texte ni défilement,
        // regroupée avec la mini-carte dans un seul panneau. Une bulle d'aide affiche le nom au survol.
        const float IconCell = 36f;
        // Rangée d'icônes dédiée, juste au-dessus de la mini-carte : "voir sous les nuages" et "enlever l'eau"
        // (déplacées hors du menu pause, voir item 5), avec bulle d'aide au survol comme le reste du panneau.
        const float ToggleRowHeight = 34f;
        static readonly string[] MiniToggleIcons = { "☁", "💧" };
        static readonly string[] MiniToggleNames = { "Voir sous les nuages", "Enlever l'eau" };

        void DrawLayerButtons()
        {
            int toggleCount = 3;
            int totalIcons = ViewNames.Length + toggleCount + PlanetView.FlowLayers;
            float width = MapWidth + 12f;
            int columns = Mathf.Max(4, Mathf.FloorToInt(width / IconCell));
            int rows = Mathf.CeilToInt(totalIcons / (float)columns);
            const float headerHeight = 22f;
            float gridHeight = rows * IconCell;
            float height = headerHeight + gridHeight + 8f;
            mapPanelRect = new Rect(Screen.width - width - 10f, Screen.height - height - ToggleRowHeight - MapHeight - 18f, width, height + ToggleRowHeight + MapHeight + 12f);
            layerRect = new Rect(mapPanelRect.x, mapPanelRect.y, width, height);
            FillRect(mapPanelRect, new Color(0.03f, 0.04f, 0.07f, 0.78f));
            FillRect(new Rect(mapPanelRect.x, mapPanelRect.y, width, 1f), new Color(1f, 1f, 1f, 0.2f));
            FillRect(new Rect(mapPanelRect.x, mapPanelRect.y + headerHeight, width, 1f), new Color(1f, 1f, 1f, 0.08f));
            GUI.Label(new Rect(layerRect.x + 8f, layerRect.y, width - 16f, headerHeight), "Couches", style);

            float gx0 = layerRect.x + 6f, gy0 = layerRect.y + headerHeight;
            Vector2 mouse = Event.current.mousePosition;
            string hoverName = null;
            for (int i = 0; i < totalIcons; i++)
            {
                int col = i % columns, row = i / columns;
                Rect cr = new Rect(gx0 + col * IconCell, gy0 + row * IconCell, IconCell - 4f, IconCell - 4f);
                string icon; string name; bool on;
                if (i < ViewNames.Length) { icon = ViewIcons[i]; name = ViewNames[i]; on = view.ViewMode == i; }
                else if (i < ViewNames.Length + toggleCount)
                {
                    int t = i - ViewNames.Length;
                    icon = ToggleIcons[t]; name = ToggleNames[t];
                    on = t == 0 ? view.ShowPlants : (t == 1 ? view.ShowClouds : view.ShowAtmosphere);
                }
                else
                {
                    int f = i - ViewNames.Length - toggleCount;
                    icon = PlanetView.FlowIcons[f]; name = PlanetView.FlowNames[f]; on = view.FlowOn[f];
                }
                var prevColor = GUI.color;
                GUI.color = on ? new Color(0.55f, 1f, 0.65f) : new Color(1f, 1f, 1f, 0.88f);
                bool clicked = GUI.Button(cr, icon, iconBtn);
                GUI.color = prevColor;
                if (on) FillRect(new Rect(cr.x + 4f, cr.yMax - 3f, cr.width - 8f, 2f), new Color(0.55f, 1f, 0.65f, 0.95f));
                if (cr.Contains(mouse)) hoverName = name;
                if (!clicked) continue;
                if (i < ViewNames.Length) SetView(i);
                else if (i < ViewNames.Length + toggleCount)
                {
                    int t = i - ViewNames.Length;
                    if (t == 0) view.ShowPlants = !view.ShowPlants;
                    else if (t == 1) view.ShowClouds = !view.ShowClouds;
                    else view.ShowAtmosphere = !view.ShowAtmosphere;
                }
                else view.FlowOn[i - ViewNames.Length - toggleCount] = !view.FlowOn[i - ViewNames.Length - toggleCount];
            }
            if (hoverName != null)
            {
                Vector2 size = tooltip.CalcSize(new GUIContent(hoverName));
                Rect tip = new Rect(mouse.x - size.x / 2f - 5f, mouse.y - size.y - 22f, size.x + 10f, size.y + 4f);
                tip.x = Mathf.Clamp(tip.x, 4f, Screen.width - tip.width - 4f);
                tip.y = Mathf.Max(4f, tip.y);
                FillRect(tip, new Color(0f, 0f, 0f, 0.85f));
                GUI.Label(tip, hoverName, tooltip);
            }
        }

        string InfoText()
        {
            int c = Grid.CellFromDir(camPos);
            int pc = Pick();
            if (pc >= 0) c = pc;
            float g = Sim.Ground(c);
            int season = Mathf.Min(3, (int)(Sim.Phase * 4f));
            int alive = 0;
            for (int i = 0; i < Eco.aliveByType.Length; i++) alive += Eco.aliveByType[i];
            var ab = Eco.aliveByType;
            string fa = "";
            for (int i = 0; i < Fauna.Count; i++) fa += global::LivingPlanet.Fauna.Names[i] + " " + Fauna.pops[i].n + "   ";
            if (materialInfo == null || Time.unscaledTime >= materialInfoRefresh)
            {
                double geo, water, salt, geoArea, waterArea, saltArea;
                Sim.MaterialIndices(out geo, out water, out salt, out geoArea, out waterArea, out saltArea);
                materialInfo = "<b>Indices de matière</b> (sommes par cellule, sans gaz ni vivant ; pas des masses physiques)\n" +
                    "Géologie " + geo.ToString("0.0") + "  pondérée par aire " + geoArea.ToString("0.0") +
                    "  eau " + water.ToString("0.0") + " (" + waterArea.ToString("0.0") + " pondérée)" +
                    "  sel " + salt.ToString("0.0") + " (" + saltArea.ToString("0.0") + " pondéré)\n" +
                    "Apports météoritiques (depuis version 11) : " + Sim.meteoriteAdded.ToString("0.0") + "\n" +
                    "Apports du mode dieu (depuis version 10) : solides " + Sim.externalAdded[PlanetSim.ExternalSolid].ToString("0.0") +
                    "  eau " + Sim.externalAdded[PlanetSim.ExternalWater].ToString("0.0") +
                    "  sel " + Sim.externalAdded[PlanetSim.ExternalSalt].ToString("0.0") + "\n" +
                    "Gaz injectés (sommes des concentrations) : O2 " + Sim.externalAdded[PlanetSim.ExternalO2].ToString("0.0") +
                    "  CO2 " + Sim.externalAdded[PlanetSim.ExternalCO2].ToString("0.0") +
                    "  CH4 " + Sim.externalAdded[PlanetSim.ExternalCH4].ToString("0.0") +
                    "  SO2 " + Sim.externalAdded[PlanetSim.ExternalSO2].ToString("0.0");
                materialInfoRefresh = Time.unscaledTime + 2f;
            }

            return "<b>Planète vivante</b>   FPS " + fps.ToString("0") +
                "   Sim " + lastTickMs.ToString("0.0") + " ms" + (paused ? "   <color=#ff8>[PAUSE]</color>" : "") + "\n" +
                "Année " + Sim.YearNumber + "  Mois " + (Mathf.Min(11, (int)(Sim.Phase * 12f)) + 1) + "/12  (" + SeasonNames[season] + ")   Vitesse x" + timeScale.ToString("0.##") +
                (!paused && effSpeed > 0f && effSpeed < timeScale * 0.9f ? " (réel x" + effSpeed.ToString("0.#") + ")" : "") + "   Vue : " + ViewNames[view.ViewMode] + "\n" +
                "Altitude caméra " + (camPos.magnitude - radius).ToString("0") + "\n\n" +
                "<b>Cellule visée</b>\nAlt " + g.ToString("0.0") + "  T " + Sim.temp[c].ToString("0.0") + "°C  humidité sol " + Sim.moist[c].ToString("0.00") +
                "  neige " + Sim.snow[c].ToString("0.0") + "  eau " + Sim.water[c].ToString("0.00") + "  sel " + (Sim.Salinity(c) * 1000f).ToString("0.0") + " g/kg" +
                (Sim.FreshWater(c) ? " (douce)" : Sim.water[c] > 0.1f ? " (non potable)" : "") +
                "  dépôt de sel " + Sim.waterState.deposit[c].ToString("0.00") + "  débit " + Sim.flux[c].ToString("0.00") + "  nuages " + Sim.cloud[c].ToString("0.00") + "\n" +
                "Sol : " + PlanetSim.MatNames[Sim.SurfaceMaterial(c)] + "   roche : " + PlanetSim.RockNames[Sim.rockT[c]] +
                (Sim.ore[c] != 0 ? " (" + PlanetSim.MatNames[Sim.ore[c]] + ")" : "") + "   couche " + Sim.sed[c].ToString("0.00") + "   fertilité " + Sim.Fertility(c).ToString("0.00") + "\n" +
                "Nappe " + Sim.GwFill(c).ToString("0.00") + "  perméabilité " + Sim.Perm(c).ToString("0.00") + "  pression " + (1013f + Sim.pres[c] * 12f).ToString("0") + " hPa  vent " + Sim.wind[c].magnitude.ToString("0.0") + "\n" +
                "O2 " + Sim.o2[c].ToString("0.0") + "%  CO2 " + Sim.co2[c].ToString("0") + " ppm  CH4 " + Sim.ch4[c].ToString("0.0") + "  SO2 " + Sim.so2[c].ToString("0.0") + "   feux " + Sim.FireCount + "\n\n" +
                "<b>Vivant</b>\nPlantes vivantes " + alive + " : feuillus " + ab[0] + ", conifères " + ab[1] + ", buissons " + ab[2] + ", palmiers " + ab[3] +
                ", cactus " + ab[4] + ", baies " + ab[5] + ", roseaux " + ab[6] + ", algues " + ab[7] + "\n" +
                "Faune : " + fa + "\n" +
                "Naissances " + Eco.births + "  morts " + Eco.deaths + "  incendies " + Eco.fires + "\n\n" +
                "<b>Géologie</b>\nVolcans actifs " + Sim.volcanoes.Count +
                "  séismes " + Sim.quakeCount + "  éboulements " + Sim.slideCount + "  tsunamis " + Sim.tsunamiCount + "\n\n" + materialInfo;
        }

        void DrawCellDebug()
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            bool locked = Cursor.lockState == CursorLockMode.Locked;
            if (!locked && (mouse == null || MouseOverUi(mouse))) return;
            int c = locked ? Pick() : PickDebugMouse(mouse);
            if (c < 0) return;

            Vector3 d = Grid.Dir[c];
            float lat = Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f)) * Mathf.Rad2Deg;
            float lon = Mathf.Atan2(d.z, d.x) * Mathf.Rad2Deg;
            float ground = Sim.Ground(c);
            int plant = Eco.cellPlant[c];
            string vegetation = plant >= 0 ? PlantNames[Eco.plants[plant].type] : "aucune";
            var animals = new System.Text.StringBuilder();
            for (int species = 0; species < Fauna.Count; species++)
            {
                var pop = Fauna.pops[species];
                int count = 0;
                for (int i = 0; i < pop.n; i++)
                    if (!pop.a[i].dead && pop.a[i].cell == c) count++;
                if (count > 0) animals.Append(Fauna.Names[species]).Append(' ').Append(count).Append("  ");
            }
            int b = c * 4;
            string text = "<b>Cellule " + c + "</b>  " + lat.ToString("0.0") + "°  " + lon.ToString("0.0") + "°\n" +
                (Sim.IsOcean(c) ? "Océan" : Sim.water[c] > 0.08f ? "Terre inondée" : "Terre") +
                "  altitude " + ground.ToString("0.0") +
                "  profondeur " + Sim.water[c].ToString("0.0") + "\n" +
                "Sol " + PlanetSim.MatNames[Sim.SurfaceMaterial(c)] + "  roche " + PlanetSim.RockNames[Sim.rockT[c]] +
                "  couche " + Sim.sed[c].ToString("0.00") + "\n" +
                "Terre/sable/gravier/argile " + Sim.mix[b].ToString("0.00") + " / " + Sim.mix[b + 1].ToString("0.00") +
                " / " + Sim.mix[b + 2].ToString("0.00") + " / " + Sim.mix[b + 3].ToString("0.00") + "\n" +
                "Fertilité " + Sim.Fertility(c).ToString("0.00") + "  humidité " + Sim.moist[c].ToString("0.00") +
                "  température " + Sim.temp[c].ToString("0.0") + " °C\n" +
                "Pression " + (1013f + Sim.pres[c] * 12f).ToString("0") + " hPa  eau " + Sim.water[c].ToString("0.00") +
                "  nappe " + Sim.gw[c].ToString("0.00") + "\n" +
                "Débit " + Sim.flux[c].ToString("0.00") + "  salinité " + (Sim.Salinity(c) * 1000f).ToString("0.0") +
                " g/kg  pente " + Sim.Slope(c).ToString("0.00") + "\n" +
                "Vent " + Sim.wind[c].magnitude.ToString("0.00") + "  magma " + Sim.magma[c].ToString("0.00") +
                "  convergence " + (Sim.conv == null ? 0f : Sim.conv[c]).ToString("0.00") +
                "  rift " + (Sim.rift == null ? 0f : Sim.rift[c]).ToString("0.00") + "\n" +
                "Végétation " + vegetation + "  couverture " + Sim.cover[c].ToString("0.00") + "\n" +
                "Faune " + (animals.Length == 0 ? "aucune" : animals.ToString());
            Vector2 pointer = locked ? new Vector2(Screen.width * 0.5f, Screen.height * 0.5f) : Event.current.mousePosition;
            float width = Mathf.Min(390f, Screen.width - 8f), height = 190f;
            float x = pointer.x + 18f;
            float y = pointer.y + 18f;
            if (x + width > Screen.width - 4f) x = pointer.x - width - 18f;
            if (y + height > Screen.height - 4f) y = pointer.y - height - 18f;
            var rect = new Rect(Mathf.Max(4f, x), Mathf.Max(4f, y), width, height);
            FillRect(rect, new Color(0f, 0f, 0f, 0.85f));
            GUI.Label(new Rect(rect.x + 6f, rect.y + 4f, rect.width - 12f, rect.height - 8f), text, style);
        }

        int PickDebugMouse(UnityEngine.InputSystem.Mouse mouse)
        {
            Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            float outer = radius + Sim.MaxHeight + 30f;
            float b = Vector3.Dot(ray.origin, ray.direction);
            float discriminant = b * b - (ray.origin.sqrMagnitude - outer * outer);
            if (discriminant < 0f) return -1;
            float root = Mathf.Sqrt(discriminant);
            float start = Mathf.Max(0f, -b - root), end = -b + root;
            float step = Mathf.Max(0.5f, Grid.CellSize * 0.25f);
            for (float distance = start; distance <= end; distance += step)
            {
                Vector3 p = ray.origin + ray.direction * distance;
                int c = Grid.CellFromDir(p);
                if (p.magnitude <= radius + Sim.Ground(c) + Mathf.Max(0f, Sim.water[c]))
                    return c;
            }
            return -1;
        }
    }
}
