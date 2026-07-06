using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Input sampled by the owner each network tick and sent to the server.
/// </summary>
public struct InputCommand : INetworkSerializable
{
    public int Tick;      // owner (client) tick this command belongs to
    public float MoveX;   // local strafe axis  (-1..1)  A/D
    public float MoveY;   // local forward axis (-1..1)  W/S
    public float Yaw;     // desired body facing (degrees, from camera)
    public float Pitch;   // look pitch (degrees) — bends the spine toward where the player looks
    public bool Sprint;
    public bool Jump;     // jump requested on this tick
    public int Emote;     // 0 = none, 1..3 = active emote id

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref Tick);
        s.SerializeValue(ref MoveX);
        s.SerializeValue(ref MoveY);
        s.SerializeValue(ref Yaw);
        s.SerializeValue(ref Pitch);
        s.SerializeValue(ref Sprint);
        s.SerializeValue(ref Jump);
        s.SerializeValue(ref Emote);
    }
}

/// <summary>
/// Authoritative simulation snapshot produced by the server (and reproduced by owner
/// prediction). Replicated to every client so remote players match the server exactly.
/// </summary>
public struct StatePayload : INetworkSerializable, IEquatable<StatePayload>
{
    public int Tick;                   // server timeline (used for remote interpolation)
    public int LastProcessedInputTick; // owner timeline (used for owner reconciliation)
    public Vector3 Position;
    public float Yaw;
    public float AimYaw;               // camera/aim yaw — spine twists toward it (offset from body Yaw)
    public float Pitch;                // look pitch, replicated so remotes bend the spine too
    public float VerticalVelocity;
    public bool Grounded;
    public bool IsSwimming;
    public bool Sprinting;             // drives faster swim-stroke playback (synced to remotes)

    // Animation-driving values (authoritative → remotes stay perfectly in sync).
    public float MoveX;
    public float MoveY;
    public float Speed;   // 0..1 (0 idle, 0.5 walk, 1 run)
    public int Emote;     // active emote id (0 = none)
    public int JumpStamp; // increments on each jump start (edge trigger for the jump anim)

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref Tick);
        s.SerializeValue(ref LastProcessedInputTick);
        s.SerializeValue(ref Position);
        s.SerializeValue(ref Yaw);
        s.SerializeValue(ref AimYaw);
        s.SerializeValue(ref Pitch);
        s.SerializeValue(ref VerticalVelocity);
        s.SerializeValue(ref Grounded);
        s.SerializeValue(ref IsSwimming);
        s.SerializeValue(ref Sprinting);
        s.SerializeValue(ref MoveX);
        s.SerializeValue(ref MoveY);
        s.SerializeValue(ref Speed);
        s.SerializeValue(ref Emote);
        s.SerializeValue(ref JumpStamp);
    }

    public bool Equals(StatePayload o) =>
        Tick == o.Tick &&
        LastProcessedInputTick == o.LastProcessedInputTick &&
        Position == o.Position &&
        Yaw == o.Yaw &&
        AimYaw == o.AimYaw &&
        Pitch == o.Pitch &&
        VerticalVelocity == o.VerticalVelocity &&
        Grounded == o.Grounded &&
        IsSwimming == o.IsSwimming &&
        Sprinting == o.Sprinting &&
        MoveX == o.MoveX &&
        MoveY == o.MoveY &&
        Speed == o.Speed &&
        Emote == o.Emote &&
        JumpStamp == o.JumpStamp;

    public override bool Equals(object obj) => obj is StatePayload o && Equals(o);
    public override int GetHashCode() => Tick;
}
