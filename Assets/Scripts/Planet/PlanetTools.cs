using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LivingPlanet
{
    // HUD des outils d'interaction : matières, gaz, catastrophes, vivant, souffle planétaire et points d'intérêt (caméra).
    public sealed partial class PlanetWorld
    {
        static readonly string[] ToolTabs = { "Matières", "Gaz", "Catastrophes", "Vivant", "Souffle", "Lieux" };
        int toolTab = -1;
        int solidSel = 4, gasSel = 1, disSel, liveSel;
        float solidQty = 4f, solidRad = 3f, gasQty = 20f, gasRad = 4f, disInt = 0.5f, liveCount = 12f, liveRad = 3f, blowStr = 1f, blowRad = 5f;
        Rect toolRect, toolbarRect;
        string toolMsg = ""; float toolMsgT;
        Vector3 blowDir; bool blowHasDir;
        int curSeed;

        // ---- points d'intérêt
        [Serializable] sealed class PoiData { public string name; public Vector3 pos; public Quaternion rot; }
        [Serializable] sealed class PoiList { public List<PoiData> items = new List<PoiData>(); }
        PoiList pois = new PoiList();
        string poiName = "";
        Vector2 poiScroll;
        string PoiKey { get { return "poi_" + curSeed + "_" + resolution + "_" + (int)radius; } }

        void LoadPois()
        {
            pois = new PoiList();
            string j = PlayerPrefs.GetString(PoiKey, "");
            if (!string.IsNullOrEmpty(j)) { try { pois = JsonUtility.FromJson<PoiList>(j) ?? new PoiList(); } catch (Exception) { pois = new PoiList(); } }
        }

        void SavePois() { PlayerPrefs.SetString(PoiKey, JsonUtility.ToJson(pois)); PlayerPrefs.Save(); }

        void GoToPoi(PoiData p)
        {
            ExitFirstPerson();
            flyFromPos = camPos; flyFromRot = camRot;
            flyToPos = p.pos; flyToRot = p.rot;
            flyT = 0f;
        }

        bool ToolArmed { get { return toolTab >= 0 && toolTab <= 4; } }

        string[] LiveNames
        {
            get
            {
                var l = new List<string>();
                for (int i = 0; i < PlantNames.Length; i++) l.Add("🌿 " + PlantNames[i]);
                for (int i = 0; i < global::LivingPlanet.Fauna.Count; i++) l.Add("🐾 " + global::LivingPlanet.Fauna.Names[i]);
                return l.ToArray();
            }
        }

        void ToolMsg(string s) { toolMsg = s; toolMsgT = 5f; }

        int ToolCell() { return fpMode ? Pick() : PickMouse(); }

        // appelé à chaque frame : applique l'outil armé sous le curseur
        void HandleTools()
        {
            var ms = Mouse.current;
            if (ms == null) return;
            toolMsgT -= Time.unscaledDeltaTime;
            if (!ToolArmed || pauseMenu) { blowHasDir = false; return; }
            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) { toolTab = -1; return; }
            bool over = MouseOverUi(ms);
            if (toolTab == 4)
            {
                if (!ms.leftButton.isPressed || over) { blowHasDir = false; return; }
                int c = ToolCell();
                if (c < 0) return;
                Vector3 n = Grid.Dir[c];
                Vector2 d = ms.delta.ReadValue();
                Vector3 want = (cam.transform.right * d.x + cam.transform.up * d.y);
                want = Vector3.ProjectOnPlane(want, n);
                if (d.sqrMagnitude > 0.5f && want.sqrMagnitude > 1e-8f)
                {
                    want.Normalize();
                    blowDir = blowHasDir ? Vector3.Slerp(blowDir, want, 0.35f) : want;
                    blowHasDir = true;
                }
                if (!blowHasDir) return;
                blowDir = Vector3.ProjectOnPlane(blowDir, n);
                if (blowDir.sqrMagnitude < 1e-8f) { blowHasDir = false; return; }
                blowDir.Normalize();
                Sim.Blow(c, blowDir, blowStr * Time.unscaledDeltaTime * 6f, Mathf.RoundToInt(blowRad));
                return;
            }
            if (!ms.leftButton.wasPressedThisFrame || over) return;
            int cell = ToolCell();
            if (cell < 0) return;
            switch (toolTab)
            {
                case 0:
                    Sim.InjectSolid(cell, solidSel, solidQty, Mathf.RoundToInt(solidRad));
                    ToolMsg(PlanetSim.SolidNames[solidSel] + " injecté (" + solidQty.ToString("0.#") + ")");
                    if (solidSel < 11 || solidSel == 12 || solidSel == 15) view.MarkDirty();
                    break;
                case 1:
                    Sim.InjectGas(cell, gasSel, gasQty, Mathf.RoundToInt(gasRad));
                    ToolMsg(PlanetSim.GasNames[gasSel] + " injecté (" + gasQty.ToString("0") + ")");
                    break;
                case 2:
                    Sim.Disaster(disSel, cell, disInt, rnd);
                    view.MarkDirty();
                    break;
                case 3:
                    {
                        int req = Mathf.RoundToInt(liveCount), placed;
                        if (liveSel < PlantNames.Length) placed = Eco.Introduce(liveSel, cell, req, Mathf.RoundToInt(liveRad));
                        else placed = Fauna.Introduce(liveSel - PlantNames.Length, cell, req, Mathf.RoundToInt(liveRad));
                        ToolMsg(LiveNames[liveSel] + " : " + placed + "/" + req + " introduits" + (placed == 0 ? " (milieu inadapté ou population au maximum)" : ""));
                        break;
                    }
            }
        }

        float ToolSlider(ref float y, Rect p, string label, float v, float a, float b, string fmt, bool integer = false)
        {
            GUI.Label(new Rect(p.x + 10, y, p.width - 20, 22), label + " : " + v.ToString(fmt), style);
            y += 22;
            v = GUI.HorizontalSlider(new Rect(p.x + 12, y + 4, p.width - 24, 18), v, a, b);
            if (integer) v = Mathf.Round(v);
            y += 26;
            return v;
        }

        void DrawTools()
        {
            float width = Mathf.Min(700f, Mathf.Max(250f, Screen.width - MapWidth - 42f));
            float centeredWidth = Screen.width - 2f * (MapWidth + 22f);
            if (centeredWidth >= 350f) width = Mathf.Min(width, centeredWidth);
            int columns = width >= 570f ? ToolTabs.Length : 3;
            int toolbarRows = (ToolTabs.Length + columns - 1) / columns;
            float height = 12f + toolbarRows * 38f + 38f;
            float centeredX = (Screen.width - width) / 2f;
            float x = Mathf.Max(10f, Mathf.Min(centeredX, Screen.width - MapWidth - 30f - width));
            toolbarRect = new Rect(x, Screen.height - height - 12f, width, height);
            FillRect(toolbarRect, new Color(0f, 0f, 0f, 0.6f));
            float cellWidth = (width - 12f) / columns;
            for (int i = 0; i < ToolTabs.Length; i++)
            {
                bool on = toolTab == i;
                if (GUI.Button(new Rect(toolbarRect.x + 6f + (i % columns) * cellWidth, toolbarRect.y + 6f + (i / columns) * 38f, cellWidth - 4f, 34f), on ? "<b>" + ToolTabs[i] + "</b>" : ToolTabs[i], btn))
                    toolTab = on ? -1 : i;
            }
            DrawTimeControls(new Rect(toolbarRect.x + 6f, toolbarRect.y + 6f + toolbarRows * 38f, width - 12f, 34f));
            if (toolTab < 0) { toolRect = new Rect(0, 0, 0, 0); DrawToolStatus(); return; }

            float w = 360f;
            float ph = toolTab == 5 ? 360f : toolTab == 3 ? 400f : toolTab == 0 ? 410f : 300f;
            toolRect = new Rect(toolbarRect.x, toolbarRect.y - ph - 6, w, ph);
            FillRect(toolRect, new Color(0f, 0f, 0f, 0.72f));
            float y = toolRect.y + 8;
            switch (toolTab)
            {
                case 0:
                    GUI.Label(new Rect(toolRect.x + 10, y, w - 20, 22), "Matière à injecter (clic sur la planète)", style); y += 24;
                    int solidRows = (PlanetSim.SolidNames.Length + 2) / 3;
                    solidSel = GUI.SelectionGrid(new Rect(toolRect.x + 10, y, w - 20, solidRows * 30f), solidSel, PlanetSim.SolidNames, 3, btn); y += solidRows * 30f + 8;
                    solidQty = ToolSlider(ref y, toolRect, "Quantité (épaisseur)", solidQty, 0.5f, 30f, "0.#");
                    solidRad = ToolSlider(ref y, toolRect, "Rayon (cellules)", solidRad, 1f, 10f, "0", true);
                    GUI.Label(new Rect(toolRect.x + 10, y, w - 20, 40), solidSel == 15 ? "Le sel se dissout dans l'eau et peut rendre les lacs impropres à la consommation." : solidSel == 13 ? "Le magma alimente la chambre locale : éruptions possibles." : "Les matières rejoignent le terrain (érosion, hydrologie, végétation).", style);
                    break;
                case 1:
                    GUI.Label(new Rect(toolRect.x + 10, y, w - 20, 22), "Gaz à injecter (clic sur la planète)", style); y += 24;
                    gasSel = GUI.SelectionGrid(new Rect(toolRect.x + 10, y, w - 20, 76), gasSel, PlanetSim.GasNames, 2, btn); y += 86;
                    gasQty = ToolSlider(ref y, toolRect, "Quantité", gasQty, 1f, 100f, "0");
                    gasRad = ToolSlider(ref y, toolRect, "Rayon (cellules)", gasRad, 1f, 12f, "0", true);
                    GUI.Label(new Rect(toolRect.x + 10, y, w - 20, 40), "Transporté par les vents ; agit sur le climat, le feu et le vivant.", style);
                    break;
                case 2:
                    GUI.Label(new Rect(toolRect.x + 10, y, w - 20, 22), "Catastrophe (clic sur la planète)", style); y += 24;
                    disSel = GUI.SelectionGrid(new Rect(toolRect.x + 10, y, w - 20, 114), disSel, PlanetSim.DisasterNames, 2, btn); y += 122;
                    disInt = ToolSlider(ref y, toolRect, "Intensité", disInt, 0f, 1f, "0.00");
                    break;
                case 3:
                    GUI.Label(new Rect(toolRect.x + 10, y, w - 20, 22), "Espèce à introduire (clic sur la planète)", style); y += 24;
                    var ln = LiveNames;
                    int rows = (ln.Length + 1) / 2;
                    liveSel = GUI.SelectionGrid(new Rect(toolRect.x + 10, y, w - 20, rows * 28f), liveSel, ln, 2, btn); y += rows * 28f + 8;
                    liveCount = ToolSlider(ref y, toolRect, "Quantité", liveCount, 1f, 80f, "0", true);
                    liveRad = ToolSlider(ref y, toolRect, "Rayon (cellules)", liveRad, 1f, 10f, "0", true);
                    break;
                case 4:
                    GUI.Label(new Rect(toolRect.x + 10, y, w - 20, 60), "Souffle planétaire : maintenez le clic gauche et déplacez le curseur dans la direction souhaitée.", style); y += 66;
                    blowStr = ToolSlider(ref y, toolRect, "Intensité", blowStr, 0.2f, 4f, "0.0");
                    blowRad = ToolSlider(ref y, toolRect, "Rayon (cellules)", blowRad, 2f, 14f, "0", true);
                    GUI.Label(new Rect(toolRect.x + 10, y, w - 20, 40), "Agit sur le vent, la pression, les nuages et les feux.", style);
                    break;
                case 5:
                    DrawPoiPanel(y);
                    break;
            }
            DrawToolStatus();
        }

        void DrawPoiPanel(float y)
        {
            float w = toolRect.width;
            GUI.Label(new Rect(toolRect.x + 10, y, w - 20, 22), "Points d'intérêt (cadrage de la caméra)", style); y += 26;
            poiName = GUI.TextField(new Rect(toolRect.x + 10, y, w - 130, 30), poiName, 24, btn);
            if (GUI.Button(new Rect(toolRect.xMax - 112, y, 102, 30), "Enregistrer", btn))
            {
                string nm = string.IsNullOrWhiteSpace(poiName) ? "Lieu " + (pois.items.Count + 1) : poiName.Trim();
                pois.items.Add(new PoiData { name = nm, pos = camPos, rot = camRot });
                SavePois(); poiName = ""; ToolMsg("Point d'intérêt « " + nm + " » enregistré");
            }
            y += 38;
            var view2 = new Rect(toolRect.x + 6, y, w - 12, toolRect.yMax - y - 8);
            var content = new Rect(0, 0, view2.width - 18, Mathf.Max(view2.height, pois.items.Count * 34f));
            poiScroll = GUI.BeginScrollView(view2, poiScroll, content);
            int del = -1;
            for (int i = 0; i < pois.items.Count; i++)
            {
                if (GUI.Button(new Rect(4, i * 34, content.width - 48, 30), pois.items[i].name, btn)) GoToPoi(pois.items[i]);
                if (GUI.Button(new Rect(content.width - 40, i * 34, 36, 30), "✕", btn)) del = i;
            }
            GUI.EndScrollView();
            if (del >= 0) { pois.items.RemoveAt(del); SavePois(); }
        }

        void DrawToolStatus()
        {
            float width = Mathf.Min(420f, Screen.width - 20f);
            float x = (Screen.width - width) / 2f, y = 96f;
            var center = new GUIStyle(style) { alignment = TextAnchor.MiddleCenter, fontSize = 12 };
            if (ToolArmed)
            {
                string t = toolTab == 4 ? "Souffle actif : maintenez le clic" : "Outil « " + ToolTabs[toolTab] + " » actif : cliquez sur la planète (Échap pour quitter)";
                FillRect(new Rect(x, y, width, 23), new Color(0.1f, 0.25f, 0.1f, 0.7f));
                GUI.Label(new Rect(x, y, width, 23), t, center);
                y += 25;
            }
            if (toolMsgT > 0f && toolMsg.Length > 0)
            {
                FillRect(new Rect(x, y, width, 23), new Color(0f, 0f, 0f, 0.65f));
                GUI.Label(new Rect(x, y, width, 23), toolMsg, center);
                y += 25;
            }
            // météores détectés : taille, délai avant l'impact
            foreach (var a in Sim.asteroids)
            {
                string kind = a.kind == PlanetSim.AstImpact ? "Astéroïde" : (a.kind == PlanetSim.AstAir ? "Bolide" : "Météore");
                FillRect(new Rect(x, y, width, 23), new Color(0.35f, 0.07f, 0.05f, 0.75f));
                GUI.Label(new Rect(x, y, width, 23), "☄ " + kind + " ⌀ " + a.DiameterM.ToString("0") + " m — impact dans " + Mathf.Max(0f, a.tti).ToString("0") + " s", center);
                y += 25;
            }
        }
    }
}
