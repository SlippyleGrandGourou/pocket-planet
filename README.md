# Pocket Planet

Simulation de planète en mode « dieu » : géologie, tectonique des plaques, érosion, climat, océans, végétation, faune et cataclysmes, sur une planète entière générée à partir d'une graine.

## Prérequis

- Unity **6000.6.8f1** (Universal Render Pipeline 17, Input System).
- Ouvrir le dossier du dépôt comme projet Unity, puis lancer la scène `Assets/Scenes/SampleScene.unity`. Le jeu crée lui-même son objet `PlanetWorld` au démarrage.

## Organisation du code

Tout le code de jeu est dans `Assets/Scripts/Planet/`, espace de noms `LivingPlanet`.

| Partie | Fichiers |
| --- | --- |
| Grille cube-sphère | `PlanetGrid.cs` |
| Simulation (`PlanetSim`, classe partielle) | `PlanetSim.cs`, `Tectonics.cs`, `Terrain.cs`, `Volcanism.cs`, `Weather.cs`, `Currents.cs`, `Groundwater.cs`, `IceSystem.cs`, `Gas.cs`, `Fire.cs`, `Clouds.cs`, `Hazards.cs`, `Asteroids.cs`, `Tools.cs` |
| Vivant | `Ecosystem.cs`, `Fauna.cs`, `EcoFields.cs` |
| Rendu (`PlanetView`, classe partielle) | `PlanetView*.cs`, shaders dans `Assets/Resources/LivingPlanet/` |
| Boucle de jeu, caméra, interface (`PlanetWorld`, classe partielle) | `PlanetWorld.cs`, `PlanetHud.cs`, `PlanetTools.cs` |
| Sauvegarde | `PlanetSave.cs`, `PlanetWorldSave.cs` |
| Bruit, parallélisme, aléatoire | `Noise.cs` |

## Contrôles

| Action | Touches |
| --- | --- |
| Déplacer la caméra sur la planète | Clic gauche glissé |
| Regarder autour | Clic droit glissé |
| Zoom | Molette |
| Voler | ZQSD / WASD, Espace ou E pour monter, Ctrl ou Q pour descendre, Maj pour aller 4 fois plus vite |
| Vitesse du temps | `[` et `]`, ou `-` et `+` |
| Pause | P |
| Menu | Échap |
| Nouvelle planète aléatoire | R |
| Vues (normal, température, humidité, altitude, plaques, magma, matériaux, gaz, nappes, pression) | F1 à F9, F11 |
| Mode humain | C (Alt libère la souris, Espace pour sauter) |
| Voir à travers les nuages | X |
| Afficher les plantes | V |
| Volcan, incendie, déluge | B, F, G |
| Séisme, tsunami, éboulement | K, T, L |
| Astéroïde (impact, explosion, météore) | M, N, J |
| Aide, infos, diagnostic de cellule | H, I, ù |
| Masquer l'interface | F10 |

Les outils du bas de l'écran (matières, gaz, catastrophes, vivant, souffle, lieux) s'appliquent d'un clic sur la planète. Échap désarme l'outil en cours.

## Sauvegardes

Les planètes sont enregistrées dans le dossier `Planets` de `Application.persistentDataPath`, avec une sauvegarde automatique toutes les 5 minutes et à la fermeture.

Le format est versionné (`PlanetSave.Version`) et repose sur le nom des champs de `PlanetSim`, `Ecosystem` et `Fauna`. Ajouter un champ sauvegardé demande donc d'incrémenter la version et de le déclarer dans la liste des champs manquants de `PlanetWorldSave.LoadPlanet` ; renommer ou supprimer un champ rend les anciennes sauvegardes illisibles.

## Validations

Les scripts de `Assets/Editor/` vérifient la conservation de la masse, les tsunamis, la tectonique et l'aller-retour des sauvegardes. Ils lèvent une exception au premier contrôle en échec. Lancement en ligne de commande, Unity fermé :

```bash
Unity -batchmode -quit -projectPath . -executeMethod FlowValidation.Run -logFile -
```

```bash
Unity -batchmode -quit -projectPath . -executeMethod TectonicValidation.Run -logFile -
```

```bash
Unity -batchmode -quit -projectPath . -executeMethod MapValidation.Run -logFile -
```

`FlowValidation.RunTsunami` lance seulement les contrôles du tsunami.
