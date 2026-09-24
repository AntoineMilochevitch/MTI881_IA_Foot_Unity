using System;
using System.Collections;
using UnityEngine;

namespace IAFoot
{
    /// <summary>
    /// Zone de but (BoxCollider en trigger placé derrière la ligne). Le but est validé quand le ballon est
    /// entièrement dans la zone, comme dans les vraies règles (ballon entièrement derrière la ligne).
    /// C'est le MatchManager qui décide si le but compte ; il déclenche ensuite <see cref="PlayCelebration"/>.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    public class GoalZone : MonoBehaviour
    {
        [Tooltip("Équipe qui défend ce but (celle qui encaisse si le ballon y entre).")]
        [SerializeField] TeamId defendingTeam = TeamId.Blue;
        [SerializeField] bool requireBallFullyInside = true;

        [Header("Célébration")]
        [Tooltip("Objet animé lors d'un but (par défaut le parent : le modèle de la cage).")]
        [SerializeField] Transform animatedTarget;
        [SerializeField, Min(0.01f)] float celebrationDuration = 0.8f;
        [SerializeField] float punchScale = 0.1f;
        [SerializeField] float shakeAngle = 5f;
        [SerializeField] ParticleSystem goalEffect;
        [SerializeField] AudioSource goalSound;

        /// <summary>Déclenché à chaque pas de physique tant que le ballon est dans le but.</summary>
        public event Action<GoalZone, SoccerBall> BallEntered;

        public TeamId DefendingTeam => defendingTeam;
        public Vector3 Center => transform.TransformPoint(Box.center);

        BoxCollider Box => box ? box : box = GetComponent<BoxCollider>();

        BoxCollider box;
        Vector3 baseScale;
        Quaternion baseRotation;
        Coroutine celebration;

        void Awake()
        {
            Box.isTrigger = true;
            if (!animatedTarget)
                animatedTarget = transform.parent ? transform.parent : transform;
            baseScale = animatedTarget.localScale;
            baseRotation = animatedTarget.localRotation;
        }

        void OnDisable()
        {
            if (celebration != null)
            {
                StopCoroutine(celebration);
                celebration = null;
                RestoreTarget();
            }
        }

        /// <summary>Configuration depuis l'outil éditeur.</summary>
        public void Configure(TeamId defending, Transform target)
        {
            defendingTeam = defending;
            animatedTarget = target;
        }

        void OnTriggerStay(Collider other)
        {
            Rigidbody body = other.attachedRigidbody;
            if (!body || !body.TryGetComponent(out SoccerBall ball))
                return;

            if (!requireBallFullyInside || ContainsSphere(ball.Position, ball.Radius))
                BallEntered?.Invoke(this, ball);
        }

        bool ContainsSphere(Vector3 worldCenter, float worldRadius)
        {
            Vector3 local = transform.InverseTransformPoint(worldCenter) - Box.center;
            Vector3 half = Box.size * 0.5f;
            Vector3 scale = transform.lossyScale;
            for (int axis = 0; axis < 3; axis++)
            {
                float radiusLocal = worldRadius / Mathf.Max(Mathf.Abs(scale[axis]), 1e-5f);
                if (Mathf.Abs(local[axis]) > half[axis] - radiusLocal)
                    return false;
            }
            return true;
        }

        public void PlayCelebration()
        {
            if (goalEffect)
                goalEffect.Play();
            if (goalSound)
                goalSound.Play();

            if (celebration != null)
                StopCoroutine(celebration);
            celebration = StartCoroutine(CelebrationRoutine());
        }

        IEnumerator CelebrationRoutine()
        {
            for (float t = 0f; t < 1f; t += Time.deltaTime / celebrationDuration)
            {
                // Oscillation amortie : la cage "encaisse" le ballon.
                float damping = 1f - t;
                float wave = Mathf.Sin(t * Mathf.PI * 6f) * damping;
                animatedTarget.localScale = baseScale * (1f + punchScale * Mathf.Abs(wave));
                animatedTarget.localRotation = baseRotation * Quaternion.Euler(0f, 0f, shakeAngle * wave);
                yield return null;
            }

            RestoreTarget();
            celebration = null;
        }

        void RestoreTarget()
        {
            animatedTarget.localScale = baseScale;
            animatedTarget.localRotation = baseRotation;
        }

        void OnDrawGizmos()
        {
            BoxCollider b = GetComponent<BoxCollider>();
            if (!b)
                return;

            Gizmos.matrix = transform.localToWorldMatrix;
            Color color = defendingTeam.DefaultColor();
            color.a = 0.2f;
            Gizmos.color = color;
            Gizmos.DrawCube(b.center, b.size);
            color.a = 1f;
            Gizmos.color = color;
            Gizmos.DrawWireCube(b.center, b.size);
        }
    }
}
