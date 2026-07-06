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
/// Movement is "rotate-to-move" (third-person adventure style): the character eases its heading toward
/// its camera-relative movement direction and always moves forwards along that heading, so pressing
/// back turns it toward the camera (turn rate is boosted with the size of the heading change, so hard
/// reversals pivot instead of arcing). Moving along the facing keeps the translation matched to the
/// forward locomotion clip, so the feet never skate while the heading is still catching up.
/// </summary>
public static class PlayerMotor
{
    // Extra heading-turn multiplier at a full reversal (0 aligned → 1 reversed), so about-faces pivot
    // quickly instead of arcing wide.
    private const float TurnReverseBoost = 2.5f;

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

        // Eased turn toward the movement heading. Turn faster the larger the heading change so a hard
        // reversal snaps around quickly instead of arcing wide (which would read as skating).
        float yaw = prev.Yaw;
        if (moving)
        {
            float targetYaw = Mathf.Atan2(worldDir.x, worldDir.z) * Mathf.Rad2Deg;
            float sharp = swimming ? cfg.swimTurnSharpness : cfg.turnSharpness;
            float misalign = 0.5f * (1f - Mathf.Cos(Mathf.DeltaAngle(prev.Yaw, targetYaw) * Mathf.Deg2Rad)); // 0 aligned..1 reversed
            sharp *= Mathf.Lerp(1f, TurnReverseBoost, misalign);
            yaw = Mathf.LerpAngle(prev.Yaw, targetYaw, 1f - Mathf.Exp(-sharp * dt));
        }
        t.rotation = Quaternion.Euler(0f, yaw, 0f);

        float planarSpeed = swimming
            ? (input.Sprint ? cfg.swimSprintSpeed : cfg.swimSpeed)
            : (input.Sprint ? cfg.runSpeed : cfg.walkSpeed);
        // Move along the CURRENT facing, not the raw input direction: the translation then always matches
        // the forward locomotion clip, so the feet never skate when the heading is still catching up.
        Vector3 horizontal = moving ? (t.rotation * Vector3.forward) * planarSpeed : Vector3.zero;

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

        // The body always moves forwards along its facing now, so the locomotion blend is pure forward.
        float animMoveX = 0f, animMoveY = moving ? moveMag : 0f;

        return new StatePayload
        {
            Tick = input.Tick,
            LastProcessedInputTick = input.Tick,
            Position = t.position,
            Yaw = yaw,
            AimYaw = input.Yaw,
            Pitch = input.Pitch,
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
