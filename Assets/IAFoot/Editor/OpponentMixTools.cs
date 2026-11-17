using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace IAFoot.Editor
{
    public static class OpponentMixTools
    {
        [MenuItem("IA Foot/Entraînement parallèle/Configurer le mélange 16 bots + 8 anciennes + 8 récentes")]
        static void ConfigureScene() => ConfigureScene(8);

        [MenuItem("IA Foot/Entraînement parallèle/Configurer le mélange 16 bots + 16 snapshots")]
        static void ConfigureTwoGroups() => ConfigureScene(16);

        // Le reste des terrains re?oit le groupe r?cent ; z?ro reste donne deux groupes.
        static void ConfigureScene(int snapshotCount)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Arrêter Play avant de configurer le mélange.");
            var replicators = UnityEngine.Object.FindObjectsByType<ArenaReplicator>(FindObjectsSortMode.None);
            if (replicators.Length != 1 || !replicators[0].SceneArena)
                throw new InvalidOperationException("La scène doit contenir un ArenaReplicator avec Scene Arena renseigné.");
            var replicator = replicators[0];
            var settings = new SerializedObject(replicator);
            settings.FindProperty("arenaCount").intValue = 32;
            settings.FindProperty("cloneSceneArena").boolValue = true;
            settings.ApplyModifiedProperties();
            var mix = replicator.GetComponent<ArenaOpponentMix>();
            if (!mix) mix = Undo.AddComponent<ArenaOpponentMix>(replicator.gameObject);
            Undo.RecordObject(mix, "Configurer le mélange d'adversaires");
            var mixSettings = new SerializedObject(mix);
            mixSettings.FindProperty("botArenaCount").intValue = 16;
            mixSettings.FindProperty("oldSnapshotArenaCount").intValue = snapshotCount;
            mixSettings.FindProperty("learnerBehavior").stringValue = "Foot";
            mixSettings.FindProperty("oldSnapshotBehavior").stringValue = "FootSnapshotOld";
            mixSettings.FindProperty("recentSnapshotBehavior").stringValue = "FootSnapshotRecent";
            mixSettings.ApplyModifiedProperties();
            mix.enabled = true;
            EditorSceneManager.MarkSceneDirty(replicator.gameObject.scene);
            Selection.activeGameObject = replicator.gameObject;
            Debug.Log("[IA Foot] Melange configure. Sauvegarder la scene puis lancer Python avec " +
                      (snapshotCount == 16 ? "--opponent FootSnapshotOld <checkpoint>" : "les deux --opponent") + " avant Play. " +
                      "Desactiver ArenaOpponentMix pour revenir a la configuration habituelle.");
        }
    }
}
