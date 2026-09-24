using UnityEngine;

namespace IAFoot
{
    /// <summary>Durée de match limitée, avec but en or optionnel en cas d'égalité.</summary>
    [CreateAssetMenu(menuName = "IA Foot/Rules/Time Limit", fileName = "TimeLimitRule")]
    public class TimeLimitRule : MatchRule
    {
        [Tooltip("Durée de jeu effectif en secondes (le temps de célébration / coup d'envoi n'est pas compté).")]
        [Min(1f)] public float matchDuration = 180f;

        [Tooltip("En cas d'égalité à la fin du temps, le prochain but gagne. Sinon : match nul.")]
        public bool goldenGoalOnDraw = true;

        public override void OnPlayingTick(MatchManager match, float deltaTime)
        {
            if (match.IsGoldenGoal || match.MatchTime < matchDuration)
                return;

            int blue = match.GetScore(TeamId.Blue);
            int red = match.GetScore(TeamId.Red);
            if (blue != red)
                match.EndMatch(blue > red ? TeamId.Blue : TeamId.Red);
            else if (goldenGoalOnDraw)
                match.IsGoldenGoal = true;
            else
                match.EndMatch(null);
        }

        public override void OnGoalScored(MatchManager match, TeamId scoringTeam)
        {
            if (match.IsGoldenGoal)
                match.EndMatch(scoringTeam);
        }

        public override string GetStatusText(MatchManager match)
        {
            if (match.IsGoldenGoal)
                return "BUT EN OR";

            int seconds = Mathf.CeilToInt(Mathf.Max(0f, matchDuration - match.MatchTime));
            return $"{seconds / 60}:{seconds % 60:00}";
        }
    }
}
