using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace IAFoot.EditorTools
{
    /// <summary>Enregistre l'arène de la scène en prefab et met en place sa duplication pour l'entraînement parallèle.</summary>
    [InitializeOnLoad]
    public static class ArenaPrefabTools
    {
        public const string PrefabFolder = "Assets/IAFoot/Prefabs";
        public const string PrefabPath = PrefabFolder + "/Arena.prefab";

        static ArenaPrefabTools() => EditorApplication.playModeStateChanged += WarnIfPrefabOutdated;

        /// <summary>
        /// Juste avant Play (en Play, Unity ne garde plus le lien entre l'arène et son prefab) : les copies
        /// viennent du prefab, donc une modification non appliquée sur l'arène de la scène ne serait pas dupliquée.
        /// </summary>
        static void WarnIfPrefabOutdated(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingEditMode)
                return;
            foreach (ArenaReplicator replicator in Object.FindObjectsByType<ArenaReplicator>(FindObjectsSortMode.None))
            {
                GameObject prefab = replicator.ArenaPrefab;
                MatchManager arena = replicator.SceneArena;
                if (!prefab || !arena)
                    continue;
                GameObject root = arena.gameObject;
                if (!PrefabUtility.IsOutermostPrefabInstanceRoot(root)
                    || PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root) != AssetDatabase.GetAssetPath(prefab))
                    Debug.LogWarning($"[IA Foot] ArenaReplicator : '{root.name}' n'est pas une instance du prefab '{prefab.name}'. " +
                                     "Les copies risquent de ne pas avoir la même configuration.", replicator);
                else if (PrefabUtility.HasPrefabInstanceAnyOverrides(root, false))
                    Debug.LogWarning($"[IA Foot] ArenaReplicator : '{root.name}' a des modifications non appliquées au prefab. " +
                                     "Les copies utilisent la version du prefab : IA Foot > Entraînement parallèle > " +
                                     "Appliquer les modifications de l'arène au prefab.", replicator);
            }
        }

        [MenuItem("IA Foot/Entraînement parallèle/1. Enregistrer l'arène en prefab")]
        static void SaveArenaPrefabMenu()
        {
            MatchManager match = FindSceneArena();
            if (!match)
            {
                EditorUtility.DisplayDialog("IA Foot", "Aucune arène (MatchManager) dans la scène. Configurez d'abord le match.", "OK");
                return;
            }

            List<string> outside = ReferencesOutsideArena(match);
            if (outside.Count > 0 && !EditorUtility.DisplayDialog("IA Foot",
                    "Ces références du MatchManager ne sont pas des enfants de l'arène et seront vides dans le prefab :\n- " +
                    string.Join("\n- ", outside) + "\n\nContinuer ?", "Continuer", "Annuler"))
                return;

            GameObject prefab = SaveArenaPrefab(match);
            if (prefab)
            {
                EditorGUIUtility.PingObject(prefab);
                Debug.Log($"[IA Foot] Arène enregistrée dans {PrefabPath}. Étape suivante : " +
                          "IA Foot > Entraînement parallèle > 2. Dupliquer l'arène.", prefab);
            }
        }

        [MenuItem("IA Foot/Entraînement parallèle/2. Dupliquer l'arène (ajouter l'ArenaReplicator)")]
        static void AddReplicatorMenu()
        {
            MatchManager match = FindSceneArena();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (!prefab)
            {
                if (!match)
                {
                    EditorUtility.DisplayDialog("IA Foot", "Aucune arène dans la scène et aucun prefab enregistré.", "OK");
                    return;
                }
                prefab = SaveArenaPrefab(match);
                if (!prefab)
                    return;
            }

            ArenaReplicator replicator = Object.FindFirstObjectByType<ArenaReplicator>(FindObjectsInactive.Include);
            if (!replicator)
            {
                var go = new GameObject("ArenaReplicator");
                Undo.RegisterCreatedObjectUndo(go, "Create ArenaReplicator");
                replicator = go.AddComponent<ArenaReplicator>();
            }

            var settings = new SerializedObject(replicator);
            settings.FindProperty("arenaPrefab").objectReferenceValue = prefab;
            settings.FindProperty("sceneArena").objectReferenceValue = match;
            settings.ApplyModifiedProperties();

            EditorSceneManager.MarkSceneDirty(replicator.gameObject.scene);
            Selection.activeGameObject = replicator.gameObject;
            Debug.Log("[IA Foot] ArenaReplicator prêt : réglez 'Arena Count' dans l'Inspector puis lancez Play. " +
                      "Toutes les arènes partagent le même TrainingBridge (un seul batch, un seul modèle).", replicator);
        }

        [MenuItem("IA Foot/Entraînement parallèle/Appliquer les modifications de l'arène au prefab")]
        static void ApplyOverridesMenu()
        {
            MatchManager match = FindSceneArena();
            if (!match || !IsInstanceOfArenaPrefab(match.gameObject))
            {
                EditorUtility.DisplayDialog("IA Foot", "L'arène de la scène n'est pas une instance de " + PrefabPath + ".", "OK");
                return;
            }
            SyncPrefab(match);
        }

        /// <summary>Crée (ou écrase) le prefab et relie l'arène de la scène à celui-ci.</summary>
        public static GameObject SaveArenaPrefab(MatchManager match)
        {
            EnsureFolder(PrefabFolder);
            GameObject root = match.gameObject;
            if (IsInstanceOfArenaPrefab(root))
            {
                SyncPrefab(match);
                return AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            }

            // Une arène issue d'un autre prefab (cas rare) devient indépendante avant d'être enregistrée.
            if (PrefabUtility.IsAnyPrefabInstanceRoot(root) && PrefabUtility.IsOutermostPrefabInstanceRoot(root))
                PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.OutermostRoot, InteractionMode.UserAction);

            GameObject prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(root, PrefabPath, InteractionMode.UserAction, out bool success);
            if (!success)
            {
                Debug.LogError($"[IA Foot] Impossible d'enregistrer l'arène dans {PrefabPath}.", root);
                return null;
            }
            EditorSceneManager.MarkSceneDirty(root.scene);
            return prefab;
        }

        /// <summary>
        /// Reporte dans le prefab les changements faits sur l'arène de la scène (ex : bascule entraînement / jeu),
        /// pour que les copies créées par l'ArenaReplicator aient la même configuration.
        /// </summary>
        public static void SyncPrefab(MatchManager match)
        {
            GameObject root = match ? match.gameObject : null;
            if (!root || !IsInstanceOfArenaPrefab(root))
                return;

            // Les changements faits via Undo.RecordObject ne deviennent des overrides qu'au flush de l'Undo (fin de frame).
            Undo.FlushUndoRecordObjects();
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component)
                    PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            }
            if (!PrefabUtility.HasPrefabInstanceAnyOverrides(root, false))
                return;
            PrefabUtility.ApplyPrefabInstance(root, InteractionMode.UserAction);
            Debug.Log($"[IA Foot] Modifications de '{root.name}' appliquées au prefab {PrefabPath}.", root);
        }

        public static bool IsInstanceOfArenaPrefab(GameObject root) =>
            PrefabUtility.IsOutermostPrefabInstanceRoot(root)
            && PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root) == PrefabPath;

        /// <summary>Arène de la sélection, sinon la première de la scène.</summary>
        static MatchManager FindSceneArena()
        {
            if (Selection.activeGameObject)
            {
                MatchManager selected = Selection.activeGameObject.GetComponentInParent<MatchManager>();
                if (selected)
                    return selected;
            }
            return Object.FindFirstObjectByType<MatchManager>(FindObjectsInactive.Include);
        }

        static List<string> ReferencesOutsideArena(MatchManager match)
        {
            var outside = new List<string>();
            Transform arena = match.transform;
            void Check(Object reference, string label)
            {
                Transform t = reference is Component c ? c.transform : reference is GameObject g ? g.transform : null;
                if (t && !t.IsChildOf(arena))
                    outside.Add($"{label} ({t.name})");
            }

            Check(match.ball, "ballon");
            Check(match.ballSpawn, "point d'apparition du ballon");
            foreach (GoalZone goal in match.goals)
                Check(goal, "but");
            foreach (TeamSetup team in match.teams)
            {
                foreach (PlayerController player in team.players)
                    Check(player, $"joueur {team.id}");
                foreach (Transform spawn in team.spawnPoints)
                    Check(spawn, $"apparition {team.id}");
            }
            return outside;
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
