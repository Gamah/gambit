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
/// <para><b>A desktop layout on a quad is a staging post, not the destination.</b> Squared onto the
/// board it is readable and the beam can click it, but it is still a 1920x1080 screen with a
/// crosshair on it, and it still covers the board it is centred on. It stands until the UI is
/// something built to be pointed at.</para>
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

	/// <summary>How far out the quad hangs, in world units. With <see cref="RenderScale"/> it sets
	/// how much of the view the HUD covers: the quad is <c>PanelSize.x * 0.05 * RenderScale</c>
	/// units wide (the engine's <c>ScenePanelObject.ScreenToWorldScale</c>), and that width at this
	/// distance is the angle it subtends.
	///
	/// <para><b>Seated, this must land SHORT of the board</b>, which is why it is not a free number:
	/// a quad centred on the board direction at more than <c>ChessRing.SeatOrbitRadius</c> (56) sits
	/// past the board centre and is buried in the tabletop, panels and all. Short of it, the quad
	/// floats between the eye and the board with the board behind its middle.</para></summary>
	[Property] public float Distance { get; set; } = 46f;

	/// <summary>How far the quad is lifted above the point it hangs in front of. Zero puts it on
	/// the view direction itself, which seated means centred on the board; lift it from inside the
	/// headset (<c>gambit_vr_hud_height</c>) to clear the pieces at the cost of that centring.</summary>
	[Property] public float Height { get; set; } = 0f;

	/// <summary>World units per panel pixel, on top of the engine's 0.05.
	///
	/// <para><b>Paired with <see cref="Distance"/>:</b> what the player sees is the ratio of the
	/// two, so the quad has to be scaled down by whatever the distance was pulled in by or the
	/// panel grows in the view. Tune either from the headset and the other follows by
	/// arithmetic.</para></summary>
	[Property] public float RenderScale { get; set; } = 0.76f;

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
	/// Both placements are in world space, off the body rather than the head: squared onto the
	/// board the player is sitting at, or ahead of the body while roaming.</para>
	///
	/// <para>Seated, the quad takes the seat anchor's whole direction, pitch included — the anchor
	/// aims at the board centre, so the quad's centre is the board's and the panels laid out
	/// against a 1920x1080 screen sit around it the way they do on the monitor. <see cref="Height"/>
	/// lifts it off the pieces from there, at the cost of that centring. Roaming, only yaw is used:
	/// a quad that pitched with the look would swing while walking.</para>
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
		// disagree about it. Seated, the FULL seat-anchor direction is taken rather than its yaw:
		// that direction is the one the anchor was built to aim at the board centre, so the quad
		// comes out centred on the board exactly as the desktop screen is — which is what the
		// 1920x1080 layout on it was composed against. Flattening it instead hangs a quad level
		// with the eye while the board is well below, and no Height offset can centre that, only
		// raise or lower a panel the board is never behind.
		if ( !VrRig.ViewAnchor( _controller, CameraObject, out var from, out var forward,
			flattenSeated: false ) ) return;

		Host.WorldPosition = from + forward * Distance + Vector3.Up * Height;
		Host.WorldRotation = Rotation.LookAt( -forward, Vector3.Up );
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

	/// <summary>World units per panel pixel: how big the HUD is at its distance.</summary>
	[ConCmd( "gambit_vr_hud_scale" )]
	public static void SetScale( float scale )
	{
		if ( Instance == null ) { Log.Info( "[Gambit] vr: no HUD (not running in VR?)" ); return; }

		Instance.RenderScale = scale;
		Log.Info( $"[Gambit] vr: HUD scale {scale}" );
	}
}
