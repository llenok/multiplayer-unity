﻿using System;
using UnityEngine;
using Facepunch.Steamworks;
using ChickenIngot.Networking;
using System.Collections.Generic;
using System.Collections;

namespace ChickenIngot.Steam
{
	public class SteamService : MonoBehaviour
	{
		private enum ConnectionOperationType
		{
			Connecting, Disconnecting
		}
		private class ConnectionOperation
		{
			public ConnectionOperationType type;
			public RMPPeer peer;
			public string version;
			public byte[] steamTicketData;
			public byte[] steamIdData;
			public string username;
			public ulong steamId;
		}

		private static SteamService _instance = null;

		[SerializeField]
		private uint _appId;
		[SerializeField]
		private RMPNetworkView _view;
		private Client _client;
		private Server _server;
		private readonly Queue<ConnectionOperation> _operationQueue = new Queue<ConnectionOperation>();
		private ConnectionOperation _waitingUser;

		public static bool Initialized { get; private set; }
		public static SteamUser Me { get; private set; }
		public static Dictionary<ulong, SteamUser> SteamUserDict { get; private set; }

		#region Events

		[SerializeField]
		private SteamUserJoinEvent _onSteamUserJoin;
		[SerializeField]
		private SteamUserExitEvent _onSteamUserExit;
		[SerializeField]
		private SteamServerOpenEvent _onSteamServerOpen;
		[SerializeField]
		private SteamServerCloseEvent _onSteamServerClose;
		[SerializeField]
		private JoinSteamServerEvent _onJoinSteamServer;
		[SerializeField]
		private ExitSteamServerEvent _onExitSteamServer;

		public SteamUserJoinEvent OnSteamUserJoin { get { return _onSteamUserJoin; } }
		public SteamUserExitEvent OnSteamUserExit { get { return _onSteamUserExit; } }
		public SteamServerOpenEvent OnSteamServerOpen { get { return _onSteamServerOpen; } }
		public SteamServerCloseEvent OnSteamServerClose { get { return _onSteamServerClose; } }
		public JoinSteamServerEvent OnJoinSteamServer { get { return _onJoinSteamServer; } }
		public ExitSteamServerEvent OnExitSteamServer { get { return _onExitSteamServer; } }

		#endregion

		void Awake()
		{
			DontDestroyOnLoad(gameObject);

			if (_instance == null)
			{
				DontDestroyOnLoad(gameObject);
				_instance = this;
			}
			else
			{
				Debug.LogWarning("Steam Unity Service instance already exsists.");
				Destroy(this);
				return;
			}
		}

		void Start()
		{
			// Configure us for this unity platform
			Config.ForUnity(Application.platform.ToString());

			Initialized = false;
			Me = null;
			SteamUserDict = new Dictionary<ulong, SteamUser>();

			RMPNetworkService.OnServerOpen.AddListener(_OnServerOpen);
		}

		void Update()
		{
			UpdateClient();
			UpdateServer();
			ProcessConnection();
		}

		void OnDestroy()
		{
			if (Multiplayer.Instance.Me != null)
			{
				var myAccount = Multiplayer.Instance.Me.Steam;
				if (myAccount != null)
				{
					myAccount.CancelAuthSessionTicket();
					myAccount = null;
				}
			}

			if (_client != null)
			{
				_client.Dispose();
				_client = null;
			}

			if (_server != null)
			{
				_server.Dispose();
				_server = null;
			}
		}

		private void UpdateClient()
		{
			if (_client == null)
				return;

			try
			{
				UnityEngine.Profiling.Profiler.BeginSample("Steam client update");
				_client.Update();
			}
			finally
			{
				UnityEngine.Profiling.Profiler.EndSample();
			}
		}

		private void UpdateServer()
		{
			if (_server == null)
				return;

			try
			{
				UnityEngine.Profiling.Profiler.BeginSample("Steam server update");
				_server.Update();
			}
			finally
			{
				UnityEngine.Profiling.Profiler.EndSample();
			}
		}

		private void ProcessConnection()
		{
			// 클라이언트 접속, 퇴장의 순서와 atomic한 연산을 보장하기 위한 큐잉처리
			if (NetworkService.IsOnline && NetworkService.IsServer)
			{
				if (_waitingUser == null && _operationQueue.Count > 0)
				{
					ConnectionOperation args = _operationQueue.Dequeue();
					Debug.Log(string.Format("Steam user {0}...", args.type));
					switch (args.type)
					{
						case ConnectionOperationType.Connecting:
							UserAuth(args);
							break;

						case ConnectionOperationType.Disconnecting:
							DisposeUser(args);
							break;
					}
				}
			}
		}

		/// <summary>
		/// 플레이어 접속을 승인할지 거부할지 결정한다.
		/// </summary>
		[ServerOnly]
		private void UserAuth(ConnectionOperation user)
		{
			string version = user.version;
			RMPPeer client = user.peer;

			if (client.Status != RMPPeer.PeerStatus.Connected)
			{
				Debug.LogWarning("User already disconnected quickly before Steam Auth started.");
				return;
			}

			// 인원수
			if (UserList.Count >= ServerConfig.maxPlayers)
			{
				string msg = "Server is full. (" + UserList.Count + "/" + ServerConfig.maxPlayers + ")";
				RejectUser(client, msg);
				return;
			}
			// 버전
			if (!version.Equals(Build.VERSION))
			{
				string msg = "Version mismatch. Client: " + version + ", Server: " + Build.VERSION;
				RejectUser(client, msg);
				return;
			}

			// 스팀인증
			byte[] ticketData = user.steamTicketData;
			ulong steamID = BitConverter.ToUInt64(user.steamIdData, 0);
			Debug.Log("Authorizing steam user. (" + steamID + ")");

			if (Server.Instance.Auth.StartSession(ticketData, steamID))
			{
				// 이후의 처리는 OnAuthChange 에서
				user.steamId = steamID;
				SetServerAuthWaitingStatus(user);
			}
			else
			{
				string msg = "Failed to start steam auth session.";
				RejectUser(client, msg);
				return;
			}
		}

		[ServerOnly]
		private void OnAuthChange(ulong steamId, ulong ownerId, ServerAuth.Status status)
		{
			ConnectionOperation waitingUser = _waitingUser;

			// 인증 진행중인 유저에 대한 이벤트일 때
			if (waitingUser != null && steamId == waitingUser.steamId)
			{
				// 인증 대기 목록에서 제거
				ResetServerAuthWaitingStatus();

				switch (status)
				{
					case ServerAuth.Status.OK:
						AcceptUser(waitingUser);
						break;

					case ServerAuth.Status.AuthTicketCanceled:
						Debug.LogWarning("Auth ticket canceled while doing user auth.");
						break;

					default:
						string message = "Steam auth failed. (" + steamId + "): " + status.ToString();
						RejectUser(waitingUser.peer, message);
						break;
				}
			}
			else
			{
				switch (status)
				{
					case ServerAuth.Status.AuthTicketCanceled:
						Debug.Log("Auth ticket canceled. (" + steamId + ")");
						break;

					default:
						Debug.Log("Steam auth changed. (" + steamId + ")");
						break;
				}
			}

		}

		[ServerOnly]
		private void AcceptUser(ConnectionOperation user)
		{
			RMPPeer client = user.peer;

			// 스팀 인증이 끝나기 전에 접속 종료한 경우
			if (client.Status != RMPPeer.PeerStatus.Connected)
			{
				Debug.LogWarning("User already disconnected while authorizing.");
				Server.Instance.Auth.EndSession(user.steamId);
				return;
			}

			var steamId = user.steamId;
			// 서버 프로그램은 직접 전달받는 것 외에는 유저의 정보를 알 방법이 없다.
			var username = user.username;
			var steamuser = new SteamUser(client, user.steamId, username);
			SteamUserDict.Add(steamId, steamuser);

			Debug.Log("Accept steam user. (" + steamId + ")");

			// accept 알림
			_view.RPC(client, "clRPC_ConnectionAccepted");
		}

		[ServerOnly]
		private void DisposeUser(ConnectionOperation user)
		{
			User target;
			if (_userManager.TryGetUser(user.peer, out target))
			{
				Debug.Log("Finishing steam session. (" + target.Steam.SteamID + ")");
				Server.Instance.Auth.EndSession(target.Steam.SteamID);
				target.OnExit();
				_userManager.RemoveUserInstance(target);
			}

			Debug.Log("User disconnection completed.");
		}

		[ServerOnly]
		private void RejectUser(RMPPeer peer, string message)
		{
			Debug.LogWarning("Reject steam user : " + message);
			_view.RPC(peer, "clRPC_ConnectionRejected", message);
			// disconnect는 클라에서 한다. 패킷은 비동기적으로 전송되기 때문에
			// 서버에서 하기 좀 곤란함.
			//peer.Disconnect();
		}

		Coroutine timeout = null;

		[ServerOnly]
		private void SetServerAuthWaitingStatus(ConnectionOperation args)
		{
			_waitingUser = args;
			timeout = StartCoroutine(UserAuthTimeout());
		}

		[ServerOnly]
		private void ResetServerAuthWaitingStatus()
		{
			_waitingUser = null;
			StopCoroutine(timeout);
			timeout = null;
		}

		[ServerOnly]
		IEnumerator UserAuthTimeout()
		{
			// 한 번에 하나의 유저만 처리한다고 가정
			// 두 명 이상의 유저가 대기중일 때에는 이 알고리즘을 적용할 수 없음
			var user = _waitingUser;

			yield return new WaitForSeconds(5.0f);

			// 딜레이 후 여전히 대기중일 경우 강제 취소
			if (user == _waitingUser)
			{
				string msg = "Steam user auth canceled. (Time out)";
				RejectUser(_waitingUser.peer, msg);
				Server.Instance.Auth.EndSession(_waitingUser.steamId);
				ResetServerAuthWaitingStatus();
			}
		}

		[RMP]
		[ClientOnly]
		private void clRPC_ConnectionAccepted()
		{
			Debug.Log("Steam server accepts connection.");
			OnJoinSteamServer.Invoke();
		}

		[RMP]
		[ClientOnly]
		private void clRPC_ConnectionRejected(string message)
		{
			Debug.LogWarning("Steam server rejects connection. : " + message);
			RMPNetworkService.StopClient();
		}

		[RMP]
		[ClientOnly]
		private void clRPC_HandShake()
		{
			Debug.Log("Getting steam auth ticket.");
			Auth.Ticket ticket = Client.Instance.Auth.GetAuthSessionTicket();
			ulong steamId = Client.Instance.SteamId;
			byte[] ticketData = ticket.Data;
			byte[] steamIDData = BitConverter.GetBytes(steamId);
			string username = Me.Steam.Username;
			// 버전, 스팀티켓, 스팀id 전송
			_view.RPC(RPCOption.ToServer, "svRPC_HandShake", Build.VERSION, ticketData, steamIDData, username);
		}

		[RMP]
		[ServerOnly]
		private void svRPC_HandShake(string version, byte[] steamTicketData, byte[] steamIDData, string username)
		{
			Debug.Log("Steam auth ticket received (" + username + ")");
			ConnectionOperation req = new ConnectionOperation();
			req.type = ConnectionOperationType.Connecting;
			req.peer = _view.MessageSender;
			req.version = version;
			req.steamTicketData = steamTicketData;
			req.steamIdData = steamIDData;
			req.username = username;
			_operationQueue.Enqueue(req);
		}

		[ServerOnly]
		private void _OnServerOpen()
		{
			// 서버를 먼저 만든 다음 스팀에 올림
			if (!StartSteamServer(OnAuthChange))
			{
				Debug.LogError("Failed to initialize steam server");
				return;
			}

			_onSteamServerOpen.Invoke();
		}

		[ServerOnly]
		private void _OnServerClose()
		{
			_onSteamServerClose.Invoke();
		}

		[ServerOnly]
		private void _OnClientConnect(RMPPeer client)
		{
			Debug.Log("Client connected. Waiting for steam auth ticket.");
			_view.RPC(client, "clRPC_HandShake");
		}

		[ServerOnly]
		private void _OnClientDisconnect(RMPPeer client)
		{
			// 다른 유저의 접속 처리 도중에 사라지면 안되기 때문에 atomic한 연산이 보장되어야 한다.
			ConnectionOperation disconnect = new ConnectionOperation();
			disconnect.type = ConnectionOperationType.Disconnecting;
			disconnect.peer = client;
			_operationQueue.Enqueue(disconnect);
		}

		[ClientOnly]
		private void _OnDisconnectFromServer()
		{
			Me.Steam.CancelAuthSessionTicket();
			_onExitSteamServer.Invoke();
		}

		public static bool StartSteamClient()
		{
			_instance._client = new Client(_instance._appId);

			if (!_instance._client.IsValid)
			{
				_instance._client = null;
				Debug.LogError("Failed to initialize steam client.");
				return false;
			}

			Me = new SteamUser(null, _instance._client.SteamId, _instance._client.Username);
			Debug.Log("Steam client initialized: " + Me.Username + " / " + Me.SteamId);
			Initialized = true;
			return true;
		}

		public static bool StartSteamServer(SteamServerConfig config, Action<ulong, ulong, ServerAuth.Status> OnAuthChange)
		{
			if (_instance._server != null)
				return false;
			
			var serverInit = new ServerInit(config.modDir, config.gameDesc);
			serverInit.Secure = config.secure;
			serverInit.VersionString = config.version;

			_instance._server = new Server(_instance._appId, serverInit);
			_instance._server.ServerName = config.name;
			_instance._server.MaxPlayers = config.maxPlayers;
			_instance._server.LogOnAnonymous();

			if (!_instance._server.IsValid)
			{
				_instance._server = null;
				Debug.LogError("Failed to initialize steam server.");
				return false;
			}

			Server.Instance.Auth.OnAuthChange = OnAuthChange;
			Debug.Log("Steam server initialized.");
			Initialized = true;
			return true;
		}

		private void EndSteamServer()
		{

		}

		private void EndSteamClient()
		{

		}

		public static void DisposeServer()
		{
			if (_instance._server != null)
			{
				_instance._server.Auth.OnAuthChange = null;
				_instance._server.Dispose();
				_instance._server = null;
			}
		}
	}
}