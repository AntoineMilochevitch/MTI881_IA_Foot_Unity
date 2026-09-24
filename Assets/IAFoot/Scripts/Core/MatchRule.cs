using UnityEngine;

namespace IAFoot
{
    /// <summary>
    /// Règle de match modulaire, branchée dans la liste "rules" du <see cref="MatchManager"/>.
    /// Les règles sont des assets partagés entre toutes les arènes (entraînement parallèle) :
    /// elles ne doivent donc pas stocker d'état propre à un match, seulement lire/modifier le MatchManager.
    /// </summary>
    public abstract class MatchRule : ScriptableObject
    {
        public virtual void OnMatchStart(MatchManager match) { }

        public virtual void OnRoundStart(MatchManager match) { }

        public virtual void OnGoalScored(MatchManager match, TeamId scoringTeam) { }

        /// <summary>Appelé à chaque pas de physique tant que le jeu est en cours (état Playing).</summary>
        public virtual void OnPlayingTick(MatchManager match, float deltaTime) { }

        /// <summary>Texte optionnel affiché par l'UI (ex : chrono). Null = rien à afficher.</summary>
        public virtual string GetStatusText(MatchManager match) => null;
    }
}
