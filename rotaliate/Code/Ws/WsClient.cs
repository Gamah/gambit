using System;
using System.Threading.Tasks;
using Sandbox;

namespace Rotaliate.Ws;

public sealed class WsClient : Component
{
	public Action<string> OnMessage { get; set; }
	public Action OnDone { get; set; }

	WebSocket _socket;
	TimeSince _lastPing;
	const float PingInterval = 30f;

	string _uri;
	bool _connected;

	public async Task Connect( string uri )
	{
		_uri = uri;
		_socket = new WebSocket();
		_socket.OnMessageReceived += msg => OnMessage?.Invoke( msg );
		_socket.OnDisconnected += ( status, reason ) =>
		{
			_connected = false;
			OnDone?.Invoke();
		};

		await _socket.Connect( uri );
		_connected = true;
		_lastPing = 0;
	}

	public async Task Send( string message )
	{
		if ( _socket != null && _connected )
			await _socket.Send( message );
	}

	public void Disconnect()
	{
		_socket?.Dispose();
		_socket = null;
		_connected = false;
	}

	protected override void OnUpdate()
	{
		if ( !_connected || _socket == null ) return;

		if ( _lastPing > PingInterval )
		{
			_ = _socket.Send( "ping" );
			_lastPing = 0;
		}
	}

	protected override void OnDestroy()
	{
		Disconnect();
	}
}
