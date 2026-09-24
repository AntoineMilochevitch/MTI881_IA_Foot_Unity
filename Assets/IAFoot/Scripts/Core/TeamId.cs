using UnityEngine;

namespace IAFoot
{
    /// <summary>
    /// Identifiant d'équipe. Par convention, Bleu défend le côté -X de l'arène et attaque vers +X.
    /// </summary>
    public enum TeamId
    {
        Blue = 0,
        Red = 1,
    }

    public static class TeamIdExtensions
    {
        public const int Count = 2;

        public static TeamId Opponent(this TeamId team) => team == TeamId.Blue ? TeamId.Red : TeamId.Blue;

        /// <summary>Signe de la direction d'attaque sur l'axe X local de l'arène (+1 pour Bleu, -1 pour Rouge).</summary>
        public static float AttackSign(this TeamId team) => team == TeamId.Blue ? 1f : -1f;

        public static Color DefaultColor(this TeamId team) =>
            team == TeamId.Blue ? new Color(0.2f, 0.45f, 1f) : new Color(1f, 0.25f, 0.2f);
    }
}
