using Sandbox;

namespace Gambit.World;

/// <summary>
/// The ⚙ BOARD SETTINGS plate on the tabletop (issue #28): one flat plate in the seated
/// player's own near-left corner, carrying one string and taking a click, which opens
/// <see cref="Gambit.UI.Screens.BoardSettingsScreen"/>.
///
/// <para><b>It is a world-space control on purpose.</b> The first version was a screen-space pill
/// in the bottom-left corner of the HUD; the owner wanted it on the board. That makes it the
/// first world-space control this repo has ever shipped — P99 built two (a tabletop plate, then a
/// banner over the clock) and deleted both, because they were an extra thing to find, aim at and
/// click in the mode whose whole point is that you are not pointing at anything. What is
/// different here: this is not a control for look aim. It opens the settings, it is used with a
/// cursor, and while look aim owns the mouse the plate stops offering a click and says
/// <c>ESC FOR CURSOR</c> instead.</para>
///
/// <para><b>ONE plate, client-local, unparented.</b> Both facts are load-bearing:
/// <list type="bullet">
/// <item>ONE, moved to whichever seat the local player is in, retires P99's other objection —
/// a plate per seat plus a yaw-180 flip to keep the words unmirrored. Nobody else can see this
/// plate, so there is never a second one to place.</item>
/// <item>Client-local and parented to NOTHING. A <see cref="ChessStation"/> is NetworkSpawned, so
/// a child of one rides the host's snapshot — its transform and its enabled state included. That
/// is exactly how the music board came to render open and unstyled on joiners (SBOX-NOTES.md, issue
/// #12). Driving it in world space off the station's transform costs two lines and cannot leak.</item>
/// </list></para>
///
/// <para><b>The click needs <see cref="WorldInput"/></b> — the engine component
/// <see cref="LobbyPlayer"/> hangs on the camera, which feeds a ray into the UI system so a
/// WorldPanel with <c>pointer-events: auto</c> becomes clickable. It is the mechanism SBOX-NOTES.md
/// names as the thing to reach for instead of a third hand-rolled plane hit test. Nothing on the
/// dev host can prove it works; a plane test could not be proven either, and only one of the two
/// is the engine's own.</para>
///
/// <para>Geometry — where the plate sits and how big it is — lives in <see cref="ChessRing"/>
/// beside the margin budget it has to fit inside. This class owns lifetime, placement and state.</para>
/// </summary>
public sealed class SeatSettingsPlate : Component
{
	/// <summary>The runtime, client-local plate. Built once, on the first frame there is a ring
	/// to ask for geometry, and kept for the session.</summary>
	GameObject _plate;
	Gambit.UI.SeatSettingsPanel _panel;

	/// <summary>The build failed once, so don't try again every frame. The only way it fails is a
	/// missing box model, which is a permanent condition — retrying would spin a create/destroy
	/// pair per frame forever.</summary>
	bool _buildFailed;

	protected override void OnUpdate()
	{
		// Only for the player sitting at a chess seat, and never while the panel it opens is
		// already up — the panel has its own ✕ and would otherwise be sitting on top of its own
		// door. A wall board opens the same panel from its own button; there is no plate there.
		var station = ChessStation.Active;
		bool show = station != null && !Gambit.UI.Screens.BoardSettingsScreen.IsOpen;

		if ( !show )
		{
			if ( _plate.IsValid() ) _plate.Enabled = false;
			return;
		}

		var ring = ChessRing.Instance;
		if ( ring == null || _buildFailed ) return;
		if ( !_plate.IsValid() && !Build( ring ) ) return;

		_plate.Enabled = true;

		// Placed in WORLD space every frame, off the station's own transform. Every frame rather
		// than on a seat change, because the ring SLIDES when the admin changes the board count
		// and an unparented plate has nothing to carry it along — and because a seat switch is
		// exactly the moment this must not lag a frame behind the camera.
		var (localPos, localRot) = ring.SeatSettingsPlateLocal( ChessStation.ActiveSeat );
		_plate.WorldPosition = station.WorldPosition + station.WorldRotation * localPos;
		_plate.WorldRotation = station.WorldRotation * localRot;

		if ( !_panel.IsValid() ) return;

		// Look aim hides the pointer, so the plate cannot be clicked then. Rather than sit there
		// looking live — the "reads as broken" failure the aim hint exists for — it says what the
		// one key that still works does. Both strings are 14 characters, which is what lets the
		// plate keep one font size and change colour only (see ChessRing.SettingsMaxChars).
		bool aiming = SeatAim.Aiming;
		_panel.Text = aiming ? "ESC FOR CURSOR" : "BOARD SETTINGS";
		_panel.State = aiming ? "inert" : "";
		_panel.OnClick ??= Gambit.UI.Screens.BoardSettingsScreen.Open;
	}

	/// <summary>Build the plate as a child of THIS GameObject — which LobbyPlayer created
	/// unparented, NotSaved and NotNetworked, deliberately not under the station or under the
	/// player, both of which are networked. A child rather than this GO itself so it can be
	/// disabled while nobody is seated without stopping <see cref="OnUpdate"/>.</summary>
	bool Build( ChessRing ring )
	{
		_plate = new GameObject( true, "Plate" );
		_plate.Parent = GameObject;
		_panel = ring.BuildSeatSettingsPlate( _plate );
		if ( _panel.IsValid() ) return true;

		// No box model — AddBox has always silently drawn nothing in that case. Drop the empty
		// GO rather than leave a husk that reports valid and renders air.
		_plate.Destroy();
		_plate = null;
		_buildFailed = true;
		return false;
	}
}
