using System;
using Sandbox;

namespace Gambit.World;

/// <summary>
/// The table's cursor/aim button (P99): a small flat plate in the near-LEFT corner of the
/// tabletop, reading "ENABLE CURSOR" while look aim owns the mouse and "ENABLE AIM" once it
/// doesn't. One control, both directions.
///
/// <para><b>Why it exists.</b> Look aim hides the pointer, so the way back to it can't be a
/// panel button — there would be nothing to click it with. Escape is the keyboard way out
/// (<see cref="LobbyPlayer"/>), and this is the way BACK IN, plus a mouse-only route in both
/// directions for a player who never learns the key. It is reachable in either mode because
/// the pick comes from <see cref="SeatAim.PickPixel"/>: the pointer when there is one, the
/// centre of the screen when there isn't.</para>
///
/// <para><b>It picks itself with plane math, not panel input.</b> The engine does have a
/// ray-driven route into world panels (<c>WorldInput</c> + <c>ChildrenWantMouseInput</c>),
/// and if a second world-space control ever appears that is the thing to reach for. This one
/// tests a rectangle on the tabletop plane instead, for two reasons: it is exactly what
/// <see cref="ChessBoardView.SquareUnderCursor"/> already does two feet away on the same
/// surface (so the board and the button cannot answer differently about where the player is
/// pointing), and no sibling project here has ever used WorldInput — this host cannot render,
/// so an unproven input path is a bug that ships. The panel stays a one-string label with
/// <c>pointer-events: none</c>, exactly like the clock face.</para>
///
/// <para><b>Local, never networked</b> — same rule as the board view and the clock. The plate
/// is only ever shown to the one client seated in <see cref="Seat"/> at this table; nobody
/// else's table grows a button because you sat down.</para>
/// </summary>
public sealed class AimToggleButton : Component
{
	/// <summary>The two labels, and the ONE place they are written. <see cref="ChessRing"/>
	/// sizes the plate's text off the longer of them (see its AimMaxChars), so a reworded
	/// label re-sizes itself instead of overflowing a plate tuned on the other string.</summary>
	public const string CursorLabel = "ENABLE CURSOR";
	public const string AimLabel = "ENABLE AIM";

	/// <summary>The table this plate belongs to.</summary>
	[Property] public ChessStation Station { get; set; }

	/// <summary>Which seat it serves. The plate sits at THAT seat's near-left corner and is
	/// shown to nobody else — the other seat has its own.</summary>
	[Property] public ChessSeat Seat { get; set; }

	/// <summary>The plate GameObject, toggled as one. The DRIVER lives on the station (not on
	/// this), for two reasons at once: the station is what every client actually receives, and
	/// disabling the GameObject a component lives on stops it updating — a button that hid
	/// itself could never show itself again.</summary>
	public GameObject Visuals { get; private set; }

	/// <summary>The one-string label. Reused from the clock face rather than reinvented:
	/// same contract (one string, no siblings), same stylesheet, same nowrap/flex-shrink.</summary>
	public Gambit.UI.TableClockTextPanel Label { get; set; }

	/// <summary>The plate mesh, tinted on hover — the only feedback there is that the thing
	/// under your crosshair is a button.</summary>
	public ModelRenderer Plate { get; set; }

	/// <summary>Plate TOP surface height, station-local (already × TableScale). The pick
	/// plane, and the one number that must match what was built — it comes from ChessRing
	/// rather than being measured here.</summary>
	public float SurfaceZ { get; set; }

	/// <summary>Plate centre in the tabletop plane, station-local.</summary>
	public Vector2 Center { get; set; }

	/// <summary>Half its extent in X and Y, station-local. The hit rectangle is exactly the
	/// plate: nothing invisible around it, so what you can click is what you can see.</summary>
	public Vector2 HalfExtent { get; set; }

	bool _hovered;

	/// <summary>
	/// Build this seat's plate, once, client-locally — and throw away anything that came over
	/// the wire first. <see cref="StationChair.EnsureChair"/>'s shape exactly, including the
	/// find-then-destroy (a foreach would mutate the collection it is walking).
	///
	/// <para>The plate is deliberately NOT networked: it is shown only to the player in this
	/// seat, so its enabled state is a different answer on every machine — see
	/// <see cref="ChessRing.BuildAimToggleView"/>.</para>
	/// </summary>
	bool EnsureVisuals( ChessRing ring )
	{
		if ( Visuals.IsValid() ) return true;

		string name = ChessRing.AimToggleName( Seat );
		GameObject.Children.Find( c => c.Name == name )?.Destroy();

		Visuals = ring.BuildAimToggleView( GameObject, Seat, this );
		return Visuals.IsValid();
	}

	protected override void OnUpdate()
	{
		bool show = ShouldShow();

		// Built the first time it is actually WANTED, not at ring-build time. A plate exists
		// for both seats of every table in the ring, and all but one of them is hidden all of
		// the time — building 2N of them (each with its own WorldPanel) up front to draw none
		// is a cost with no moment where it pays. The clock builds eagerly because a clock is
		// always on show; this isn't.
		if ( !show )
		{
			if ( Visuals.IsValid() && Visuals.Enabled ) Visuals.Enabled = false;
			_hovered = false;
			return;
		}

		var ring = ChessRing.Instance;
		if ( ring == null || !EnsureVisuals( ring ) ) return;
		if ( !Visuals.Enabled ) Visuals.Enabled = true;

		_hovered = HitTest();

		// The label says what the click WILL do, not what mode you are in. "Enable cursor"
		// while aiming, "Enable aim" once the cursor is out — the flip is the whole reason
		// this is one plate and not two.
		if ( Label.IsValid() )
		{
			Label.Text = SeatAim.Aiming ? CursorLabel : AimLabel;
			Label.State = _hovered ? "on" : "";
		}

		if ( Plate.IsValid() )
			Plate.Tint = _hovered ? ChessRing.ClockPlateOnColor : ChessRing.ClockPlateColor;

		if ( _hovered && Input.Pressed( "Select" ) )
			SeatAim.Toggle();
	}

	/// <summary>Drawn only for the player it belongs to, only while there is a mode to
	/// switch between: the setting on, us in this seat, and a game actually PLAYING (which
	/// is the only time look aim is ever engaged — see <see cref="SeatAim"/>).
	///
	/// <para>Hidden while a modal has forced the cursor out, because in that moment the
	/// button cannot do what its label promises: aim resumes when the picker is answered,
	/// not when you click a plate. A control that visibly does nothing is worse than no
	/// control, and this one is gone for about as long as it takes to pick a queen.</para></summary>
	bool ShouldShow()
	{
		if ( Station == null ) return false;
		if ( ChessStation.Active != Station || ChessStation.ActiveSeat != Seat ) return false;
		if ( !SeatAim.Enabled || SeatAim.ModalOpen ) return false;

		var src = Gambit.Game.BoardGame.Source(
			Gambit.Game.LocalGameController.For( Station ),
			Gambit.Game.LichessGameController.For( Station ) );
		return src?.Playing ?? false;
	}

	/// <summary>Is the player pointing at the plate? The pick ray projected onto the plate's
	/// own top surface, in station-local space — <see cref="ChessBoardView.SquareUnderCursor"/>'s
	/// arithmetic against a rectangle instead of a grid, and deliberately the same shape so
	/// the two never drift.</summary>
	bool HitTest()
	{
		var camera = Scene?.Camera;
		if ( camera == null || Station == null ) return false;

		var ray = camera.ScreenPixelToRay( SeatAim.PickPixel() );
		var origin = Station.GameObject.WorldTransform.PointToLocal( ray.Position );
		var dir = Station.GameObject.WorldRotation.Inverse * ray.Forward;

		if ( MathF.Abs( dir.z ) < 0.0001f ) return false;
		float t = ( SurfaceZ - origin.z ) / dir.z;
		if ( t <= 0f ) return false;

		var local = origin + dir * t;
		return MathF.Abs( local.x - Center.x ) <= HalfExtent.x
			&& MathF.Abs( local.y - Center.y ) <= HalfExtent.y;
	}
}
