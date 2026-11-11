using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using IAFoot.Learning;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IAFoot.Editor
{
    /// <summary>
    /// Vérifie en Play réel la duplication des arènes : N matchs indépendants et sans chevauchement,
    /// un seul canal d'enregistrement qui reçoit les transitions de toutes les arènes, et un modèle
    /// installé d'un coup sur les agents de toutes les arènes.
    ///
    /// Sans Python : le modèle est installé par réflexion sur le canal partagé.
    /// Avec IAFOOT_CHECK_PYTHON=1 et `python train.py --resend-every 2` lancé : vrais batchs et vrais modèles.
    /// Unity.exe -batchmode -nographics -projectPath . -executeMethod IAFoot.Editor.ArenaReplicatorChecks.RunPlayModeBatch
    /// </summary>
    [InitializeOnLoad]
    public static class ArenaReplicatorChecks
    {
        const string Key = "IAFoot.ArenaReplicatorChecks";
        const string TempFolder = "Assets/IAFootArenaCheck";
        static readonly int ArenaCount = int.TryParse(Environment.GetEnvironmentVariable("IAFOOT_CHECK_ARENAS"), out int n) && n > 0 ? n : 4;
        const int PlayersPerArena = 2;
        const int SpawnSeed = 5;

        static readonly FieldInfo AgentChannel = typeof(FootAgent).GetField("channel", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo ChannelBuffer = typeof(BehaviorChannel).GetField("Buffer", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo ChannelBatches = typeof(BehaviorChannel).GetField("BatchesSent", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo BufferAgentIds = typeof(ExperienceBuffer).GetField("agentIds", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly PropertyInfo ChannelPolicy = typeof(BehaviorChannel).GetProperty("Policy");

        static bool structureChecked;
        static int maxArenasInOneBatch;
        static Dictionary<FootAgent, Vector3> startPositions;
        static float phaseStart;

        static ArenaReplicatorChecks()
        {
            if (SessionState.GetBool(Key, false))
                EditorApplication.update += CheckPlayMode;
        }

        static bool WithPython => Environment.GetEnvironmentVariable("IAFOOT_CHECK_PYTHON") == "1";
        // Exerce la scène comme source, avec un prefab volontairement resté en self-play.
        static bool WithSnapshot => Environment.GetEnvironmentVariable("IAFOOT_CHECK_SNAPSHOT") == "1";

        public static void RunPlayModeBatch()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("Ce test crée des assets temporaires : le lancer en batchmode.");
            try
            {
                CheckGridMath();
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                if (!AssetDatabase.IsValidFolder(TempFolder))
                    AssetDatabase.CreateFolder("Assets", "IAFootArenaCheck");

                GameObject arena = BuildArena();
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(arena, TempFolder + "/CheckArena.prefab");
                MatchManager sceneArena = null;
                if (WithSnapshot)
                {
                    sceneArena = arena.GetComponent<MatchManager>();
                    sceneArena.randomSpawns.mode = RoundSpawnMode.AroundBall;
                    FootAgent opponent = sceneArena.teams[1].players[0].GetComponent<FootAgent>();
                    var opponentSettings = new SerializedObject(opponent);
                    opponentSettings.FindProperty("behaviorName").stringValue = "FootSnapshot";
                    opponentSettings.FindProperty("recordExperience").boolValue = false;
                    opponentSettings.ApplyModifiedPropertiesWithoutUndo();
                }
                else
                    Object.DestroyImmediate(arena);

                var replicator = new GameObject("ArenaReplicator").AddComponent<ArenaReplicator>();
                replicator.Configure(prefab, sceneArena, ArenaCount);

                var bridge = new GameObject("TrainingBridge").AddComponent<TrainingBridge>();
                var settings = new SerializedObject(bridge);
                settings.FindProperty("connectOnStart").boolValue = WithPython;
                if (int.TryParse(Environment.GetEnvironmentVariable("IAFOOT_CHECK_PORT"), out int port))
                    settings.FindProperty("port").intValue = port;
                // Plusieurs décisions par batch, pour que le batch en cours contienne toujours des transitions à inspecter.
                settings.FindProperty("batchSize").intValue = WithPython ? Mathf.Max(64, ArenaCount * PlayersPerArena * 4) : 100000;
                settings.FindProperty("showOverlay").boolValue = false;
                settings.FindProperty("verboseLogs").boolValue = WithPython;
                settings.ApplyModifiedPropertiesWithoutUndo();

                EditorSceneManager.SaveScene(replicator.gameObject.scene, TempFolder + "/CheckScene.unity");
                SessionState.SetBool(Key, true);
                SessionState.SetFloat(Key + ".deadline", (float)EditorApplication.timeSinceStartup + 120f);
                EditorApplication.update -= CheckPlayMode;
                EditorApplication.update += CheckPlayMode;
                EditorApplication.EnterPlaymode();
            }
            catch (Exception error) { Finish(error); }
        }

        static void CheckGridMath()
        {
            Require(ArenaReplicator.AutoColumns(1) == 1 && ArenaReplicator.AutoColumns(4) == 2
                    && ArenaReplicator.AutoColumns(5) == 3 && ArenaReplicator.AutoColumns(16) == 4, "AutoColumns");
            var step = new Vector2(6f, 5f);
            Require(ArenaReplicator.GridOffset(0, 3, step) == Vector3.zero, "GridOffset 0");
            Require(ArenaReplicator.GridOffset(2, 3, step) == new Vector3(12f, 0f, 0f), "GridOffset 2");
            Require(ArenaReplicator.GridOffset(4, 3, step) == new Vector3(6f, 0f, 5f), "GridOffset 4");
        }

        /// <summary>Arène minimale : sol, ballon, deux joueurs pilotés par un FootAgent.</summary>
        static GameObject BuildArena()
        {
            var root = new GameObject("CheckArena");
            var match = root.AddComponent<MatchManager>();
            match.kickoffFreezeDuration = 0f;
            match.goalCelebrationDuration = 0f;
            match.randomSpawns.seed = SpawnSeed;

            var ground = Child(root, "Ground", new Vector3(0f, -0.05f, 0f)).AddComponent<BoxCollider>();
            ground.size = new Vector3(4f, 0.1f, 3f);
            match.ballSpawn = Child(root, "BallSpawn", new Vector3(0f, 0.1f, 0f)).transform;
            GameObject ball = Child(root, "Ball", new Vector3(0f, 0.1f, 0f));
            match.ball = ball.AddComponent<SoccerBall>();
            ball.GetComponent<SphereCollider>().radius = 0.05f;

            match.teams.Add(new TeamSetup { id = TeamId.Blue });
            match.teams.Add(new TeamSetup { id = TeamId.Red });
            for (int i = 0; i < PlayersPerArena; i++)
            {
                Vector3 position = new Vector3(i % 2 == 0 ? -0.8f : 0.8f, 0.005f, 0f);
                Transform spawn = Child(root, "Spawn" + i, position).transform;
                GameObject obj = Child(root, "Player" + i, position);
                CapsuleCollider capsule = obj.AddComponent<CapsuleCollider>();
                capsule.center = new Vector3(0f, 0.15f, 0f);
                capsule.height = 0.3f;
                capsule.radius = 0.08f;
                PlayerController player = obj.AddComponent<PlayerController>();
                obj.AddComponent<FootAgent>();
                match.teams[i % 2].players.Add(player);
                match.teams[i % 2].spawnPoints.Add(spawn);
            }
            return root;
        }

        static GameObject Child(GameObject parent, string name, Vector3 localPosition)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(parent.transform, false);
            obj.transform.localPosition = localPosition;
            return obj;
        }

        static void CheckPlayMode()
        {
            try
            {
                if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + ".deadline", 0f))
                    throw new TimeoutException("Le test Play n'a pas terminé en 120 secondes.");
                if (!EditorApplication.isPlaying || Time.time < 0.2f)
                    return;

                FootAgent[] agents = Object.FindObjectsByType<FootAgent>(FindObjectsSortMode.InstanceID);
                var channel = (BehaviorChannel)AgentChannel.GetValue(agents.First(a => a.TeamName == "Blue"));
                BehaviorChannel[] channels = agents.Select(a => (BehaviorChannel)AgentChannel.GetValue(a)).Distinct().ToArray();
                if (!structureChecked)
                {
                    CheckStructure(agents, channel);
                    structureChecked = true;
                    startPositions = agents.ToDictionary(a => a, a => a.transform.position);
                    phaseStart = Time.time;
                    if (!WithPython)
                        foreach (BehaviorChannel item in channels)
                            ChannelPolicy.SetValue(item, VersionedPolicy(item, 42));
                }

                // Le batch en cours doit mélanger les transitions de toutes les arènes.
                var ids = new HashSet<int>((IList<int>)BufferAgentIds.GetValue(ChannelBuffer.GetValue(channel)));
                int arenasInBatch = agents.Where(a => ids.Contains(a.AgentId)).Select(a => a.ArenaName).Distinct().Count();
                maxArenasInOneBatch = Mathf.Max(maxArenasInOneBatch, arenasInBatch);

                int batchesSent = (int)ChannelBatches.GetValue(channel);
                int version = channel.Policy.Version;
                bool ready = WithPython ? batchesSent >= (WithSnapshot ? 10 : 4) && version >= 2 : Time.time - phaseStart > 2f;
                if (!ready)
                    return;

                Require(maxArenasInOneBatch == ArenaCount,
                    $"Batch : transitions de {maxArenasInOneBatch} arène(s) seulement, {ArenaCount} attendues.");
                foreach (FootAgent agent in agents)
                {
                    var ownChannel = (BehaviorChannel)AgentChannel.GetValue(agent);
                    int ownVersion = ownChannel.Policy.Version;
                    Require(agent.ModelVersion == ownVersion, "Modèle : agent désynchronisé de son comportement");
                    Require(!WithPython || ownVersion >= 1, "Modèle : aucune politique reçue de Python.");
                    Require(WithPython || ownVersion == 42, "Modèle : la politique installée n'est pas lue par les agents.");
                    if (WithSnapshot && agent.TeamName == "Red")
                    {
                        Require(((IList<int>)BufferAgentIds.GetValue(ChannelBuffer.GetValue(ownChannel))).Count == 0,
                            "Snapshot : l'adversaire enregistre des transitions");
                        Require((int)ChannelBatches.GetValue(ownChannel) == 0, "Snapshot : batch adverse envoyé");
                    }
                }
                int moved = agents.Count(a => Vector3.ProjectOnPlane(a.transform.position - startPositions[a], Vector3.up).magnitude > 0.01f);
                Require(moved >= agents.Length - 1, $"Simulation : seuls {moved}/{agents.Length} joueurs ont bougé.");

                CheckResetIsolation();
                Debug.Log($"[IA Foot Tests] PASS : {ArenaCount} arènes indépendantes, sans chevauchement, " +
                          $"{agents.Length} agents, {channels.Length} comportement(s), batchs mélangeant les {ArenaCount} arènes" +
                          (WithPython ? $", {batchesSent} batchs envoyés à Python, modèle v{version} reçu et installé partout."
                                      : ", modèle v42 installé sur tous les agents."));
                Finish(null);
            }
            catch (Exception error) { Finish(error); }
        }

        static void CheckStructure(FootAgent[] agents, BehaviorChannel channel)
        {
            var replicator = Object.FindFirstObjectByType<ArenaReplicator>();
            MatchManager[] matches = Object.FindObjectsByType<MatchManager>(FindObjectsSortMode.None);
            Require(matches.Length == ArenaCount && replicator.Arenas.Count == ArenaCount,
                $"Structure : {matches.Length} arène(s), {ArenaCount} attendues.");
            Require(matches.Select(m => m.name).Distinct().Count() == ArenaCount, "Structure : noms d'arènes en double.");
            foreach (MatchManager m in matches)
            {
                Require(m.State == MatchState.Playing, $"Structure : {m.name} ne joue pas ({m.State}).");
                Require(m.AllPlayers.Count == PlayersPerArena, $"Structure : {m.name} a {m.AllPlayers.Count} joueur(s).");
                Require(m.AllPlayers.All(p => p.Match == m && p.transform.IsChildOf(m.transform)), $"Structure : joueurs de {m.name} mal rattachés.");
            }
            Require(matches.Select(m => m.randomSpawns.seed).Distinct().Count() == ArenaCount, "Graines : identiques entre arènes.");

            List<Bounds> footprints = matches.Select(m => WorldBounds(m.transform)).ToList();
            for (int i = 0; i < footprints.Count; i++)
                for (int j = i + 1; j < footprints.Count; j++)
                    Require(!footprints[i].Intersects(footprints[j]), $"Grille : {matches[i].name} et {matches[j].name} se chevauchent.");

            Require(agents.Length == ArenaCount * PlayersPerArena, $"Agents : {agents.Length} trouvés.");
            Require(channel != null, "Agents : canal absent.");
            if (WithSnapshot)
            {
                Require(agents.Select(a => AgentChannel.GetValue(a)).Distinct().Count() == 2, "Snapshot : deux comportements attendus");
                Require(agents.Where(a => a.TeamName == "Blue").All(a => AgentChannel.GetValue(a) == channel), "Snapshot : canal bleu incorrect");
                Require(agents.Where(a => a.TeamName == "Red").All(a => AgentChannel.GetValue(a) != channel), "Snapshot : rouge sur canal apprenant");
                Require(matches.All(m => m.randomSpawns.mode == RoundSpawnMode.AroundBall), "Copies : réglages de scène ignorés");
                foreach (MatchManager m in matches)
                {
                    // Refaire un reset permet de vérifier les positions initiales, avant les déplacements.
                    m.RestartRound();
                    float blueX = m.transform.InverseTransformPoint(m.GetTeam(TeamId.Blue).players[0].Position).x;
                    float redX = m.transform.InverseTransformPoint(m.GetTeam(TeamId.Red).players[0].Position).x;
                    Require(blueX < redX, "Copies : ordre bleu/rouge incorrect");
                }
            }
            else
                Require(agents.All(a => AgentChannel.GetValue(a) == channel), "Agents : plusieurs canaux d'enregistrement.");
            Require(agents.Select(a => a.AgentId).Distinct().Count() == agents.Length, "Agents : identifiants en double.");
            Require(agents.Select(a => a.ArenaName).Distinct().Count() == ArenaCount, "Agents : nom d'arène manquant.");
            Require(agents.All(a => a.EpisodeActive), "Agents : épisode inactif.");
        }

        static void CheckResetIsolation()
        {
            var matches = Object.FindFirstObjectByType<ArenaReplicator>().Arenas;
            if (matches.Count < 2) return;
            MatchManager other = matches[1];
            Vector3 ball = other.Ball.Position;
            Vector3[] players = other.AllPlayers.Select(p => p.Position).ToArray();
            float time = other.RoundTime;
            int roundEvents = 0;
            other.RoundStarted += () => roundEvents++;
            matches[0].RestartRound();
            Require(other.Ball.Position == ball && other.RoundTime == time && roundEvents == 0,
                "Isolation : reset transmis à une autre arène");
            Require(other.AllPlayers.Select(p => p.Position).SequenceEqual(players), "Isolation : joueurs étrangers déplacés");
        }

        static Bounds WorldBounds(Transform root)
        {
            Collider[] colliders = root.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger).ToArray();
            Bounds bounds = colliders[0].bounds;
            foreach (Collider c in colliders)
                bounds.Encapsulate(c.bounds);
            return bounds;
        }

        static Policy VersionedPolicy(BehaviorChannel channel, int version)
        {
            Policy random = Policy.CreateRandom(channel.Actions);
            return new Policy(version, random.Model, random.Decoder);
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        static void Finish(Exception error)
        {
            SessionState.SetBool(Key, false);
            EditorApplication.update -= CheckPlayMode;
            if (error != null) Debug.LogException(error);
            if (EditorApplication.isPlaying)
                EditorApplication.ExitPlaymode();
            EditorApplication.delayCall += () =>
            {
                AssetDatabase.DeleteAsset(TempFolder);
                EditorApplication.Exit(error == null ? 0 : 1);
            };
        }
    }
}
