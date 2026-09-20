using Raylib_cs;
using System.Numerics;

namespace Mnemosyne.Viewer;

// WASD + right-mouse-look free camera. Scroll dollies, middle-mouse pans.
public sealed class FreeCamera
{
    public Vector3 Position = new(0, 100, 100);
    public float Yaw = -MathF.PI / 2;   // radians, 0 = +X
    public float Pitch = -0.5f;
    public float MoveSpeed = 60.0f;     // units/sec, scaled by zone size on load

    private const float LookSensitivity = 0.003f;

    public Vector3 Forward => new(
        MathF.Cos(Pitch) * MathF.Cos(Yaw),
        MathF.Sin(Pitch),
        MathF.Cos(Pitch) * MathF.Sin(Yaw));

    public void Update(float dt, bool inputCaptured)
    {
        if (Raylib.IsMouseButtonPressed(MouseButton.Right))
            Raylib.DisableCursor();
        if (Raylib.IsMouseButtonReleased(MouseButton.Right))
            Raylib.EnableCursor();

        if (Raylib.IsMouseButtonDown(MouseButton.Right))
        {
            var delta = Raylib.GetMouseDelta();
            Yaw += delta.X * LookSensitivity;
            Pitch = Math.Clamp(Pitch - delta.Y * LookSensitivity, -1.55f, 1.55f);
        }

        var forward = Forward;
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));

        if (!inputCaptured)
        {
            float speed = MoveSpeed * dt;
            if (Raylib.IsKeyDown(KeyboardKey.LeftShift)) speed *= 4;
            if (Raylib.IsKeyDown(KeyboardKey.LeftAlt)) speed *= 0.2f;
            if (Raylib.IsKeyDown(KeyboardKey.W)) Position += forward * speed;
            if (Raylib.IsKeyDown(KeyboardKey.S)) Position -= forward * speed;
            if (Raylib.IsKeyDown(KeyboardKey.D)) Position += right * speed;
            if (Raylib.IsKeyDown(KeyboardKey.A)) Position -= right * speed;
            if (Raylib.IsKeyDown(KeyboardKey.E) || Raylib.IsKeyDown(KeyboardKey.Space)) Position += Vector3.UnitY * speed;
            if (Raylib.IsKeyDown(KeyboardKey.Q) || Raylib.IsKeyDown(KeyboardKey.LeftControl)) Position -= Vector3.UnitY * speed;
        }

        float wheel = Raylib.GetMouseWheelMove();
        if (wheel != 0 && !inputCaptured)
            Position += forward * wheel * MoveSpeed * 0.25f;

        if (Raylib.IsMouseButtonDown(MouseButton.Middle))
        {
            var delta = Raylib.GetMouseDelta();
            var up = Vector3.Cross(right, forward);
            Position += (-right * delta.X + up * delta.Y) * MoveSpeed * 0.004f;
        }
    }

    // frame the given bounds from a 45-degree southeast vantage
    public void FrameBounds(Vector3 min, Vector3 max)
    {
        var center = (min + max) * 0.5f;
        var extent = MathF.Max(MathF.Max(max.X - min.X, max.Z - min.Z), 50);
        // cap the start distance: elongated zones (corridor dungeons) otherwise put the
        // camera so far back that the content is tiny - zooming out is easy, finding it isn't
        var dist = MathF.Min(extent, 700);
        Position = center + new Vector3(dist * 0.6f, dist * 0.75f, dist * 0.6f);
        var to = Vector3.Normalize(center - Position);
        Yaw = MathF.Atan2(to.Z, to.X);
        Pitch = MathF.Asin(to.Y);
        MoveSpeed = MathF.Max(extent * 0.1f, 30);
    }

    public Camera3D ToCamera3D() => new()
    {
        Position = Position,
        Target = Position + Forward,
        Up = Vector3.UnitY,
        FovY = 60,
        Projection = CameraProjection.Perspective,
    };
}
