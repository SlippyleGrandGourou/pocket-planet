using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace LivingPlanet
{
    public sealed partial class PlanetWorld
    {
        string saveMessage = "";
        string planetSavePath;
        const float AutoSaveInterval = 300f;
        float nextAutoSave;
        bool quitSaved, selectingPlanet;
        readonly List<PlanetSave.Preview> savedPlanets = new List<PlanetSave.Preview>();
        int selectedPlanet = -1;
        int deleteConfirmIndex = -1;
        Vector2 savesScroll;
        Texture2D previewTexture;
        Color32[] previewPixels;
        float previewRefreshAt;
        PlanetSave.Preview loadingPreview;
        Task backgroundSave;

        // Aperçu en direct du menu principal : généré avant toute vraie simulation (pas de Grid/Sim), à partir
        // d'un échantillonnage de bruit indépendant mais calqué sur la formule de PlanetSim.Generate, pour rester
        // léger et ne pas démarrer de processus lourd tant que le joueur choisit juste ses options.
        PlanetSave.Preview menuPreview;
        int menuPreviewSeed;
        int lastMenuPreviewSeed = int.MinValue;
        float lastMenuPreviewAge = float.MinValue;

        int ParseMenuSeed()
        {
            int parsed;
            return int.TryParse(seedText, out parsed) && parsed != 0 ? parsed : menuPreviewSeed;
        }

        void RefreshMenuPreviewIfNeeded()
        {
            int s = ParseMenuSeed();
            if (menuPreview != null && s == lastMenuPreviewSeed && Mathf.Approximately(ageGyr, lastMenuPreviewAge)) return;
            menuPreview = MakeMenuPreview(s);
            lastMenuPreviewSeed = s; lastMenuPreviewAge = ageGyr;
        }

        PlanetSave.Preview MakeMenuPreview(int s)
        {
            Noise.Seed(s);
            var rnd = new System.Random(s * 31 + 7);
            Vector3 Off(float sc) { return new Vector3((float)rnd.NextDouble() * 80f - 40f, (float)rnd.NextDouble() * 80f - 40f, (float)rnd.NextDouble() * 80f - 40f) * sc; }
            Vector3 o1 = Off(1), o2 = Off(1), o3 = Off(1), o4 = Off(1), o5 = Off(1);
            int w = PlanetSave.Preview.Width, h = PlanetSave.Preview.Height;
            var raw = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    Vector3 d = MapDirection((x + 0.5f) / w, (y + 0.5f) / h);
                    Vector3 wv = d + 0.22f * new Vector3(
                        Noise.Perlin(d.x * 2f + o2.x, d.y * 2f + o2.y, d.z * 2f + o2.z),
                        Noise.Perlin(d.x * 2f + o3.x, d.y * 2f + o3.y, d.z * 2f + o3.z),
                        Noise.Perlin(d.x * 2f + o4.x, d.y * 2f + o4.y, d.z * 2f + o4.z));
                    raw[y * w + x] = Noise.Fbm(wv * 1.25f + o1, 5) + 0.25f * Noise.Fbm(d * 5f + o5, 4);
                }
            var sorted = (float[])raw.Clone();
            Array.Sort(sorted);
            float seaRaw = sorted[Mathf.Clamp((int)(0.64f * sorted.Length), 0, sorted.Length - 1)];
            float lo = sorted[0], hi = sorted[sorted.Length - 1];
            float youth = Mathf.InverseLerp(8f, 0.5f, ageGyr);
            var preview = new PlanetSave.Preview { seed = s, year = 1, day = 1, season = 0, pixels = new Color32[w * h] };
            for (int i = 0; i < raw.Length; i++)
            {
                float v = raw[i];
                float elev = v >= seaRaw ? Mathf.InverseLerp(seaRaw, hi, v) * 140f : -Mathf.InverseLerp(seaRaw, lo, v) * 90f;
                Color color = elev < 0f
                    ? Color.Lerp(new Color(0.08f, 0.29f, 0.43f), new Color(0.015f, 0.06f, 0.18f), Mathf.Clamp01(-elev / 50f))
                    : Color.Lerp(new Color(0.25f, 0.46f, 0.20f), new Color(0.46f, 0.37f, 0.25f), Mathf.Clamp01(elev / 80f));
                if (elev >= 0f)
                {
                    color = Color.Lerp(color, new Color(0.9f, 0.9f, 0.88f), Mathf.Clamp01((elev - 110f) / 100f));
                    color = Color.Lerp(color, new Color(0.55f, 0.16f, 0.08f), youth * 0.3f);
                }
                preview.pixels[i] = color;
            }
            return preview;
        }

        static void WriteVector(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        static Vector3 ReadVector(BinaryReader r) { return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
        static void WriteRotation(BinaryWriter w, Quaternion q) { w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); }
        static Quaternion ReadRotation(BinaryReader r) { return new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }

        void WriteRandomStates(BinaryWriter w)
        {
            w.Write(rnd.State); w.Write(Sim.WeatherRandomState); w.Write(Sim.VolcanoRandomState);
            w.Write(Sim.FireRandomState); w.Write(Eco.RandomState); w.Write(Fauna.RandomState);
        }

        static void WriteSeries(BinaryWriter w, Series[] series)
        {
            w.Write(series != null);
            if (series == null) return;
            w.Write(series.Length);
            foreach (var s in series)
            {
                w.Write(s.name); w.Write(s.unit);
                w.Write(s.col.r); w.Write(s.col.g); w.Write(s.col.b); w.Write(s.col.a);
                w.Write(s.n); w.Write(s.head);
                for (int i = 0; i < GraphCap; i++) w.Write(s.v[i]);
                w.Write(s.ln); w.Write(s.lhead);
                for (int i = 0; i < LongCap; i++) w.Write(s.lv[i]);
            }
        }

        static Series[] ReadSeries(BinaryReader r, int version)
        {
            if (!r.ReadBoolean()) return null;
            int count = r.ReadInt32();
            if (count < 0 || count > 32) throw new InvalidDataException("Nombre de courbes invalide.");
            var series = new Series[count];
            for (int i = 0; i < count; i++)
            {
                var s = new Series { name = r.ReadString(), unit = r.ReadString() };
                s.col = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                s.n = r.ReadInt32(); s.head = r.ReadInt32();
                if (s.n < 0 || s.n > GraphCap || s.head < 0 || s.head >= GraphCap)
                    throw new InvalidDataException("Historique des graphiques invalide.");
                for (int j = 0; j < GraphCap; j++) s.v[j] = r.ReadSingle();
                if (version >= 12)
                {
                    s.ln = r.ReadInt32(); s.lhead = r.ReadInt32();
                    if (s.ln < 0 || s.ln > LongCap || s.lhead < 0 || s.lhead >= LongCap)
                        throw new InvalidDataException("Historique long des graphiques invalide.");
                    for (int j = 0; j < LongCap; j++) s.lv[j] = r.ReadSingle();
                }
                series[i] = s;
            }
            return series;
        }

        PlanetSave.Preview MakePreview()
        {
            int totalDays = Mathf.Max(0, Mathf.FloorToInt(view.vDays));
            int day = totalDays % (int)DaysPerYear + 1;
            var preview = new PlanetSave.Preview
            {
                seed = curSeed, year = totalDays / (int)DaysPerYear + 1, day = day, name = planetName,
                season = (day - 1) / 3, pixels = new Color32[PlanetSave.Preview.Width * PlanetSave.Preview.Height]
            };
            for (int y = 0; y < PlanetSave.Preview.Height; y++)
                for (int x = 0; x < PlanetSave.Preview.Width; x++)
                {
                    Vector3 direction = MapDirection((x + 0.5f) / PlanetSave.Preview.Width, (y + 0.5f) / PlanetSave.Preview.Height);
                    int c = Grid.CellFromDir(direction);
                    float h = Sim.Ground(c) - Sim.SeaLevel;
                    Color color = h < 0f
                        ? Color.Lerp(new Color(0.08f, 0.29f, 0.43f), new Color(0.015f, 0.06f, 0.18f), Mathf.Clamp01(-h / 50f))
                        : Color.Lerp(new Color(0.25f, 0.46f, 0.20f), new Color(0.46f, 0.37f, 0.25f), Mathf.Clamp01(h / 80f));
                    if (h >= 0f) color = Color.Lerp(color, new Color(0.9f, 0.9f, 0.88f), Mathf.Clamp01((h - 110f) / 100f));
                    preview.pixels[y * PlanetSave.Preview.Width + x] = color;
                }
            return preview;
        }

        bool SavePlanet()
        {
            try
            {
                if (!FinishBackgroundSave(true)) return false;
                if (!ready || view == null || string.IsNullOrEmpty(planetSavePath))
                    throw new InvalidOperationException("Aucune planète prête à sauvegarder.");
                var preview = MakePreview();
                PlanetSave.WriteFile(planetSavePath, w =>
                {
                    PlanetSave.WritePreview(w, preview);
                    w.Write(resolution); w.Write(radius); w.Write(curSeed); w.Write(dayMinutes); w.Write(ageGyr);
                    w.Write(planetName ?? "");
                    w.Write(timeScale); w.Write(paused); w.Write(autoVolcanoes); w.Write(autoAsteroids);
                    WriteVector(w, camPos); WriteRotation(w, camRot);
                    w.Write(view.ViewMode); w.Write(view.ShowPlants); w.Write(view.ShowClouds);
                    w.Write(view.ShowAtmosphere);
                    w.Write(view.HideWater);
                    for (int i = 0; i < PlanetView.FlowLayers; i++) w.Write(view.FlowOn[i]);
                    w.Write(view.vDays); w.Write(view.SaveDayAngle());
                    w.Write(sampleAcc); w.Write(lastQuakes); w.Write(lastSlides); w.Write(longCounter);
                    WriteRandomStates(w);
                    WriteSeries(w, sClimate); WriteSeries(w, sVeg); WriteSeries(w, sFauna); WriteSeries(w, sGeo); WriteSeries(w, sRes);
                    PlanetSave.WriteObject(w, Sim);
                    PlanetSave.WriteObject(w, Eco);
                    PlanetSave.WriteObject(w, Fauna);
                    PlanetSave.WriteObject(w, Fauna.F);
                    PlanetSave.WriteObject(w, Sim.waterState);
                });
                saveMessage = "";
                nextAutoSave = Time.realtimeSinceStartup + AutoSaveInterval;
                return true;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
                saveMessage = "Échec de la sauvegarde : " + e.Message;
                nextAutoSave = Time.realtimeSinceStartup + 30f;
                return false;
            }
        }

        void StartBackgroundSave()
        {
            if (backgroundSave != null) return;
            try
            {
                var preview = MakePreview();
                byte[] header;
                using (var stream = new MemoryStream())
                {
                    using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                    {
                        PlanetSave.WritePreview(w, preview);
                        w.Write(resolution); w.Write(radius); w.Write(curSeed); w.Write(dayMinutes); w.Write(ageGyr);
                        w.Write(planetName ?? "");
                        w.Write(timeScale); w.Write(paused); w.Write(autoVolcanoes); w.Write(autoAsteroids);
                        WriteVector(w, camPos); WriteRotation(w, camRot);
                        w.Write(view.ViewMode); w.Write(view.ShowPlants); w.Write(view.ShowClouds);
                        w.Write(view.ShowAtmosphere);
                        w.Write(view.HideWater);
                        for (int i = 0; i < PlanetView.FlowLayers; i++) w.Write(view.FlowOn[i]);
                        w.Write(view.vDays); w.Write(view.SaveDayAngle());
                        w.Write(sampleAcc); w.Write(lastQuakes); w.Write(lastSlides); w.Write(longCounter);
                        WriteRandomStates(w);
                        WriteSeries(w, sClimate); WriteSeries(w, sVeg); WriteSeries(w, sFauna); WriteSeries(w, sGeo); WriteSeries(w, sRes);
                    }
                    header = stream.ToArray();
                }
                var sim = PlanetSave.Capture(Sim);
                var eco = PlanetSave.Capture(Eco);
                var fauna = PlanetSave.Capture(Fauna);
                var fields = PlanetSave.Capture(Fauna.F);
                var waterState = PlanetSave.Capture(Sim.waterState);
                string path = planetSavePath;
                backgroundSave = Task.Run(() => PlanetSave.WriteFile(path, w =>
                {
                    w.Write(header);
                    PlanetSave.WriteSnapshot(w, sim);
                    PlanetSave.WriteSnapshot(w, eco);
                    PlanetSave.WriteSnapshot(w, fauna);
                    PlanetSave.WriteSnapshot(w, fields);
                    PlanetSave.WriteSnapshot(w, waterState);
                }));
                nextAutoSave = Time.realtimeSinceStartup + AutoSaveInterval;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
                saveMessage = "Échec de la sauvegarde : " + e.Message;
                nextAutoSave = Time.realtimeSinceStartup + 30f;
            }
        }

        bool FinishBackgroundSave(bool wait)
        {
            if (backgroundSave == null || (!wait && !backgroundSave.IsCompleted)) return true;
            try
            {
                backgroundSave.GetAwaiter().GetResult();
                saveMessage = "";
                return true;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
                saveMessage = "Échec de la sauvegarde : " + e.Message;
                nextAutoSave = Time.realtimeSinceStartup + 30f;
                return false;
            }
            finally { backgroundSave = null; }
        }

        void FindSavedPlanets()
        {
            savedPlanets.Clear();
            selectedPlanet = -1;
            deleteConfirmIndex = -1;
            try
            {
                var paths = new List<string>();
                if (File.Exists(PlanetSave.PathName)) paths.Add(PlanetSave.PathName);
                if (Directory.Exists(PlanetSave.SaveDirectory)) paths.AddRange(Directory.GetFiles(PlanetSave.SaveDirectory, "*.save"));
                paths.Sort((a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
                foreach (string path in paths)
                {
                    try
                    {
                        var preview = PlanetSave.ReadPreview(path);
                        preview.savedAt = File.GetLastWriteTime(path);
                        savedPlanets.Add(preview);
                    }
                    catch (Exception e)
                    {
                        UnityEngine.Debug.LogException(e);
                        saveMessage = "Certaines sauvegardes sont illisibles. Consultez le journal.";
                    }
                }
                if (savedPlanets.Count > 0) selectedPlanet = 0;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogException(e);
                saveMessage = "Impossible de lire les sauvegardes : " + e.Message;
            }
            previewRefreshAt = 0f;
        }

        void DrawSavedPlanets()
        {
            if (selectedPlanet >= 0) DrawPlanetPreview(savedPlanets[selectedPlanet]);
            float width = Mathf.Min(460f, Mathf.Max(200f, UiW - 20f));
            Rect panel = new Rect(10f, 10f, width, Mathf.Max(150f, UiH - 20f));
            FillRect(panel, new Color(0f, 0f, 0f, 0.72f));
            GUI.Label(new Rect(panel.x + 10f, panel.y + 8f, width - 20f, 40f), "Planètes sauvegardées", title);
            float listHeight = Mathf.Max(30f, panel.height - 210f);
            var listRect = new Rect(panel.x + 10f, panel.y + 52f, width - 20f, listHeight);
            bool uiEnabled = GUI.enabled;
            GUI.enabled = uiEnabled && deleteConfirmIndex < 0;
            savesScroll = GUI.BeginScrollView(listRect, savesScroll, new Rect(0f, 0f, listRect.width - 20f, savedPlanets.Count * 38f));
            var delBtn = new GUIStyle(btn) { fontSize = 16 };
            for (int i = 0; i < savedPlanets.Count; i++)
            {
                var p = savedPlanets[i];
                string label = string.IsNullOrEmpty(p.name) ? ("Planète " + p.seed) : p.name;
                string text = label + " · " + p.savedAt.ToString("g");
                if (GUI.Button(new Rect(0f, i * 38f, listRect.width - 24f - 38f, 34f), (i == selectedPlanet ? "● " : "○ ") + text, btn))
                { selectedPlanet = i; previewRefreshAt = 0f; }
                if (GUI.Button(new Rect(listRect.width - 24f - 34f, i * 38f, 34f, 34f), "🗑", delBtn)) deleteConfirmIndex = i;
            }
            GUI.EndScrollView();
            float y = listRect.yMax + 6f;
            if (selectedPlanet >= 0)
            {
                var p = savedPlanets[selectedPlanet];
                string info = p.pixels == null ? "Ancienne sauvegarde : aperçu disponible après chargement." :
                    "Année " + p.year + " · Jour " + p.day + "/" + (int)DaysPerYear + " · " + SeasonNames[p.season];
                GUI.Label(new Rect(panel.x + 12f, y, width - 24f, 42f), info, new GUIStyle(style) { wordWrap = true });
            }
            else GUI.Label(new Rect(panel.x + 12f, y, width - 24f, 42f), "Aucune planète sauvegardée.", style);
            y += 46f;
            GUI.enabled = uiEnabled && deleteConfirmIndex < 0 && selectedPlanet >= 0;
            if (GUI.Button(new Rect(panel.x + 10f, y, width - 20f, 36f), "Charger cette planète", btn))
            {
                loadingPreview = savedPlanets[selectedPlanet];
                string path = savedPlanets[selectedPlanet].path;
                selectingPlanet = false;
                StartCoroutine(LoadPlanet(path));
            }
            GUI.enabled = uiEnabled && deleteConfirmIndex < 0;
            y += 40f;
            if (GUI.Button(new Rect(panel.x + 10f, y, width - 20f, 36f), "Retour", btn)) selectingPlanet = false;
            GUI.Label(new Rect(panel.x + 10f, y + 38f, width - 20f, 36f), saveMessage, new GUIStyle(style) { wordWrap = true });
            GUI.enabled = uiEnabled;
            DrawDeleteConfirm();
        }

        // boîte de confirmation avant suppression définitive d'une sauvegarde
        void DrawDeleteConfirm()
        {
            if (deleteConfirmIndex < 0 || deleteConfirmIndex >= savedPlanets.Count) return;
            var p = savedPlanets[deleteConfirmIndex];
            FillRect(new Rect(0f, 0f, UiW, UiH), new Color(0f, 0f, 0f, 0.5f));
            float w = 380f, h = 150f;
            Rect r = new Rect((UiW - w) / 2f, (UiH - h) / 2f, w, h);
            FillRect(r, new Color(0.1f, 0.03f, 0.03f, 0.92f));
            string msg = "Supprimer définitivement la planète " + p.seed + " (" + p.savedAt.ToString("g") + ") ?\nCette action est irréversible.";
            GUI.Label(new Rect(r.x + 16f, r.y + 12f, w - 32f, 80f), msg, new GUIStyle(style) { wordWrap = true });
            var redBtn = new GUIStyle(btn);
            redBtn.normal.textColor = new Color(1f, 0.45f, 0.45f);
            redBtn.hover.textColor = new Color(1f, 0.6f, 0.6f);
            float bw = (w - 40f) / 2f;
            if (GUI.Button(new Rect(r.x + 16f, r.yMax - 46f, bw, 34f), "Supprimer", redBtn))
            {
                try { File.Delete(p.path); }
                catch (Exception e) { UnityEngine.Debug.LogException(e); saveMessage = "Suppression impossible : " + e.Message; }
                savedPlanets.RemoveAt(deleteConfirmIndex);
                if (selectedPlanet == deleteConfirmIndex) selectedPlanet = savedPlanets.Count > 0 ? 0 : -1;
                else if (selectedPlanet > deleteConfirmIndex) selectedPlanet--;
                deleteConfirmIndex = -1;
                previewRefreshAt = 0f;
            }
            if (GUI.Button(new Rect(r.x + 24f + bw, r.yMax - 46f, bw, 34f), "Annuler", btn)) deleteConfirmIndex = -1;
        }

        static Texture2D glowTexture;

        static Texture2D GlowTexture()
        {
            if (glowTexture != null) return glowTexture;
            const int s = 64;
            glowTexture = new Texture2D(s, s, TextureFormat.RGBA32, false);
            glowTexture.wrapMode = TextureWrapMode.Clamp;
            glowTexture.filterMode = FilterMode.Bilinear;
            var px = new Color32[s * s];
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float nx = (x + 0.5f) / s * 2f - 1f, ny = (y + 0.5f) / s * 2f - 1f;
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(nx * nx + ny * ny));
                    a *= a;
                    px[y * s + x] = new Color(0.55f, 0.78f, 1f, a * 0.35f);
                }
            glowTexture.SetPixels32(px);
            glowTexture.Apply(false, true);
            return glowTexture;
        }

        // displayRect permet de réutiliser le même rendu (aperçu du menu des sauvegardes, écran de chargement)
        void DrawPlanetPreview(PlanetSave.Preview preview, Rect? displayRect = null)
        {
            if (preview.pixels == null) return;
            const int size = 256;
            if (previewTexture == null || previewTexture.width != size)
            {
                if (previewTexture != null) UnityEngine.Object.Destroy(previewTexture);
                previewTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
                previewTexture.filterMode = FilterMode.Bilinear;
                previewPixels = new Color32[size * size];
            }
            if (Event.current.type == EventType.Repaint && Time.realtimeSinceStartup >= previewRefreshAt)
            {
                float angle = Time.realtimeSinceStartup * 0.12f;
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float nx = (x + 0.5f) * 2f / size - 1f, ny = (y + 0.5f) * 2f / size - 1f;
                        float d = nx * nx + ny * ny;
                        if (d >= 1f) { previewPixels[y * size + x] = new Color32(0, 0, 0, 0); continue; }
                        float z = Mathf.Sqrt(1f - d);
                        float longitude = Mathf.Atan2(nx, z) + angle;
                        float u = Mathf.Repeat(longitude / (2f * Mathf.PI) + 0.5f, 1f);
                        float v = 0.5f - Mathf.Asin(ny) / Mathf.PI;
                        int px = Mathf.Clamp((int)(u * PlanetSave.Preview.Width), 0, PlanetSave.Preview.Width - 1);
                        int py = Mathf.Clamp((int)(v * PlanetSave.Preview.Height), 0, PlanetSave.Preview.Height - 1);
                        Color c = preview.pixels[py * PlanetSave.Preview.Width + px];
                        float light = 0.38f + 0.62f * Mathf.Clamp01(z * 0.85f - nx * 0.35f + ny * 0.25f);
                        c.r *= light; c.g *= light; c.b *= light;
                        previewPixels[y * size + x] = c;
                    }
                previewTexture.SetPixels32(previewPixels);
                previewTexture.Apply(false, false);
                previewRefreshAt = Time.realtimeSinceStartup + 0.08f;
            }
            float display = Mathf.Min(UiH * 0.82f, UiW * 0.72f);
            Rect dr = displayRect ?? new Rect(UiW - display - 20f, (UiH - display) / 2f, display, display);
            float glowSize = dr.width * 1.5f;
            GUI.DrawTexture(new Rect(dr.center.x - glowSize / 2f, dr.center.y - glowSize / 2f, glowSize, glowSize), GlowTexture());
            GUI.DrawTexture(dr, previewTexture);
        }

        // écran plein de chargement/lecture d'une sauvegarde : affiche la planète en rotation plutôt qu'un texte brut
        void DrawLoadingScreen()
        {
            if (loadingPreview != null && loadingPreview.pixels != null)
            {
                float display = Mathf.Min(UiH * 0.5f, UiW * 0.4f);
                Rect dr = new Rect((UiW - display) / 2f, (UiH - display) / 2f - 30f, display, display);
                DrawPlanetPreview(loadingPreview, dr);
                int dots = 1 + Mathf.FloorToInt(Time.unscaledTime * 2f) % 3;
                var loadStyle = new GUIStyle(style) { fontSize = 18, alignment = TextAnchor.MiddleCenter };
                GUI.Label(new Rect(0f, dr.yMax + 18f, UiW, 28f), status + new string('.', dots), loadStyle);
            }
            else GUI.Label(new Rect(20, 20, 800, 30), status, style);
            DrawLoadProgressBars();
        }

        // Une barre de progression par étape de chargement, alignées en bas de l'écran : permet de voir où
        // en est le chargement (terrain, climat, vie, historique, rendu) sans que la simulation ne démarre
        // avant que tout (y compris le rendu) ne soit prêt (voir "ready" qui ne passe à true qu'à la toute fin).
        void DrawLoadProgressBars()
        {
            int count = loadPhaseNames.Length;
            if (count <= 0) return;
            float margin = 20f, gap = 10f;
            float totalW = Mathf.Min(UiW - margin * 2f, 900f);
            float barW = (totalW - gap * (count - 1)) / count;
            float barH = 14f;
            float y = UiH - barH - 34f;
            float x0 = (UiW - totalW) / 2f;
            var nameStyle = new GUIStyle(style) { fontSize = 11, alignment = TextAnchor.UpperCenter };
            for (int i = 0; i < count; i++)
            {
                Rect br = new Rect(x0 + i * (barW + gap), y, barW, barH);
                FillRect(br, new Color(1f, 1f, 1f, 0.15f));
                float t = loadPhaseT[i];
                bool active = i == loadPhase;
                Color fillC = t >= 1f ? new Color(0.5f, 0.85f, 0.5f, 0.95f) : active ? new Color(0.45f, 0.7f, 1f, 0.95f) : new Color(1f, 1f, 1f, 0.25f);
                FillRect(new Rect(br.x, br.y, br.width * t, br.height), fillC);
                FillRect(new Rect(br.x, br.y, br.width, 1f), new Color(1f, 1f, 1f, 0.35f));
                GUI.Label(new Rect(br.x, br.y - 18f, br.width, 18f), loadPhaseNames[i], nameStyle);
            }
        }

        IEnumerator LoadPlanet(string path)
        {
            generating = true;
            mainMenu = false;
            ready = false;
            BeginLoadPhases(LoadPhaseNamesSave);
            SetLoadPhase(0, 0f, "Chargement de la planète...");
            yield return null;
            Exception failure = null;
            float savedDays = 0f, savedAngle = 0f;
            int savedView = 0;
            bool plants = false, clouds = false, atmosphere = true, waterHidden = false;
            var flows = new bool[PlanetView.FlowLayers];
            try
            {
                PlanetSave.ReadFile(path, (r, version) =>
                {
                    int n = r.ReadInt32();
                    float savedRadius = r.ReadSingle();
                    int savedSeed = r.ReadInt32();
                    float savedDayMinutes = r.ReadSingle(), savedAge = r.ReadSingle();
                    if (Array.IndexOf(ResOptions, n) < 0 || Array.IndexOf(RadOptions, savedRadius) < 0 ||
                        float.IsNaN(savedDayMinutes) || savedDayMinutes < 10f || savedDayMinutes > 60f ||
                        float.IsNaN(savedAge) || savedAge < 0.5f || savedAge > 8f)
                        throw new InvalidDataException("Paramètres de planète invalides.");
                    resolution = n; radius = savedRadius; curSeed = seed = savedSeed;
                    dayMinutes = savedDayMinutes; ageGyr = savedAge;
                    yearSeconds = dayMinutes * 60f * DaysPerYear;
                    planetName = version >= 17 ? r.ReadString() : DefaultPlanetName(savedSeed);
                    timeScale = r.ReadSingle(); paused = r.ReadBoolean();
                    if (Array.IndexOf(SpeedPresets, timeScale) < 0) throw new InvalidDataException("Vitesse invalide.");
                    autoVolcanoes = r.ReadBoolean(); autoAsteroids = r.ReadBoolean();
                    camPos = ReadVector(r); camRot = ReadRotation(r);
                    savedView = r.ReadInt32(); plants = r.ReadBoolean(); clouds = r.ReadBoolean();
                    if (version >= 3) atmosphere = r.ReadBoolean();
                    if (version >= 4) waterHidden = r.ReadBoolean();
                    if (savedView < 0 || savedView >= ViewNames.Length) throw new InvalidDataException("Vue invalide.");
                    for (int i = 0; i < flows.Length; i++) flows[i] = r.ReadBoolean();
                    savedDays = r.ReadSingle(); savedAngle = r.ReadSingle();
                    sampleAcc = r.ReadSingle(); lastQuakes = r.ReadInt32(); lastSlides = r.ReadInt32();
                    longCounter = version >= 12 ? r.ReadInt32() : 0;
                    if (longCounter < 0 || longCounter >= LongEvery) throw new InvalidDataException("Compteur d'historique invalide.");
                    int[] randomStates = null;
                    if (version >= 12)
                    {
                        randomStates = new int[6];
                        for (int i = 0; i < randomStates.Length; i++)
                        {
                            randomStates[i] = r.ReadInt32();
                            if (randomStates[i] == 0) throw new InvalidDataException("État aléatoire invalide.");
                        }
                    }
                    sClimate = ReadSeries(r, version); sVeg = ReadSeries(r, version); sFauna = ReadSeries(r, version);
                    sGeo = ReadSeries(r, version); sRes = ReadSeries(r, version);

                    Noise.Seed(curSeed);
                    rnd = new PlanetRandom(curSeed);
                    Grid = new PlanetGrid(resolution, radius);
                    Sim = new PlanetSim(Grid);
                    // Doit couvrir le plus grand tableau par cellule existant (ex. subductionLayers = CellCount * 6) :
                    // une marge généreuse évite de devoir à nouveau toucher cette valeur si un futur champ grossit.
                    int maxItems = Grid.CellCount * 8 + 1024;
                    var missingFields = new List<string>();
                    if (version < 5) missingFields.Add("subductionLayers");
                    if (version < 6) missingFields.AddRange(new[] { "seaIce", "glacier", "iceMeltNutrient", "icebergs" });
                    if (version < 8) missingFields.AddRange(new[] { "windHigh", "deepCurrent", "deepCurrentN" });
                    if (version < 9) missingFields.Add("magmaInflation");
                    if (version < 10) missingFields.AddRange(new[] { "mantleSolid", "externalAdded" });
                    if (version < 11) missingFields.Add("meteoriteAdded");
                    if (version < 13) missingFields.Add("mantleResidual");
                    if (version < 15) missingFields.Add("activePlates");
                    if (version < 18) missingFields.AddRange(new[] { "mantleSolidN", "mantleResidualN", "subductionLayersN", "boundaryDecay", "orogenyHeightVariance", "channelNoise", "CoastalErode", "CoastalErodeDepth", "riftDecay" });
                    if (version < 19) missingFields.AddRange(new[] { "LithifyDepth", "LithifyRate", "LithifyCompaction" });
                    if (version < 20) missingFields.AddRange(new[] { "tsunamiBase", "tsunamiMomentum", "tsunamiActive", "tsunamiAccum" });
                    var ignoreFields = new List<string>();
                    // versions 5 et 6 ont déjà subductionLayers mais à l'ancienne taille (3 couches/cellule au lieu de 6) :
                    // on relit ces octets pour ne pas désynchroniser le flux, mais on garde le tableau neuf (remis à zéro).
                    if (version >= 5 && version < 7) ignoreFields.Add("subductionLayers");
                    PlanetSave.ReadObject(r, Sim, maxItems, missingFields.ToArray(), ignoreFields.ToArray());
                    // Les anciens anneaux visuels n'ont pas d'état d'onde réutilisable ; l'eau est conservée.
                    if (version < 20) Sim.tsunamis.Clear();
                    // L'humidité des sols est étalonnée à la génération sur la pluie de l'époque (CalibrateMoisture).
                    // Depuis la version 21 la pluie entre dans les continents : sur trois planètes de référence, le
                    // même étalonnage donne une échelle 9 à 18 fois plus petite. Sans cette correction, une ancienne
                    // planète se couvrirait de sols détrempés.
                    if (version < 21) Sim.moistScale *= 0.08f;
                    if (version < 12) Sim.MigrateIcebergVolumes();
                    if (version < 4) Sim.MigrateTectonicTimescale();
                    else if (version < 9 && Mathf.Abs(Sim.plateSpeed - 0.2f * Sim.Activity) < 0.0001f)
                        Sim.MigrateTectonicTimescale();
                    if (version < 14) Sim.MigratePlateDriftSpeed();
                    if (version < 15)
                    {
                        Sim.MigratePlateDriftSpeed();
                        Sim.MigrateActivePlates();
                    }
                    if (version < 16) Sim.MigrateVisiblePlateDrift();
                    if (version < 9 && Mathf.Abs(Sim.orogeny - 0.06f * Sim.Activity) < 0.0001f)
                        Sim.orogeny = 0.0004f * Sim.Activity;
                    if (version < 9 && Mathf.Abs(Sim.riftSink - 0.012f) < 0.000001f)
                        Sim.riftSink = 0.00008f;
                    if (version < 9 && Mathf.Abs(Sim.Ke - 0.03f) < 0.000001f)
                        Sim.Ke = 0.0003f;
                    if (version < 9 && Mathf.Abs(Sim.kThermal - 0.12f) < 0.000001f)
                        Sim.kThermal = 0.00008f;
                    Eco = new Ecosystem(Sim, curSeed);
                    PlanetSave.ReadObject(r, Eco, maxItems);
                    Fauna = new Fauna(this);
                    PlanetSave.ReadObject(r, Fauna, maxItems);
                    PlanetSave.ReadObject(r, Fauna.F, maxItems, version < 13 ? new[] { "fruit" } : null);
                    if (randomStates != null)
                    {
                        rnd.State = randomStates[0]; Sim.WeatherRandomState = randomStates[1];
                        Sim.VolcanoRandomState = randomStates[2]; Sim.FireRandomState = randomStates[3];
                        Eco.RandomState = randomStates[4]; Fauna.RandomState = randomStates[5];
                    }
                    if (version >= 3)
                    {
                        Sim.waterState = new WaterState(Sim.M);
                        PlanetSave.ReadObject(r, Sim.waterState, maxItems);
                    }
                    else Sim.InitializeWater();
                    if (Sim.plate == null && savedView == 4) throw new InvalidDataException("Vue des plaques indisponible.");
                });
                SetLoadPhase(0, 1f, "Sauvegarde lue");

                Sim.onDisturb = Eco.Disturb;
                Sim.onLightning = c => { if (Sim.moist[c] < 0.6f) Eco.Ignite(c); };
                Sim.onBlastPlant = Eco.Blast;
                Sim.onBlastFauna = Fauna.KillNear;
                Sim.ClearTransientEffectsAfterLoad();
                LoadPois();
            }
            catch (Exception e) { failure = e; }

            if (failure == null)
            {
                SetLoadPhase(1, 0f, "Construction du rendu...");
                yield return null;
                try
                {
                    view = new PlanetView(this, cam);
                    view.ViewMode = savedView;
                    view.ShowPlants = plants; view.ShowClouds = clouds;
                    view.ShowAtmosphere = atmosphere;
                    view.HideWater = waterHidden;
                    Array.Copy(flows, view.FlowOn, flows.Length);
                    view.RestoreClock(savedDays, savedAngle);
                    view.Build();
                    SetLoadPhase(1, 1f, "Planète prête");
                    cam.transform.SetPositionAndRotation(camPos, camRot);
                    cam.nearClipPlane = 0.5f;
                    fpMode = false; flyT = 1f; tickAccum = 0f;
                    pauseMenu = false; toolTab = -1;
                    timeScale = 1f; paused = false;
                    planetSavePath = path;
                    nextAutoSave = Time.realtimeSinceStartup + AutoSaveInterval;
                    quitSaved = false;
                    ready = true;
                    saveMessage = "";
                    loadingPreview = null;
                }
                catch (Exception e) { failure = e; }
            }

            if (failure != null)
            {
                UnityEngine.Debug.LogException(failure);
                if (view != null) { view.Destroy(); view = null; }
                Grid = null; Sim = null; Eco = null; Fauna = null;
                ResetStats();
                loadingPreview = null;
                mainMenu = true;
                selectingPlanet = true;
                saveMessage = "Échec du chargement : " + failure.Message;
            }
            generating = false;
        }
    }
}
