using UnityEngine;

namespace IAFoot
{
    /// <summary>Relance la manche si le ballon sort de la zone de jeu (utile sans murs, ou si le ballon passe au travers).</summary>
    [CreateAssetMenu(menuName = "IA Foot/Rules/Ball Out Of Bounds", fileName = "BallOutOfBoundsRule")]
    public class BallOutOfBoundsRule : MatchRule
    {
        [Tooltip("Centre de la zone, dans l'espace local de l'arène.")]
        public Vector3 center;

        [Tooltip("Demi-dimensions de la zone, dans l'espace local de l'arène.")]
        public Vector3 halfExtents = new Vector3(2.8f, 2f, 2f);

        public override void OnPlayingTick(MatchManager match, float deltaTime)
        {
            if (!match.Ball)
                return;

            Vector3 p = match.transform.InverseTransformPoint(match.Ball.Position) - center;
            if (Mathf.Abs(p.x) > halfExtents.x || Mathf.Abs(p.y) > halfExtents.y || Mathf.Abs(p.z) > halfExtents.z)
                match.RestartRound();
        }
    }
}
