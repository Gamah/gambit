using System;
using System.Collections.Generic;
using Sandbox;

namespace Gambit.World;

/// <summary>
/// Draws the lobby's screen UI on one quad hung in the room in front of the player.
///
/// <para><b>A screen panel cannot work in a stereo view.</b> <c>GameRootPanel.IsWorldPanel</c> is
/// false, so it composites flat into each eye at identical screen coordinates — no disparity, so
/// it reads as infinitely far away while it covers things an arm's length off. On top of that
/// <c>GameRootPanel.UpdateScale</c> forces <c>Scale = 2.33</c> in high-quality VR, and a panel
/// anchored to a screen edge at that scale lands outside the lens-visible part of the eye render.
/// Both facts are in the engine's <c>ScreenPanel.cs</c>; neither is tunable from CSS.</para>
///
/// <para><b>So the root panel is swapped, not the panels.</b> This takes the GameObject the lobby's
/// panels already live on, destroys its <c>ScreenPanel</c> and gives it a <c>WorldPanel</c>, which
/// keeps every panel, every stylesheet and every <c>Ensure*</c> attach point in
/// <see cref="LobbyPlayer"/> exactly as the desktop has them. Panel pixels become panel pixels on a
/// quad, so edge-anchored rows sit on the quad's own edges instead of in the periphery.</para>
///
/// <para><b>A desktop layout on a quad is a staging post, not the destination.</b> Hung over the
/// board it is readable and the beam can click it, but it is still a 1920x1080 screen with a
/// crosshair on it, and at a size big enough to read it covers most of the view. It stands until
/// the UI is something built to be pointed at.</para>
/// </summary>
public sealed class VrHud : Component
{
	public static VrHud Instance { get; private set; }

	/// <summary>The headset camera, handed over by <see cref="LobbyPlayer"/>.</summary>
	public GameObject CameraObject { get; set; }

	/// <summary>The GameObject carrying the lobby's panels — the scene's UI object, with a world
	/// panel in place of its screen panel. <see cref="LobbyPlayer"/> attaches its runtime panels
	/// here, so it must keep working after the swap.</summary>
	public GameObject Host { get; private set; }

	/// <summary>How far out the quad hangs, in world units. With <see cref="Size"/> it sets how much
	/// of the view the HUD covers.
	///
	/// <para><b>Seated, this must land SHORT of the board</b>, which is why it is not a free number:
	/// the seat's eye anchor is about 39 units from the board centre
	/// (<c>ChessRing.SeatEyeBack</c> back and <c>SeatEyeHeight</c> above it), and a quad hung
	/// further along that direction is buried in the tabletop, panels and all. Short of it, the
	/// quad floats between the eye and the board with the board behind its middle.</para></summary>
	[Property] public float Distance { get; set; } = 30f;

	/// <summary>How far the quad is lifted above the point it hangs in front of — what puts it
	/// OVER the board rather than across it. Zero puts it on the view direction itself, which
	/// seated means centred on the board.</summary>
	[Property] public float Height { get; set; } = 14f;

	/// <summary>How much of the seated view's downward pitch the quad takes: 1 squares it onto the
	/// board, 0 stands it upright.
	///
	/// <para><b>Fully squared onto the board is a panel lying back towards the ceiling.</b> The
	/// seat's eye anchor looks about 30° down at the board, so a quad normal to it is tilted that
	/// far back; readable, but it reads as a panel on the floor rather than one in front of you.
	/// The pitch is worth keeping some of — it is what aims the quad at the thing the player is
	/// looking at — so this is a blend and not a flag.</para></summary>
	[Property, Range( 0f, 1f )] public float PitchBlend { get; set; } = 0.4f;

	/// <summary>The quad's size in the world, as the host object's scale.
	///
	/// <para><b>This is the only knob that changes how big the quad is</b>: its world width is
	/// <c>PanelSize.x * 0.05 * Size</c> (the engine's <c>ScenePanelObject.ScreenToWorldScale</c>),
	/// and with <see cref="Distance"/> that width is the angle it subtends.</para></summary>
	[Property] public float Size { get; set; } = 0.7f;

	/// <summary>The engine's panel-space divisor, which is <b>not</b> a size: <c>WorldPanel</c>
	/// multiplies the quad's transform by it and divides the layout rect by it, so the world size
	/// cancels out and only the layout space moves — at 0.76 a 1920-wide panel lays out in 2526
	/// px and every row in it is drawn smaller
	/// (<c>engine/Sandbox.Engine/Scene/Components/UI/WorldPanel.cs</c>, <c>OnPreRender</c>;
	/// <b>[SOURCE]</b> — read off the implementation, not documented). It is 1 so that
	/// <see cref="PanelSize"/> is the layout space the desktop's stylesheets were written
	/// against.</summary>
	[Property] public float RenderScale { get; set; } = 1f;

	/// <summary>The quad's pixel size, which is the layout space every panel's CSS is written
	/// against. 1920x1080 is what the desktop's <c>ConsistentHeight</c> screen panel gives them, so
	/// the same stylesheets lay out the same way here.</summary>
	[Property] public Vector2 PanelSize { get; set; } = new Vector2( 1920f, 1080f );

	WorldPanel _panel;
	PlayerController _controller;

	/// <summary>Where a hidden HUD waits: far enough below the room that nothing sees it, close
	/// enough that its coordinates stay sane.</summary>
	static readonly Vector3 ParkedAt = new( 0f, 0f, -20000f );

	/// <summary>Whether the desktop screen UI is drawn at all in VR.
	///
	/// <para><b>On, because it is the only way to drive a game.</b> It is a 1920x1080 screen layout
	/// with edge-anchored rows, a crosshair and keyboard hints — not a VR UI, and no amount of
	/// scaling makes it one — but every control a player needs to start a game, offer a draw or
	/// stand up lives on it, and the beam can click it. <c>gambit_vr_hud</c> parks it.</para></summary>
	public static bool Shown { get; private set; } = true;

	/// <summary>Move the lobby's panels off the screen and onto a quad. Called before
	/// <see cref="LobbyPlayer"/>'s runtime panels attach, so they are built against the world panel
	/// rather than re-parented afterwards.
	///
	/// <para>The screen panel is destroyed rather than disabled, which leaves the object with
	/// exactly one root panel and takes the screen pass out of every camera's UI render — the
	/// companion camera's included, so the HUD is not also painted flat over the monitor. The
	/// authored component goes at runtime only; the scene asset is untouched.</para>
	///
	/// <para>Panels already built are rebuilt by toggling them, which is what re-parents them: a
	/// panel whose parent was deleted does not re-home itself, because <c>EnsureParentPanel</c>
	/// only acts on a null parent.</para></summary>
	public void Adopt( ScreenPanel screen )
	{
		if ( Host.IsValid() || !screen.IsValid() ) return;

		Host = screen.GameObject;
		Instance = this;
		_controller = Components.Get<PlayerController>();

		screen.Destroy();

		_panel = Host.AddComponent<WorldPanel>();
		_panel.PanelSize = PanelSize;
		_panel.RenderScale = RenderScale;
		Host.WorldScale = Size;
		_panel.LookAtCamera = false; // Place() owns the rotation, in the same frame as the position

		Rehome();
		Place();
	}

	/// <summary>Rebuild every panel on the host so each re-parents to the root that is live now. A
	/// panel whose parent was deleted does not re-home itself — <c>EnsureParentPanel</c> acts only
	/// on a null parent — so the toggle is the mechanism, not a nudge.
	///
	/// <para>The list is copied first: rebuilding mutates the component list being walked.</para></summary>
	void Rehome()
	{
		if ( !Host.IsValid() ) return;

		foreach ( var c in new List<PanelComponent>( Host.Components.GetAll<PanelComponent>() ) )
		{
			if ( !c.IsValid() ) continue;
			c.Enabled = false;
			c.Enabled = true;
		}
	}

	/// <summary>Draw the adopted desktop UI, or don't.</summary>
	[ConCmd( "gambit_vr_hud" )]
	public static void ToggleHud()
	{
		Shown = !Shown;
		Log.Info( $"[Gambit] vr: desktop HUD {(Shown ? "ON" : "OFF")}" );
	}

	protected override void OnDestroy()
	{
		if ( Instance == this ) Instance = null;
	}

	protected override void OnUpdate() => Place();
	protected override void OnPreRender() => Place();

	/// <summary>Hang the quad in the world, in front of the player but never on their head.
	///
	/// <para><b>Nothing here follows head rotation.</b> A panel welded to the view turns with every
	/// glance, which makes it impossible to look away from and impossible to read while moving.
	/// Both placements are in world space, off the body rather than the head: over the board the
	/// player is sitting at, or ahead of the body while roaming.</para>
	///
	/// <para>Seated, the quad hangs along the seat's eye direction, which aims at the board centre,
	/// with <see cref="PitchBlend"/> rotating it back towards upright and <see cref="Height"/>
	/// lifting it above the pieces. Roaming, the direction is already level: a quad that pitched
	/// with the look would swing while walking.</para>
	///
	/// <para>Roaming, it follows the body: position from the feet at eye height, direction from the
	/// controller's eye angles rather than the headset's. Walking and turning carry it along;
	/// turning your head does not.</para>
	///
	/// <para>The rotation is written here rather than left to <c>WorldPanel.LookAtCamera</c>, which
	/// would both reintroduce head tracking and derive it in its own <c>OnPreRender</c>, a
	/// different frame's camera from the position set here.</para></summary>
	void Place()
	{
		if ( !Host.IsValid() || !CameraObject.IsValid() ) return;

		// Re-applied so the tuning commands take effect without a re-adopt.
		if ( _panel.IsValid() ) _panel.RenderScale = RenderScale;
		Host.WorldScale = Size;

		// Hidden means PARKED, not disabled. Disabling the world panel deletes the root its panels
		// are children of, and a PanelComponent whose parent was deleted keeps building its tree
		// against it. Every panel here stays alive and attached; it is simply nowhere near the
		// player.
		if ( !Shown )
		{
			Host.WorldPosition = ParkedAt;
			return;
		}

		// VrRig owns where "in front of the player" is, so this and the state board cannot
		// disagree about it. Seated, the UNFLATTENED direction is asked for: it aims at the board
		// centre, which is the only direction that puts the board behind the quad rather than
		// below it. How much of that aim the quad keeps is PitchBlend's, below — taken here and
		// flattened there, because the flattening has to happen after the pitch is known.
		if ( !VrRig.ViewAnchor( _controller, CameraObject, out var from, out var forward,
			flattenSeated: false ) ) return;

		forward = BlendPitch( forward, PitchBlend );

		Host.WorldPosition = from + forward * Distance + Vector3.Up * Height;
		Host.WorldRotation = Rotation.LookAt( -forward, Vector3.Up );
	}

	/// <summary>Rotate a direction towards horizontal, keeping its yaw. <paramref name="blend"/> 1
	/// returns it untouched; 0 flattens it.
	///
	/// <para>Position and rotation both take the blended direction, so the quad is always square
	/// to the player and <see cref="Height"/> alone decides how far above the board it hangs. A
	/// quad positioned down the full board direction but faced horizontally would be read at the
	/// same oblique angle this is here to remove.</para>
	///
	/// <para>A straight-up or straight-down direction has no yaw to keep, so it is returned as it
	/// came rather than snapped to an arbitrary one.</para></summary>
	static Vector3 BlendPitch( Vector3 forward, float blend )
	{
		var flat = forward.WithZ( 0f );
		if ( flat.LengthSquared < 0.0001f ) return forward;

		flat = flat.Normal;
		float pitch = MathF.Asin( Math.Clamp( forward.z, -1f, 1f ) ) * blend;
		return flat * MathF.Cos( pitch ) + Vector3.Up * MathF.Sin( pitch );
	}

	/// <summary>How far in front of the eye the HUD quad sits. Tune it from inside the headset.</summary>
	[ConCmd( "gambit_vr_hud_distance" )]
	public static void SetDistance( float units )
	{
		if ( Instance == null ) { Log.Info( "[Gambit] vr: no HUD (not running in VR?)" ); return; }

		Instance.Distance = units;
		Log.Info( $"[Gambit] vr: HUD distance {units}" );
	}

	/// <summary>How far the HUD sits above the eye it hangs in front of.</summary>
	[ConCmd( "gambit_vr_hud_height" )]
	public static void SetHeight( float units )
	{
		if ( Instance == null ) { Log.Info( "[Gambit] vr: no HUD (not running in VR?)" ); return; }

		Instance.Height = units;
		Log.Info( $"[Gambit] vr: HUD height {units}" );
	}

	/// <summary>How big the HUD quad is in the world: with the distance, how much of the view it
	/// covers.</summary>
	[ConCmd( "gambit_vr_hud_scale" )]
	public static void SetScale( float scale )
	{
		if ( Instance == null ) { Log.Info( "[Gambit] vr: no HUD (not running in VR?)" ); return; }

		Instance.Size = scale;
		Log.Info( $"[Gambit] vr: HUD scale {scale}" );
	}

	/// <summary>How much of the seated view's downward pitch the quad takes: 1 squares it onto the
	/// board, 0 stands it upright.</summary>
	[ConCmd( "gambit_vr_hud_pitch" )]
	public static void SetPitch( float blend )
	{
		if ( Instance == null ) { Log.Info( "[Gambit] vr: no HUD (not running in VR?)" ); return; }

		Instance.PitchBlend = Math.Clamp( blend, 0f, 1f );
		Log.Info( $"[Gambit] vr: HUD pitch blend {Instance.PitchBlend}" );
	}
}
