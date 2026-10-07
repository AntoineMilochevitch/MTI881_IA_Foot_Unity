using IAFoot.Learning;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace IAFoot.EditorTools
{
    /// <summary>Bascule la scène ouverte entre le mode "jeu" (humain contre bot) et le mode "entraînement" (agents IA).</summary>
    public static class TrainingSceneTools
    {
        [MenuItem("IA Foot/Entraînement/Préparer la scène (tous les joueurs deviennent des agents)")]
        static void EnableTraining()
        {
            PlayerController[] players = Object.FindObjectsByType<PlayerController>(FindObjectsSortMode.None);
            if (players.Length == 0)
            {
                EditorUtility.DisplayDialog("IA Foot", "Aucun joueur dans la scène. Configurez d'abord le match.", "OK");
                return;
            }

            TrainingBridge bridge = Object.FindFirstObjectByType<TrainingBridge>(FindObjectsInactive.Include);
            if (!bridge)
            {
                var go = new GameObject("TrainingBridge");
                Undo.RegisterCreatedObjectUndo(go, "Create TrainingBridge");
                bridge = go.AddComponent<TrainingBridge>();
            }
            SetActive(bridge.gameObject, true);

            foreach (PlayerController player in players)
            {
                SetBrainEnabled(player.GetComponent<HumanPlayerInput>(), false);
                SetBrainEnabled(player.GetComponent<SimpleBotInput>(), false);
                FootAgent agent = player.GetComponent<FootAgent>();
                if (!agent)
                    agent = Undo.AddComponent<FootAgent>(player.gameObject);
                SetBrainEnabled(agent, true);
            }

            // Pas de temps mort entre deux épisodes pendant l'entraînement.
            SetMatchTimings(0f, 0f, 0f);

            MarkDirty(bridge.gameObject);
            Selection.activeGameObject = bridge.gameObject;
            Debug.Log($"[IA Foot] Scène prête pour l'entraînement : {players.Length} agent(s). " +
                      "Lancez `python train.py` dans le dossier python/, puis Play.", bridge);
        }

        [MenuItem("IA Foot/Entraînement/Revenir au mode jeu (humain contre bot)")]
        static void DisableTraining()
        {
            foreach (PlayerController player in Object.FindObjectsByType<PlayerController>(FindObjectsSortMode.None))
            {
                SetBrainEnabled(player.GetComponent<FootAgent>(), false);
                SetBrainEnabled(player.GetComponent<HumanPlayerInput>(), true);
                SetBrainEnabled(player.GetComponent<SimpleBotInput>(), true);
            }

            TrainingBridge bridge = Object.FindFirstObjectByType<TrainingBridge>(FindObjectsInactive.Include);
            if (bridge)
            {
                SetActive(bridge.gameObject, false);
                MarkDirty(bridge.gameObject);
            }

            SetMatchTimings(1f, 2.5f, 4f);
            Debug.Log("[IA Foot] Scène revenue au mode jeu.");
        }

        static void SetMatchTimings(float kickoff, float celebration, float matchOver)
        {
            foreach (MatchManager match in Object.FindObjectsByType<MatchManager>(FindObjectsSortMode.None))
            {
                Undo.RecordObject(match, "Match timings");
                match.kickoffFreezeDuration = kickoff;
                match.goalCelebrationDuration = celebration;
                match.matchOverDuration = matchOver;
                EditorUtility.SetDirty(match);
                MarkDirty(match.gameObject);
            }
        }

        static void SetBrainEnabled(Behaviour brain, bool enabled)
        {
            if (!brain || brain.enabled == enabled)
                return;
            Undo.RecordObject(brain, "Toggle brain");
            brain.enabled = enabled;
            EditorUtility.SetDirty(brain);
        }

        static void SetActive(GameObject go, bool active)
        {
            if (go.activeSelf == active)
                return;
            Undo.RecordObject(go, "Toggle TrainingBridge");
            go.SetActive(active);
        }

        static void MarkDirty(GameObject go) => EditorSceneManager.MarkSceneDirty(go.scene);
    }
}
