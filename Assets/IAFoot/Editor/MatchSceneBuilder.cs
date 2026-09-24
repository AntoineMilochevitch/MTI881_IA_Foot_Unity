using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace IAFoot.EditorTools
{
    /// <summary>
    /// Monte une arène jouable à partir des prefabs du pack Football Essentials 3D :
    /// MatchManager, buts (trigger + collider), ballon, 1 joueur humain vs 1 bot, points d'apparition, UI et règles.
    /// </summary>
    public static class MatchSceneBuilder
    {
        const string PackPrefabs = "Assets/Lightning Poly/Football Essentials 3D/Prefabs/";
        const string BallPrefab = PackPrefabs + "Ball.prefab";
        const string CharacterPrefab = PackPrefabs + "Character.prefab";
        const string GoalPrefab = PackPrefabs + "Goal.prefab";
        const string GroundPrefab = PackPrefabs + "Ground.prefab";
        const string SettingsFolder = "Assets/IAFoot/Settings";
        const string ScenesFolder = "Assets/IAFoot/Scenes";

        [MenuItem("IA Foot/Configurer le match dans la scène ouverte")]
        static void SetupOpenScene()
        {
            if (Object.FindFirstObjectByType<MatchManager>())
            {
                EditorUtility.DisplayDialog("IA Foot", "Cette scène contient déjà un MatchManager.", "OK");
                return;
            }

            BuildArena(createWalls: false);
        }

        [MenuItem("IA Foot/Créer une nouvelle scène de match")]
        static void CreateNewScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            Camera cam = Camera.main;
            if (cam)
                cam.transform.SetPositionAndRotation(new Vector3(0f, 2.4f, -3.2f), Quaternion.Euler(38f, 0f, 0f));

            BuildArena(createWalls: true);

            EnsureFolder(ScenesFolder);
            string path = AssetDatabase.GenerateUniqueAssetPath(ScenesFolder + "/Match.unity");
            EditorSceneManager.SaveScene(scene, path);
        }

        [MenuItem("IA Foot/Ajouter des coins en biseau (anti-blocage)")]
        static void AddCornerDeflectorsMenu()
        {
            MatchManager match = Object.FindFirstObjectByType<MatchManager>();
            GameObject walls = GameObject.Find("Walls");
            if (!walls)
            {
                EditorUtility.DisplayDialog("IA Foot", "Aucun objet \"Walls\" trouvé dans la scène.", "OK");
                return;
            }

            Vector3 center = match && match.ballSpawn ? match.ballSpawn.position : walls.transform.position;
            Transform parent = match ? match.transform : null;
            if (CreateCornerDeflectors(walls, center, parent))
                EditorSceneManager.MarkSceneDirty(walls.scene);
        }

        /// <summary>
        /// Ajoute 4 murs invisibles à 45° dans les coins du terrain, déduits des murs verticaux existants,
        /// pour que le ballon ne puisse plus s'y coincer.
        /// </summary>
        static bool CreateCornerDeflectors(GameObject walls, Vector3 fieldCenter, Transform parent, float cornerSize = 0.4f)
        {
            float minX = float.NegativeInfinity, maxX = float.PositiveInfinity;
            float minZ = float.NegativeInfinity, maxZ = float.PositiveInfinity;
            float bottom = float.PositiveInfinity, top = float.NegativeInfinity;

            foreach (BoxCollider wall in walls.GetComponentsInChildren<BoxCollider>())
            {
                if (wall.isTrigger || wall.transform.parent && wall.transform.parent.name == "CornerDeflectors")
                    continue;

                Bounds b = wall.bounds;
                if (b.size.y < Mathf.Min(b.size.x, b.size.z) * 1.5f)
                    continue; // plafond ou sol

                bottom = Mathf.Min(bottom, b.min.y);
                top = Mathf.Max(top, b.max.y);
                if (b.size.x >= b.size.z) // mur le long de X : bord nord / sud
                {
                    if (b.center.z > fieldCenter.z) maxZ = Mathf.Min(maxZ, b.min.z);
                    else minZ = Mathf.Max(minZ, b.max.z);
                }
                else // mur le long de Z : bord est / ouest
                {
                    if (b.center.x > fieldCenter.x) maxX = Mathf.Min(maxX, b.min.x);
                    else minX = Mathf.Max(minX, b.max.x);
                }
            }

            if (float.IsInfinity(minX) || float.IsInfinity(maxX) || float.IsInfinity(minZ) || float.IsInfinity(maxZ))
            {
                Debug.LogWarning("[IA Foot] Impossible de déduire les 4 bords du terrain à partir des murs.", walls);
                return false;
            }

            GameObject previous = GameObject.Find("CornerDeflectors");
            if (previous)
                Undo.DestroyObjectImmediate(previous);

            Transform root = CreateChild(parent, "CornerDeflectors", Vector3.zero, Quaternion.identity);
            float height = top - bottom;
            float y = (top + bottom) * 0.5f;
            int index = 0;
            foreach (float x in new[] { minX, maxX })
            {
                foreach (float z in new[] { minZ, maxZ })
                {
                    // Pan coupé : face intérieure à cornerSize/√2 du coin, le long de la diagonale.
                    Vector3 inward = new Vector3(x < fieldCenter.x ? 1f : -1f, 0f, z < fieldCenter.z ? 1f : -1f).normalized;
                    Vector3 corner = new Vector3(x, y, z);
                    Vector3 position = corner + inward * (cornerSize / Mathf.Sqrt(2f) - cornerSize * 0.5f);

                    Transform deflector = CreateChild(root, $"Corner_{index++}", position, Quaternion.LookRotation(inward));
                    deflector.gameObject.AddComponent<BoxCollider>().size =
                        new Vector3(cornerSize * 2f * Mathf.Sqrt(2f), height, cornerSize);
                }
            }

            Debug.Log("[IA Foot] Coins en biseau ajoutés (objet CornerDeflectors).", root);
            return true;
        }

        static void BuildArena(bool createWalls)
        {
            Undo.SetCurrentGroupName("IA Foot : configurer le match");
            int undoGroup = Undo.GetCurrentGroup();

            var arena = new GameObject("Arena");
            Undo.RegisterCreatedObjectUndo(arena, "Create Arena");
            var match = arena.AddComponent<MatchManager>();

            // --- Terrain
            GameObject ground = FindInstances(GroundPrefab).FirstOrDefault()
                                ?? InstantiatePrefab(GroundPrefab, new Vector3(0f, -0.1358f, 0f), Quaternion.Euler(0f, 90f, 0f));
            Parent(ground, arena);
            Bounds groundBounds = GetBounds(ground);
            float groundTop = groundBounds.max.y;

            GameObject walls = GameObject.Find("Walls");
            if (walls)
                Parent(walls, arena);
            else if (createWalls)
                CreateWalls(arena, groundBounds);

            // --- Buts : celui du côté -X est défendu par les Bleus
            List<GameObject> goalObjects = FindInstances(GoalPrefab).ToList();
            if (goalObjects.Count == 0)
            {
                goalObjects.Add(InstantiatePrefab(GoalPrefab, new Vector3(1.738f, 0.387f, -0.03f), Quaternion.Euler(0f, -90f, 0f)));
                goalObjects.Add(InstantiatePrefab(GoalPrefab, new Vector3(-1.73f, 0.387f, -0.03f), Quaternion.Euler(0f, 90f, 0f)));
            }
            else if (goalObjects.Count == 1)
            {
                Transform existing = goalObjects[0].transform;
                Vector3 p = existing.position;
                goalObjects.Add(InstantiatePrefab(GoalPrefab, new Vector3(-p.x, p.y, p.z),
                    Quaternion.Euler(0f, 180f, 0f) * existing.rotation));
            }

            goalObjects = goalObjects.OrderBy(g => g.transform.position.x).Take(2).ToList();
            foreach (GameObject goal in goalObjects)
                Parent(goal, arena);

            var goals = new List<GoalZone>
            {
                ConfigureGoal(goalObjects[0], TeamId.Blue),
                ConfigureGoal(goalObjects[1], TeamId.Red),
            };

            Vector3 blueGoal = goalObjects[0].transform.position;
            Vector3 redGoal = goalObjects[1].transform.position;
            Vector3 center = (blueGoal + redGoal) * 0.5f;
            float halfLength = Mathf.Abs(redGoal.x - blueGoal.x) * 0.5f;

            // --- Ballon
            GameObject ballObject = FindInstances(BallPrefab).FirstOrDefault()
                                    ?? InstantiatePrefab(BallPrefab, Vector3.zero, Quaternion.identity);
            Parent(ballObject, arena);
            SoccerBall ball = GetOrAdd<SoccerBall>(ballObject);

            Transform ballSpawn = CreateChild(arena.transform, "BallSpawn",
                new Vector3(center.x, groundTop + ball.Radius + 0.03f, center.z), Quaternion.identity);
            ballObject.transform.position = ballSpawn.position;

            GameObject fieldWalls = GameObject.Find("Walls");
            if (fieldWalls)
                CreateCornerDeflectors(fieldWalls, ballSpawn.position, arena.transform);

            // --- Points d'apparition (index 0 = attaquant pour le 1v1, index 1 = défenseur pour le 2v2)
            Transform spawnRoot = CreateChild(arena.transform, "SpawnPoints", Vector3.zero, Quaternion.identity);
            var teams = new List<TeamSetup>();
            foreach (TeamId id in new[] { TeamId.Blue, TeamId.Red })
            {
                float side = -id.AttackSign();
                Quaternion facing = Quaternion.LookRotation(Vector3.right * id.AttackSign());
                var setup = new TeamSetup
                {
                    id = id,
                    displayName = id == TeamId.Blue ? "Bleus" : "Rouges",
                    color = id.DefaultColor(),
                };
                setup.spawnPoints.Add(CreateChild(spawnRoot, $"{id}_0",
                    new Vector3(center.x + side * halfLength * 0.5f, groundTop + 0.005f, center.z), facing));
                setup.spawnPoints.Add(CreateChild(spawnRoot, $"{id}_1",
                    new Vector3(center.x + side * halfLength * 0.8f, groundTop + 0.005f, center.z), facing));
                teams.Add(setup);
            }

            // --- Joueurs : les personnages existants sont répartis alternativement entre les équipes
            List<GameObject> characters = FindInstances(CharacterPrefab).OrderBy(c => c.transform.position.x).ToList();
            while (characters.Count < 2)
                characters.Add(InstantiatePrefab(CharacterPrefab, Vector3.zero, Quaternion.identity));

            PlayerController human = null;
            for (int i = 0; i < characters.Count; i++)
            {
                GameObject character = characters[i];
                TeamSetup team = teams[i % 2];
                int indexInTeam = i / 2;
                Parent(character, arena);
                Undo.RecordObject(character, "Rename player");
                character.name = $"{team.id}_Player_{indexInTeam}";

                RemoveVendorPlayer(character);
                PlayerController controller = GetOrAdd<PlayerController>(character);
                GetOrAdd<PlayerVisuals>(character);
                if (i == 0)
                {
                    GetOrAdd<HumanPlayerInput>(character);
                    human = controller;
                }
                else
                {
                    GetOrAdd<SimpleBotInput>(character);
                }

                team.players.Add(controller);
                if (indexInTeam < team.spawnPoints.Count)
                {
                    Transform spawn = team.spawnPoints[indexInTeam];
                    character.transform.SetPositionAndRotation(spawn.position, spawn.rotation);
                }
            }

            FixVendorCamera(human ? human.transform : null);

            // --- MatchManager
            match.ball = ball;
            match.ballSpawn = ballSpawn;
            match.goals = goals;
            match.teams = teams;
            match.rules = CreateDefaultRules(arena.transform, groundBounds);

            CreateUI(arena, match);

            EditorUtility.SetDirty(match);
            EditorSceneManager.MarkSceneDirty(arena.scene);
            Selection.activeGameObject = arena;
            Undo.CollapseUndoOperations(undoGroup);

            Debug.Log("[IA Foot] Arène configurée : Bleus (humain) vs Rouges (bot). " +
                      "Vérifiez la taille des zones de but (gizmos colorés) puis lancez Play.", arena);
        }

        // ------------------------------------------------------------------ Buts

        static GoalZone ConfigureGoal(GameObject goal, TeamId defendingTeam)
        {
            Undo.RecordObject(goal, "Rename goal");
            goal.name = $"Goal_{defendingTeam}";

            MeshFilter meshFilter = goal.GetComponentInChildren<MeshFilter>();
            Mesh mesh = meshFilter ? meshFilter.sharedMesh : null;

            // Collider des poteaux / du filet : le ballon rebondit dessus et reste dans la cage.
            if (!goal.GetComponent<Collider>() && mesh)
            {
                var meshCollider = Undo.AddComponent<MeshCollider>(meshFilter.gameObject);
                meshCollider.sharedMesh = mesh;
            }

            var trigger = new GameObject("GoalTrigger");
            Undo.RegisterCreatedObjectUndo(trigger, "Create goal trigger");
            trigger.transform.SetParent(meshFilter ? meshFilter.transform : goal.transform, false);

            var box = trigger.AddComponent<BoxCollider>();
            box.isTrigger = true;
            if (mesh)
            {
                // Volume intérieur de la cage (légèrement réduit pour ne pas dépasser des poteaux).
                box.center = mesh.bounds.center;
                box.size = mesh.bounds.size * 0.9f;
            }

            var zone = trigger.AddComponent<GoalZone>();
            zone.Configure(defendingTeam, goal.transform);
            return zone;
        }

        // ------------------------------------------------------------------ Règles

        static List<MatchRule> CreateDefaultRules(Transform arena, Bounds groundBounds)
        {
            EnsureFolder(SettingsFolder);

            var scoreLimit = LoadOrCreate<ScoreLimitRule>("ScoreLimitRule");
            var timeLimit = LoadOrCreate<TimeLimitRule>("TimeLimitRule");
            var outOfBounds = LoadOrCreate<BallOutOfBoundsRule>("BallOutOfBoundsRule");

            outOfBounds.center = arena.InverseTransformPoint(groundBounds.center);
            outOfBounds.halfExtents = groundBounds.extents + new Vector3(0.5f, 2f, 0.5f);
            EditorUtility.SetDirty(outOfBounds);
            AssetDatabase.SaveAssets();

            return new List<MatchRule> { scoreLimit, timeLimit, outOfBounds };
        }

        static T LoadOrCreate<T>(string name) where T : ScriptableObject
        {
            string path = $"{SettingsFolder}/{name}.asset";
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset)
                return asset;

            asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        // ------------------------------------------------------------------ UI

        static void CreateUI(GameObject arena, MatchManager match)
        {
            var canvasObject = new GameObject("MatchUI", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Undo.RegisterCreatedObjectUndo(canvasObject, "Create UI");
            canvasObject.transform.SetParent(arena.transform, false);

            canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            Text score = CreateText(canvasObject.transform, "Score", new Vector2(0.5f, 1f), new Vector2(0f, -60f), 64, FontStyle.Bold);
            Text status = CreateText(canvasObject.transform, "Status", new Vector2(0.5f, 1f), new Vector2(0f, -130f), 36, FontStyle.Normal);
            Text message = CreateText(canvasObject.transform, "Message", new Vector2(0.5f, 0.5f), new Vector2(0f, 80f), 120, FontStyle.Bold);

            canvasObject.AddComponent<ScoreboardUI>().Configure(match, score, status, message);
        }

        static Text CreateText(Transform parent, string name, Vector2 anchor, Vector2 position, int fontSize, FontStyle style)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text), typeof(Outline));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(1400f, fontSize * 2.5f);

            var text = go.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = true;
            text.raycastTarget = false;
            text.color = Color.white;

            var outline = go.GetComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.8f);
            outline.effectDistance = new Vector2(3f, -3f);
            return text;
        }

        // ------------------------------------------------------------------ Divers

        static void CreateWalls(GameObject arena, Bounds field)
        {
            Transform root = CreateChild(arena.transform, "Walls", Vector3.zero, Quaternion.identity);
            const float thickness = 0.3f;
            const float height = 2f;
            Vector3 c = field.center;
            Vector3 e = field.extents;
            float y = field.max.y + height * 0.5f;

            CreateWall(root, "Wall_North", new Vector3(c.x, y, c.z + e.z + thickness * 0.5f), new Vector3(e.x * 2f + thickness * 2f, height, thickness));
            CreateWall(root, "Wall_South", new Vector3(c.x, y, c.z - e.z - thickness * 0.5f), new Vector3(e.x * 2f + thickness * 2f, height, thickness));
            CreateWall(root, "Wall_East", new Vector3(c.x + e.x + thickness * 0.5f, y, c.z), new Vector3(thickness, height, e.z * 2f));
            CreateWall(root, "Wall_West", new Vector3(c.x - e.x - thickness * 0.5f, y, c.z), new Vector3(thickness, height, e.z * 2f));
            CreateWall(root, "Ceiling", new Vector3(c.x, field.max.y + height, c.z), new Vector3(e.x * 2f, thickness, e.z * 2f));
        }

        static void CreateWall(Transform parent, string name, Vector3 position, Vector3 size)
        {
            Transform wall = CreateChild(parent, name, position, Quaternion.identity);
            wall.gameObject.AddComponent<BoxCollider>().size = size;
        }

        static void RemoveVendorPlayer(GameObject character)
        {
            // Le script de démo du pack lit l'ancien Input Manager (désactivé dans ce projet) : on le retire.
            var vendor = character.GetComponent<LightningPoly.FootballEssentials3D.Player>();
            if (vendor)
                Undo.DestroyObjectImmediate(vendor);
        }

        static void FixVendorCamera(Transform target)
        {
            var follow = Object.FindFirstObjectByType<LightningPoly.FootballEssentials3D.CameraMovement>();
            if (!follow)
                return;

            Undo.RecordObject(follow, "Camera target");
            if (target)
                follow.player = target;
            else
                follow.enabled = false;
            PrefabUtility.RecordPrefabInstancePropertyModifications(follow);
        }

        static Bounds GetBounds(GameObject go)
        {
            Collider collider = go.GetComponentInChildren<Collider>();
            if (collider && collider.bounds.size != Vector3.zero)
                return collider.bounds;

            Renderer renderer = go.GetComponentInChildren<Renderer>();
            return renderer ? renderer.bounds : new Bounds(go.transform.position, new Vector3(4.6f, 0.1f, 3f));
        }

        static IEnumerable<GameObject> FindInstances(string prefabPath)
        {
            foreach (Transform t in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
            {
                GameObject go = t.gameObject;
                if (PrefabUtility.IsAnyPrefabInstanceRoot(go)
                    && PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go) == prefabPath)
                    yield return go;
            }
        }

        static GameObject InstantiatePrefab(string path, Vector3 position, Quaternion rotation)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (!prefab)
                throw new System.IO.FileNotFoundException($"[IA Foot] Prefab introuvable : {path}");

            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.transform.SetPositionAndRotation(position, rotation);
            Undo.RegisterCreatedObjectUndo(go, "Instantiate " + prefab.name);
            return go;
        }

        static Transform CreateChild(Transform parent, string name, Vector3 position, Quaternion rotation)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(position, rotation);
            return go.transform;
        }

        static void Parent(GameObject child, GameObject parent)
        {
            if (child.transform.parent != parent.transform)
                Undo.SetTransformParent(child.transform, parent.transform, "Parent to arena");
        }

        static T GetOrAdd<T>(GameObject go) where T : Component
        {
            return go.TryGetComponent(out T component) ? component : Undo.AddComponent<T>(go);
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;

            string parent = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent))
                EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
        }
    }
}
