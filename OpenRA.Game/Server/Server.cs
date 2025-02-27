#region Copyright & License Information
/*
 * Copyright 2007-2021 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenRA.FileFormats;
using OpenRA.Network;
using OpenRA.Primitives;
using OpenRA.Support;
using OpenRA.Traits;

namespace OpenRA.Server
{
	public enum ServerState
	{
		WaitingPlayers = 1,
		GameStarted = 2,
		ShuttingDown = 3
	}

	public enum ServerType
	{
		Local = 0,
		Multiplayer = 1,
		Dedicated = 2
	}

	public sealed class Server
	{
		public readonly string TwoHumansRequiredText = "This server requires at least two human players to start a match.";

		public readonly MersenneTwister Random = new MersenneTwister();
		public readonly ServerType Type;

		public List<Connection> Conns = new List<Connection>();

		public Session LobbyInfo;
		public ServerSettings Settings;
		public ModData ModData;
		public List<string> TempBans = new List<string>();

		// Managed by LobbyCommands
		public MapPreview Map;
		public readonly MapStatusCache MapStatusCache;
		public GameSave GameSave = null;

		// Default to the next frame for ServerType.Local - MP servers take the value from the selected GameSpeed.
		public int OrderLatency = 1;

		readonly int randomSeed;
		readonly List<TcpListener> listeners = new List<TcpListener>();
		readonly TypeDictionary serverTraits = new TypeDictionary();
		readonly PlayerDatabase playerDatabase;

		volatile ServerState internalState = ServerState.WaitingPlayers;

		readonly BlockingCollection<IServerEvent> events = new BlockingCollection<IServerEvent>();

		ReplayRecorder recorder;
		GameInformation gameInfo;
		readonly List<GameInformation.Player> worldPlayers = new List<GameInformation.Player>();

		public ServerState State
		{
			get => internalState;
			set => internalState = value;
		}

		public static void SyncClientToPlayerReference(Session.Client c, PlayerReference pr)
		{
			if (pr == null)
				return;

			if (pr.LockFaction)
				c.Faction = pr.Faction;
			if (pr.LockSpawn)
				c.SpawnPoint = pr.Spawn;
			if (pr.LockTeam)
				c.Team = pr.Team;
			if (pr.LockHandicap)
				c.Handicap = pr.Handicap;

			c.Color = pr.LockColor ? pr.Color : c.PreferredColor;
		}

		public void Shutdown()
		{
			State = ServerState.ShuttingDown;
		}

		public void EndGame()
		{
			foreach (var t in serverTraits.WithInterface<IEndGame>())
				t.GameEnded(this);

			recorder?.Dispose();
			recorder = null;
		}

		// Craft a fake handshake request/response because that's the
		// only way to expose the Version and OrdersProtocol.
		public void RecordFakeHandshake()
		{
			var request = new HandshakeRequest
			{
				Mod = ModData.Manifest.Id,
				Version = ModData.Manifest.Metadata.Version,
			};

			recorder.ReceiveFrame(0, 0, new Order("HandshakeRequest", null, false)
			{
				Type = OrderType.Handshake,
				IsImmediate = true,
				TargetString = request.Serialize(),
			}.Serialize());

			var response = new HandshakeResponse()
			{
				Mod = ModData.Manifest.Id,
				Version = ModData.Manifest.Metadata.Version,
				OrdersProtocol = ProtocolVersion.Orders,
				Client = new Session.Client(),
			};

			recorder.ReceiveFrame(0, 0, new Order("HandshakeResponse", null, false)
			{
				Type = OrderType.Handshake,
				IsImmediate = true,
				TargetString = response.Serialize(),
			}.Serialize());
		}

		void MapStatusChanged(string uid, Session.MapStatus status)
		{
			lock (LobbyInfo)
			{
				if (LobbyInfo.GlobalSettings.Map == uid)
					LobbyInfo.GlobalSettings.MapStatus = status;

				SyncLobbyInfo();
			}
		}

		public Server(List<IPEndPoint> endpoints, ServerSettings settings, ModData modData, ServerType type)
		{
			Log.AddChannel("server", "server.log", true);

			SocketException lastException = null;
			foreach (var endpoint in endpoints)
			{
				var listener = new TcpListener(endpoint);
				try
				{
					try
					{
						listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, 1);
					}
					catch (Exception ex)
					{
						if (ex is SocketException || ex is ArgumentException)
							Log.Write("server", "Failed to set socket option on {0}: {1}", endpoint.ToString(), ex.Message);
						else
							throw;
					}

					listener.Start();
					listeners.Add(listener);

					new Thread(() =>
					{
						while (true)
						{
							if (State != ServerState.WaitingPlayers)
							{
								listener.Stop();
								return;
							}

							// Use a 1s timeout so we can stop listening once the game starts
							if (listener.Server.Poll(1000000, SelectMode.SelectRead))
							{
								try
								{
									events.Add(new ClientConnectEvent(listener.AcceptSocket()));
								}
								catch (Exception)
								{
									// Ignore the exception that may be generated if the connection
									// drops while we are trying to connect
								}
							}
						}
					}) { Name = $"Connection listener ({listener.LocalEndpoint})", IsBackground = true }.Start();
				}
				catch (SocketException ex)
				{
					lastException = ex;
					Log.Write("server", "Failed to listen on {0}: {1}", endpoint.ToString(), ex.Message);
				}
			}

			if (listeners.Count == 0)
				throw lastException;

			Type = type;
			Settings = settings;

			Settings.Name = OpenRA.Settings.SanitizedServerName(Settings.Name);

			ModData = modData;

			playerDatabase = modData.Manifest.Get<PlayerDatabase>();

			randomSeed = (int)DateTime.Now.ToBinary();

			if (type != ServerType.Local && settings.EnableGeoIP)
				GeoIP.Initialize();

			if (type != ServerType.Local)
				Nat.TryForwardPort(Settings.ListenPort, Settings.ListenPort);

			foreach (var trait in modData.Manifest.ServerTraits)
				serverTraits.Add(modData.ObjectCreator.CreateObject<ServerTrait>(trait));

			serverTraits.TrimExcess();

			Map = ModData.MapCache[settings.Map];
			MapStatusCache = new MapStatusCache(modData, MapStatusChanged, type == ServerType.Dedicated && settings.EnableLintChecks);

			LobbyInfo = new Session
			{
				GlobalSettings =
				{
					RandomSeed = randomSeed,
					Map = Map.Uid,
					MapStatus = Session.MapStatus.Unknown,
					ServerName = settings.Name,
					EnableSingleplayer = settings.EnableSingleplayer || Type != ServerType.Dedicated,
					EnableSyncReports = settings.EnableSyncReports,
					GameUid = Guid.NewGuid().ToString(),
					Dedicated = Type == ServerType.Dedicated
				}
			};

			if (Settings.RecordReplays && Type == ServerType.Dedicated)
			{
				recorder = new ReplayRecorder(() => { return Game.TimestampedFilename(extra: "-Server"); });

				// We only need one handshake to initialize the replay.
				// Add it now, then ignore the redundant handshakes from each client
				RecordFakeHandshake();
			}

			new Thread(_ =>
			{
				// Initial status is set off the main thread to avoid triggering a load screen when joining a skirmish game
				LobbyInfo.GlobalSettings.MapStatus = MapStatusCache[Map];
				foreach (var t in serverTraits.WithInterface<INotifyServerStart>())
					t.ServerStarted(this);

				Log.Write("server", "Initial mod: {0}", ModData.Manifest.Id);
				Log.Write("server", "Initial map: {0}", LobbyInfo.GlobalSettings.Map);

				while (true)
				{
					if (State != ServerState.ShuttingDown)
					{
						if (events.TryTake(out var e, 1000))
							e.Invoke(this);

						// PERF: Dedicated servers need to drain the action queue to remove references blocking the GC from cleaning up disposed objects.
						if (Type == ServerType.Dedicated)
							Game.PerformDelayedActions();

						foreach (var t in serverTraits.WithInterface<ITick>())
							t.Tick(this);
					}

					if (State == ServerState.ShuttingDown)
					{
						EndGame();
						if (type != ServerType.Local)
							Nat.TryRemovePortForward();
						break;
					}
				}

				foreach (var t in serverTraits.WithInterface<INotifyServerShutdown>())
					t.ServerShutdown(this);

				Conns.Clear();
			})
			{ IsBackground = true }.Start();
		}

		int nextPlayerIndex;
		public int ChooseFreePlayerIndex()
		{
			return nextPlayerIndex++;
		}

		void OnClientPacket(Connection conn, int frame, byte[] data)
		{
			events.Add(new ClientPacketEvent(conn, frame, data));
		}

		void OnClientDisconnect(Connection conn)
		{
			events.Add(new ClientDisconnectEvent(conn));
		}

		void AcceptConnection(Socket socket)
		{
			if (State != ServerState.WaitingPlayers)
				return;

			// Validate player identity by asking them to sign a random blob of data
			// which we can then verify against the player public key database
			var token = Convert.ToBase64String(OpenRA.Exts.MakeArray(256, _ => (byte)Random.Next()));

			var newConn = new Connection(socket, ChooseFreePlayerIndex(), token, OnClientPacket, OnClientDisconnect);
			try
			{
				// Send handshake and client index.
				var ms = new MemoryStream(8);
				ms.WriteArray(BitConverter.GetBytes(ProtocolVersion.Handshake));
				ms.WriteArray(BitConverter.GetBytes(newConn.PlayerIndex));
				newConn.SendData(ms.ToArray());

				// Dispatch a handshake order
				var request = new HandshakeRequest
				{
					Mod = ModData.Manifest.Id,
					Version = ModData.Manifest.Metadata.Version,
					AuthToken = token
				};

				DispatchOrdersToClient(newConn, 0, 0, new Order("HandshakeRequest", null, false)
				{
					Type = OrderType.Handshake,
					IsImmediate = true,
					TargetString = request.Serialize()
				}.Serialize());
			}
			catch (Exception e)
			{
				Log.Write("server", $"Handshake for client {newConn.EndPoint} failed: {e}");
			}

			Conns.Add(newConn);
		}

		void ValidateClient(Connection newConn, string data)
		{
			try
			{
				if (State == ServerState.GameStarted)
				{
					Log.Write("server", "Rejected connection from {0}; game is already started.", newConn.EndPoint);

					SendOrderTo(newConn, "ServerError", "The game has already started");
					DropClient(newConn);
					return;
				}

				var handshake = HandshakeResponse.Deserialize(data);

				if (!string.IsNullOrEmpty(Settings.Password) && handshake.Password != Settings.Password)
				{
					var message = string.IsNullOrEmpty(handshake.Password) ? "Server requires a password" : "Incorrect password";
					SendOrderTo(newConn, "AuthenticationError", message);
					DropClient(newConn);
					return;
				}

				var ipAddress = ((IPEndPoint)newConn.EndPoint).Address;
				var client = new Session.Client
				{
					Name = OpenRA.Settings.SanitizedPlayerName(handshake.Client.Name),
					IPAddress = ipAddress.ToString(),
					AnonymizedIPAddress = Type != ServerType.Local && Settings.ShareAnonymizedIPs ? Session.AnonymizeIP(ipAddress) : null,
					Location = GeoIP.LookupCountry(ipAddress),
					Index = newConn.PlayerIndex,
					PreferredColor = handshake.Client.PreferredColor,
					Color = handshake.Client.Color,
					Faction = "Random",
					SpawnPoint = 0,
					Team = 0,
					Handicap = 0,
					State = Session.ClientState.Invalid,
				};

				if (ModData.Manifest.Id != handshake.Mod)
				{
					Log.Write("server", "Rejected connection from {0}; mods do not match.",
						newConn.EndPoint);

					SendOrderTo(newConn, "ServerError", "Server is running an incompatible mod");
					DropClient(newConn);
					return;
				}

				if (ModData.Manifest.Metadata.Version != handshake.Version)
				{
					Log.Write("server", "Rejected connection from {0}; Not running the same version.", newConn.EndPoint);

					SendOrderTo(newConn, "ServerError", "Server is running an incompatible version");
					DropClient(newConn);
					return;
				}

				if (handshake.OrdersProtocol != ProtocolVersion.Orders)
				{
					Log.Write("server", "Rejected connection from {0}; incompatible Orders protocol version {1}.",
						newConn.EndPoint, handshake.OrdersProtocol);

					SendOrderTo(newConn, "ServerError", "Server is running an incompatible protocol");
					DropClient(newConn);
					return;
				}

				// Check if IP is banned
				var bans = Settings.Ban.Union(TempBans);
				if (bans.Contains(client.IPAddress))
				{
					Log.Write("server", "Rejected connection from {0}; Banned.", newConn.EndPoint);
					SendOrderTo(newConn, "ServerError", $"You have been {(Settings.Ban.Contains(client.IPAddress) ? "banned" : "temporarily banned")} from the server");
					DropClient(newConn);
					return;
				}

				Action completeConnection = () =>
				{
					lock (LobbyInfo)
					{
						client.Slot = LobbyInfo.FirstEmptySlot();
						client.IsAdmin = !LobbyInfo.Clients.Any(c1 => c1.IsAdmin);

						if (client.IsObserver && !LobbyInfo.GlobalSettings.AllowSpectators)
						{
							SendOrderTo(newConn, "ServerError", "The game is full");
							DropClient(newConn);
							return;
						}

						if (client.Slot != null)
							SyncClientToPlayerReference(client, Map.Players.Players[client.Slot]);
						else
							client.Color = Color.White;

						// Promote connection to a valid client
						LobbyInfo.Clients.Add(client);
						newConn.Validated = true;

						var clientPing = new Session.ClientPing { Index = client.Index };
						LobbyInfo.ClientPings.Add(clientPing);

						Log.Write("server", "Client {0}: Accepted connection from {1}.", newConn.PlayerIndex, newConn.EndPoint);

						if (client.Fingerprint != null)
							Log.Write("server", "Client {0}: Player fingerprint is {1}.", newConn.PlayerIndex, client.Fingerprint);

						foreach (var t in serverTraits.WithInterface<IClientJoined>())
							t.ClientJoined(this, newConn);

						SyncLobbyInfo();

						Log.Write("server", "{0} ({1}) has joined the game.", client.Name, newConn.EndPoint);

						if (Type != ServerType.Local)
							SendMessage($"{client.Name} has joined the game.");

						// Send initial ping
						SendOrderTo(newConn, "Ping", Game.RunTime.ToString(CultureInfo.InvariantCulture));

						if (Type == ServerType.Dedicated)
						{
							var motdFile = Path.Combine(Platform.SupportDir, "motd.txt");
							if (!File.Exists(motdFile))
								File.WriteAllText(motdFile, "Welcome, have fun and good luck!");

							var motd = File.ReadAllText(motdFile);
							if (!string.IsNullOrEmpty(motd))
								SendOrderTo(newConn, "Message", motd);
						}

						if ((LobbyInfo.GlobalSettings.MapStatus & Session.MapStatus.UnsafeCustomRules) != 0)
							SendOrderTo(newConn, "Message", "This map contains custom rules. Game experience may change.");

						if (!LobbyInfo.GlobalSettings.EnableSingleplayer)
							SendOrderTo(newConn, "Message", TwoHumansRequiredText);
						else if (Map.Players.Players.Where(p => p.Value.Playable).All(p => !p.Value.AllowBots))
							SendOrderTo(newConn, "Message", "Bots have been disabled on this map.");
					}
				};

				if (Type == ServerType.Local)
				{
					// Local servers can only be joined by the local client, so we can trust their identity without validation
					client.Fingerprint = handshake.Fingerprint;
					completeConnection();
				}
				else if (!string.IsNullOrEmpty(handshake.Fingerprint) && !string.IsNullOrEmpty(handshake.AuthSignature))
				{
					Task.Run(async () =>
					{
						var httpClient = HttpClientFactory.Create();
						var httpResponseMessage = await httpClient.GetAsync(playerDatabase.Profile + handshake.Fingerprint);
						var result = await httpResponseMessage.Content.ReadAsStringAsync();
						PlayerProfile profile = null;

						try
						{
							var yaml = MiniYaml.FromString(result).First();
							if (yaml.Key == "Player")
							{
								profile = FieldLoader.Load<PlayerProfile>(yaml.Value);

								var publicKey = Encoding.ASCII.GetString(Convert.FromBase64String(profile.PublicKey));
								var parameters = CryptoUtil.DecodePEMPublicKey(publicKey);
								if (!profile.KeyRevoked && CryptoUtil.VerifySignature(parameters, newConn.AuthToken, handshake.AuthSignature))
								{
									client.Fingerprint = handshake.Fingerprint;
									Log.Write("server", "{0} authenticated as {1} (UID {2})", newConn.EndPoint,
										profile.ProfileName, profile.ProfileID);
								}
								else if (profile.KeyRevoked)
								{
									profile = null;
									Log.Write("server", "{0} failed to authenticate as {1} (key revoked)", newConn.EndPoint, handshake.Fingerprint);
								}
								else
								{
									profile = null;
									Log.Write("server", "{0} failed to authenticate as {1} (signature verification failed)",
										newConn.EndPoint, handshake.Fingerprint);
								}
							}
							else
								Log.Write("server", "{0} failed to authenticate as {1} (invalid server response: `{2}` is not `Player`)",
									newConn.EndPoint, handshake.Fingerprint, yaml.Key);
						}
						catch (Exception ex)
						{
							Log.Write("server", "{0} failed to authenticate as {1} (exception occurred)",
								newConn.EndPoint, handshake.Fingerprint);
							Log.Write("server", ex.ToString());
						}

						events.Add(new CallbackEvent(() =>
						{
							var notAuthenticated = Type == ServerType.Dedicated && profile == null && (Settings.RequireAuthentication || Settings.ProfileIDWhitelist.Any());
							var blacklisted = Type == ServerType.Dedicated && profile != null && Settings.ProfileIDBlacklist.Contains(profile.ProfileID);
							var notWhitelisted = Type == ServerType.Dedicated && Settings.ProfileIDWhitelist.Any() &&
								(profile == null || !Settings.ProfileIDWhitelist.Contains(profile.ProfileID));

							if (notAuthenticated)
							{
								Log.Write("server", "Rejected connection from {0}; Not authenticated.", newConn.EndPoint);
								SendOrderTo(newConn, "ServerError", "Server requires players to have an OpenRA forum account");
								DropClient(newConn);
							}
							else if (blacklisted || notWhitelisted)
							{
								if (blacklisted)
									Log.Write("server", "Rejected connection from {0}; In server blacklist.", newConn.EndPoint);
								else
									Log.Write("server", "Rejected connection from {0}; Not in server whitelist.", newConn.EndPoint);

								SendOrderTo(newConn, "ServerError", "You do not have permission to join this server");
								DropClient(newConn);
							}
							else
								completeConnection();
						}));
					});
				}
				else
				{
					if (Type == ServerType.Dedicated && (Settings.RequireAuthentication || Settings.ProfileIDWhitelist.Any()))
					{
						Log.Write("server", "Rejected connection from {0}; Not authenticated.", newConn.EndPoint);
						SendOrderTo(newConn, "ServerError", "Server requires players to have an OpenRA forum account");
						DropClient(newConn);
					}
					else
						completeConnection();
				}
			}
			catch (Exception ex)
			{
				Log.Write("server", "Dropping connection {0} because an error occurred:", newConn.EndPoint);
				Log.Write("server", ex.ToString());
				DropClient(newConn);
			}
		}

		byte[] CreateFrame(int client, int frame, byte[] data)
		{
			var ms = new MemoryStream(data.Length + 12);
			ms.WriteArray(BitConverter.GetBytes(data.Length + 4));
			ms.WriteArray(BitConverter.GetBytes(client));
			ms.WriteArray(BitConverter.GetBytes(frame));
			ms.WriteArray(data);
			return ms.GetBuffer();
		}

		byte[] CreateAckFrame(int frame)
		{
			var ms = new MemoryStream(13);
			ms.WriteArray(BitConverter.GetBytes(5));
			ms.WriteArray(BitConverter.GetBytes(0));
			ms.WriteArray(BitConverter.GetBytes(frame));
			ms.WriteByte((byte)OrderType.Ack);
			return ms.GetBuffer();
		}

		void DispatchOrdersToClient(Connection c, int client, int frame, byte[] data)
		{
			DispatchFrameToClient(c, client, CreateFrame(client, frame, data));
		}

		void DispatchFrameToClient(Connection c, int client, byte[] frameData)
		{
			try
			{
				c.SendData(frameData);
			}
			catch (Exception e)
			{
				DropClient(c);
				Log.Write("server", "Dropping client {0} because dispatching orders failed: {1}",
					client.ToString(CultureInfo.InvariantCulture), e);
			}
		}

		bool AnyUndefinedWinStates()
		{
			var lastTeam = -1;
			var remainingPlayers = gameInfo.Players.Where(p => p.Outcome == WinState.Undefined);
			foreach (var player in remainingPlayers)
			{
				if (lastTeam >= 0 && (player.Team != lastTeam || player.Team == 0))
					return true;

				lastTeam = player.Team;
			}

			return false;
		}

		void SetPlayerDefeat(int playerIndex)
		{
			var defeatedPlayer = worldPlayers[playerIndex];
			if (defeatedPlayer == null || defeatedPlayer.Outcome != WinState.Undefined)
				return;

			defeatedPlayer.Outcome = WinState.Lost;
			defeatedPlayer.OutcomeTimestampUtc = DateTime.UtcNow;

			// Set remaining players as winners if only one side remains
			if (!AnyUndefinedWinStates())
			{
				var now = DateTime.UtcNow;
				var remainingPlayers = gameInfo.Players.Where(p => p.Outcome == WinState.Undefined);
				foreach (var winner in remainingPlayers)
				{
					winner.Outcome = WinState.Won;
					winner.OutcomeTimestampUtc = now;
				}
			}
		}

		void OutOfSync(int frame)
		{
			Log.Write("server", "Out of sync detected at frame {0}, cancel replay recording", frame);

			// Make sure the written file is not valid
			// TODO: storing a serverside replay on desync would be extremely useful
			recorder.Metadata = null;

			recorder.Dispose();

			// Stop the recording
			recorder = null;
		}

		readonly Dictionary<int, byte[]> syncForFrame = new Dictionary<int, byte[]>();
		int lastDefeatStateFrame;
		ulong lastDefeatState;

		void HandleSyncOrder(int frame, byte[] packet)
		{
			if (syncForFrame.TryGetValue(frame, out var existingSync))
			{
				if (packet.Length != existingSync.Length)
				{
					OutOfSync(frame);
					return;
				}

				for (var i = 0; i < packet.Length; i++)
				{
					if (packet[i] != existingSync[i])
					{
						OutOfSync(frame);
						return;
					}
				}
			}
			else
			{
				// Update player losses based on the new defeat state.
				// Do this once for the first player, the check above
				// guarantees a desync if any other player disagrees.
				var playerDefeatState = BitConverter.ToUInt64(packet, 1 + 4);
				if (frame > lastDefeatStateFrame && lastDefeatState != playerDefeatState)
				{
					var newDefeats = playerDefeatState & ~lastDefeatState;
					for (var i = 0; i < worldPlayers.Count; i++)
						if ((newDefeats & (1UL << i)) != 0)
							SetPlayerDefeat(i);

					lastDefeatState = playerDefeatState;
					lastDefeatStateFrame = frame;
				}

				syncForFrame.Add(frame, packet);
			}
		}

		public void DispatchOrdersToClients(Connection conn, int frame, byte[] data)
		{
			var from = conn.PlayerIndex;
			var frameData = CreateFrame(from, frame, data);
			foreach (var c in Conns.ToList())
				if (c != conn && c.Validated)
					DispatchFrameToClient(c, from, frameData);

			RecordOrder(frame, data, from);
		}

		void RecordOrder(int frame, byte[] data, int from)
		{
			if (recorder != null)
			{
				recorder.ReceiveFrame(from, frame, data);

				if (data.Length > 0 && data[0] == (byte)OrderType.SyncHash)
				{
					if (data.Length == Order.SyncHashOrderLength)
						HandleSyncOrder(frame, data);
					else
						Log.Write("server", $"Dropped sync order with length {data.Length} from client {from}. Expected length {Order.SyncHashOrderLength}.");
				}
			}
		}

		public void DispatchServerOrdersToClients(Order order)
		{
			DispatchServerOrdersToClients(order.Serialize());
		}

		public void DispatchServerOrdersToClients(byte[] data)
		{
			var from = 0;
			var frame = 0;
			var frameData = CreateFrame(from, frame, data);
			foreach (var c in Conns.ToList())
				if (c.Validated)
					DispatchFrameToClient(c, from, frameData);

			RecordOrder(frame, data, from);
		}

		public void ReceiveOrders(Connection conn, int frame, byte[] data)
		{
			if (frame == 0)
				InterpretServerOrders(conn, data);
			else
			{
				// Non-immediate orders must be projected into the future so that all players can
				// apply them on the same world tick. We can do this directly when forwarding the
				// packet on to other clients, but sending the same data back to the client that
				// sent it just to update the frame number would be wasteful. We instead send them
				// a separate Ack packet that tells them to apply the order from a locally stored queue.
				// TODO: Replace static latency with a dynamic order buffering system
				if (data.Length == 0 || data[0] != (byte)OrderType.SyncHash)
				{
					frame += OrderLatency;
					DispatchFrameToClient(conn, conn.PlayerIndex, CreateAckFrame(frame));
				}

				DispatchOrdersToClients(conn, frame, data);
			}

			GameSave?.DispatchOrders(conn, frame, data);
		}

		void InterpretServerOrders(Connection conn, byte[] data)
		{
			var ms = new MemoryStream(data);
			var br = new BinaryReader(ms);

			try
			{
				while (ms.Position < ms.Length)
				{
					var o = Order.Deserialize(null, br);
					if (o != null)
						InterpretServerOrder(conn, o);
				}
			}
			catch (EndOfStreamException) { }
			catch (NotImplementedException) { }
		}

		public void SendOrderTo(Connection conn, string order, string data)
		{
			DispatchOrdersToClient(conn, 0, 0, Order.FromTargetString(order, data, true).Serialize());
		}

		public void SendMessage(string text)
		{
			DispatchServerOrdersToClients(Order.FromTargetString("Message", text, true));

			if (Type == ServerType.Dedicated)
				Console.WriteLine($"[{DateTime.Now.ToString(Settings.TimestampFormat)}] {text}");
		}

		void InterpretServerOrder(Connection conn, Order o)
		{
			lock (LobbyInfo)
			{
				// Only accept handshake responses from unvalidated clients
				// Anything else may be an attempt to exploit the server
				if (!conn.Validated)
				{
					if (o.OrderString == "HandshakeResponse")
						ValidateClient(conn, o.TargetString);
					else
					{
						Log.Write("server", "Rejected connection from {0}; Order `{1}` is not a `HandshakeResponse`.",
							conn.EndPoint, o.OrderString);

						DropClient(conn);
					}

					return;
				}

				switch (o.OrderString)
				{
					case "Command":
						{
							var handledBy = serverTraits.WithInterface<IInterpretCommand>()
								.FirstOrDefault(t => t.InterpretCommand(this, conn, GetClient(conn), o.TargetString));

							if (handledBy == null)
							{
								Log.Write("server", "Unknown server command: {0}", o.TargetString);
								SendOrderTo(conn, "Message", $"Unknown server command: {o.TargetString}");
							}

							break;
						}

					case "Chat":
						DispatchOrdersToClients(conn, 0, o.Serialize());
						break;
					case "Pong":
						{
							if (!OpenRA.Exts.TryParseInt64Invariant(o.TargetString, out var pingSent))
							{
								Log.Write("server", "Invalid order pong payload: {0}", o.TargetString);
								break;
							}

							var client = GetClient(conn);
							if (client == null)
								return;

							var pingFromClient = LobbyInfo.PingFromClient(client);
							if (pingFromClient == null)
								return;

							var history = pingFromClient.LatencyHistory.ToList();
							history.Add(Game.RunTime - pingSent);

							// Cap ping history at 5 values (25 seconds)
							if (history.Count > 5)
								history.RemoveRange(0, history.Count - 5);

							pingFromClient.Latency = history.Sum() / history.Count;
							pingFromClient.LatencyJitter = (history.Max() - history.Min()) / 2;
							pingFromClient.LatencyHistory = history.ToArray();

							SyncClientPing();

							break;
						}

					case "GameSaveTraitData":
						{
							if (GameSave != null)
							{
								var data = MiniYaml.FromString(o.TargetString)[0];
								GameSave.AddTraitData(int.Parse(data.Key), data.Value);
							}

							break;
						}

					case "CreateGameSave":
						{
							if (GameSave != null)
							{
								// Sanitize potentially malicious input
								var filename = o.TargetString;
								var invalidIndex = -1;
								var invalidChars = Path.GetInvalidFileNameChars();
								while ((invalidIndex = filename.IndexOfAny(invalidChars)) != -1)
									filename = filename.Remove(invalidIndex, 1);

								var baseSavePath = Path.Combine(
									Platform.SupportDir,
									"Saves",
									ModData.Manifest.Id,
									ModData.Manifest.Metadata.Version);

								if (!Directory.Exists(baseSavePath))
									Directory.CreateDirectory(baseSavePath);

								GameSave.Save(Path.Combine(baseSavePath, filename));
								DispatchServerOrdersToClients(Order.FromTargetString("GameSaved", filename, true));
							}

							break;
						}

					case "LoadGameSave":
						{
							if (Type == ServerType.Dedicated || State >= ServerState.GameStarted)
								break;

							// Sanitize potentially malicious input
							var filename = o.TargetString;
							var invalidIndex = -1;
							var invalidChars = Path.GetInvalidFileNameChars();
							while ((invalidIndex = filename.IndexOfAny(invalidChars)) != -1)
								filename = filename.Remove(invalidIndex, 1);

							var savePath = Path.Combine(
								Platform.SupportDir,
								"Saves",
								ModData.Manifest.Id,
								ModData.Manifest.Metadata.Version,
								filename);

							GameSave = new GameSave(savePath);
							LobbyInfo.GlobalSettings = GameSave.GlobalSettings;
							LobbyInfo.Slots = GameSave.Slots;

							// Reassign clients to slots
							//  - Bot ordering is preserved
							//  - Humans are assigned on a first-come-first-serve basis
							//  - Leftover humans become spectators

							// Start by removing all bots and assigning all players as spectators
							foreach (var c in LobbyInfo.Clients)
							{
								if (c.Bot != null)
								{
									LobbyInfo.Clients.Remove(c);
									var ping = LobbyInfo.PingFromClient(c);
									if (ping != null)
										LobbyInfo.ClientPings.Remove(ping);
								}
								else
									c.Slot = null;
							}

							// Rebuild/remap the saved client state
							// TODO: Multiplayer saves should leave all humans as spectators so they can manually pick slots
							var adminClientIndex = LobbyInfo.Clients.First(c => c.IsAdmin).Index;
							foreach (var kv in GameSave.SlotClients)
							{
								if (kv.Value.Bot != null)
								{
									var bot = new Session.Client()
									{
										Index = ChooseFreePlayerIndex(),
										State = Session.ClientState.NotReady,
										BotControllerClientIndex = adminClientIndex
									};

									kv.Value.ApplyTo(bot);
									LobbyInfo.Clients.Add(bot);
								}
								else
								{
									// This will throw if the server doesn't have enough human clients to fill all player slots
									// See TODO above - this isn't a problem in practice because MP saves won't use this
									var client = LobbyInfo.Clients.First(c => c.Slot == null);
									kv.Value.ApplyTo(client);
								}
							}

							SyncLobbyInfo();
							SyncLobbyClients();
							SyncClientPing();

							break;
						}
				}
			}
		}

		public Session.Client GetClient(Connection conn)
		{
			if (conn == null)
				return null;

			return LobbyInfo.ClientWithIndex(conn.PlayerIndex);
		}

		public void DropClient(Connection toDrop)
		{
			lock (LobbyInfo)
			{
				Conns.Remove(toDrop);

				var dropClient = LobbyInfo.Clients.FirstOrDefault(c1 => c1.Index == toDrop.PlayerIndex);
				if (dropClient == null)
				{
					toDrop.Dispose();
					return;
				}

				var suffix = "";
				if (State == ServerState.GameStarted)
					suffix = dropClient.IsObserver ? " (Spectator)" : dropClient.Team != 0 ? $" (Team {dropClient.Team})" : "";
				SendMessage($"{dropClient.Name}{suffix} has disconnected.");

				// Send disconnected order, even if still in the lobby
				DispatchOrdersToClients(toDrop, 0, Order.FromTargetString("Disconnected", "", true).Serialize());

				if (gameInfo != null && !dropClient.IsObserver)
				{
					var disconnectedPlayer = gameInfo.Players.First(p => p.ClientIndex == toDrop.PlayerIndex);
					disconnectedPlayer.DisconnectFrame = toDrop.MostRecentFrame;
				}

				LobbyInfo.Clients.RemoveAll(c => c.Index == toDrop.PlayerIndex);
				LobbyInfo.ClientPings.RemoveAll(p => p.Index == toDrop.PlayerIndex);

				// Client was the server admin
				// TODO: Reassign admin for game in progress via an order
				if (Type == ServerType.Dedicated && dropClient.IsAdmin && State == ServerState.WaitingPlayers)
				{
					// Remove any bots controlled by the admin
					LobbyInfo.Clients.RemoveAll(c => c.Bot != null && c.BotControllerClientIndex == toDrop.PlayerIndex);

					var nextAdmin = LobbyInfo.Clients.Where(c1 => c1.Bot == null)
						.MinByOrDefault(c => c.Index);

					if (nextAdmin != null)
					{
						nextAdmin.IsAdmin = true;
						SendMessage($"{nextAdmin.Name} is now the admin.");
					}
				}

				var disconnectPacket = new MemoryStream(5);
				disconnectPacket.WriteByte((byte)OrderType.Disconnect);
				disconnectPacket.Write(toDrop.PlayerIndex);
				DispatchServerOrdersToClients(disconnectPacket.ToArray());

				// All clients have left: clean up
				if (!Conns.Any(c => c.Validated))
					foreach (var t in serverTraits.WithInterface<INotifyServerEmpty>())
						t.ServerEmpty(this);

				if (Conns.Any(c => c.Validated) || Type == ServerType.Dedicated)
					SyncLobbyClients();

				if (Type != ServerType.Dedicated && dropClient.IsAdmin)
					Shutdown();
			}

			toDrop.Dispose();
		}

		public void SyncLobbyInfo()
		{
			lock (LobbyInfo)
			{
				if (State == ServerState.WaitingPlayers) // Don't do this while the game is running, it breaks things!
					DispatchServerOrdersToClients(Order.FromTargetString("SyncInfo", LobbyInfo.Serialize(), true));

				foreach (var t in serverTraits.WithInterface<INotifySyncLobbyInfo>())
					t.LobbyInfoSynced(this);
			}
		}

		public void SyncLobbyClients()
		{
			if (State != ServerState.WaitingPlayers)
				return;

			lock (LobbyInfo)
			{
				// TODO: Only need to sync the specific client that has changed to avoid conflicts!
				var clientData = LobbyInfo.Clients.Select(client => client.Serialize()).ToList();

				DispatchServerOrdersToClients(Order.FromTargetString("SyncLobbyClients", clientData.WriteToString(), true));

				foreach (var t in serverTraits.WithInterface<INotifySyncLobbyInfo>())
					t.LobbyInfoSynced(this);
			}
		}

		public void SyncLobbySlots()
		{
			if (State != ServerState.WaitingPlayers)
				return;

			lock (LobbyInfo)
			{
				// TODO: Don't sync all the slots if just one changed!
				var slotData = LobbyInfo.Slots.Select(slot => slot.Value.Serialize()).ToList();

				DispatchServerOrdersToClients(Order.FromTargetString("SyncLobbySlots", slotData.WriteToString(), true));

				foreach (var t in serverTraits.WithInterface<INotifySyncLobbyInfo>())
					t.LobbyInfoSynced(this);
			}
		}

		public void SyncLobbyGlobalSettings()
		{
			if (State != ServerState.WaitingPlayers)
				return;

			lock (LobbyInfo)
			{
				var sessionData = new List<MiniYamlNode> { LobbyInfo.GlobalSettings.Serialize() };

				DispatchServerOrdersToClients(Order.FromTargetString("SyncLobbyGlobalSettings", sessionData.WriteToString(), true));

				foreach (var t in serverTraits.WithInterface<INotifySyncLobbyInfo>())
					t.LobbyInfoSynced(this);
			}
		}

		public void SyncClientPing()
		{
			lock (LobbyInfo)
			{
				// TODO: Split this further into per client ping orders
				var clientPings = LobbyInfo.ClientPings.Select(ping => ping.Serialize()).ToList();

				// Note that syncing pings doesn't trigger INotifySyncLobbyInfo
				DispatchServerOrdersToClients(Order.FromTargetString("SyncClientPings", clientPings.WriteToString(), true));
			}
		}

		public void StartGame()
		{
			lock (LobbyInfo)
			{
				Console.WriteLine("[{0}] Game started", DateTime.Now.ToString(Settings.TimestampFormat));

				// Drop any players who are not ready
				foreach (var c in Conns.Where(c => !c.Validated || GetClient(c).IsInvalid).ToArray())
				{
					SendOrderTo(c, "ServerError", "You have been kicked from the server!");
					DropClient(c);
				}

				// Enable game saves for singleplayer missions only
				// TODO: Enable for multiplayer (non-dedicated servers only) once the lobby UI has been created
				LobbyInfo.GlobalSettings.GameSavesEnabled = Type != ServerType.Dedicated && LobbyInfo.NonBotClients.Count() == 1;

				// Player list for win/loss tracking
				// HACK: NonCombatant and non-Playable players are set to null to simplify replay tracking
				// The null padding is needed to keep the player indexes in sync with world.Players on the clients
				// This will need to change if future code wants to use worldPlayers for other purposes
				var playerRandom = new MersenneTwister(LobbyInfo.GlobalSettings.RandomSeed);
				foreach (var cmpi in Map.WorldActorInfo.TraitInfos<ICreatePlayersInfo>())
					cmpi.CreateServerPlayers(Map, LobbyInfo, worldPlayers, playerRandom);

				if (recorder != null)
				{
					gameInfo = new GameInformation
					{
						Mod = Game.ModData.Manifest.Id,
						Version = Game.ModData.Manifest.Metadata.Version,
						MapUid = Map.Uid,
						MapTitle = Map.Title,
						StartTimeUtc = DateTime.UtcNow,
					};

					// Replay metadata should only include the playable players
					foreach (var p in worldPlayers)
						if (p != null)
							gameInfo.Players.Add(p);

					recorder.Metadata = new ReplayMetadata(gameInfo);
				}

				SyncLobbyInfo();
				State = ServerState.GameStarted;

				if (Type != ServerType.Local)
				{
					var gameSpeeds = Game.ModData.Manifest.Get<GameSpeeds>();
					var gameSpeedName = LobbyInfo.GlobalSettings.OptionOrDefault("gamespeed", gameSpeeds.DefaultSpeed);
					OrderLatency = gameSpeeds.Speeds[gameSpeedName].OrderLatency;
				}

				if (GameSave == null && LobbyInfo.GlobalSettings.GameSavesEnabled)
					GameSave = new GameSave();

				var startGameData = "";
				if (GameSave != null)
				{
					GameSave.StartGame(LobbyInfo, Map);
					if (GameSave.LastOrdersFrame >= 0)
					{
						startGameData = new List<MiniYamlNode>()
						{
							new MiniYamlNode("SaveLastOrdersFrame", GameSave.LastOrdersFrame.ToString()),
							new MiniYamlNode("SaveSyncFrame", GameSave.LastSyncFrame.ToString())
						}.WriteToString();
					}
				}

				DispatchServerOrdersToClients(Order.FromTargetString("StartGame", startGameData, true));

				foreach (var t in serverTraits.WithInterface<IStartGame>())
					t.GameStarted(this);

				var firstFrame = 1;
				if (GameSave != null && GameSave.LastOrdersFrame >= 0)
				{
					GameSave.ParseOrders(LobbyInfo, (frame, client, data) =>
					{
						foreach (var c in Conns)
							if (c.Validated)
								DispatchOrdersToClient(c, client, frame, data);
					});

					firstFrame += GameSave.LastOrdersFrame;
				}

				// ReceiveOrders projects player orders into the future so that all players can
				// apply them on the same world tick.
				// Clients require every frame to have an orders packet associated with it, so we must
				// inject an empty packet for each frame that we are skipping forwards.
				// TODO: Replace static latency with a dynamic order buffering system
				var conns = Conns.Where(c => c.Validated).ToList();
				foreach (var from in conns)
				{
					for (var i = 0; i < OrderLatency; i++)
					{
						var frame = firstFrame + i;
						var frameData = CreateFrame(from.PlayerIndex, frame, Array.Empty<byte>());
						foreach (var to in conns)
							DispatchFrameToClient(to, from.PlayerIndex, frameData);

						RecordOrder(frame, Array.Empty<byte>(), from.PlayerIndex);
						GameSave?.DispatchOrders(from, frame, Array.Empty<byte>());
					}
				}
			}
		}

		public ConnectionTarget GetEndpointForLocalConnection()
		{
			var endpoints = new List<DnsEndPoint>();
			foreach (var listener in listeners)
			{
				var endpoint = (IPEndPoint)listener.LocalEndpoint;
				if (IPAddress.IPv6Any.Equals(endpoint.Address))
					endpoints.Add(new DnsEndPoint(IPAddress.IPv6Loopback.ToString(), endpoint.Port));
				else if (IPAddress.Any.Equals(endpoint.Address))
					endpoints.Add(new DnsEndPoint(IPAddress.Loopback.ToString(), endpoint.Port));
				else
					endpoints.Add(new DnsEndPoint(endpoint.Address.ToString(), endpoint.Port));
			}

			return new ConnectionTarget(endpoints);
		}

		interface IServerEvent { void Invoke(Server server); }

		class ClientConnectEvent : IServerEvent
		{
			readonly Socket socket;
			public ClientConnectEvent(Socket socket)
			{
				this.socket = socket;
			}

			void IServerEvent.Invoke(Server server)
			{
				server.AcceptConnection(socket);
			}
		}

		class ClientDisconnectEvent : IServerEvent
		{
			readonly Connection connection;
			public ClientDisconnectEvent(Connection connection)
			{
				this.connection = connection;
			}

			void IServerEvent.Invoke(Server server)
			{
				server.DropClient(connection);
			}
		}

		class ClientPacketEvent : IServerEvent
		{
			readonly Connection connection;
			readonly int frame;
			readonly byte[] data;

			public ClientPacketEvent(Connection connection, int frame, byte[] data)
			{
				this.connection = connection;
				this.frame = frame;
				this.data = data;
			}

			void IServerEvent.Invoke(Server server)
			{
				server.ReceiveOrders(connection, frame, data);
			}
		}

		class CallbackEvent : IServerEvent
		{
			readonly Action action;

			public CallbackEvent(Action action)
			{
				this.action = action;
			}

			void IServerEvent.Invoke(Server server)
			{
				action();
			}
		}
	}
}
