using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using IAFoot.Learning;
using Object = UnityEngine.Object;

namespace IAFoot.Editor
{
    /// <summary>Vérifications avec les vrais colliders Unity, sans scène ni asset de test à installer.</summary>
    [InitializeOnLoad]
    public static class RandomSpawnChecks
    {
        const string PlayCheckKey = "IAFoot.RandomSpawnChecks.PlayMode";
        static MatchManager playingMatch;
        static Vector3[] initialPositions;
        static float sampleStart;
        static bool checkingFixedMode;

        static RandomSpawnChecks()
        {
            if (SessionState.GetBool(PlayCheckKey, false))
                EditorApplication.update += CheckPlayMode;
        }

        [MenuItem("IA Foot/Tests/Vérifier les apparitions aléatoires")]
        public static void Run()
        {
            if (EditorApplication.isPlaying)
                throw new InvalidOperationException("Arrêter Play avant de lancer les vérifications.");
            BoundsSeparationAndResets();
            SeededLayouts();
            ObstaclesAndGoals();
            MissingGroundAndImpossibleLayouts();
            FixedModeAndEpisodeEvents();
            TranslatedRotatedArena();
            SoloAndInactivePlayer();
            AroundBallLayouts();
            AroundBallSeedsAndFailures();
            Debug.Log("[IA Foot Tests] PASS : 9 vérifications des apparitions aléatoires (dont placement autour du ballon).");
        }

        // Unity -batchmode -nographics -projectPath ... -executeMethod IAFoot.Editor.RandomSpawnChecks.RunBatch
        public static void RunBatch()
        {
            try { Run(); EditorApplication.Exit(0); }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }

        /// <summary>Charge une vraie scène en Play : aucun Awake n'est appelé manuellement.</summary>
        public static void RunPlayModeBatch()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("Ce test crée une scène temporaire : utiliser un projet de test en batchmode.");
            try
            {
                Run();
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var fixture = new Fixture(2, initialize: false);
                fixture.match.startOnPlay = true;
                var bridge = new GameObject("TrainingBridge").AddComponent<TrainingBridge>();
                var bridgeSettings = new SerializedObject(bridge);
                bridgeSettings.FindProperty("connectOnStart").boolValue = false;
                bridgeSettings.FindProperty("showOverlay").boolValue = false;
                bridgeSettings.ApplyModifiedPropertiesWithoutUndo();
                var agent = fixture.players[0].gameObject.AddComponent<FootAgent>();
                var agentSettings = new SerializedObject(agent);
                agentSettings.FindProperty("seed").intValue = 123;
                agentSettings.ApplyModifiedPropertiesWithoutUndo();
                fixture.players[1].gameObject.AddComponent<SimpleBotInput>();
                EditorSceneManager.SaveScene(fixture.root.scene, "Assets/RandomSpawnPlayCheck.unity");
                SessionState.SetBool(PlayCheckKey, true);
                SessionState.SetFloat(PlayCheckKey + ".deadline", (float)EditorApplication.timeSinceStartup + 90f);
                EditorApplication.update -= CheckPlayMode;
                EditorApplication.update += CheckPlayMode;
                EditorApplication.EnterPlaymode();
            }
            catch (Exception error) { FinishPlayCheck(error); }
        }

        static void CheckPlayMode()
        {
            try
            {
                if (EditorApplication.timeSinceStartup > SessionState.GetFloat(PlayCheckKey + ".deadline", 0f))
                    throw new TimeoutException("Le test Play n'a pas terminé en 90 secondes.");
                if (!EditorApplication.isPlaying || Time.time < 0.1f)
                    return;
                if (!playingMatch)
                {
                    playingMatch = Object.FindFirstObjectByType<MatchManager>();
                    Require(playingMatch && playingMatch.AllPlayers.Count == 2,
                        "Play : les deux joueurs actifs doivent être inscrits avant leur Start.");
                    Require(playingMatch.State == MatchState.Playing, "Play : le match ne démarre pas.");
                    foreach (PlayerController player in playingMatch.AllPlayers)
                        Require(player.Match == playingMatch && !player.IsFrozen, "Play : joueur sans match ou gelé.");
                    Require(Object.FindFirstObjectByType<FootAgent>().EpisodeActive, "Play : l'épisode IA ne démarre pas.");
                    initialPositions = PlayingPositions();
                    sampleStart = Time.time;
                }
                if (Time.time - sampleStart < 2f)
                    return;
                Vector3[] current = PlayingPositions();
                for (int i = 0; i < current.Length; i++)
                    Require(Vector3.ProjectOnPlane(current[i] - initialPositions[i], Vector3.up).magnitude > 0.01f,
                        "Play : le joueur " + i + " reste immobile (aléatoire=" + !checkingFixedMode + ").");
                if (!checkingFixedMode)
                {
                    checkingFixedMode = true;
                    playingMatch.randomSpawns.enabled = false;
                    playingMatch.RestartRound();
                    Require(Object.FindFirstObjectByType<FootAgent>().EpisodeActive, "Play : épisode non redémarré.");
                    initialPositions = PlayingPositions();
                    sampleStart = Time.time;
                    return;
                }
                Debug.Log("[IA Foot Tests] PASS : Play réel, agent et bot inscrits, épisodes actifs, déplacements après départ aléatoire et fixe.");
                FinishPlayCheck(null);
            }
            catch (Exception error) { FinishPlayCheck(error); }
        }

        static Vector3[] PlayingPositions()
        {
            var positions = new Vector3[playingMatch.AllPlayers.Count];
            for (int i = 0; i < positions.Length; i++) positions[i] = playingMatch.AllPlayers[i].Position;
            return positions;
        }

        static void FinishPlayCheck(Exception error)
        {
            SessionState.SetBool(PlayCheckKey, false);
            EditorApplication.update -= CheckPlayMode;
            if (error != null) Debug.LogException(error);
            EditorApplication.Exit(error == null ? 0 : 1);
        }

        static void BoundsSeparationAndResets()
        {
            using (var f = new Fixture(4))
            {
                Vector3[] first = null;
                for (int round = 0; round < 300; round++)
                {
                    foreach (Rigidbody body in f.root.GetComponentsInChildren<Rigidbody>())
                    {
                        body.linearVelocity = Vector3.one;
                        body.angularVelocity = Vector3.one;
                    }
                    f.match.RestartRound();
                    Vector3[] positions = f.Positions();
                    for (int i = 0; i < positions.Length; i++)
                    {
                        Vector3 local = f.root.transform.InverseTransformPoint(positions[i]);
                        Require(Mathf.Abs(local.x) < 1.3f && Mathf.Abs(local.z) < 0.7f, "Hors de la zone");
                        Require(Mathf.Abs(local.y - (i == 0 ? 0.1f : 0.005f)) < 1e-4f, "Hauteur modifiée");
                        for (int j = 0; j < i; j++)
                            Require(Vector3.ProjectOnPlane(positions[i] - positions[j], f.root.transform.up).magnitude
                                >= 0.3999f, "Objets trop proches");
                        if (first != null)
                            Require((positions[i] - first[i]).sqrMagnitude > 1e-8f, "Objet resté à sa première position");
                    }
                    first = first ?? positions;
                    foreach (Rigidbody body in f.root.GetComponentsInChildren<Rigidbody>())
                        Require(body.linearVelocity == Vector3.zero && body.angularVelocity == Vector3.zero,
                            "Vitesse conservée au reset");
                }
            }
        }

        static void SeededLayouts()
        {
            Vector3[][] expected = new Vector3[10][];
            using (var f = new Fixture(2))
                for (int i = 0; i < expected.Length; i++)
                {
                    f.match.RestartRound();
                    expected[i] = f.Positions();
                }
            using (var f = new Fixture(2))
            {
                for (int i = 0; i < expected.Length; i++)
                {
                    UnityEngine.Random.InitState(i * 71); // Ne doit pas influencer le RNG des placements.
                    f.match.RestartRound();
                    EqualPositions(expected[i], f.Positions());
                }
                f.match.randomSpawns.seed = 987;
                f.match.RestartRound();
                Require((expected[0][0] - f.match.ball.Position).sqrMagnitude > 1e-6f, "Seed ignorée");
            }
        }

        static void ObstaclesAndGoals()
        {
            using (var f = new Fixture(2))
            {
                BoxCollider wall = f.Box("Obstacle", new Vector3(-0.6f, 0.5f, 0f), new Vector3(0.3f, 1f, 0.65f));
                BoxCollider goal = f.Box("Goal", new Vector3(0.6f, 0.3f, 0f), new Vector3(0.4f, 0.6f, 0.65f));
                goal.isTrigger = true;
                goal.gameObject.AddComponent<GoalZone>();
                for (int i = 0; i < 100; i++)
                {
                    f.match.RestartRound();
                    foreach (Collider body in f.DynamicColliders())
                        foreach (Collider obstacle in new Collider[] { wall, goal })
                            Require(!Physics.ComputePenetration(body, body.transform.position, body.transform.rotation,
                                obstacle, obstacle.transform.position, obstacle.transform.rotation, out _, out _),
                                "Apparition dans un obstacle ou un but");
                }
            }
        }

        static void MissingGroundAndImpossibleLayouts()
        {
            using (var f = new Fixture(2))
            {
                f.match.randomSpawns.enabled = false;
                f.match.RestartRound();
                Vector3[] anchors = f.Positions();
                f.match.randomSpawns.enabled = true;
                f.match.randomSpawns.areaSize = new Vector2(0.1f, 0.1f);
                Require(!f.match.randomSpawns.TryPlace(f.match), "Zone impossible acceptée");
                EqualPositions(anchors, f.Positions());
                f.match.randomSpawns.areaSize = new Vector2(2.6f, 1.4f);
                f.match.randomSpawns.minimumDistance = 10f;
                Require(!f.match.randomSpawns.TryPlace(f.match), "Espacement impossible accepté");
                EqualPositions(anchors, f.Positions());
                f.match.randomSpawns.minimumDistance = 0.4f;
                f.floor.enabled = false;
                Require(!f.match.randomSpawns.TryPlace(f.match), "Apparition sans sol acceptée");
                EqualPositions(anchors, f.Positions());
                f.floor.enabled = true;
                f.match.randomSpawns.areaSize = new Vector2(float.NaN, 1f);
                Require(!f.match.randomSpawns.TryPlace(f.match), "NaN accepté");
                EqualPositions(anchors, f.Positions());
            }
        }

        static void FixedModeAndEpisodeEvents()
        {
            using (var f = new Fixture(2))
            {
                f.match.randomSpawns.enabled = false;
                f.match.StartMatch();
                Vector3[] anchors = f.Positions();
                Require((anchors[0] - f.match.ballSpawn.position).sqrMagnitude < 1e-8f, "BallSpawn ignoré");
                for (int i = 0; i < f.players.Count; i++)
                    Require((anchors[i + 1] - f.match.teams[i % 2].spawnPoints[i / 2].position).sqrMagnitude < 1e-8f,
                        "Point de joueur ignoré");
                f.match.RestartRound();
                EqualPositions(anchors, f.Positions());
                var events = new List<string>();
                f.match.RoundEnding += () => { EqualPositions(anchors, f.Positions()); events.Add("ending"); };
                f.match.RoundStarted += () =>
                {
                    Require((f.Positions()[0] - anchors[0]).sqrMagnitude > 1e-8f, "RoundStarted avant le placement");
                    events.Add("started");
                };
                f.match.PlayStarted += () => events.Add("playing");
                f.match.randomSpawns.enabled = true;
                f.match.RestartRound();
                Require(string.Join(",", events) == "ending,started,playing", "Ordre des événements modifié");
            }
        }

        static void TranslatedRotatedArena()
        {
            using (var f = new Fixture(2))
            {
                f.root.transform.SetPositionAndRotation(new Vector3(40f, 2f, -30f), Quaternion.Euler(0f, 63f, 0f));
                f.root.transform.localScale = new Vector3(1.5f, 1f, 1.5f);
                for (int i = 0; i < 30; i++)
                {
                    f.match.RestartRound();
                    foreach (Vector3 position in f.Positions())
                    {
                        Vector3 local = f.root.transform.InverseTransformPoint(position);
                        Require(Mathf.Abs(local.x) < 1.3f && Mathf.Abs(local.z) < 0.7f, "Repère d'arène ignoré");
                    }
                }
            }
        }

        static void SoloAndInactivePlayer()
        {
            using (var f = new Fixture(2))
            {
                f.players[1].gameObject.SetActive(false);
                Vector3 inactivePosition = f.players[1].transform.position;
                f.match.SendMessage("Awake");
                Require(f.match.AllPlayers.Count == 1, "Joueur inactif enregistré");
                f.match.RestartRound();
                Require(f.players[1].transform.position == inactivePosition, "Joueur inactif déplacé");
                Require((f.match.ball.Position - f.match.ballSpawn.position).sqrMagnitude > 1e-8f, "Solo non aléatoire");
            }
        }

        static void AroundBallLayouts()
        {
            using (var f = new Fixture(2))
            {
                RoundSpawnRandomizer settings = f.match.randomSpawns;
                settings.mode = RoundSpawnMode.AroundBall;
                // Le tri gauche/droite doit suivre le terrain, pas les axes du monde.
                f.root.transform.SetPositionAndRotation(new Vector3(1000f, 2f, 1000f), Quaternion.Euler(0f, 63f, 0f));
                f.root.transform.localScale = new Vector3(1.2f, 1f, 0.9f);
                var quadrants = new int[4];
                int bothRightOfBall = 0, bothLeftOfBall = 0;
                float smallestBallX = float.PositiveInfinity, largestBallX = float.NegativeInfinity;
                for (int round = 0; round < 1000; round++)
                {
                    Require(settings.TryPlace(f.match), "AroundBall : placement valide introuvable");
                    Vector3[] positions = f.Positions();
                    Vector3 ball = f.root.transform.InverseTransformPoint(positions[0]);
                    Vector3 blue = f.root.transform.InverseTransformPoint(positions[1]);
                    Vector3 red = f.root.transform.InverseTransformPoint(positions[2]);
                    Require(blue.x < red.x, "AroundBall : bleu à droite du rouge");
                    quadrants[(ball.x >= 0 ? 1 : 0) + (ball.z >= 0 ? 2 : 0)]++;
                    smallestBallX = Mathf.Min(smallestBallX, ball.x);
                    largestBallX = Mathf.Max(largestBallX, ball.x);
                    if (blue.x > ball.x) bothRightOfBall++;
                    if (red.x < ball.x) bothLeftOfBall++;
                    for (int i = 1; i < positions.Length; i++)
                    {
                        Vector2 range = settings.DistanceRange(f.match, f.players[i - 1].Team, positions[0]);
                        float distance = Vector3.ProjectOnPlane(positions[i] - positions[0], f.root.transform.up).magnitude;
                        Require(distance >= range.x - 0.001f && distance <= range.y + 0.001f,
                            "AroundBall : distance hors de son intervalle");
                        Vector3 local = f.root.transform.InverseTransformPoint(positions[i]);
                        Require(Mathf.Abs(local.x) < 1.3f && Mathf.Abs(local.z) < 0.7f, "AroundBall : hors terrain");
                    }
                    for (int i = 0; i < positions.Length; i++)
                        for (int j = 0; j < i; j++)
                            Require(Vector3.ProjectOnPlane(positions[i] - positions[j], f.root.transform.up).magnitude >= 0.399f,
                                "AroundBall : séparation non respectée");
                }
                Require(Array.TrueForAll(quadrants, count => count > 100), "AroundBall : ballon limité à une partie du terrain");
                Require(smallestBallX < -1f && largestBallX > 1f, "AroundBall : ballon limité au centre");
                Require(bothLeftOfBall > 10 && bothRightOfBall > 10, "AroundBall : ballon toujours entre les joueurs");

                Vector3 left = f.root.transform.TransformPoint(new Vector3(-1f, 0f, 0f));
                Vector3 right = f.root.transform.TransformPoint(new Vector3(1f, 0f, 0f));
                Require(settings.DistanceRange(f.match, TeamId.Blue, left).y < settings.DistanceRange(f.match, TeamId.Blue, right).y,
                    "AroundBall : proximité du but bleu ignorée");
                Require(settings.DistanceRange(f.match, TeamId.Red, right).y < settings.DistanceRange(f.match, TeamId.Red, left).y,
                    "AroundBall : proximité du but rouge ignorée");
                // Une borne angulaire étroite doit aussi être respectée.
                settings.angleDegrees = Vector2.zero;
                for (int i = 0; i < 30; i++)
                {
                    Require(settings.TryPlace(f.match), "AroundBall : angle nul impossible");
                    Vector3 ball = f.root.transform.InverseTransformPoint(f.match.ball.Position);
                    foreach (PlayerController player in f.players)
                    {
                        Vector3 local = f.root.transform.InverseTransformPoint(player.Position);
                        Require(Mathf.Abs(local.z - ball.z) < 0.001f, "AroundBall : angle ignoré");
                        Require((local.x - ball.x) * player.Team.AttackSign() < 0f, "AroundBall : angle vers le mauvais camp");
                    }
                }
            }
        }

        static void AroundBallSeedsAndFailures()
        {
            Vector3[][] expected = new Vector3[10][];
            using (var f = new Fixture(2))
            {
                f.match.randomSpawns.mode = RoundSpawnMode.AroundBall;
                for (int i = 0; i < expected.Length; i++)
                {
                    Require(f.match.randomSpawns.TryPlace(f.match), "AroundBall : premier tirage impossible");
                    expected[i] = f.Positions();
                }
            }
            using (var f = new Fixture(2))
            {
                var settings = f.match.randomSpawns;
                settings.mode = RoundSpawnMode.AroundBall;
                for (int i = 0; i < expected.Length; i++)
                {
                    UnityEngine.Random.InitState(91 * i);
                    Require(settings.TryPlace(f.match), "AroundBall : second tirage impossible");
                    EqualPositions(expected[i], f.Positions());
                }
                Vector3[] before = f.Positions();
                settings.nearGoalDistance = new Vector2(float.NaN, 0.5f);
                Require(!settings.TryPlace(f.match), "AroundBall : NaN accepté");
                EqualPositions(before, f.Positions());
                settings.nearGoalDistance = settings.farGoalDistance = new Vector2(0.01f, 0.02f);
                Require(!settings.TryPlace(f.match), "AroundBall : chevauchement accepté");
                EqualPositions(before, f.Positions());
                settings.nearGoalDistance = new Vector2(0.4f, 0.55f);
                settings.farGoalDistance = new Vector2(0.55f, 0.8f);
                BoxCollider wall = f.Box("Obstacle", new Vector3(0f, 0.5f, 0f), new Vector3(0.2f, 1f, 0.3f));
                BoxCollider goal = f.Box("Goal", new Vector3(0.9f, 0.3f, 0f), new Vector3(0.3f, 0.6f, 0.3f));
                goal.isTrigger = true;
                goal.gameObject.AddComponent<GoalZone>();
                for (int i = 0; i < 100; i++)
                {
                    Require(settings.TryPlace(f.match), "AroundBall : obstacle non contourné");
                    foreach (Collider body in f.DynamicColliders())
                        foreach (Collider obstacle in new Collider[] { wall, goal })
                            Require(!Physics.ComputePenetration(body, body.transform.position, body.transform.rotation,
                                obstacle, obstacle.transform.position, obstacle.transform.rotation, out _, out _),
                                "AroundBall : pénétration d'un obstacle ou d'un but");
                }
                f.floor.enabled = false;
                before = f.Positions();
                Require(!settings.TryPlace(f.match), "AroundBall : absence de sol ignorée");
                EqualPositions(before, f.Positions());
                f.floor.enabled = true;
                f.players[1].gameObject.SetActive(false);
                Require(settings.TryPlace(f.match), "AroundBall : solo impossible");
                Require(f.players[1].Position == before[2], "AroundBall : joueur inactif déplacé");
            }
        }

        static void EqualPositions(Vector3[] expected, Vector3[] actual)
        {
            Require(expected.Length == actual.Length, "Nombre d'objets différent");
            for (int i = 0; i < actual.Length; i++)
                Require((expected[i] - actual[i]).sqrMagnitude < 1e-8f, "Positions différentes");
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        sealed class Fixture : IDisposable
        {
            public readonly GameObject root = new GameObject("RandomSpawnChecks");
            public readonly MatchManager match;
            public readonly BoxCollider floor;
            public readonly List<PlayerController> players = new List<PlayerController>();

            public Fixture(int playerCount, bool initialize = true)
            {
                // Éloigne les tests de la scène ouverte, sans la fermer ni la modifier.
                root.transform.position = new Vector3(1000f, 0f, 1000f);
                match = root.AddComponent<MatchManager>();
                match.startOnPlay = false;
                match.kickoffFreezeDuration = 0f;
                match.randomSpawns.seed = 123;
                floor = Box("Ground", new Vector3(0f, -0.05f, 0f), new Vector3(4f, 0.1f, 3f));
                match.ballSpawn = Child("BallSpawn", new Vector3(0f, 0.1f, 0f));
                GameObject ball = Child("Ball", new Vector3(0f, 0.1f, 0f)).gameObject;
                ball.AddComponent<SphereCollider>().radius = 0.05f;
                match.ball = ball.AddComponent<SoccerBall>();
                if (initialize) match.ball.SendMessage("Awake");
                match.teams.Add(new TeamSetup { id = TeamId.Blue });
                match.teams.Add(new TeamSetup { id = TeamId.Red });
                for (int i = 0; i < playerCount; i++)
                {
                    var spawn = Child("Spawn" + i, new Vector3(i % 2 == 0 ? -0.8f : 0.8f, 0.005f, i / 2 * 0.4f));
                    GameObject obj = Child("Player" + i, spawn.localPosition).gameObject;
                    CapsuleCollider capsule = obj.AddComponent<CapsuleCollider>();
                    capsule.center = new Vector3(0f, 0.15f, 0f);
                    capsule.height = 0.3f;
                    capsule.radius = 0.08f;
                    PlayerController player = obj.AddComponent<PlayerController>();
                    if (initialize) player.SendMessage("Awake");
                    players.Add(player);
                    match.teams[i % 2].players.Add(player);
                    match.teams[i % 2].spawnPoints.Add(spawn);
                }
                if (initialize) match.SendMessage("Awake");
            }

            Transform Child(string name, Vector3 localPosition)
            {
                var obj = new GameObject(name);
                obj.transform.SetParent(root.transform, false);
                obj.transform.localPosition = localPosition;
                return obj.transform;
            }

            public BoxCollider Box(string name, Vector3 position, Vector3 size)
            {
                BoxCollider box = Child(name, position).gameObject.AddComponent<BoxCollider>();
                box.size = size;
                return box;
            }

            public IEnumerable<Collider> DynamicColliders()
            {
                yield return match.ball.GetComponent<SphereCollider>();
                foreach (PlayerController player in players)
                    yield return player.GetComponent<CapsuleCollider>();
            }

            public Vector3[] Positions()
            {
                var result = new Vector3[players.Count + 1];
                result[0] = match.ball.Position;
                for (int i = 0; i < players.Count; i++) result[i + 1] = players[i].Position;
                return result;
            }

            public void Dispose() => Object.DestroyImmediate(root);
        }
    }
}
