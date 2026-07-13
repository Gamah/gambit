using System;
using System.Collections.Generic;
using Sandbox;
using Gambit.UI;

namespace Gambit.World;

/// <summary>
/// Hangs six display-only leaderboard WorldPanels on the lobby's north wall, grouped
/// in pairs (Daily moves/time | Hourly moves/time | 2P/4P wins), with each pair's
/// rollover countdown (bare timestamp) centered between its two boards just under
/// the wall top (the MP pair has none) and a "L E A D E R B O A R D S" heading
/// floating above the wall. ALL sizes and positions are fractions of the wall
/// (LobbyRoom.RoomSize × WallHeight, read from this GO) so retuning the room reflows
/// the whole arrangement; board width is whatever wall width remains after margins
/// and gaps, split six ways. Client-side only — each client builds and fetches its
/// own copies, nothing is networked. Same editor-preview pattern as LobbyRoom:
/// OnEnabled/OnValidate rebuild NotSaved GOs, plus a wall-dimension watch in
/// OnUpdate (LobbyRoom's own OnValidate can't reach this component). WorldPanel
/// scale is a multiplier on the panel's intrinsic size, not world units; GO scales
/// are kept uniform so glyphs aren't stretched (boards shape their aspect with a
/// CSS sub-rect via WidthFraction).
/// </summary>
public sealed class LeaderboardWall : Component, Component.ExecuteInEditor
{
	/// <summary>World units between the wall's inner face (RoomSize / 2) and the
	/// panel plane.</summary>
	[Property] public float WallInset { get; set; } = 4f;

	/// <summary>Empty wall kept at each end of the row, as a fraction of wall width.</summary>
	[Property] public float EdgeMarginFrac { get; set; } = 0.002f;

	/// <summary>Gap between the two boards of a pair, as a fraction of wall width —
	/// the pair's countdown is centered on it (but drawn in front, so it may be
	/// wider than the gap).</summary>
	[Property] public float PairGapFrac { get; set; } = 0.002f;

	/// <summary>Gap between adjacent pairs, as a fraction of wall width.</summary>
	[Property] public float GroupGapFrac { get; set; } = 0.006f;

	/// <summary>Board height as a fraction of wall height, centered on the wall's
	/// vertical center.</summary>
	[Property] public float BoardHeightFrac { get; set; } = 0.933f;

	/// <summary>Countdown center height as a fraction of wall height.</summary>
	[Property] public float TimerHeightFrac { get; set; } = 0.95f;

	/// <summary>Countdown quad size as a fraction of wall height — the timestamp's
	/// size within it comes from the px values in WallCountdownPanel.</summary>
	[Property] public float TimerScaleFrac { get; set; } = 0.6f;

	/// <summary>Heading center height as a fraction of wall height (>1 floats it
	/// above the wall top — keep the glyph bottoms clear of the wall edge).</summary>
	[Property] public float TitleHeightFrac { get; set; } = 1.15f;

	/// <summary>Heading quad size as a fraction of wall height.</summary>
	[Property] public float TitleScaleFrac { get; set; } = 3.4f;

	/// <summary>Approximate world size of a WorldPanel quad per unit of GO scale —
	/// the same ~36-units-at-scale-1 intrinsic size as the cabinet screens.</summary>
	[Property] public float PanelUnitWidth { get; set; } = 36f;

	static readonly WallLeaderboardPanel.LbKind[] Kinds =
	{
		WallLeaderboardPanel.LbKind.DailyMoves,
		WallLeaderboardPanel.LbKind.DailyTime,
		WallLeaderboardPanel.LbKind.HourlyMoves,
		WallLeaderboardPanel.LbKind.HourlyTime,
		WallLeaderboardPanel.LbKind.Multi2,
		WallLeaderboardPanel.LbKind.Multi4,
	};

	/// <summary>Distance from the wall plane to the camera anchor for the engage flow.</summary>
	[Property] public float ViewDistance { get; set; } = 200f;

	/// <summary>Horizontal walk-up range for the "Press E" prompt.</summary>
	[Property] public float InteractRange { get; set; } = 130f;

	readonly List<GameObject> _spawned = new();

	LobbyRoom Room => Components.Get<LobbyRoom>();
	float WallWidth => Room?.RoomSize ?? 800f;
	float WallHeight => Room?.WallHeight ?? 150f;

	Vector2 _builtWall;

	protected override void OnEnabled() => Rebuild();

	protected override void OnValidate() => Rebuild();

	/// <summary>Re-run the build after a code hotload (Editor/HotloadRebuild.cs).</summary>
	public void RebuildPreview() => Rebuild();

	protected override void OnDisabled() => Clear();

	protected override void OnUpdate()
	{
		if ( _builtWall != new Vector2( WallWidth, WallHeight ) )
			Rebuild();
	}

	void Rebuild()
	{
		if ( !Active ) return;
		Clear();
		_builtWall = new Vector2( WallWidth, WallHeight );

		// North wall runs along X; panels face -Y, back into the room
		var facing = Rotation.FromYaw( -90f );
		float wallY = WallWidth * 0.5f - WallInset;

		// heading sits 1.5 in front of the wall plane — the taller-than-wall board
		// quads share its height range, so coplanar would flicker
		float titleScale = TitleScaleFrac * WallHeight / PanelUnitWidth;
		var title = MakePanel( "LeaderboardTitle",
			new Vector3( 0, wallY - 1.5f, TitleHeightFrac * WallHeight ), facing,
			new Vector3( 1f, titleScale, titleScale ) );
		// "LEADERBOARDS" with letter-spacing for the spaced look — literal spaces PLUS
		// letter-spacing overran the WorldPanel's fixed intrinsic pixel width and clipped
		// both ends. FontSize is intrinsic px (the GO scale enlarges it ~14× in world).
		var titlePanel = title.AddComponent<WallTextPanel>();
		titlePanel.Text = "LEADERBOARDS";
		titlePanel.FontSize = 13f;

		// Three pairs: x advances by w+pairGap inside a pair, w+groupGap between
		// pairs. w is the board's VISUAL width; the quads can be wider (uniform
		// scale with transparent margins), so alternate boards sit a hair off the
		// wall plane to keep overlapping quads from coplanar flicker.
		float pairGap = PairGapFrac * WallWidth;
		float groupGap = GroupGapFrac * WallWidth;
		float boardH = BoardHeightFrac * WallHeight;
		float boardScale = boardH / PanelUnitWidth;
		float timerScale = TimerScaleFrac * WallHeight / PanelUnitWidth;
		float w = ( WallWidth * ( 1f - 2f * EdgeMarginFrac ) - 3f * pairGap - 2f * groupGap ) / 6f;
		float boardZ = WallHeight * 0.5f;

		float x = -( 6f * w + 3f * pairGap + 2f * groupGap ) * 0.5f + w * 0.5f;

		// Track pair center X values to build engage stations after all boards are placed.
		float[] pairCenterX = new float[3];

		for ( int i = 0; i < Kinds.Length; i++ )
		{
			var go = MakePanel( $"Leaderboard {Kinds[i]}",
				new Vector3( x, wallY - ( i % 2 ) * 0.5f, boardZ ), facing,
				new Vector3( 1f, boardScale, boardScale ) );
			var board = go.AddComponent<WallLeaderboardPanel>();
			board.Kind = Kinds[i];
			board.WidthFraction = MathF.Min( w / boardH, 1f );

			// daily/hourly pairs: bare rollover countdown centered between the two
			// boards, just under the wall top, drawn in front of the boards
			if ( i is 0 or 2 )
			{
				var timer = MakePanel( $"Rollover {Kinds[i]}",
					new Vector3( x + ( w + pairGap ) * 0.5f, wallY - 1f, TimerHeightFrac * WallHeight ), facing,
					new Vector3( 1f, timerScale, timerScale ) );
				timer.AddComponent<WallCountdownPanel>().Hourly = i == 2;
			}

			// Record the left board's X for computing pair center
			if ( i % 2 == 0 ) pairCenterX[i / 2] = x;
			else pairCenterX[i / 2] += ( x - pairCenterX[i / 2] ) * 0.5f;

			x += w + ( i % 2 == 0 ? pairGap : groupGap );
		}

		// One engage station per pair — camera anchor in the room looking at the wall
		// The pair half-extent covers both boards + the gap between them
		float pairHalfExtent = w + pairGap * 0.5f;
		for ( int p = 0; p < 3; p++ )
		{
			var stationGo = new GameObject( true, $"LeaderboardStation{p}" );
			stationGo.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
			stationGo.Parent = GameObject;
			stationGo.LocalPosition = new Vector3( pairCenterX[p], wallY, boardZ );
			stationGo.LocalRotation = facing;

			var station = stationGo.AddComponent<LeaderboardStation>();
			station.PairIndex = p;
			station.InteractRange = InteractRange;

			_spawned.Add( stationGo );
		}

		// The giant floating spectator board (host setting; idle until the admin turns
		// it on) is now a scene component on the Room GO — see SpectatorBoard.
	}

	GameObject MakePanel( string name, Vector3 localPos, Rotation localRot, Vector3 scale )
	{
		var go = new GameObject( true, name );
		go.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		go.Parent = GameObject;
		go.LocalPosition = localPos;
		go.LocalRotation = localRot;
		go.LocalScale = scale;
		go.AddComponent<WorldPanel>();
		_spawned.Add( go );
		return go;
	}

	void Clear()
	{
		foreach ( var go in _spawned )
		{
			if ( go.IsValid() )
				go.Destroy();
		}
		_spawned.Clear();
	}
}
