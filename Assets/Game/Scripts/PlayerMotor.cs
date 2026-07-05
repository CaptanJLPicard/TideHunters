using UnityEngine;

/// <summary>
/// Tunable movement parameters shared by owner prediction and server simulation.
/// </summary>
[System.Serializable]
public struct MotorConfig
{
    public float walkSpeed;
    public float runSpeed;
    public float swimSpeed;
    public float swimSprintSpeed;  // faster swim while holding sprint
    public float gravity;          // negative
    public float jumpHeight;       // metres
    public float turnSharpness;    // higher = snappier heading turn on land (eased, per second)

    [Header("Swim")]
    public float waterLevelY;         // world Y of the actual water surface
    public float swimEnterDepth;      // feet must sink this far below the surface before swimming starts
    public float swimFloatDepth;      // buoyancy target while treading (idle) — feet depth below the surface
    public float swimFloatDepthMoving;// buoyancy target while swimming (moving) — shallower so the
                                      // horizontal crawl body stays half-submerged at the surface
    public float swimTurnSharpness;   // eased heading turn rate while swimming

    public float groundStick;      // small downward velocity kept while grounded

    public static MotorConfig Default => new MotorConfig
    {
        walkSpeed = 1.9f,           // matched to the walk clip's stride so feet don't skate
        runSpeed = 4.6f,            // matched to the run clip's stride
        swimSpeed = 2.5f,
        swimSprintSpeed = 4.0f,     // sprint-swim
        gravity = -20f,
        jumpHeight = 1.2f,
        turnSharpness = 5.5f,       // eased land turn — smooth, readable (not too snappy)
        waterLevelY = -1.6f,        // TideHunters ocean surface (SM_Env_Ocean_Tile ≈ -1.58)
        swimEnterDepth = 0.5f,      // only swim once actually in the water
        swimFloatDepth = 1.4f,      // treading (idle): waterline around the chest, arms at the surface
        swimFloatDepthMoving = 0.9f,// swimming (moving): shallower so the crawl body rides the surface
        swimTurnSharpness = 5f,     // floatier turn while swimming
        groundStick = -3f,
    };
}

/// <summary>
/// Pure, deterministic movement step. Given the same start state + input + dt it produces the same
/// output on client and server.
///
/// Movement is "rotate-to-move" (third-person adventure style): the character eases its heading
/// toward its camera-relative movement direction and moves forwards, so pressing back turns it toward
/// the camera. While the heading is still catching up to a genuine turn it reports a lateral / backward
/// blend (a readable strafe / backpedal lean); a small dead-zone keeps straight running from leaning.
/// </summary>
public static class PlayerMotor
{
    // Lean shaping: dead-zone (deg) below which no lean is reported, then a gentle gain. Kept modest so
    // the lean reads on real turns without twitching on small course corrections.
    private const float LeanDeadzoneDeg = 6f;
    private const float LeanGain = 1.4f;

    public static StatePayload Simulate(CharacterController cc, in StatePayload prev, in InputCommand input, float dt, in MotorConfig cfg)
    {
        Transform t = cc.transform;

        bool swimming = (cfg.waterLevelY - t.position.y) > cfg.swimEnterDepth;

        // Desired planar direction (camera-relative from the input).
        Vector3 local = new Vector3(input.MoveX, 0f, input.MoveY);
        if (local.sqrMagnitude > 1f) local.Normalize();
        float moveMag = Mathf.Clamp01(local.magnitude);
        bool moving = moveMag > 0.1f;
        Vector3 worldDir = Quaternion.Euler(0f, input.Yaw, 0f) * local;

        // Eased turn toward the movement heading (fast to start, smooth to settle → no lag, no jitter).
        float yaw = prev.Yaw;
        if (moving)
        {
            float targetYaw = Mathf.Atan2(worldDir.x, worldDir.z) * Mathf.Rad2Deg;
            float sharp = swimming ? cfg.swimTurnSharpness : cfg.turnSharpness;
            yaw = Mathf.LerpAngle(prev.Yaw, targetYaw, 1f - Mathf.Exp(-sharp * dt));
        }
        t.rotation = Quaternion.Euler(0f, yaw, 0f);

        float planarSpeed = swimming
            ? (input.Sprint ? cfg.swimSprintSpeed : cfg.swimSpeed)
            : (input.Sprint ? cfg.runSpeed : cfg.walkSpeed);
        Vector3 moveDir = moving ? worldDir.normalized : Vector3.zero;
        Vector3 horizontal = moveDir * planarSpeed;

        float vertVel = prev.VerticalVelocity;
        int jumpStamp = prev.JumpStamp;

        if (swimming)
        {
            // Buoyancy: float shallower while actively swimming (crawl rides the surface), deeper while
            // treading (idle). Blend by requested movement.
            float floatDepth = Mathf.Lerp(cfg.swimFloatDepth, cfg.swimFloatDepthMoving, moveMag);
            float targetY = cfg.waterLevelY - floatDepth;
            float diff = targetY - t.position.y;
            vertVel = Mathf.Clamp(diff * 3f, -cfg.swimSpeed, cfg.swimSpeed);
            if (input.Jump) vertVel = cfg.swimSpeed; // swim up
        }
        else
        {
            if (prev.Grounded && vertVel < 0f) vertVel = cfg.groundStick;
            if (input.Jump && prev.Grounded)
            {
                vertVel = Mathf.Sqrt(2f * cfg.jumpHeight * -cfg.gravity);
                jumpStamp++;
            }
            vertVel += cfg.gravity * dt;
        }

        Vector3 motion = (horizontal + Vector3.up * vertVel) * dt;
        cc.Move(motion);

        bool grounded = cc.isGrounded;
        if (grounded && !swimming && vertVel < 0f) vertVel = cfg.groundStick;

        float speedNorm = swimming
            ? (moveMag > 0.05f ? 1f : 0f)
            : moveMag * (input.Sprint ? 1f : 0.5f);

        // Lean = movement direction expressed in the character's (still-turning) facing, with a small
        // dead-zone so only real turns lean. Eases to pure forward as the heading aligns.
        float animMoveX = 0f, animMoveY = moving ? moveMag : 0f;
        if (moving)
        {
            Vector3 localDir = Quaternion.Euler(0f, -yaw, 0f) * moveDir;
            float angDeg = Mathf.Atan2(localDir.x, localDir.z) * Mathf.Rad2Deg;
            float gated = Mathf.Sign(angDeg) * Mathf.Max(0f, Mathf.Abs(angDeg) - LeanDeadzoneDeg);
            float leanRad = Mathf.Clamp(gated * LeanGain, -175f, 175f) * Mathf.Deg2Rad;
            animMoveX = Mathf.Sin(leanRad) * moveMag;
            animMoveY = Mathf.Cos(leanRad) * moveMag;
        }

        return new StatePayload
        {
            Tick = input.Tick,
            LastProcessedInputTick = input.Tick,
            Position = t.position,
            Yaw = yaw,
            VerticalVelocity = vertVel,
            Grounded = grounded,
            IsSwimming = swimming,
            Sprinting = input.Sprint,
            MoveX = animMoveX,
            MoveY = animMoveY,
            Speed = speedNorm,
            Emote = input.Emote,
            JumpStamp = jumpStamp,
        };
    }
}
