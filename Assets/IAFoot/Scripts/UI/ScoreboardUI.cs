using UnityEngine;
using UnityEngine.UI;

namespace IAFoot
{
    /// <summary>Affiche le score, le texte des règles (chrono...) et les messages (coup d'envoi, but, fin de match).</summary>
    public class ScoreboardUI : MonoBehaviour
    {
        [SerializeField] MatchManager match;
        [SerializeField] Text scoreText;
        [SerializeField] Text statusText;
        [SerializeField] Text messageText;

        [Header("Messages")]
        [SerializeField, Min(0.1f)] float messageDuration = 2f;
        [SerializeField, Min(0f)] float messageFadeTime = 0.3f;
        [SerializeField, Min(1f)] float messagePopScale = 1.6f;
        [SerializeField, Min(0.01f)] float messagePopTime = 0.25f;

        float messageAge;
        float currentMessageDuration;
        Color messageColor = Color.white;
        int shownBlue = -1;
        int shownRed = -1;

        /// <summary>Configuration depuis l'outil éditeur.</summary>
        public void Configure(MatchManager targetMatch, Text score, Text status, Text message)
        {
            match = targetMatch;
            scoreText = score;
            statusText = status;
            messageText = message;
        }

        void OnEnable()
        {
            if (!match)
                match = GetComponentInParent<MatchManager>();
            if (!match)
                return;

            match.RoundStarted += OnRoundStarted;
            match.PlayStarted += OnPlayStarted;
            match.GoalScored += OnGoalScored;
            match.MatchEnded += OnMatchEnded;
            HideMessage();
        }

        void OnDisable()
        {
            if (!match)
                return;

            match.RoundStarted -= OnRoundStarted;
            match.PlayStarted -= OnPlayStarted;
            match.GoalScored -= OnGoalScored;
            match.MatchEnded -= OnMatchEnded;
        }

        void OnRoundStarted()
        {
            if (match.kickoffFreezeDuration > 0.2f)
                ShowMessage("Prêts ?", Color.white, match.kickoffFreezeDuration);
        }

        void OnPlayStarted() => ShowMessage("C'est parti !", Color.white, 0.8f);

        void OnGoalScored(TeamId scorer) =>
            ShowMessage($"BUT !\n{match.GetTeamName(scorer)}", match.GetTeamColor(scorer), messageDuration);

        void OnMatchEnded(TeamId? winner)
        {
            if (winner.HasValue)
                ShowMessage($"{match.GetTeamName(winner.Value)} gagne !", match.GetTeamColor(winner.Value), match.matchOverDuration);
            else
                ShowMessage("Match nul", Color.white, match.matchOverDuration);
        }

        void Update()
        {
            if (!match)
                return;

            UpdateScore();

            if (statusText)
                statusText.text = match.GetStatusText();

            UpdateMessage(Time.deltaTime);
        }

        void UpdateScore()
        {
            int blue = match.GetScore(TeamId.Blue);
            int red = match.GetScore(TeamId.Red);
            if (!scoreText || (blue == shownBlue && red == shownRed))
                return;

            shownBlue = blue;
            shownRed = red;
            scoreText.text = $"{Colored(TeamId.Blue, match.GetTeamName(TeamId.Blue))}   {blue} - {red}   {Colored(TeamId.Red, match.GetTeamName(TeamId.Red))}";
        }

        string Colored(TeamId team, string text) =>
            $"<color=#{ColorUtility.ToHtmlStringRGB(match.GetTeamColor(team))}>{text}</color>";

        void ShowMessage(string text, Color color, float duration)
        {
            if (!messageText)
                return;
            messageText.text = text;
            messageColor = color;
            messageAge = 0f;
            currentMessageDuration = duration;
            messageText.enabled = true;
        }

        void HideMessage()
        {
            if (messageText)
                messageText.enabled = false;
        }

        void UpdateMessage(float dt)
        {
            if (!messageText || !messageText.enabled)
                return;

            messageAge += dt;
            if (messageAge >= currentMessageDuration)
            {
                HideMessage();
                return;
            }

            // Apparition en "pop" puis fondu de sortie.
            float pop = Mathf.Clamp01(messageAge / messagePopTime);
            float scale = Mathf.Lerp(messagePopScale, 1f, 1f - (1f - pop) * (1f - pop));
            messageText.rectTransform.localScale = Vector3.one * scale;

            float remaining = currentMessageDuration - messageAge;
            Color color = messageColor;
            color.a = messageFadeTime > 0f ? Mathf.Clamp01(remaining / messageFadeTime) : 1f;
            messageText.color = color;
        }
    }
}
