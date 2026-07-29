// The P99 look-aim gate. Run it with:
//
//     PATH=$HOME/.local/share/toolchains/dotnet10:$PATH dotnet run
//
// from this folder. It compiles the REAL Code/World/SeatAim.cs (see the csproj) against the
// stand-ins below, so it fails when the state machine changes behaviour — not when the shim
// drifts from an engine it barely touches.
//
// Minimal stand-ins for the engine symbols SeatAim touches, so the STATE MACHINE can be
// run on this host. Nothing here is the engine's behaviour beyond the two facts SeatAim
// actually depends on, both read from sbox-public on 2026-07-29:
//   - Mouse.Visibility is a settable global (Visible / Auto / Hidden).
//   - Input.AnalogLook is an angular delta, and the engine ZEROES it while a cursor is
//     visible (Input.ComputeAnalogLook). The harness mirrors that: AnalogLook reads zero
//     unless the mouse is Hidden — which is what makes "aim can only accumulate in the
//     frames we own the mouse" testable rather than asserted.
namespace Sandbox;

public enum MouseVisibility { Visible, Auto, Hidden }

public static class Mouse
{
	public static MouseVisibility Visibility { get; set; } = MouseVisibility.Auto;
	public static Vector2 Position { get; set; } = new Vector2( 640, 360 );
}

public static class Screen
{
	public static float Width = 1920f;
	public static float Height = 1080f;
}

public static class Input
{
	/// <summary>What the mouse moved this frame, before the engine's cursor-visible gate.</summary>
	public static Angles RawLook = default;

	public static Angles AnalogLook =>
		Mouse.Visibility == MouseVisibility.Hidden ? RawLook : default;
}

public struct Angles
{
	public float pitch, yaw, roll;
	public Angles( float p, float y, float r ) { pitch = p; yaw = y; roll = r; }
	public static Angles operator +( Angles a, Angles b ) =>
		new Angles( a.pitch + b.pitch, a.yaw + b.yaw, a.roll + b.roll );
	public override string ToString() => $"({pitch:0.##},{yaw:0.##},{roll:0.##})";
}

public struct Vector2
{
	public float x, y;
	public Vector2( float x, float y ) { this.x = x; this.y = y; }
	public static Vector2 operator *( Vector2 v, float f ) => new Vector2( v.x * f, v.y * f );
	public override string ToString() => $"({x:0.##},{y:0.##})";
}
