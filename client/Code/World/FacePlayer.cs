using System;
using Sandbox;

namespace Gambit.World;

/// <summary>
/// Yaws the GameObject so its -forward side (a WorldPanel's front face) points at the
/// local player model (camera fallback before the player spawns). Yaw only — the board
/// stays upright. Optional intro: spin at full speed for SpinSeconds, ramp down over
/// RampSeconds, then ease onto the target.
/// </summary>
public sealed class FacePlayer : Component
{
	/// <summary>Flip 180° if the panel renders on the other side than expected.</summary>
	[Property] public bool Flip { get; set; }

	/// <summary>Full-speed intro spin duration; 0 disables the intro.</summary>
	[Property] public float SpinSeconds { get; set; } = 1.5f;

	/// <summary>Intro spin rate in degrees/second.</summary>
	[Property] public float SpinSpeed { get; set; } = 2160f;

	/// <summary>How long the spin takes to decelerate after SpinSeconds.</summary>
	[Property] public float RampSeconds { get; set; } = 1.5f;

	float _age;
	float _yaw;
	bool _locked;

	protected override void OnStart()
	{
		_yaw = WorldRotation.Angles().yaw;
		_locked = SpinSeconds <= 0f;
	}

	protected override void OnUpdate()
	{
		// Track the player's body, not the camera — the third-person camera orbits
		// behind the avatar, which made the board face past the player
		Vector3? targetPos = LobbyPlayer.Local?.WorldPosition ?? Scene?.Camera?.WorldPosition;
		if ( targetPos == null ) return;

		Vector3 toPlayer = targetPos.Value - WorldPosition;
		toPlayer.z = 0;
		if ( toPlayer.IsNearZeroLength ) return;

		// Panel front turned out to face the GO's +forward — point it at the player
		var dir = Flip ? -toPlayer : toPlayer;
		var target = Rotation.LookAt( dir.Normal );

		if ( _locked )
		{
			WorldRotation = target;
			return;
		}

		_age += Time.Delta;
		float speed = _age < SpinSeconds
			? SpinSpeed
			: SpinSpeed * (1f - (_age - SpinSeconds) / Math.Max( RampSeconds, 0.01f ));

		if ( speed > 90f )
		{
			_yaw += speed * Time.Delta;
			WorldRotation = Rotation.FromYaw( _yaw );
			return;
		}

		// Slow enough — keep turning in the spin direction until the camera-facing
		// yaw is reached, then lock. Floor the speed so deceleration never stalls.
		float targetYaw = target.Angles().yaw;
		float remaining = (targetYaw - _yaw) % 360f;
		if ( remaining < 0f ) remaining += 360f; // always finish in the spin direction

		float step = Math.Max( speed, 60f ) * Time.Delta;
		if ( step >= remaining )
		{
			WorldRotation = target;
			_locked = true;
			return;
		}

		_yaw += step;
		WorldRotation = Rotation.FromYaw( _yaw );
	}
}
