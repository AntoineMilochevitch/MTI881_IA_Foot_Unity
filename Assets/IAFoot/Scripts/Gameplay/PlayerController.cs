using System;
using System.Collections.Generic;
using UnityEngine;

namespace IAFoot
{
    /// <summary>
    /// "Moteur" d'un joueur : déplacement, rotation, tir chargé et tacle.
    /// Il ne lit aucune entrée lui-même : un "cerveau" l'alimente via l'API de commande
    /// (<see cref="SetMoveInput"/>, <see cref="StartCharge"/>/<see cref="ReleaseCharge"/>, <see cref="Shoot"/>, <see cref="Tackle"/>).
    /// Ce cerveau peut être <see cref="HumanPlayerInput"/>, <see cref="SimpleBotInput"/> ou un Agent ML-Agents.
    /// Les valeurs par défaut sont calibrées pour l'échelle du pack Football Essentials 3D (joueur ≈ 0.3 unité).
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class PlayerController : MonoBehaviour
    {
        [Tooltip("Équipe par défaut (remplacée par le MatchManager au lancement).")]
        [SerializeField] TeamId team = TeamId.Blue;

        [Header("Déplacement")]
        [SerializeField, Min(0f)] float moveSpeed = 1.6f;
        [Tooltip("Accélération en unités/s².")]
        [SerializeField, Min(0f)] float acceleration = 12f;
        [Tooltip("Décélération en unités/s² quand il n'y a plus d'entrée.")]
        [SerializeField, Min(0f)] float deceleration = 14f;
        [Tooltip("Vitesse de l'interpolation de rotation vers la direction de déplacement.")]
        [SerializeField, Min(0f)] float turnSharpness = 12f;

        [Header("Tir")]
        [Tooltip("Temps de maintien (s) pour atteindre une intensité de 1.")]
        [SerializeField, Min(0.01f)] float maxChargeTime = 1f;
        [Tooltip("Multiplicateur de vitesse pendant la charge du tir.")]
        [SerializeField, Range(0f, 1f)] float chargingMoveMultiplier = 0.6f;
        [SerializeField, Min(0f)] float minShotSpeed = 1.2f;
        [SerializeField, Min(0f)] float maxShotSpeed = 6f;
        [Tooltip("Composante verticale du tir à intensité 0 (0 = ras du sol).")]
        [SerializeField] float minShotLift = 0.05f;
        [Tooltip("Composante verticale du tir à intensité 1.")]
        [SerializeField] float maxShotLift = 0.35f;
        [Tooltip("Rétro-effet appliqué à intensité 1 (rad/s), donne un peu de portance via l'effet Magnus.")]
        [SerializeField, Min(0f)] float maxBackspin = 40f;
        [Tooltip("Effet latéral maximal (rad/s) pour Shoot(power, curve).")]
        [SerializeField, Min(0f)] float maxCurveSpin = 60f;
        [Tooltip("Part de la vitesse du joueur transmise au ballon.")]
        [SerializeField, Range(0f, 1f)] float carryVelocity = 0.3f;
        [Tooltip("Rayon de la zone de frappe devant le joueur.")]
        [SerializeField, Min(0f)] float kickReach = 0.15f;
        [Tooltip("Demi-angle du cône de frappe devant le joueur.")]
        [SerializeField, Range(0f, 180f)] float kickHalfAngle = 75f;
        [SerializeField, Min(0f)] float shotCooldown = 0.35f;
        [Tooltip("Hauteur du point de frappe par rapport aux pieds.")]
        [SerializeField, Min(0f)] float footHeight = 0.07f;

        [Header("Saut")]
        [Tooltip("Vitesse verticale au décollage (≈ 2.2 pour sauter à peu près la hauteur du joueur).")]
        [SerializeField, Min(0f)] float jumpSpeed = 2.2f;
        [Tooltip("Part de l'accélération conservée en l'air.")]
        [SerializeField, Range(0f, 1f)] float airControl = 0.6f;
        [SerializeField, Min(0f)] float jumpCooldown = 0.2f;
        [SerializeField] LayerMask groundMask = ~0;

        [Header("Tacle")]
        [SerializeField, Min(0f)] float tackleSpeed = 3f;
        [SerializeField, Min(0.01f)] float tackleDuration = 0.35f;
        [SerializeField, Min(0f)] float tackleCooldown = 1.2f;
        [SerializeField, Min(0f)] float tackleReach = 0.14f;
        [Tooltip("Vitesse donnée au ballon quand le tacle le touche.")]
        [SerializeField, Min(0f)] float tackleBallSpeed = 2.5f;
        [Tooltip("Durée pendant laquelle un joueur taclé ne peut plus agir.")]
        [SerializeField, Min(0f)] float stunDuration = 1f;
        [SerializeField, Min(0f)] float knockbackSpeed = 1.5f;
        [SerializeField] bool friendlyTackles;

        /// <summary>(joueur, intensité, ballon touché ?)</summary>
        public event Action<PlayerController, float, bool> Kicked;
        public event Action<PlayerController> Jumped;
        public event Action<PlayerController> TackleStarted;
        /// <summary>(tacleur, victime)</summary>
        public event Action<PlayerController, PlayerController> TackleHit;
        public event Action<PlayerController> Stunned;

        public TeamId Team => team;
        public MatchManager Match { get; private set; }
        public Color TeamColor => Match ? Match.GetTeamColor(team) : team.DefaultColor();
        public Rigidbody Body => rb;
        public Vector3 Position => rb.position;
        public Vector3 Velocity => rb.linearVelocity;
        public Vector3 Forward => FlatForward();
        public Vector3 MoveInput => moveInput;
        public float MoveSpeed => moveSpeed;

        public bool IsFrozen { get; private set; }
        public bool IsGrounded { get; private set; }
        public bool IsCharging { get; private set; }
        /// <summary>Intensité du tir en cours de charge, entre 0 et 1.</summary>
        public float ChargeAmount => IsCharging ? Mathf.Clamp01(chargeTimer / maxChargeTime) : 0f;
        public bool IsTackling => tackleTimer > 0f;
        public bool IsStunned => stunTimer > 0f;
        public bool CanAct => !IsFrozen && !IsStunned && !IsTackling;
        public float ShotCooldown01 => shotCooldown > 0f ? shotCooldownTimer / shotCooldown : 0f;
        public float TackleCooldown01 => tackleCooldown > 0f ? tackleCooldownTimer / tackleCooldown : 0f;

        Rigidbody rb;
        Vector3 moveInput;
        float chargeTimer;
        float shotCooldownTimer;
        float tackleTimer;
        float tackleCooldownTimer;
        float stunTimer;
        float jumpCooldownTimer;
        Vector3 tackleDirection;
        bool tackleHitBall;
        readonly List<PlayerController> tackleVictims = new List<PlayerController>();
        readonly Collider[] overlapBuffer = new Collider[16];

        void Awake()
        {
            rb = GetComponent<Rigidbody>();
            rb.freezeRotation = true; // la rotation est pilotée par le script
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }

        public void Initialize(MatchManager match, TeamId teamId)
        {
            Match = match;
            team = teamId;
        }

        // ------------------------------------------------------------------ API de commande

        /// <summary>Direction de déplacement souhaitée en espace monde (magnitude 0..1, Y ignoré).</summary>
        public void SetMoveInput(Vector3 worldDirection)
        {
            worldDirection.y = 0f;
            moveInput = Vector3.ClampMagnitude(worldDirection, 1f);
        }

        /// <summary>Commence à charger un tir (touche enfoncée).</summary>
        public void StartCharge()
        {
            if (!CanAct || IsCharging)
                return;
            IsCharging = true;
            chargeTimer = 0f;
        }

        /// <summary>Relâche la charge : tire avec l'intensité accumulée.</summary>
        public void ReleaseCharge()
        {
            if (!IsCharging)
                return;
            float power = ChargeAmount;
            IsCharging = false;
            Shoot(power);
        }

        public void CancelCharge()
        {
            IsCharging = false;
            chargeTimer = 0f;
        }

        /// <summary>
        /// Tire immédiatement dans la direction du regard.
        /// power : intensité 0..1. curve : effet latéral -1..1 (positif = courbe vers la droite du tireur).
        /// Retourne vrai si le ballon a été frappé (sinon c'est un tir dans le vide, l'animation est jouée quand même).
        /// </summary>
        public bool Shoot(float power, float curve = 0f)
        {
            if (!CanAct || shotCooldownTimer > 0f)
                return false;

            power = Mathf.Clamp01(power);
            curve = Mathf.Clamp(curve, -1f, 1f);
            IsCharging = false;
            shotCooldownTimer = shotCooldown;

            SoccerBall ball = FindBallInKickRange();
            if (ball)
            {
                Vector3 forward = FlatForward();
                float lift = Mathf.Lerp(minShotLift, maxShotLift, power);
                Vector3 direction = (forward + Vector3.up * lift).normalized;
                Vector3 velocity = direction * Mathf.Lerp(minShotSpeed, maxShotSpeed, power)
                                   + Flat(rb.linearVelocity) * carryVelocity;
                // Rétro-effet autour de (avant × haut) + effet latéral autour de la verticale.
                Vector3 spin = Vector3.Cross(forward, Vector3.up) * (maxBackspin * power)
                               + Vector3.up * (maxCurveSpin * curve);
                ball.Kick(velocity, spin, this);
            }

            Kicked?.Invoke(this, power, ball != null);
            return ball != null;
        }

        /// <summary>Saute si le joueur est au sol.</summary>
        public bool Jump()
        {
            if (!CanAct || !IsGrounded || jumpCooldownTimer > 0f)
                return false;

            jumpCooldownTimer = jumpCooldown;
            IsGrounded = false;
            Vector3 velocity = rb.linearVelocity;
            rb.linearVelocity = new Vector3(velocity.x, jumpSpeed, velocity.z);
            Jumped?.Invoke(this);
            return true;
        }

        /// <summary>Glissade vers l'avant (ou vers la direction d'entrée) : étourdit l'adversaire touché et pousse le ballon.</summary>
        public bool Tackle()
        {
            if (!CanAct || tackleCooldownTimer > 0f)
                return false;

            CancelCharge();
            tackleDirection = moveInput.sqrMagnitude > 0.01f ? moveInput.normalized : FlatForward();
            tackleTimer = tackleDuration;
            tackleCooldownTimer = tackleCooldown;
            tackleHitBall = false;
            tackleVictims.Clear();
            TackleStarted?.Invoke(this);
            return true;
        }

        /// <summary>Vrai si le ballon est actuellement dans la zone de frappe.</summary>
        public bool CanKickBall() => FindBallInKickRange() != null;

        // ------------------------------------------------------------------ API du MatchManager

        /// <summary>Téléporte le joueur et remet tout son état à zéro (coup d'envoi).</summary>
        public void ResetState(Vector3 position, Quaternion rotation)
        {
            moveInput = Vector3.zero;
            CancelCharge();
            shotCooldownTimer = tackleTimer = tackleCooldownTimer = stunTimer = jumpCooldownTimer = 0f;
            rb.position = position;
            rb.rotation = rotation;
            transform.SetPositionAndRotation(position, rotation);
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        public void SetFrozen(bool frozen)
        {
            IsFrozen = frozen;
            if (!frozen)
                return;
            CancelCharge();
            moveInput = Vector3.zero;
            tackleTimer = 0f;
            rb.linearVelocity = new Vector3(0f, rb.linearVelocity.y, 0f);
        }

        /// <summary>Appelé par le tacleur quand ce joueur est touché.</summary>
        public void ReceiveTackle(PlayerController tackler, Vector3 direction, float duration)
        {
            if (IsFrozen)
                return;

            CancelCharge();
            tackleTimer = 0f;
            stunTimer = duration;
            Vector3 knockback = Flat(direction).normalized * knockbackSpeed;
            rb.linearVelocity = new Vector3(knockback.x, rb.linearVelocity.y, knockback.z);
            Stunned?.Invoke(this);
        }

        // ------------------------------------------------------------------ Simulation

        void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            TickTimers(dt);
            UpdateGrounded();

            if (IsTackling)
                UpdateTackle();
            else
                UpdateMovement(dt);

            UpdateRotation(dt);
        }

        void TickTimers(float dt)
        {
            if (IsCharging)
                chargeTimer += dt;
            shotCooldownTimer = Mathf.Max(0f, shotCooldownTimer - dt);
            tackleCooldownTimer = Mathf.Max(0f, tackleCooldownTimer - dt);
            stunTimer = Mathf.Max(0f, stunTimer - dt);
            jumpCooldownTimer = Mathf.Max(0f, jumpCooldownTimer - dt);
            tackleTimer = Mathf.Max(0f, tackleTimer - dt);
        }

        void UpdateGrounded()
        {
            // Petite sphère lancée sous les pieds (la capsule du joueur, chevauchée au départ, est ignorée).
            const float probeRadius = 0.06f;
            const float probeLift = 0.02f;
            IsGrounded = Physics.SphereCast(rb.position + Vector3.up * (probeRadius + probeLift), probeRadius,
                Vector3.down, out _, probeLift + 0.04f, groundMask, QueryTriggerInteraction.Ignore);
        }

        void UpdateMovement(float dt)
        {
            Vector3 desired = Vector3.zero;
            if (!IsFrozen && !IsStunned)
                desired = moveInput * (moveSpeed * (IsCharging ? chargingMoveMultiplier : 1f));

            Vector3 velocity = rb.linearVelocity;
            Vector3 horizontal = Flat(velocity);
            float rate = (desired.sqrMagnitude > 1e-4f ? acceleration : deceleration) * (IsGrounded ? 1f : airControl);
            horizontal = Vector3.MoveTowards(horizontal, desired, rate * dt);
            rb.linearVelocity = new Vector3(horizontal.x, velocity.y, horizontal.z);
        }

        void UpdateTackle()
        {
            // Vitesse maximale au début de la glissade, qui retombe linéairement.
            float remaining = tackleTimer / tackleDuration;
            Vector3 velocity = tackleDirection * (tackleSpeed * remaining);
            rb.linearVelocity = new Vector3(velocity.x, rb.linearVelocity.y, velocity.z);
            ResolveTackleHits();
        }

        void ResolveTackleHits()
        {
            Vector3 center = rb.position + tackleDirection * (tackleReach * 0.5f) + Vector3.up * footHeight;
            int count = Physics.OverlapSphereNonAlloc(center, tackleReach, overlapBuffer, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                Rigidbody body = overlapBuffer[i].attachedRigidbody;
                if (!body || body == rb)
                    continue;

                if (body.TryGetComponent(out SoccerBall ball))
                {
                    if (tackleHitBall)
                        continue;
                    tackleHitBall = true;
                    Vector3 pokeDirection = (tackleDirection + Vector3.up * 0.1f).normalized;
                    ball.Kick(pokeDirection * tackleBallSpeed, Vector3.zero, this);
                }
                else if (body.TryGetComponent(out PlayerController other)
                         && !tackleVictims.Contains(other)
                         && (friendlyTackles || other.Team != team)
                         && !other.IsStunned)
                {
                    tackleVictims.Add(other);
                    other.ReceiveTackle(this, tackleDirection, stunDuration);
                    TackleHit?.Invoke(this, other);
                }
            }
        }

        void UpdateRotation(float dt)
        {
            if (IsFrozen || IsStunned)
                return;

            Vector3 look = IsTackling ? tackleDirection : moveInput;
            if (look.sqrMagnitude < 0.0025f)
                return;

            Quaternion target = Quaternion.LookRotation(look.normalized, Vector3.up);
            float t = 1f - Mathf.Exp(-turnSharpness * dt);
            rb.MoveRotation(Quaternion.Slerp(rb.rotation, target, t));
        }

        SoccerBall FindBallInKickRange()
        {
            Vector3 forward = FlatForward();
            Vector3 center = rb.position + forward * (kickReach * 0.5f) + Vector3.up * footHeight;
            int count = Physics.OverlapSphereNonAlloc(center, kickReach, overlapBuffer, ~0, QueryTriggerInteraction.Ignore);

            SoccerBall best = null;
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                Rigidbody body = overlapBuffer[i].attachedRigidbody;
                if (!body || !body.TryGetComponent(out SoccerBall ball))
                    continue;

                Vector3 toBall = Flat(ball.Position - rb.position);
                if (toBall.sqrMagnitude > 1e-6f && Vector3.Angle(forward, toBall) > kickHalfAngle)
                    continue;

                float distance = toBall.sqrMagnitude;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = ball;
                }
            }
            return best;
        }

        Vector3 FlatForward()
        {
            Vector3 forward = Flat(transform.forward);
            return forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
        }

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        void OnDrawGizmosSelected()
        {
            Vector3 forward = FlatForward();
            Vector3 feet = transform.position + Vector3.up * footHeight;
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(feet + forward * (kickReach * 0.5f), kickReach);
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(feet + forward * (tackleReach * 0.5f), tackleReach);
        }
    }
}
