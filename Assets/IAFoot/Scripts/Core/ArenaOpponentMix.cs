using System;
using System.Linq;
using IAFoot.Learning;
using UnityEngine;

namespace IAFoot
{
    /// <summary>
    /// Répartit des adversaires fixes entre les arènes, avant toute inscription
    /// au pont. Un seul apprenant bleu par terrain ; tous partagent "Foot".
    /// Les rouges sont soit des bots, soit des snapshots sans enregistrement.
    /// Désactiver ce composant restitue la duplication habituelle de la scène.
    /// </summary>
    [RequireComponent(typeof(ArenaReplicator))]
    public class ArenaOpponentMix : MonoBehaviour
    {
        [SerializeField, Min(1)] int botArenaCount = 16;
        [SerializeField, Min(1)] int oldSnapshotArenaCount = 8;
        [SerializeField] string learnerBehavior = "Foot";
        [SerializeField] string oldSnapshotBehavior = "FootSnapshotOld";
        [SerializeField] string recentSnapshotBehavior = "FootSnapshotRecent";

        public void ValidateCount(int total)
        {
            if (botArenaCount < 1 || oldSnapshotArenaCount < 1 ||
                botArenaCount + oldSnapshotArenaCount >= total)
                throw new InvalidOperationException("Le mélange exige au moins un terrain par adversaire (défaut : 16 + 8 + 8 = 32).");
            string[] names = { learnerBehavior, oldSnapshotBehavior, recentSnapshotBehavior };
            if (names.Any(n => string.IsNullOrWhiteSpace(n) || n == "*") || names.Distinct().Count() != 3)
                throw new InvalidOperationException("Trois comportements distincts sont nécessaires pour le mélange.");
        }

        public string GroupForArena(int index) => index < botArenaCount ? "bot"
            : index < botArenaCount + oldSnapshotArenaCount ? "snapshot_old" : "snapshot_recent";

        public void Apply(MatchManager match, int index)
        {
            // Utiliser les équipes sérialisées : MatchManager.Awake n'a pas
            // encore construit ses listes d'exécution sur les copies inactives.
            var blue = match.teams.Single(t => t.id == TeamId.Blue);
            var red = match.teams.Single(t => t.id == TeamId.Red);
            if (blue.players.Count != 1 || red.players.Count != 1 ||
                !blue.players[0] || !red.players[0] ||
                !blue.players[0].gameObject.activeSelf || !red.players[0].gameObject.activeSelf)
                throw new InvalidOperationException("ArenaOpponentMix exige un joueur actif par équipe (1 contre 1).");
            string group = GroupForArena(index);
            ConfigurePlayer(blue.players[0], learnerBehavior, true, false, group);
            string opponentBehavior = group == "snapshot_old" ? oldSnapshotBehavior : recentSnapshotBehavior;
            ConfigurePlayer(red.players[0], opponentBehavior, false, group == "bot", group);
        }

        static void ConfigurePlayer(PlayerController player, string behavior, bool record, bool bot, string group)
        {
            var agent = player.GetComponent<FootAgent>();
            if (!agent)
                throw new InvalidOperationException("Chaque joueur source doit posséder un FootAgent configuré (même schéma).");
            // Désactiver les contrôleurs concurrents, sans toucher au moteur physique.
            foreach (HumanPlayerInput human in player.GetComponents<HumanPlayerInput>())
                human.enabled = false;
            var botInput = player.GetComponent<SimpleBotInput>();
            if (bot && !botInput)
                botInput = player.gameObject.AddComponent<SimpleBotInput>();
            if (botInput) botInput.enabled = bot;
            agent.ConfigureTrainingRole(behavior, record, group);
            agent.enabled = !bot;
        }
    }
}
