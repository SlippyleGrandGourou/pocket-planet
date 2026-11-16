using System.Collections;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LivingPlanet
{
    public sealed partial class PlanetWorld : MonoBehaviour
    {
        [Header("Planète")]
        public int resolution = 256;
        public float radius = 600f;
        public int seed = 0;

        [Header("Simulation")]
        public float timeScale = 1f;    // 1 = temps réel (journée = dayMinutes)
        public float dayMinutes = 48f;     // durée d'une journée complète (10..60 min)
        public const float DaysPerYear = 12f;
        const float FaunaSecondsPerDay = 30f;
        const int FaunaSubstepsPerTick = 12;
        public float ageGyr = 4.5f;        // âge initial de la planète (milliards d'années)
        public float yearSeconds = 48f * 60f * DaysPerYear;
        public int spinupTicks = 250;
        public float tickBudgetMs = 20f;
        public bool autoVolcanoes = true, autoAsteroids = true;

        public PlanetGrid Grid;
        public PlanetSim Sim;
        public Ecosystem Eco;
        public Fauna Fauna;
        float shake;
        PlanetView view;
        Camera cam;
        PlanetRandom rnd;

        bool ready, paused, showHud = true, showHelp = false;
        bool mainMenu = true, pauseMenu, generating;
        // Statistiques (plein écran, depuis le menu pause) et infos (surcouche légère, touche dédiée) :
        // deux affichages désormais indépendants du panneau "Options" qui a disparu (voir item 5).
        bool showStats, showInfo, showCellDebug;
        bool statsSinceStart = true;
        int resIdx = 2, radIdx = 1;
        static readonly float[] RadOptions = { 600f, 1200f, 2000f, 3000f, 4000f };
        static readonly string[] RadNames = { "Petite (600)", "Grande (1200)", "Géante (2000)", "Colossale (3000)", "Immense (4000)" };
        internal float MaxCamAlt { get { return 2500f + radius * 0.8f; } }
        string seedText = "";
        public string planetName = "";
        bool renamingPlanet, renameFocused;
        string renameBuffer = "";
        static string DefaultPlanetName(int seed) { return "Planète " + seed; }
        string DisplayPlanetName() { return string.IsNullOrEmpty(planetName) ? DefaultPlanetName(curSeed) : planetName; }
        static readonly int[] ResOptions = { 128, 192, 256, 320 };
        static readonly string[] ResNames = { "Basse (128)", "Moyenne (192)", "Haute (256)", "Ultra (320)" };
        static readonly float[] SpeedPresets = { 1f, 300f, 1000f };
        string status = "";
        // Progression du chargement : une barre par étape (voir DrawLoadingScreen), remplie de 0 à 1.
        static readonly string[] GenPhaseNames = { "Terrain", "Climat & érosion", "Vie", "Historique", "Rendu" };
        static readonly string[] LoadPhaseNamesSave = { "Lecture de la sauvegarde", "Rendu" };
        string[] loadPhaseNames = GenPhaseNames;
        float[] loadPhaseT = new float[GenPhaseNames.Length];
        int loadPhase;
        void BeginLoadPhases(string[] names)
        {
            loadPhaseNames = names;
            loadPhaseT = new float[names.Length];
            loadPhase = 0;
        }
        void SetLoadPhase(int phase, float t, string text) { loadPhase = phase; loadPhaseT[phase] = Mathf.Clamp01(t); status = text; }
        float tickAccum;
        float fps = 60f;
        float lastTickMs;
        float effTicks, effTimer, effSpeed;
        bool leftDrag;

        // camera state
        Vector3 camPos;
        Quaternion camRot;

        // mode humain
        bool fpMode, lensOn = true;
        Vector3 fpDir, fpHead;
        float fpPitch, fpGround, fpY, fpVy;

        // vol vers une catastrophe
        float flyT = 1f;
        Vector3 flyFromPos, flyToPos;
        Quaternion flyFromRot, flyToRot;
        Rect eventsRect, dialRect, infoRect;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (FindAnyObjectByType<PlanetWorld>() != null) return;
            new GameObject("PlanetWorld").AddComponent<PlanetWorld>();
        }

        void Start()
        {
            cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = go.AddComponent<Camera>();
                go.AddComponent<AudioListener>();
            }
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.01f, 0.012f, 0.03f);
            Application.targetFrameRate = 120;
            Application.wantsToQuit += WantsToQuit;
            PlanetSave.AdoptLegacySaves();
            menuPreviewSeed = UnityEngine.Random.Range(1, 1000000);
        }

        void BeginGenerate()
        {
            if (generating) return;
            saveMessage = "";
            resolution = ResOptions[resIdx];
            radius = RadOptions[radIdx];
            int parsed;
            seed = int.TryParse(seedText, out parsed) ? parsed : 0;
            mainMenu = false; pauseMenu = false;
            StartGeneration();
        }

        // Point d'entrée unique de toute génération (menu ou touche R) : les barres de progression sont
        // toujours remises aux phases de génération, quel que soit l'écran de chargement précédent.
        void StartGeneration()
        {
            planetSavePath = PlanetSave.NewPath();
            quitSaved = false;
            loadingPreview = null;
            BeginLoadPhases(GenPhaseNames);
            StartCoroutine(GenerateSafely());
        }

        // Touche R : nouvelle planète aléatoire, qui ne garde ni la graine ni le nom de la précédente.
        void RegenerateRandom()
        {
            seed = 0;
            planetName = "";
            StartGeneration();
        }

        // Un `yield` est interdit dans un try/catch : on fait donc avancer Generate() pas à pas depuis ici,
        // pour qu'une exception ramène au menu avec un message au lieu de figer l'écran de chargement.
        IEnumerator GenerateSafely()
        {
            var steps = Generate();
            while (true)
            {
                bool more;
                try { more = steps.MoveNext(); }
                catch (System.Exception e) { FailGeneration(e); more = false; }
                if (!more) break;
                yield return steps.Current;
            }
        }

        void FailGeneration(System.Exception e)
        {
            UnityEngine.Debug.LogException(e);
            if (view != null) { view.Destroy(); view = null; }
            Grid = null; Sim = null; Eco = null; Fauna = null;
            ResetStats();
            generating = false; ready = false;
            mainMenu = true; selectingPlanet = false;
            saveMessage = "Échec de la génération : " + e.Message;
        }

        IEnumerator Generate()
        {
            generating = true;
            ready = false;
            ResetStats();
            if (view != null) { view.Destroy(); view = null; }
            int s = seed != 0 ? seed : Random.Range(1, 1000000);
            rnd = new PlanetRandom(s);
            curSeed = s; LoadPois();
            planetName = string.IsNullOrWhiteSpace(planetName) ? DefaultPlanetName(s) : planetName.Trim();
            SetLoadPhase(0, 0f, "Génération du terrain (graine " + s + ")...");
            yield return null;

            Grid = new PlanetGrid(resolution, radius);
            Sim = new PlanetSim(Grid);
            yearSeconds = dayMinutes * 60f * DaysPerYear;
            Sim.spinRate = 48f / dayMinutes;
            Sim.ApplyAge(ageGyr);
            float hk = Mathf.Sqrt(Mathf.Max(1f, Grid.CellSize / 3.68f));
            Sim.MaxHeight *= hk; Sim.MountainAmp *= hk;
            Sim.Generate(s);
            SetLoadPhase(0, 1f, "Terrain généré");
            yield return null;

            Sim.precipRate = 0.05f;
            int spin = Mathf.RoundToInt(spinupTicks * (1f + 0.8f * Sim.Oldness));
            for (int i = 0; i < spin; i++)
            {
                Sim.StepClimate();
                Sim.StepOcean();
                if (i % 4 == 0) Sim.StepClouds();
                Sim.StepHydro(); Sim.StepGroundwater();
                Sim.StepIce(rnd);
                if (i > 40) { Sim.StepGeology(); Sim.StepLithification(); }
                Sim.StepCoastline();
                Sim.tick++;
                if (i % 5 == 0) { SetLoadPhase(1, (float)i / spin, "Climat, rivières et érosion " + (100 * i / spin) + "%"); yield return null; }
            }
            Sim.CalibrateMoisture();
            Sim.precipRate = 0.01f;
            Sim.tick = 0;
            SetLoadPhase(1, 1f, "Climat et érosion stabilisés");

            SetLoadPhase(2, 0f, "Plantation de la végétation...");
            yield return null;
            Eco = new Ecosystem(Sim, s);
            Eco.PlantInitial();
            Eco.Step(0.1f);
            Sim.onDisturb = Eco.Disturb;
            Sim.onLightning = lc => { if (Sim.moist[lc] < 0.6f) Eco.Ignite(lc); };
            Fauna = new Fauna(this);
            Fauna.Init();
            Sim.onBlastPlant = Eco.Blast;
            Sim.onBlastFauna = Fauna.KillNear;
            SetLoadPhase(2, 1f, "Végétation et faune en place");

            // historique pre-charge : une annee simulee, pour que les graphiques soient lisibles des le depart
            int preTicks = PlanetSim.TicksPerYear;
            int perTick = GraphCap / preTicks;
            for (int i = 0; i < preTicks; i++)
            {
                StepOnce();
                for (int k = 0; k < perTick; k++) RecordStats();
                if (i % 4 == 0) { SetLoadPhase(3, (float)i / preTicks, "Préchargement de l'historique " + (100 * i / preTicks) + "%"); yield return null; }
            }
            Sim.tick = 0;
            Sim.newQuakes.Clear();
            sampleAcc = 0f;
            SetLoadPhase(3, 1f, "Historique préchargé");
            SetLoadPhase(4, 0f, "Construction du rendu...");
            yield return null;
            view = new PlanetView(this, cam);
            view.Build();
            SetLoadPhase(4, 1f, "Planète prête");

            camPos = new Vector3(0.35f, 0.25f, 1f).normalized * (radius + 300f + radius * 0.75f);
            camRot = Quaternion.LookRotation(-camPos.normalized, Vector3.up);
            tickAccum = 0f; paused = false; timeScale = 1f;
            fpMode = false; flyT = 1f;
            cam.nearClipPlane = 0.5f;
            ready = true;
            generating = false;
            SavePlanet();
        }

        // ------------------------------------------------------------------ main loop
        void Update()
        {
            fps = Mathf.Lerp(fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f), 0.05f);
            if (!ready) return;

            HandleInput();
            if (!ready) return;
            bool menuOpen = pauseMenu;
            UpdateCursor();
            if (!menuOpen && !showStats && !renamingPlanet && !poiTyping) { HandleTools(); UpdateCamera(); }

            float simDt = 0f;
            if (!paused && !menuOpen)
            {
                simDt = Time.deltaTime * timeScale / yearSeconds;
                tickAccum += Time.deltaTime * timeScale * (PlanetSim.TicksPerYear / yearSeconds);
                if (tickAccum > 6f) tickAccum = 6f;
                var sw = Stopwatch.StartNew();
                int done = 0;
                while (tickAccum >= 1f)
                {
                    StepOnce();
                    TickStats(yearSeconds / PlanetSim.TicksPerYear);
                    done++;
                    tickAccum -= 1f;
                    if (sw.Elapsed.TotalMilliseconds > tickBudgetMs) { tickAccum = Mathf.Min(tickAccum, 1f); break; }
                }
                lastTickMs = (float)sw.Elapsed.TotalMilliseconds;
                effTicks += done;
            }
            effTimer += Time.unscaledDeltaTime;
            if (effTimer >= 1f)
            {
                effSpeed = effTicks / effTimer / (PlanetSim.TicksPerYear / yearSeconds);
                effTicks = 0f; effTimer = 0f;
            }

            if (!paused && !pauseMenu)
            {
                if (Sim.StepTsunamis(Time.deltaTime)) view.MarkDirty();
                Sim.UpdateAsteroids(Time.deltaTime, timeScale, autoAsteroids, rnd);
                if (Sim.terrainDirty) { Sim.terrainDirty = false; view.MarkDirty(); }
            }
            for (int i = 0; i < Sim.newQuakes.Count; i++)
            {
                var q = Sim.newQuakes[i];
                float near = Mathf.Clamp01((Vector3.Dot(camPos.normalized, q.dir) - 0.7f) / 0.3f);
                shake = Mathf.Max(shake, (q.mag - 4f) * 0.35f * near);
            }
            Sim.newQuakes.Clear();
            shake = Mathf.MoveTowards(shake, 0f, Time.unscaledDeltaTime * 0.8f);

            view.TimeScale = (paused || pauseMenu) ? 0f : timeScale;
            var lensRay = cam.ScreenPointToRay(Mouse.current != null ? (Vector3)Mouse.current.position.ReadValue() : new Vector3(Screen.width / 2f, Screen.height / 2f));
            view.SetLens(lensOn && !fpMode && !pauseMenu, lensRay.origin, lensRay.direction);
            view.Tick(Time.deltaTime, simDt, camPos, camRot * Vector3.forward);
            FinishBackgroundSave(false);
            if (Time.realtimeSinceStartup >= nextAutoSave) StartBackgroundSave();
        }

        void LateUpdate()
        {
            if (ready) { view.DrawPlants(); }
        }

        void StepOnce()
        {
            Sim.StepClimate();
            Sim.StepOcean(timeScale);
            Sim.StepHydro(); Sim.StepGroundwater();
            Sim.StepIce(rnd);
            Sim.StepGeology();
            Sim.StepLithification();
            Sim.StepTectonics(timeScale);
            Sim.StepCoastline();
            Sim.StepWeather(rnd);
            if (Sim.tick % 4 == 0) Sim.StepClouds();
            Sim.StepFire();
            float day = (Sim.tick % PlanetSim.TicksPerYear) * (DaysPerYear / PlanetSim.TicksPerYear);
            float angle = 2f * Mathf.PI * day;
            float declination = Sim.Declination;
            float cosDeclination = Mathf.Cos(declination);
            Fauna.sunDir = new Vector3(cosDeclination * Mathf.Cos(angle), Mathf.Sin(declination), cosDeclination * Mathf.Sin(angle));
            float days = DaysPerYear / PlanetSim.TicksPerYear / FaunaSubstepsPerTick;
            for (int i = 0; i < FaunaSubstepsPerTick; i++) Fauna.Step(days * FaunaSecondsPerDay, days);
            if (Sim.tick % 5 == 0) Sim.StepGas();
            Sim.StepHazards(rnd);
            Sim.tick++;
            if (Sim.tick % 10 == 0) Eco.Step(10f / PlanetSim.TicksPerYear);
            if (autoVolcanoes) Sim.MaybeRandomEruption(rnd);
        }

        // ------------------------------------------------------------------ input / camera
        void HandleInput()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (renamingPlanet)
            {
                if (kb.escapeKey.wasPressedThisFrame) CancelRenamePlanet();
                else if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) ConfirmRenamePlanet();
                return;
            }
            if (poiTyping)
            {
                // Saisie d'un nom de lieu : aucune lettre ne doit déclencher son raccourci (R, M, K, T...).
                var mouse = Mouse.current;
                bool clickedAway = mouse != null && mouse.leftButton.wasPressedThisFrame && !MouseOverUi(mouse);
                if (clickedAway || kb.escapeKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
                    StopPoiTyping();
                return;
            }
            if (kb.escapeKey.wasPressedThisFrame)
            {
                if (showStats) showStats = false;
                else if (ToolArmed && !pauseMenu) toolTab = -1;
                else pauseMenu = !pauseMenu;
            }
            if (pauseMenu) return;
            if (kb.pKey.wasPressedThisFrame) paused = !paused;
            if (kb.rightBracketKey.wasPressedThisFrame || kb.equalsKey.wasPressedThisFrame || kb.numpadPlusKey.wasPressedThisFrame) StepSpeed(1);
            if (kb.leftBracketKey.wasPressedThisFrame || kb.minusKey.wasPressedThisFrame || kb.numpadMinusKey.wasPressedThisFrame) StepSpeed(-1);
            if (kb.f1Key.wasPressedThisFrame) SetView(0);
            if (kb.f2Key.wasPressedThisFrame) SetView(1);
            if (kb.f3Key.wasPressedThisFrame) SetView(2);
            if (kb.f4Key.wasPressedThisFrame) SetView(3);
            if (kb.f5Key.wasPressedThisFrame) SetView(4);
            if (kb.f6Key.wasPressedThisFrame) SetView(5);
            if (kb.f7Key.wasPressedThisFrame) SetView(6);
            if (kb.f8Key.wasPressedThisFrame) SetView(7);
            if (kb.f9Key.wasPressedThisFrame) SetView(8);
            if (kb.f11Key.wasPressedThisFrame) SetView(9);
            if (kb.cKey.wasPressedThisFrame) { if (fpMode) ExitFirstPerson(); else EnterFirstPerson(); }
            if (kb.xKey.wasPressedThisFrame) lensOn = !lensOn;
            if (kb.hKey.wasPressedThisFrame) showHelp = !showHelp;
            if (kb.f10Key.wasPressedThisFrame) showHud = !showHud;
            if (kb.iKey.wasPressedThisFrame) showInfo = !showInfo;
            var debugKey = kb.FindKeyOnCurrentKeyboardLayout("ù");
            if (debugKey != null && debugKey.wasPressedThisFrame) showCellDebug = !showCellDebug;
            if (kb.vKey.wasPressedThisFrame) view.ShowPlants = !view.ShowPlants;
            if (kb.rKey.wasPressedThisFrame && SavePlanet()) { RegenerateRandom(); return; }

            if (kb.bKey.wasPressedThisFrame) { int c = Pick(); if (c >= 0) Sim.StartVolcano(c); }
            if (kb.fKey.wasPressedThisFrame) { int c = Pick(); if (c >= 0) Eco.Ignite(c); }
            if (kb.gKey.wasPressedThisFrame) { int c = Pick(); if (c >= 0) AddWater(c); }
            if (kb.kKey.wasPressedThisFrame) { int c = Pick(); if (c >= 0) Sim.ForceQuake(c, rnd); }
            if (kb.tKey.wasPressedThisFrame) { int c = Pick(); if (c >= 0) Sim.ForceTsunami(c); }
            if (kb.lKey.wasPressedThisFrame) { int c = Pick(); if (c >= 0) { Sim.ForceLandslide(c); view.MarkDirty(); } }
            if (kb.mKey.wasPressedThisFrame) { int c = Pick(); if (c >= 0) Sim.SpawnAsteroid(PlanetSim.AstImpact, c, rnd); }
            if (kb.nKey.wasPressedThisFrame) { int c = Pick(); if (c >= 0) Sim.SpawnAsteroid(PlanetSim.AstAir, c, rnd); }
            if (kb.jKey.wasPressedThisFrame) { int c = Pick(); if (c >= 0) Sim.SpawnAsteroid(PlanetSim.AstBurn, c, rnd); }
        }

        void SetView(int m)
        {
            if (m == 4 && Sim.plate == null) return;
            view.ViewMode = m;
            view.RefreshAll();
        }

        void AddWater(int c)
        {
            Sim.water[c] += 40f;
            Sim.externalAdded[PlanetSim.ExternalWater] += 40f;
        }

        // cell under the screen centre
        int Pick()
        {
            Vector3 o = camPos, d = camRot * Vector3.forward;
            if (fpMode || o.magnitude < radius + 10f)
            {
                for (float s = 2f; s < 400f; s += 4f)
                {
                    Vector3 p = o + d * s;
                    int pc = Grid.CellFromDir(p);
                    if (p.magnitude < radius + Sim.Ground(pc)) return pc;
                }
                return -1;
            }
            float r = radius + 10f;
            float b = Vector3.Dot(o, d);
            float cc = Vector3.Dot(o, o) - r * r;
            float disc = b * b - cc;
            if (disc < 0f) return -1;
            float t = -b - Mathf.Sqrt(disc);
            if (t < 0f) t = -b + Mathf.Sqrt(disc);
            if (t < 0f) return -1;
            return Grid.CellFromDir(o + d * t);
        }

        void UpdateCursor()
        {
            var kb = Keyboard.current;
            bool free = kb != null && kb.leftAltKey.isPressed;
            bool lockIt = fpMode && !pauseMenu && !mainMenu && !showStats && !free;
            Cursor.lockState = lockIt ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !lockIt;
        }

        bool GoodSpawn(int c)
        {
            float g = Sim.Ground(c);
            if (g < 2f || g > 80f || Sim.water[c] > 0.2f || Sim.lava[c] > 0.1f) return false;
            for (int k = 0; k < 4; k++)
            {
                int n = Grid.Nbr[c * 4 + k];
                if (n >= 0 && Mathf.Abs(Sim.Ground(n) - g) / Grid.CellSize > 0.4f) return false;
            }
            return true;
        }

        float FpGroundAt(Vector3 dir)
        {
            int c = Grid.CellFromDir(dir);
            float s = 0f; int n = 0;
            for (int k = 0; k < 4; k++)
            {
                int nb = Grid.Nbr[c * 4 + k];
                if (nb >= 0) { s += Sim.Ground(nb); n++; }
            }
            float g = Sim.Ground(c);
            return n > 0 ? 0.3f * g + 0.7f * (s / n) : g;
        }

        void EnterFirstPerson()
        {
            int c = Pick();
            if (c < 0 || !GoodSpawn(c))
            {
                c = -1;
                Vector3 cd = camPos.normalized;
                for (int i = 0; i < 300 && c < 0; i++)
                {
                    int t = Grid.CellFromDir((cd + Random.insideUnitSphere * 0.5f).normalized);
                    if (GoodSpawn(t)) c = t;
                }
                for (int i = 0; i < 2000 && c < 0; i++)
                {
                    int t = rnd.Next(Grid.Dir.Length);
                    if (GoodSpawn(t)) c = t;
                }
            }
            if (c < 0) return;
            fpDir = Grid.Dir[c];
            fpHead = Vector3.ProjectOnPlane(camRot * Vector3.forward, fpDir);
            if (fpHead.sqrMagnitude < 1e-3f) fpHead = Vector3.ProjectOnPlane(Vector3.up, fpDir);
            if (fpHead.sqrMagnitude < 1e-3f) fpHead = Vector3.ProjectOnPlane(Vector3.right, fpDir);
            fpHead.Normalize();
            fpPitch = 0f; fpY = 0f; fpVy = 0f;
            fpGround = FpGroundAt(fpDir);
            fpMode = true; flyT = 1f;
            cam.nearClipPlane = 0.15f;
        }

        void ExitFirstPerson()
        {
            if (!fpMode) return;
            fpMode = false;
            cam.nearClipPlane = 0.5f;
            camPos = fpDir * (radius + Mathf.Max(0f, fpGround) + 250f);
            camRot = Quaternion.LookRotation(-fpDir, fpHead);
            cam.transform.SetPositionAndRotation(camPos, camRot);
        }

        void UpdateFirstPerson(float dt)
        {
            var kb = Keyboard.current;
            var ms = Mouse.current;
            if (ms != null && Cursor.lockState == CursorLockMode.Locked)
            {
                Vector2 dl = ms.delta.ReadValue() * 0.1f;
                fpHead = (Quaternion.AngleAxis(dl.x, fpDir) * fpHead);
                fpPitch = Mathf.Clamp(fpPitch + dl.y, -85f, 85f);
            }
            Vector3 right = Vector3.Cross(fpDir, fpHead);
            Vector3 wish = Vector3.zero;
            bool grounded = fpY <= 0.01f;
            if (kb != null)
            {
                if (kb.wKey.isPressed) wish += fpHead;
                if (kb.sKey.isPressed) wish -= fpHead;
                if (kb.dKey.isPressed) wish += right;
                if (kb.aKey.isPressed) wish -= right;
                if (grounded && kb.spaceKey.wasPressedThisFrame) fpVy = 6.5f;
            }
            if (wish.sqrMagnitude > 0.01f)
            {
                float speed = kb != null && kb.leftShiftKey.isPressed ? 14f : 5f;
                float dist = speed * dt;
                Vector3 nd = (fpDir + wish.normalized * dist / (radius + fpGround)).normalized;
                int nc = Grid.CellFromDir(nd);
                float ng = FpGroundAt(nd);
                bool ok = ng > 0.3f && Sim.water[nc] < 0.6f && Sim.lava[nc] < 0.2f && (ng - fpGround) / Mathf.Max(dist, 1e-4f) < 1.2f;
                if (ok)
                {
                    fpDir = nd;
                    fpHead = Vector3.ProjectOnPlane(fpHead, fpDir).normalized;
                }
            }
            float tg = FpGroundAt(fpDir);
            fpGround = Mathf.Lerp(fpGround, tg, 1f - Mathf.Exp(-14f * dt));
            fpY += fpVy * dt;
            fpVy -= 16f * dt;
            if (fpY <= 0f) { fpY = 0f; fpVy = 0f; }

            camPos = fpDir * (radius + fpGround + fpY + 1.8f);
            float pr = fpPitch * Mathf.Deg2Rad;
            camRot = Quaternion.LookRotation(fpHead * Mathf.Cos(pr) + fpDir * Mathf.Sin(pr), fpDir);
            cam.transform.SetPositionAndRotation(camPos, camRot);
            if (shake > 0.01f)
                cam.transform.rotation = camRot * Quaternion.Euler(Random.Range(-shake, shake), Random.Range(-shake, shake), Random.Range(-shake, shake) * 0.5f);
        }

        void FocusOn(int c)
        {
            if (c < 0) return;
            ExitFirstPerson();
            timeScale = 1f; paused = false; pauseMenu = false;
            Vector3 dir = Grid.Dir[c];
            flyFromPos = camPos; flyFromRot = camRot;
            flyToPos = dir * (radius + Mathf.Max(0f, Sim.Ground(c)) + 240f);
            Vector3 upv = Vector3.ProjectOnPlane(Vector3.up, dir);
            if (upv.sqrMagnitude < 1e-3f) upv = Vector3.ProjectOnPlane(Vector3.forward, dir);
            flyToRot = Quaternion.LookRotation(-dir, upv.normalized);
            flyT = 0f;
        }

        // Redresse la caméra actuelle (même position, même direction de visée) pour que le pôle nord
        // de la planète redevienne "en haut" de l'écran, sans revenir à la vue de génération initiale.
        public void StraightenCameraNorth()
        {
            Vector3 fwd = camRot * Vector3.forward;
            Vector3 up = Vector3.ProjectOnPlane(Vector3.up, fwd);
            if (up.sqrMagnitude < 1e-3f) up = Vector3.ProjectOnPlane(Vector3.forward, fwd);
            flyFromPos = camPos; flyFromRot = camRot;
            flyToPos = camPos;
            flyToRot = Quaternion.LookRotation(fwd, up.normalized);
            flyT = 0f;
        }

        void UpdateFly(float dt)
        {
            flyT = Mathf.Min(1f, flyT + dt / 1.4f);
            float k = Mathf.SmoothStep(0f, 1f, flyT);
            Vector3 d = Vector3.Slerp(flyFromPos.normalized, flyToPos.normalized, k);
            camPos = d * Mathf.Lerp(flyFromPos.magnitude, flyToPos.magnitude, k);
            camRot = Quaternion.Slerp(flyFromRot, flyToRot, k);
            cam.transform.SetPositionAndRotation(camPos, camRot);
        }

        void UpdateCamera()
        {
            var kb = Keyboard.current;
            var ms = Mouse.current;
            float dt = Time.unscaledDeltaTime;
            if (fpMode) { UpdateFirstPerson(dt); return; }
            if (flyT < 1f) { UpdateFly(dt); return; }
            Vector3 up = camPos.normalized;

            int ci = Grid.CellFromDir(camPos);
            float floor = radius + Sim.Ground(ci);
            float alt = camPos.magnitude - floor;

            if (ms != null)
            {
                bool overUi = MouseOverUi(ms);
                if (ms.leftButton.wasPressedThisFrame) leftDrag = !overUi && !ToolArmed;
                if (!ms.leftButton.isPressed) leftDrag = false;
                if (ms.rightButton.isPressed && !overUi)
                {
                    Vector2 dl = ms.delta.ReadValue() * 0.12f;
                    camRot = Quaternion.AngleAxis(dl.x, up) * camRot;
                    float p0 = Mathf.Asin(Mathf.Clamp(Vector3.Dot(camRot * Vector3.forward, up), -1f, 1f)) * Mathf.Rad2Deg;
                    float p1 = Mathf.Clamp(p0 + dl.y, -89f, 89f);
                    camRot = camRot * Quaternion.AngleAxis(-(p1 - p0), Vector3.right);
                }
                if (leftDrag)
                {
                    // glisser : la planète suit la souris (déplacement tangent, proportionnel à l'altitude)
                    Vector2 d = ms.delta.ReadValue();
                    float k = Mathf.Max(alt, 4f) * 2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(Screen.height, 1);
                    Vector3 fwd = Vector3.ProjectOnPlane(camRot * Vector3.forward, up);
                    if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(camRot * Vector3.up, up);
                    fwd.Normalize();
                    Vector3 right = Vector3.Cross(up, fwd);
                    float len0 = camPos.magnitude;
                    Vector3 oUp = up;
                    camPos += (-right * d.x - fwd * d.y) * k;
                    camPos = camPos.normalized * len0;
                    camRot = Quaternion.FromToRotation(oUp, camPos.normalized) * camRot;
                    up = camPos.normalized;
                }
                float scroll = ms.scroll.ReadValue().y;
                if (scroll != 0f && !overUi)
                {
                    float step = Mathf.Max(alt, 6f) * 0.2f * Mathf.Sign(scroll);
                    camPos += camRot * Vector3.forward * step;
                }
            }

            float speed = Mathf.Clamp(alt * 0.9f, 3f, 700f);

            Vector3 mv = Vector3.zero;
            if (kb != null)
            {
                if (kb.wKey.isPressed) mv += Vector3.forward;
                if (kb.sKey.isPressed) mv += Vector3.back;
                if (kb.dKey.isPressed) mv += Vector3.right;
                if (kb.aKey.isPressed) mv += Vector3.left;
                if (kb.spaceKey.isPressed || kb.eKey.isPressed) mv += Vector3.up;
                if (kb.leftCtrlKey.isPressed || kb.qKey.isPressed) mv += Vector3.down;
                if (kb.leftShiftKey.isPressed) speed *= 4f;
            }
            Vector3 world = camRot * Vector3.forward * mv.z + camRot * Vector3.right * mv.x + up * mv.y;
            Vector3 oldUp = up;
            camPos += world * speed * dt;

            float minR = radius + Sim.Ground(Grid.CellFromDir(camPos)) + 2f;
            float len = camPos.magnitude;
            if (len < minR) camPos = camPos.normalized * minR;
            if (len > radius + MaxCamAlt) camPos = camPos.normalized * (radius + MaxCamAlt);

            Vector3 newUp = camPos.normalized;
            camRot = Quaternion.FromToRotation(oldUp, newUp) * camRot;

            cam.transform.SetPositionAndRotation(camPos, camRot);
            if (shake > 0.01f)
                cam.transform.rotation = camRot * Quaternion.Euler(Random.Range(-shake, shake), Random.Range(-shake, shake), Random.Range(-shake, shake) * 0.5f);
        }

        // ------------------------------------------------------------------ HUD
        static readonly string[] SeasonNames = { "Printemps", "Été", "Automne", "Hiver" };
        static readonly string[] ViewNames = { "Normal", "Température", "Humidité", "Altitude", "Plaques", "Magma", "Matériaux", "Gaz", "Nappes", "Pression", "Érosion eau", "Érosion vent" };
        static readonly string[] ViewIcons = { "🌍", "🌡", "💧", "⛰", "🧩", "🌋", "🪨", "💨", "🚰", "🌀", "🌊", "🏜" };
        static readonly string[] ToggleNames = { "Plantes", "Nuages", "Scattering atmosphérique" };
        static readonly string[] ToggleIcons = { "🌿", "☁", "✨" };
        GUIStyle style, btn, title, box, evBtn, iconBtn, tooltip;
        // Variantes de `style` utilisées à chaque image : créées une fois, pas à chaque événement d'interface.
        GUIStyle wrapStyle, infoStyle, centerStyle, centerBoldStyle, statusStyle, smallStyle;

        // L'interface est dessinée pour un écran de 1080 lignes, puis agrandie d'un bloc sur les écrans plus
        // hauts : sans cela elle garde sa taille en pixels et devient illisible en 1440p ou en 4K.
        float uiScale = 1f;
        float UiW { get { return Screen.width / uiScale; } }
        float UiH { get { return Screen.height / uiScale; } }

        bool MouseOverUi(Mouse ms)
        {
            Vector2 p = ms.position.ReadValue();
            p.y = Screen.height - p.y;
            p /= uiScale;
            return toolRect.Contains(p) || toolbarRect.Contains(p) || eventsRect.Contains(p) || mapPanelRect.Contains(p) ||
                dialRect.Contains(p) || infoRect.Contains(p);
        }

        void DrawEvents()
        {
            eventsRect = new Rect(0, 0, 0, 0);
            if (pauseMenu) return;
            float now = Time.realtimeSinceStartup;
            float y = 15f;
            for (int i = Sim.events.Count - 1; i >= 0; i--)
            {
                var e = Sim.events[i];
                if (now - e.t > 45f) continue;
                string text = e.text.StartsWith("☄ ") ? e.text.Substring(2) : e.text;
                string label = EventSymbol(text) + " " + text;
                float width = Mathf.Min(Mathf.Min(420f, UiW * 0.48f), evBtn.CalcSize(new GUIContent(label)).x + 18f);
                if (GUI.Button(new Rect(15, y, width, 25), label, evBtn) && e.cell >= 0) FocusOn(e.cell);
                y += 27f;
            }
            if (y > 15f) eventsRect = new Rect(15, 15, Mathf.Min(420f, UiW * 0.48f), y - 15f);
        }

        static string EventSymbol(string text)
        {
            if (text.IndexOf("séisme", System.StringComparison.OrdinalIgnoreCase) >= 0) return "◉";
            if (text.IndexOf("tsunami", System.StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("inondation", System.StringComparison.OrdinalIgnoreCase) >= 0) return "🌊";
            if (text.IndexOf("éruption", System.StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("cratère", System.StringComparison.OrdinalIgnoreCase) >= 0) return "🌋";
            if (text.IndexOf("éboulement", System.StringComparison.OrdinalIgnoreCase) >= 0) return "⛰";
            if (text.IndexOf("tempête", System.StringComparison.OrdinalIgnoreCase) >= 0) return "⛈";
            if (text.IndexOf("incendie", System.StringComparison.OrdinalIgnoreCase) >= 0) return "🔥";
            if (text.IndexOf("astéroïde", System.StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("météore", System.StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("bolide", System.StringComparison.OrdinalIgnoreCase) >= 0) return "☄";
            return "⚠";
        }

        void InitStyles()
        {
            if (style != null) return;
            style = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true };
            style.normal.textColor = Color.white;
            btn = new GUIStyle(GUI.skin.button) { fontSize = 15, richText = true };
            title = new GUIStyle(style) { fontSize = 34, alignment = TextAnchor.MiddleCenter };
            box = new GUIStyle(GUI.skin.box);
            evBtn = new GUIStyle(GUI.skin.button) { fontSize = 12, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            evBtn.normal.textColor = new Color(1f, 0.75f, 0.4f);
            evBtn.hover.textColor = Color.white;
            iconBtn = new GUIStyle(GUI.skin.button) { fontSize = 20, alignment = TextAnchor.MiddleCenter, richText = false, padding = new RectOffset(0, 0, 0, 0) };
            tooltip = new GUIStyle(style) { fontSize = 12, alignment = TextAnchor.MiddleCenter, richText = false };
            tooltip.normal.textColor = Color.white;
            link = new GUIStyle(style) { richText = true, alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
            wrapStyle = new GUIStyle(style) { wordWrap = true };
            infoStyle = new GUIStyle(style) { wordWrap = true, fontSize = 13 };
            centerStyle = new GUIStyle(style) { alignment = TextAnchor.MiddleCenter };
            centerBoldStyle = new GUIStyle(style) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            statusStyle = new GUIStyle(style) { alignment = TextAnchor.MiddleCenter, fontSize = 12 };
            smallStyle = new GUIStyle(style) { fontSize = 11 };
        }

        // ----------------------------------------------------------- menu principal : texte sélectionnable + panneau arrondi
        GUIStyle link;
        Texture2D cornerMaskTex;

        // Masque alpha en quart de cercle (coin haut-gauche), réutilisé pour les 4 coins d'un panneau arrondi
        // via GUI.DrawTextureWithTexCoords (retournement d'UV) : évite de générer une texture par coin/par taille.
        Texture2D CornerMask()
        {
            if (cornerMaskTex != null) return cornerMaskTex;
            const int size = 48;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = size - x, dy = size - y;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(size - d + 0.5f);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            cornerMaskTex = tex;
            return tex;
        }

        void DrawRoundedPanel(Rect r, float radius, Color col)
        {
            radius = Mathf.Min(radius, Mathf.Min(r.width, r.height) * 0.5f);
            var tex = CornerMask();
            FillRect(new Rect(r.x + radius, r.y, Mathf.Max(0f, r.width - 2f * radius), r.height), col);
            FillRect(new Rect(r.x, r.y + radius, radius, Mathf.Max(0f, r.height - 2f * radius)), col);
            FillRect(new Rect(r.xMax - radius, r.y + radius, radius, Mathf.Max(0f, r.height - 2f * radius)), col);
            var old = GUI.color;
            GUI.color = col;
            GUI.DrawTextureWithTexCoords(new Rect(r.x, r.y, radius, radius), tex, new Rect(0f, 0f, 1f, 1f));
            GUI.DrawTextureWithTexCoords(new Rect(r.xMax - radius, r.y, radius, radius), tex, new Rect(1f, 0f, -1f, 1f));
            GUI.DrawTextureWithTexCoords(new Rect(r.x, r.yMax - radius, radius, radius), tex, new Rect(0f, 1f, 1f, -1f));
            GUI.DrawTextureWithTexCoords(new Rect(r.xMax - radius, r.yMax - radius, radius, radius), tex, new Rect(1f, 1f, -1f, -1f));
            GUI.color = old;
        }

        // Option/action sous forme de texte simple (pas de bouton encadré) : surbrillance au survol / sélection,
        // zone de clic invisible superposée (GUIStyle.none) pour conserver l'apparence purement textuelle.
        bool TextOption(Rect r, string label, bool selected)
        {
            bool hover = r.Contains(Event.current.mousePosition);
            var st = new GUIStyle(link) { fontSize = selected ? 17 : 15, fontStyle = selected ? FontStyle.Bold : FontStyle.Normal };
            st.normal.textColor = selected ? new Color(0.55f, 1f, 0.65f) : (hover ? Color.white : new Color(1f, 1f, 1f, 0.62f));
            GUI.Label(r, label, st);
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        Vector2 mainMenuScroll, pauseMenuScroll;

        void BeginMenuPanel(float w, float h, ref Vector2 scroll, out Rect r)
        {
            float width = Mathf.Min(w, Mathf.Max(1f, UiW - 20f));
            float height = Mathf.Min(h, Mathf.Max(1f, UiH - 20f));
            Rect panel = new Rect((UiW - width) / 2f, (UiH - height) / 2f, width, height);
            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.DrawTexture(panel, Texture2D.whiteTexture);
            GUI.color = old;
            scroll = GUI.BeginScrollView(panel, scroll, new Rect(0f, 0f, w, h));
            r = new Rect(0f, 0f, w, h);
        }

        // Menu principal : panneau arrondi "flottant" docké à gauche, uniquement du texte sélectionnable (pas de
        // bouton encadré), avec à droite un aperçu en direct de la planète qui correspond aux options choisies.
        void DrawMainMenu()
        {
            if (!selectingPlanet) RefreshMenuPreviewIfNeeded();

            float margin = 24f;
            float panelW = Mathf.Min(420f, UiW * 0.42f);
            Rect panel = new Rect(margin, margin, panelW, UiH - margin * 2f);
            DrawRoundedPanel(panel, 26f, new Color(0.04f, 0.05f, 0.08f, 0.82f));

            Rect content = new Rect(panel.x + 34f, panel.y + 26f, panel.width - 68f, panel.height - 52f);
            float innerH = 800f;
            Vector2 scroll = mainMenuScroll;
            scroll = GUI.BeginScrollView(content, scroll, new Rect(0f, 0f, content.width, innerH));
            mainMenuScroll = scroll;
            float x = 0f, w = content.width, y = 0f;

            GUI.Label(new Rect(x, y, w, 56), "Pocket Planet", new GUIStyle(title) { fontSize = 30, alignment = TextAnchor.MiddleLeft });
            y += 64f;

            GUI.Label(new Rect(x, y, w, 22), "Résolution du terrain", style); y += 26f;
            for (int i = 0; i < ResNames.Length; i++)
            {
                if (TextOption(new Rect(x + 8f, y, w - 8f, 24f), ResNames[i], resIdx == i)) resIdx = i;
                y += 24f;
            }
            y += 14f;

            GUI.Label(new Rect(x, y, w, 22), "Taille de la planète", style); y += 26f;
            for (int i = 0; i < RadNames.Length; i++)
            {
                if (TextOption(new Rect(x + 8f, y, w - 8f, 24f), RadNames[i], radIdx == i)) radIdx = i;
                y += 24f;
            }
            y += 20f;

            GUI.Label(new Rect(x, y, w, 22), "Graine (vide = aléatoire)", style); y += 26f;
            seedText = GUI.TextField(new Rect(x, y, Mathf.Min(220f, w), 26f), seedText, 9, btn);
            if (GUI.Button(new Rect(x + Mathf.Min(220f, w) + 8f, y - 1f, 28f, 28f), "🎲", iconBtn))
                seedText = UnityEngine.Random.Range(1, 999999999).ToString();
            y += 38f;

            GUI.Label(new Rect(x, y, w, 22), "Nom de la planète (optionnel)", style); y += 26f;
            planetName = GUI.TextField(new Rect(x, y, Mathf.Min(260f, w), 26f), planetName, 40, btn);
            y += 38f;

            GUI.Label(new Rect(x, y, w, 22), "Âge de la planète : " + ageGyr.ToString("0.0") + " Md d'années", style); y += 24f;
            ageGyr = Mathf.Round(GUI.HorizontalSlider(new Rect(x, y, w, 20), ageGyr, 0.5f, 8f) * 10f) / 10f; y += 22f;
            GUI.Label(new Rect(x, y, w, 20), ageGyr < 2f ? "Jeune : chaude, volcanique, atmosphère toxique" : ageGyr > 6f ? "Ancienne : refroidie, érodée, calme" : "Mature",
                new GUIStyle(style) { fontSize = 12 }); y += 30f;

            GUI.Label(new Rect(x, y, w, 22), "Durée d'un jour : " + Mathf.RoundToInt(dayMinutes) + " min", style); y += 24f;
            dayMinutes = Mathf.Round(GUI.HorizontalSlider(new Rect(x, y, w, 20), dayMinutes, 10f, 60f)); y += 34f;

            FillRect(new Rect(x, y, w, 1f), new Color(1f, 1f, 1f, 0.12f)); y += 24f;

            if (TextOption(new Rect(x, y, w, 30), "▶ Nouvelle planète", false)) BeginGenerate();
            y += 36f;
            if (TextOption(new Rect(x, y, w, 30), "▶ Charger une planète sauvegardée", false))
            {
                saveMessage = "";
                FindSavedPlanets();
                selectingPlanet = true;
            }
            y += 36f;
            if (TextOption(new Rect(x, y, w, 30), "▶ Quitter", false)) QuitGame();
            y += 44f;

            GUI.Label(new Rect(x, y, w, 50), saveMessage, new GUIStyle(style) { wordWrap = true, fontSize = 12 });
            GUI.EndScrollView();

            if (!selectingPlanet && menuPreview != null)
            {
                float display = Mathf.Min(UiH * 0.72f, (UiW - panel.xMax - margin) * 0.85f);
                display = Mathf.Max(80f, display);
                Rect pr = new Rect(panel.xMax + (UiW - panel.xMax - display) / 2f, (UiH - display) / 2f, display, display);
                DrawPlanetPreview(menuPreview, pr);
            }
        }

        void DrawPauseMenu()
        {
            Rect r; BeginMenuPanel(360, 300, ref pauseMenuScroll, out r);
            GUI.Label(new Rect(r.x, r.y + 12, r.width, 44), "Pause", title);
            float y = r.y + 76;
            if (GUI.Button(new Rect(r.x + 30, y, 300, 44), "Statistiques", btn))
            {
                pauseMenu = false;
                showStats = true;
            }
            y += 56;
            if (GUI.Button(new Rect(r.x + 30, y, 300, 44), "Menu principal", btn))
            {
                if (SavePlanet())
                {
                    pauseMenu = false; mainMenu = true; ready = false;
                    if (planetName == DefaultPlanetName(curSeed)) planetName = "";
                    if (view != null) { view.Destroy(); view = null; }
                    cam.backgroundColor = new Color(0.01f, 0.012f, 0.03f);
                }
            }
            y += 56;
            if (GUI.Button(new Rect(r.x + 30, y, 300, 44), "Quitter", btn)) QuitGame();
            y += 56;
            GUI.Label(new Rect(r.x + 30, y, 300, 40), string.IsNullOrEmpty(saveMessage) ?
                "Sauvegarde automatique toutes les 5 minutes." : saveMessage, wrapStyle);
            GUI.EndScrollView();
        }

        // Statistiques en plein écran (accessible depuis le menu pause) : un onglet par système, avec une
        // bascule d'historique long (activée par défaut), au lieu de la
        // fenêtre glissante jour/saison/année, pour suivre une évolution de long terme (ex. climat, géologie).
        void DrawStatsScreen()
        {
            FillRect(new Rect(0f, 0f, UiW, UiH), new Color(0.015f, 0.018f, 0.035f, 0.97f));
            float pad = 24f;
            var r = new Rect(pad, pad, UiW - pad * 2f, UiH - pad * 2f);
            GUI.Label(new Rect(r.x, r.y, r.width - 140f, 44f), "Statistiques", title);
            if (GUI.Button(new Rect(r.xMax - 120f, r.y + 6f, 120f, 36f), "Fermer (Échap)", btn)) showStats = false;

            float y = r.y + 56f;
            graphTab = GUI.SelectionGrid(new Rect(r.x, y, r.width * 0.72f, 34f), graphTab, GraphTabs, 5, btn);
            if (GUI.Button(new Rect(r.x + r.width * 0.72f + 10f, y, r.width * 0.28f - 10f, 34f),
                statsSinceStart ? "✓ Historique long" : "Historique long", btn)) statsSinceStart = !statsSinceStart;
            y += 42f;
            if (!statsSinceStart)
            {
                graphPeriod = GUI.SelectionGrid(new Rect(r.x, y, r.width, 28f), graphPeriod, GraphPeriods, 3, btn);
                y += 36f;
            }

            Series[] ss = graphTab == 0 ? sClimate : (graphTab == 1 ? sVeg : (graphTab == 2 ? sFauna : (graphTab == 3 ? sRes : sGeo)));
            if (ss == null) { GUI.Label(new Rect(r.x, y, r.width, 24f), "Collecte des données...", style); return; }
            float legendH = ((ss.Length + 1) / 2) * 22f;
            float gh = Mathf.Max(120f, r.yMax - y - legendH - 14f);
            DrawGraph(new Rect(r.x, y, r.width, gh), ss, (graphTab == 1 || graphTab == 2), statsSinceStart);
            y += gh + 8f;
            DrawLegend(new Rect(r.x, y, r.width, r.yMax - y), ss);
        }

        // Surcouche d'informations détaillées (cellule visée, climat, géologie, vivant) : touche I dédiée,
        // indépendante du menu pause et des statistiques (remplace l'ancien onglet "Infos" du menu Options).
        void DrawInfoOverlay()
        {
            var wrap = infoStyle;
            string info = InfoText();
            float w = Mathf.Min(520f, UiW - 20f);
            float h = wrap.CalcHeight(new GUIContent(info), w - 24f) + 20f;
            var r = new Rect(UiW - w - 10f, 52f, w, h);
            infoRect = r;
            FillRect(r, new Color(0f, 0f, 0f, 0.65f));
            GUI.Label(new Rect(r.x + 12f, r.y + 10f, r.width - 24f, r.height - 20f), info, wrap);
        }

        void QuitGame()
        {
            if (ready && !SavePlanet()) return;
            quitSaved = true;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        bool WantsToQuit()
        {
            if (!ready || quitSaved) return true;
            if (!SavePlanet()) return false;
            quitSaved = true;
            return true;
        }

        void OnApplicationQuit()
        {
            if (ready && !quitSaved) { if (SavePlanet()) quitSaved = true; }
        }

        void StepSpeed(int dir)
        {
            int cur = 0;
            for (int i = 0; i < SpeedPresets.Length; i++) if (SpeedPresets[i] <= timeScale * 1.001f) cur = i;
            timeScale = SpeedPresets[Mathf.Clamp(cur + dir, 0, SpeedPresets.Length - 1)];
            paused = false;
        }

        void DrawTimeControls(Rect r)
        {
            float width = r.width / 4f;
            var prev = GUI.color;
            if (paused) GUI.color = new Color(0.45f, 1f, 0.5f);
            if (GUI.Button(new Rect(r.x, r.y, width - 4f, r.height), "❚❚ Pause", btn)) paused = true;
            GUI.color = prev;
            string[] names = { "▶ Lecture", "▶▶ Rapide", "▶▶▶ Très rapide" };
            for (int i = 0; i < SpeedPresets.Length; i++)
            {
                bool sel = !paused && Mathf.Approximately(timeScale, SpeedPresets[i]);
                if (sel) GUI.color = new Color(0.45f, 1f, 0.5f);
                if (GUI.Button(new Rect(r.x + (i + 1) * width, r.y, width - 4f, r.height), names[i], btn)) { timeScale = SpeedPresets[i]; paused = false; }
                GUI.color = prev;
            }
        }

        static readonly string[] Cardinals = { "N", "NE", "E", "SE", "S", "SO", "O", "NO" };

        // cellule sous la souris (mode dieu)
        int PickMouse()
        {
            // Marche le long du rayon jusqu'au relief réel : une sphère lisse décalait la cellule visée sur les
            // montagnes et ne renvoyait plus rien dès que la caméra descendait sous son rayon.
            return Mouse.current == null ? Grid.CellFromDir(camPos) : PickDebugMouse(Mouse.current);
        }

        // cadran : date, saison, heure locale et boussole. En mode dieu il suit la position de la souris sur la planète.
        void DrawDial()
        {
            int c = fpMode ? Grid.CellFromDir(camPos) : PickMouse();
            if (c < 0) c = Grid.CellFromDir(camPos);
            Vector3 d = Grid.Dir[c];
            float lat = Sim.latA[c] * Mathf.Rad2Deg;
            float lon = Mathf.Atan2(d.z, d.x) * Mathf.Rad2Deg;
            int dpy = (int)DaysPerYear;
            float vd = view.vDays;
            int year = Mathf.FloorToInt(vd / dpy);
            int day = Mathf.Clamp(Mathf.FloorToInt(vd - year * dpy), 0, dpy - 1) + 1;
            int season = Mathf.Min(3, (day - 1) / 3);
            if (lat < 0f) season = (season + 2) % 4;
            float sunA = Mathf.Atan2(view.SunDir.z, view.SunDir.x);
            float cellA = Mathf.Atan2(d.z, d.x);
            float h = Mathf.Repeat(12f + Mathf.DeltaAngle(cellA * Mathf.Rad2Deg, sunA * Mathf.Rad2Deg) / 15f, 24f);
            int hh = (int)h, mm = (int)((h - hh) * 60f);

            float w = 560f, x0 = UiW / 2f - w / 2f;
            float nameBarH = 30f, y0 = 6f + nameBarH + 4f;
            dialRect = new Rect(x0, 6f, w, y0 + 84f - 6f);
            DrawPlanetNameBar(x0, 6f, w, nameBarH);
            var old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(x0, y0, w, 84f), Texture2D.whiteTexture);
            GUI.color = old;
            var center = centerStyle;
            string hemi = lat >= 0f ? "N" : "S";
            string head = "Année " + (year + 1) + "  ·  Jour " + day + "/" + dpy + "  ·  <b>" + SeasonNames[season] + "</b>  ·  " + hh.ToString("00") + ":" + mm.ToString("00");
            GUI.Label(new Rect(x0, y0 + 2f, w, 26f), head, center);
            string sub = (fpMode ? "Position" : "Pointeur") + " : " + Mathf.Abs(lat).ToString("0") + "°" + hemi + "  " + Mathf.Abs(lon).ToString("0") + "°" + (lon >= 0f ? "E" : "O");
            if (view.EclipseAmount > 0.05f) sub += "   <color=#ff6040>Éclipse lunaire</color>";
            GUI.Label(new Rect(x0, y0 + 24f, w, 24f), sub, center);

            // boussole : cap de la caméra par rapport au nord local
            Vector3 north = Vector3.up - d * d.y;
            north = north.sqrMagnitude < 1e-6f ? Vector3.forward : north.normalized;
            Vector3 east = Vector3.Cross(d, north).normalized;
            Vector3 fwd = camRot * Vector3.forward;
            float heading = Mathf.Atan2(Vector3.Dot(fwd, east), Vector3.Dot(fwd, north)) * Mathf.Rad2Deg;
            float cy = y0 + 52f, half = w / 2f - 40f;
            GUI.color = new Color(1f, 1f, 1f, 0.35f);
            GUI.DrawTexture(new Rect(x0 + 20f, cy + 12f, w - 40f, 1f), Texture2D.whiteTexture);
            GUI.color = old;
            for (int i = 0; i < 8; i++)
            {
                float delta = Mathf.DeltaAngle(heading, i * 45f);
                if (Mathf.Abs(delta) > 80f) continue;
                float px = x0 + w / 2f + delta / 80f * half;
                bool cardinal = i % 2 == 0;
                GUI.Label(new Rect(px - 20f, cy - 2f, 40f, 24f), i == 0 ? "<color=#ff5050><b>N</b></color>" : (cardinal ? "<b>" + Cardinals[i] + "</b>" : Cardinals[i]), center);
            }
            GUI.Label(new Rect(x0 + w / 2f - 30f, cy + 14f, 60f, 20f), "▲ " + Mathf.Repeat(heading, 360f).ToString("0") + "°", center);
        }

        // Barre de nom de la planète, affichée tout en haut au-dessus du cadran (date/boussole) : nom actuel +
        // bouton crayon pour renommer en direct, ou champ de saisie + validation/annulation pendant l'édition.
        void DrawPlanetNameBar(float x0, float y, float w, float h)
        {
            var old = GUI.color;
            if (!renamingPlanet)
            {
                GUI.color = new Color(0f, 0f, 0f, 0.55f);
                GUI.DrawTexture(new Rect(x0, y, w, h), Texture2D.whiteTexture);
                GUI.color = old;
                var center = centerBoldStyle;
                GUI.Label(new Rect(x0, y, w, h), DisplayPlanetName(), center);
                if (GUI.Button(new Rect(x0 + w - h, y, h, h), "✎", iconBtn)) BeginRenamePlanet();
            }
            else
            {
                float fieldW = Mathf.Min(300f, w - 2f * h - 16f);
                float fx = x0 + (w - fieldW) / 2f - h - 4f;
                GUI.SetNextControlName("PlanetNameField");
                renameBuffer = GUI.TextField(new Rect(fx, y + 2f, fieldW, h - 4f), renameBuffer, 40, btn);
                if (GUI.Button(new Rect(fx + fieldW + 6f, y, h, h), "✔", iconBtn)) ConfirmRenamePlanet();
                if (GUI.Button(new Rect(fx + fieldW + 12f + h, y, h, h), "✕", iconBtn)) CancelRenamePlanet();
                if (!renameFocused) { GUI.FocusControl("PlanetNameField"); renameFocused = true; }
            }
        }

        void BeginRenamePlanet()
        {
            renameBuffer = DisplayPlanetName();
            renamingPlanet = true;
            renameFocused = false;
        }

        void ConfirmRenamePlanet()
        {
            string trimmed = renameBuffer != null ? renameBuffer.Trim() : "";
            planetName = trimmed.Length > 0 ? trimmed : DefaultPlanetName(curSeed);
            renamingPlanet = false;
            GUIUtility.keyboardControl = 0;
        }

        void CancelRenamePlanet()
        {
            renamingPlanet = false;
            GUIUtility.keyboardControl = 0;
        }

        void OnGUI()
        {
            InitStyles();
            uiScale = Mathf.Max(1f, Screen.height / 1080f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(uiScale, uiScale, 1f));
            if (mainMenu) { if (selectingPlanet) DrawSavedPlanets(); else DrawMainMenu(); return; }
            if (!ready)
            {
                DrawLoadingScreen();
                return;
            }
            if (showStats) { DrawStatsScreen(); return; }
            if (!showHud && !pauseMenu)
            {
                // F10 : plus aucun élément d'interface, ni dessiné ni cliquable (F10 de nouveau pour la retrouver).
                toolRect = toolbarRect = eventsRect = dialRect = infoRect = layerRect = mapPanelRect = new Rect(0, 0, 0, 0);
                return;
            }
            if (!pauseMenu) DrawTools();
            else toolRect = toolbarRect = new Rect(0, 0, 0, 0);
            DrawEvents();
            if (!string.IsNullOrEmpty(saveMessage))
                GUI.Label(new Rect(15f, UiH - 185f, 500f, 45f), saveMessage, wrapStyle);
            DrawDial();
            if (pauseMenu) DrawPauseMenu();
            if (!ready) return;
            bool guiEnabled = GUI.enabled;
            GUI.enabled = guiEnabled && !pauseMenu;
            DrawLayerButtons();
            DrawMiniMap();
            GUI.enabled = guiEnabled;
            if (showCellDebug && !pauseMenu) DrawCellDebug();
            infoRect = new Rect(0, 0, 0, 0);
            if (!showHud) { layerRect = new Rect(0, 0, 0, 0); return; }

            if (showInfo && !pauseMenu) DrawInfoOverlay();

            if (showHelp)
            {
                string h = "<b>Contrôles</b>\nClic gauche glisser : déplacer la caméra sur la planète   Clic droit glisser : regarder   Molette : zoom   ZQSD/WASD : voler   Espace/E : monter   Ctrl/Q : descendre   Shift : x4\n" +
                    "[ ] ou - + : vitesse du temps   P : pause   Échap : menu   R : nouvelle planète\n" +
                    "F1-F9, F11 : vues (normal, température, humidité, altitude, plaques, magma, matériaux, gaz, nappes, pression)   C : mode humain (Alt = libérer la souris, Espace = saut)   X : voir à travers les nuages   V : plantes   B : volcan   F : incendie   M/N/J : astéroïde (impact / explosion / météore)   G : déluge   K : séisme   T : tsunami   L : éboulement   H : aide   I : infos   ù : diagnostic cellule   F10 : masquer l'interface";
                GUI.Label(new Rect(15, UiH - 150, 1700, 90), h, style);
            }
            GUI.Label(new Rect(UiW / 2 - 6, UiH / 2 - 12, 20, 24), "+", style);
        }
    }
}
