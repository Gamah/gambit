using Sandbox;

namespace Gambit.World;

/// <summary>
/// The table's cursor/aim button (P99): one plate floating above the middle of the clock, in
/// the clock's own tilted plane, reading "USE CURSOR" while look aim owns the mouse and "USE
/// AIM" once it doesn't. One control, both directions.
///
/// <para><b>Why it exists.</b> Look aim hides the pointer, so the way back to it can't be a
/// panel button — there would be nothing to click it with. Escape is the keyboard way out
/// (<see cref="LobbyPlayer"/>), and this is the way BACK IN, plus a mouse-only route in both
/// directions for a player who never learns the key. It is reachable in either mode because
/// the pick comes from <see cref="SeatAim.PickPixel"/>: the pointer when there is one, the
/// centre of the screen (where the crosshair is) when there isn't.</para>
///
/// <para><b>Why over the clock.</b> It hangs where the player is already looking, in the one
/// facing this table has that both seats can read (the clock's whole argument for being at
/// −Y), which is also what makes ONE plate enough where the tabletop corner it started in
/// needed two — a corner is a different world axis for each seat. See ChessRing's banner
/// section for the geometry and for why it floats instead of standing in the gap between the
/// two dials.</para>
///
/// <para><b>It picks itself with plane math, not panel input.</b> The engine does have a
/// ray-driven route into world panels (<c>WorldInput</c> + <c>ChildrenWantMouseInput</c>),
/// and if a second world-space control ever appears that is the thing to reach for. No
/// sibling project here has ever used it, and this host cannot render, so an unproven input
/// path is a bug that ships. Instead the ray is tested against the plate's rectangle by
/// <see cref="SeatAim.PlateHit"/> — Sandbox-free precisely so the tilted-plane arithmetic can
/// be RUN here rather than reviewed. The panel stays a one-string label with
/// <c>pointer-events: none</c>, exactly like the clock face.</para>
///
/// <para><b>Local, never networked</b> — same rule as the board view and the clock. The plate
/// is only ever shown to the one client seated at this table; nobody else's table grows a
/// button because you sat down.</para>
/// </summary>
public sealed class AimToggleButton : Component
{
	/// <summary>The two labels, and the ONE place they are written. <see cref="ChessRing"/>
	/// sizes the plate's text off the longer of them (see its AimMaxChars), so a reworded
	/// label re-sizes itself instead of overflowing a plate tuned on the other string.
	///
	/// <para>Kept SHORT for a geometric reason, not a stylistic one: the banner's text height
	/// falls out of its length divided by the character count, so every character costs
	/// legibility on a plate this size. "ENABLE CURSOR" rendered about a quarter of a clock
	/// digit; these render about 0.4 of one.</para></summary>
	public const string CursorLabel = "USE CURSOR";
	public const string AimLabel = "USE AIM";

	/// <summary>The table this plate belongs to.</summary>
	[Property] public ChessStation Station { get; set; }

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

	/// <summary>The plate's geometry, station-local and already × TableScale — handed over by
	/// ChessRing from the numbers that BUILT it rather than measured here, so the rectangle
	/// the player can click is exactly the one they can see. X is always 0 (dead centre).
	/// The hit area is exactly the plate: nothing invisible around it.</summary>
	public float CenterY { get; set; }
	public float CenterZ { get; set; }

	/// <summary>Half the plate's thickness: the pick lands on the FRONT FACE, not the
	/// mid-plane.</summary>
	public float FaceOutset { get; set; }

	/// <summary>The plate's pitch — the clock's, since it shares that plane.</summary>
	public float TiltDegrees { get; set; }

	public float HalfLength { get; set; }
	public float HalfHeight { get; set; }

	bool _hovered;

	/// <summary>
	/// Build the plate, once, client-locally — and throw away anything that came over the
	/// wire first. <see cref="StationChair.EnsureChair"/>'s shape exactly, including the
	/// find-then-destroy (a foreach would mutate the collection it is walking).
	///
	/// <para>The plate is deliberately NOT networked: it is shown only to the player seated
	/// here, so its enabled state is a different answer on every machine — see
	/// <see cref="ChessRing.BuildAimToggleView"/>.</para>
	/// </summary>
	bool EnsureVisuals( ChessRing ring )
	{
		if ( Visuals.IsValid() ) return true;

		GameObject.Children.Find( c => c.Name == ChessRing.AimToggleName )?.Destroy();

		Visuals = ring.BuildAimToggleView( GameObject, this );
		return Visuals.IsValid();
	}

	protected override void OnUpdate()
	{
		bool show = ShouldShow();

		// Built the first time it is actually WANTED, not at ring-build time. A plate exists
		// for every table in the ring and all but one of them is hidden all of the time —
		// building N of them (each with its own WorldPanel) up front to draw none is a cost
		// with no moment where it pays. The clock builds eagerly because a clock is always on
		// show; this isn't.
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

		// The label says what the click WILL do, not what mode you are in: "use cursor" while
		// aiming, "use aim" once the cursor is out — the flip is the whole reason this is one
		// control and not two.
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
	/// switch between: the setting on, us seated at THIS table (either seat — one banner
	/// serves both), and a game actually PLAYING (which is the only time look aim is ever
	/// engaged — see <see cref="SeatAim"/>).
	///
	/// <para>Hidden while a modal has forced the cursor out, because in that moment the
	/// button cannot do what its label promises: aim resumes when the picker is answered,
	/// not when you click a plate. A control that visibly does nothing is worse than no
	/// control, and this one is gone for about as long as it takes to pick a queen.</para></summary>
	bool ShouldShow()
	{
		if ( Station == null ) return false;
		if ( ChessStation.Active != Station ) return false;
		if ( !SeatAim.Enabled || SeatAim.ModalOpen ) return false;

		var src = Gambit.Game.BoardGame.Source(
			Gambit.Game.LocalGameController.For( Station ),
			Gambit.Game.LichessGameController.For( Station ) );
		return src?.Playing ?? false;
	}

	/// <summary>Is the player pointing at the plate? The pick ray taken into station-local
	/// space and handed to <see cref="SeatAim.PlateHit"/>, which owns the arithmetic — this
	/// method does the engine half (a camera, a ray, a transform) and nothing else, so the
	/// half that can be wrong is the half the harness runs.</summary>
	bool HitTest()
	{
		var camera = Scene?.Camera;
		if ( camera == null || Station == null ) return false;

		var ray = camera.ScreenPixelToRay( SeatAim.PickPixel() );
		var origin = Station.GameObject.WorldTransform.PointToLocal( ray.Position );
		var dir = Station.GameObject.WorldRotation.Inverse * ray.Forward;

		return SeatAim.PlateHit(
			origin.x, origin.y, origin.z,
			dir.x, dir.y, dir.z,
			CenterY, CenterZ, FaceOutset, TiltDegrees,
			HalfLength, HalfHeight );
	}
}
