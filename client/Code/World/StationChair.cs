using System;
using Sandbox;

namespace Gambit.World;

/// <summary>
/// What a chair DOES at runtime (M13): wears the room theme, and slides in and out as its
/// seat empties and fills. <see cref="ChessRing.BuildStationChair"/> builds the thing; this
/// is the only part of it that moves.
///
/// <para><b>Both jobs, one component, because both are the same per-frame poll on the same
/// GameObject.</b> M13 specified a tint-only <c>ChairTheme</c>, but the tuck needs an
/// OnUpdate on the chair as well, and two components racing to write one transform and one
/// tint is a worse shape than one that owns both.</para>
///
/// <para><b>Purely local, and both halves are the doctrine working rather than a
/// shortcut.</b> Stations are network-spawned so their seat occupancy replicates, and
/// everything cosmetic is rebuilt per client — so the tint is per-client BY DESIGN (your
/// room theme is yours), and the tuck costs ZERO new networking because it derives from
/// the <c>[Sync(FromHost)]</c> occupancy that is already on the wire. Every client animates
/// the same chair the same way without anyone sending anything.</para>
///
/// <para>Not ExecuteInEditor, matching <see cref="MarqueeGlow"/>: the editor preview keeps
/// the pose and tint ChessRing built, which is the pulled-out chair whose clearances are
/// the ones worth looking at.</para>
/// </summary>
public sealed class StationChair : Component
{
	/// <summary>The table this chair belongs to — read for seat occupancy only.</summary>
	[Property] public ChessStation Station { get; set; }

	/// <summary>Which seat's chair this is. Decides both the tint and which side of the
	/// table it slides on.</summary>
	[Property] public ChessSeat Seat { get; set; }

	ModelRenderer _pad;
	ModelRenderer _back;
	ModelRenderer _return;

	// Theme cache, shared by every chair in the room and refreshed when the settings
	// version bumps — MarqueeGlow's shape exactly (World/MarqueeGlow.cs), because the
	// problem is identical: a value that changes at runtime from the settings board, read
	// by many components that must not each hit PlayerData every frame.
	static int _version = -1;
	static Color _accent = Color.White;

	/// <summary>1 = tucked under the table, 0 = pulled out to the seated position.
	/// Negative until the first frame, so a chair that spawns at an occupied seat starts
	/// out rather than sliding out from under someone.</summary>
	float _tuck = -1f;

	/// <summary>Hand over the panel renderers. They are the only things themed — the frame
	/// is its seat's colour and never changes.</summary>
	public void SetPanels( GameObject pad, GameObject back, GameObject backReturn )
	{
		// Each may be null: AddBoxGo returns null when the box model is missing, and has
		// always been allowed to draw nothing.
		_pad = pad?.GetComponent<ModelRenderer>();
		_back = back?.GetComponent<ModelRenderer>();
		_return = backReturn?.GetComponent<ModelRenderer>();
	}

	protected override void OnUpdate()
	{
		var ring = ChessRing.Instance;
		if ( ring == null ) return;

		if ( _version != Gambit.UI.SettingsModel.SettingsVersion )
			RefreshTheme();

		Tint( _pad );
		Tint( _back );
		Tint( _return );

		UpdateTuck( ring );
	}

	void Tint( ModelRenderer r )
	{
		if ( r.IsValid() ) r.Tint = _accent;
	}

	/// <summary>
	/// The panels wear <see cref="Gambit.UI.WallTheme.Accent"/> — the room theme.
	///
	/// <para><b>This is a new precedent and worth naming.</b> WallTheme's own comment says
	/// "the cabinet UI is intentionally NOT themed from this"; themed world GEOMETRY has
	/// never existed here before. It is defensible on the grounds that a chair is
	/// furniture and the room theme is a room-tuning knob exactly like voice range — but
	/// without saying so, the next reader finds that comment and files the chair as a
	/// mistake.</para>
	///
	/// <para>Accent already falls back to its dark-grey default on AUTO/empty, so there is
	/// no fallback to re-implement here.</para>
	/// </summary>
	static void RefreshTheme()
	{
		_version = Gambit.UI.SettingsModel.SettingsVersion;
		_accent = Gambit.UI.WallTheme.Accent;
	}

	/// <summary>
	/// Slide toward the table when nobody is sitting here, and out again when they are.
	///
	/// <para><b>Only one direction exists, and that is not an oversight.</b> The instinct
	/// is "sitting pulls the chair out" — but at the seated position the terry's hips are
	/// already ~36 back with about 29 of reach, which just gets to the near rank at 17.06.
	/// Pull the chair out any FURTHER and the player cannot reach their own back rank. So
	/// the geometry ChessRing builds IS the pulled-out state, and tucking is the only move
	/// available.</para>
	///
	/// <para><see cref="ChessStation.SeatTaken"/> includes the local player's optimistic
	/// claim, so the chair starts sliding out the moment you press E rather than waiting a
	/// round trip on the host.</para>
	/// </summary>
	void UpdateTuck( ChessRing ring )
	{
		float target = Station.IsValid() && Station.SeatTaken( Seat ) ? 0f : 1f;

		if ( _tuck < 0f )
			_tuck = target;                     // first frame: be there, don't travel there
		else if ( ring.ChairTuckSeconds <= 0f )
			_tuck = target;
		else
			_tuck = Approach( _tuck, target, Time.Delta / ring.ChairTuckSeconds );

		// Clamped against the foot plate rather than trusted: ChairTuckInset is a
		// [Property], and a chair tucked past the table's foot is inside the table.
		//
		// MathF.Max on the ceiling, because a clamp is not automatically safe: Math.Clamp
		// THROWS when min > max, and ChairMaxTuck is derived from SeatPitch — itself a
		// [Property, Range(15, 85)]. Past ~75° the seat spot is so close to the table that
		// the max tuck goes NEGATIVE, and this line would throw every frame on every chair.
		// A degenerate chair at a degenerate camera angle is survivable; 32 exceptions a
		// frame is not.
		float inset = Math.Clamp( ring.ChairTuckInset, 0f, MathF.Max( ring.ChairMaxTuck, 0f ) );
		float side = Seat == ChessSeat.White ? -1f : +1f;

		// Smoothstep: a chair being pushed in accelerates and decelerates. Linear reads as
		// a chair on rails.
		float t = _tuck * _tuck * ( 3f - 2f * _tuck );
		LocalPosition = new Vector3( side * ( ring.ChairCenterX - inset * t ), 0f, 0f );
	}

	static float Approach( float value, float target, float step )
	{
		if ( value < target ) return value + step >= target ? target : value + step;
		if ( value > target ) return value - step <= target ? target : value - step;
		return target;
	}
}
