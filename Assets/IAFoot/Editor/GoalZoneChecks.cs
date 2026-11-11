using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IAFoot.Editor
{
    /// <summary>
    /// Un but ne compte que si le ballon entre par l'ouverture de la cage.
    /// Calcul de la face d'entrée (hors Play), puis vrais tirs avec la physique Unity : de face, par le côté,
    /// par l'arrière et par le dessus ; seul le tir de face doit déclencher le but.
    /// Unity.exe -batchmode -nographics -projectPath . -executeMethod IAFoot.Editor.GoalZoneChecks.RunPlayModeBatch
    /// </summary>
    [InitializeOnLoad]
    public static class GoalZoneChecks
    {
        const string Key = "IAFoot.GoalZoneChecks";
        const string ScenePath = "Assets/GoalZonePlayCheck.unity";
        static readonly string[] Shots = { "face", "cote", "arriere", "dessus" };
        static readonly Dictionary<string, int> goals = new Dictionary<string, int>();

        static GoalZoneChecks()
        {
            if (SessionState.GetBool(Key, false))
                EditorApplication.update += CheckPlayMode;
        }

        [MenuItem("IA Foot/Tests/Vérifier les buts (entrée par l'ouverture)")]
        public static void Run()
        {
            var half = new Vector3(0.5f, 0.3f, 0.3f);
            int front = GoalZone.FaceCode(0, true);
            // Centre encore hors de la zone.
            Require(GoalZone.EntryFace(new Vector3(0.55f, 0f, 0f), new Vector3(-3f, 0f, 0f), half) == front, "face avant, centre dehors");
            Require(GoalZone.EntryFace(new Vector3(0f, 0f, 0.34f), new Vector3(0f, 0f, -3f), half) == GoalZone.FaceCode(2, true), "côté");
            Require(GoalZone.EntryFace(new Vector3(-0.52f, 0f, 0f), new Vector3(3f, 0f, 0f), half) == GoalZone.FaceCode(0, false), "arrière");
            Require(GoalZone.EntryFace(new Vector3(0.1f, 0.32f, 0f), new Vector3(0f, -3f, 0f), half) == GoalZone.FaceCode(1, true), "dessus");
            // Centre déjà dedans : rayon remonté à contre-sens de la vitesse.
            Require(GoalZone.EntryFace(new Vector3(0.4f, 0f, 0f), new Vector3(-3f, 0f, 0.5f), half) == front, "diagonale par l'avant");
            Require(GoalZone.EntryFace(new Vector3(0.4f, 0f, 0.25f), new Vector3(0f, 0f, -3f), half) == GoalZone.FaceCode(2, true),
                "côté, près de l'ouverture");
            // Immobile dedans : face la plus proche.
            Require(GoalZone.EntryFace(new Vector3(-0.45f, 0f, 0f), Vector3.zero, half) == GoalZone.FaceCode(0, false), "immobile");
            Debug.Log("[IA Foot Tests] PASS : calcul de la face d'entrée dans le but.");
        }

        public static void RunPlayModeBatch()
        {
            if (!Application.isBatchMode)
                throw new InvalidOperationException("Ce test crée une scène temporaire : le lancer en batchmode.");
            try
            {
                Run();
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                for (int i = 0; i < Shots.Length; i++)
                    BuildShot(Shots[i], new Vector3(i * 10f, 1f, 0f));
                EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene(), ScenePath);
                SessionState.SetBool(Key, true);
                SessionState.SetFloat(Key + ".deadline", (float)EditorApplication.timeSinceStartup + 60f);
                EditorApplication.update -= CheckPlayMode;
                EditorApplication.update += CheckPlayMode;
                EditorApplication.EnterPlaymode();
            }
            catch (Exception error) { Finish(error); }
        }

        /// <summary>Une cage (zone 1 x 0.6 x 0.6, ouverture vers +X) et un ballon lancé vers son centre.</summary>
        static void BuildShot(string shot, Vector3 position)
        {
            var root = new GameObject("Shot_" + shot);
            root.transform.position = position;
            var zone = new GameObject("GoalTrigger");
            zone.transform.SetParent(root.transform, false);
            zone.AddComponent<BoxCollider>().size = new Vector3(1f, 0.6f, 0.6f);
            zone.AddComponent<GoalZone>().SetMouthDirection(Vector3.right);

            Vector3 from = shot == "face" ? Vector3.right : shot == "cote" ? Vector3.forward
                         : shot == "arriere" ? Vector3.left : Vector3.up;
            var ball = new GameObject("Ball");
            ball.transform.SetParent(root.transform, false);
            ball.transform.localPosition = from * 1.2f;
            ball.AddComponent<SoccerBall>();
            ball.GetComponent<SphereCollider>().radius = 0.05f;
            ball.GetComponent<Rigidbody>().useGravity = false;
        }

        static void CheckPlayMode()
        {
            try
            {
                if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + ".deadline", 0f))
                    throw new TimeoutException("Le test Play n'a pas terminé en 60 secondes.");
                if (!EditorApplication.isPlaying)
                    return;

                if (goals.Count == 0)
                {
                    foreach (string shot in Shots)
                    {
                        Transform root = GameObject.Find("Shot_" + shot).transform;
                        string name = shot;
                        goals[name] = 0;
                        root.GetComponentInChildren<GoalZone>().BallEntered += (zone, ball) => goals[name]++;
                        var ballBody = root.GetComponentInChildren<SoccerBall>().GetComponent<Rigidbody>();
                        ballBody.linearVelocity = (root.position - ballBody.position).normalized * 3f;
                    }
                    SessionState.SetFloat(Key + ".start", Time.time);
                    return;
                }
                if (Time.time - SessionState.GetFloat(Key + ".start", 0f) < 1.5f)
                    return;

                Require(goals["face"] > 0, "Tir de face : le but n'est pas compté.");
                foreach (string shot in new[] { "cote", "arriere", "dessus" })
                    Require(goals[shot] == 0, $"Tir par {shot} : le but est compté à tort.");
                Debug.Log("[IA Foot Tests] PASS : Play réel, but compté de face, refusé par le côté, l'arrière et le dessus.");
                Finish(null);
            }
            catch (Exception error) { Finish(error); }
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
                AssetDatabase.DeleteAsset(ScenePath);
                EditorApplication.Exit(error == null ? 0 : 1);
            };
        }
    }
}
