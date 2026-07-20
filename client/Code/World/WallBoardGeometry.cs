using System.Linq;
using Sandbox;
using Sandbox.UI;

namespace Gambit.World;

/// <summary>
/// THE one control for every lobby wall <b>display board</b> — the east-wall info board
/// (CenterInfoPanel), the dev-notes board, the lichess board, the south-wall settings boards
/// (WallSettingsPanel), and the spectator walk-up board (SpectatorInfoPanel). Size, aspect and
/// the floor anchor all live here, so they read as the same sign on the same wall. Change a
/// value here once and every board moves together.
///
/// <para><b>If you are adding a board, use <see cref="BoardScale"/> and
/// <see cref="FloorAnchor"/>, and nothing else.</b> Every board that has looked wrong in this
/// scene looked wrong because it hand-rolled its own numbers instead — a GO scale invented on
/// the spot doesn't match, and worse, it can't be fixed from here. There is deliberately no
/// per-wall scale knob to override this with.</para>
///
/// <para>What makes boards match is the COMBINATION, not the GO scale alone: every board leaves
/// its <see cref="WorldPanel"/> at the default PanelSize (so they share one intrinsic pixel
/// space, and therefore one font scale — which is why px values are copyable between them),
/// lays its content out <c>height:auto</c>, applies <see cref="BoardScale"/> as its GO
/// LocalScale, and floor-anchors with <see cref="FloorAnchor"/>. Break any one of those and the
/// board stops matching the others.</para>
/// </summary>
public static class WallBoardGeometry
{
	/// <summary>Aspect stretch applied to the default (square) WorldPanel rect: portrait, width
	/// along local Y, height along local Z. CenterInfoPanel's fonts are calibrated to this
	/// stretch, so every shared board reuses the same font sizes.</summary>
	public static readonly Vector3 Stretch = new( 1f, 1.8f, 2.4f );

	/// <summary>Master multiplier on top of <see cref="Stretch"/> — the single size knob, tuned
	/// by the user on the east-wall info board.</summary>
	public const float Master = 2.2f;

	/// <summary>GO LocalScale for a wall display board. Use this; don't invent one.</summary>
	public static Vector3 BoardScale => Stretch * Master;

	/// <summary>World half-height of a board's intrinsic rect per unit of GO Z-scale — the
	/// calibration constant behind <see cref="FloorAnchor"/>. It only means anything for a board
	/// left at the default PanelSize, which is one more reason boards must not change it.</summary>
	public const float HalfHeightPerScale = 18f;

	// Floor CLEARANCE is deliberately not here. It genuinely differs per wall — the east
	// wall runs its boards at 30 and the others at 60 — so it stays a per-board
	// [Property] that the wall passes in. A "default" here would be a number that lies
	// about half the scene.

	/// <summary>
	/// Lift a board so its <b>content's</b> bottom edge sits <paramref name="floorClearance"/>
	/// above z=0, and keep it there as the content changes height.
	///
	/// <para>Call this every frame from the board's <c>OnUpdate</c>. It has to be per-frame and
	/// content-measured rather than a fixed Z, because these boards are <c>height:auto</c>: one
	/// that gains a line (a dev note appearing, a lichess link resolving) grows upward from a
	/// fixed Z and drifts off its neighbours. Anchoring the BOTTOM is what keeps a row of boards
	/// sitting on one line no matter what's in them.</para>
	///
	/// <para>Reads the panel's FIRST child as the content — every board here wraps its content in
	/// a single root div for exactly this reason. No child, or a panel that hasn't laid out yet
	/// (<c>Rect.Height == 0</c>), is a no-op: it settles on a later frame.</para>
	/// </summary>
	public static void FloorAnchor( PanelComponent board, float floorClearance )
	{
		if ( board is null ) return;

		var content = board.Panel?.Children.FirstOrDefault();
		if ( content is null || board.Panel.Box.Rect.Height <= 0f ) return;

		float rootHalf = HalfHeightPerScale * board.WorldScale.z;
		float contentHeight = 1.5f * rootHalf * ( content.Box.Rect.Height / board.Panel.Box.Rect.Height );

		var pos = board.LocalPosition;
		pos.z = floorClearance + contentHeight - rootHalf;
		board.LocalPosition = pos;
	}

	/// <summary>
	/// Uniformly scale <paramref name="content"/> DOWN so its natural height fits within
	/// <paramref name="availableHeight"/> (the same pixel space as the content's own Box).
	/// Shared by both info surfaces: the engaged InfoScreen fits its card to the viewport,
	/// and the wall boards fit their content to the board's own rect, so neither can clip
	/// vertically. It never scales UP (clamped to 1), so content that already fits is left
	/// untouched.
	///
	/// <para>Safe to call every frame: an s&amp;box transform is visual-only and does not
	/// change <c>Box.Rect</c>, so the measured natural height is stable frame to frame and
	/// cannot feed back on itself. The scaled element sets its own <c>transform-origin</c>
	/// (centre for a viewport-centred card; bottom for a floor-anchored wall board) so the
	/// shrink happens about the right point.</para>
	/// </summary>
	public static void FitToHeight( Panel content, float availableHeight,
		float margin = 0.96f, float minScale = 0.4f )
	{
		if ( content is null ) return;

		float natural = content.Box.Rect.Height;
		if ( natural <= 0f || availableHeight <= 0f ) return;

		float scale = MathX.Clamp( availableHeight * margin / natural, minScale, 1f );

		var t = new PanelTransform();
		t.AddScale( scale );
		content.Style.Transform = t;
	}
}
