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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenRA.Support;
using OpenRA.Widgets;

namespace OpenRA.Network
{
	public sealed class OrderManager : IDisposable
	{
		readonly SyncReport syncReport;
		readonly Dictionary<int, Queue<(int Frame, OrderPacket Orders)>> pendingOrders = new Dictionary<int, Queue<(int, OrderPacket)>>();
		readonly Dictionary<int, (int SyncHash, ulong DefeatState)> syncForFrame = new Dictionary<int, (int, ulong)>();

		public Session LobbyInfo = new Session();
		public Session.Client LocalClient => LobbyInfo.ClientWithIndex(Connection.LocalClientId);
		public World World;

		public string ServerError = null;
		public bool AuthenticationFailed = false;

		public int NetFrameNumber { get; private set; }
		public int LocalFrameNumber;

		public TickTime LastTickTime;

		public bool GameStarted => NetFrameNumber != 0;
		public IConnection Connection { get; private set; }

		internal int GameSaveLastFrame = -1;
		internal int GameSaveLastSyncFrame = -1;

		readonly List<Order> localOrders = new List<Order>();
		readonly List<Order> localImmediateOrders = new List<Order>();

		readonly List<TextNotification> notificationsCache = new List<TextNotification>();

		public IReadOnlyList<TextNotification> NotificationsCache => notificationsCache;

		bool disposed;
		bool generateSyncReport = false;
		int sentOrdersFrame = 0;

		public struct ClientOrder
		{
			public int Client;
			public Order Order;

			public override string ToString()
			{
				return $"ClientId: {Client} {Order}";
			}
		}

		void OutOfSync(int frame)
		{
			syncReport.DumpSyncReport(frame);
			throw new InvalidOperationException($"Out of sync in frame {frame}.\n Compare syncreport.log with other players.");
		}

		public void StartGame()
		{
			if (GameStarted)
				return;

			foreach (var client in LobbyInfo.Clients)
				if (!client.IsBot)
					pendingOrders.Add(client.Index, new Queue<(int, OrderPacket)>());

			// Generating sync reports is expensive, so only do it if we have
			// other players to compare against if a desync did occur
			generateSyncReport = !(Connection is ReplayConnection) && LobbyInfo.GlobalSettings.EnableSyncReports;

			NetFrameNumber = 1;
			LocalFrameNumber = 0;
			LastTickTime.Value = Game.RunTime;

			Connection.StartGame();
		}

		public OrderManager(IConnection conn)
		{
			Connection = conn;
			syncReport = new SyncReport(this);
			AddTextNotification += CacheTextNotification;

			LastTickTime = new TickTime(() => SuggestedTimestep, Game.RunTime);
		}

		public void IssueOrders(Order[] orders)
		{
			foreach (var order in orders)
				IssueOrder(order);
		}

		public void IssueOrder(Order order)
		{
			if (order.IsImmediate)
				localImmediateOrders.Add(order);
			else
				localOrders.Add(order);
		}

		public Action<TextNotification> AddTextNotification = (notification) => { };
		void CacheTextNotification(TextNotification notification)
		{
			notificationsCache.Add(notification);
		}

		void SendImmediateOrders()
		{
			if (localImmediateOrders.Count != 0 && GameSaveLastFrame < NetFrameNumber)
				Connection.SendImmediate(localImmediateOrders);
			localImmediateOrders.Clear();
		}

		public void ReceiveDisconnect(int clientIndex)
		{
			pendingOrders.Remove(clientIndex);
		}

		public void ReceiveSync((int Frame, int SyncHash, ulong DefeatState) sync)
		{
			if (syncForFrame.TryGetValue(sync.Frame, out var s))
			{
				if (s.SyncHash != sync.SyncHash || s.DefeatState != sync.DefeatState)
					OutOfSync(sync.Frame);
			}
			else
				syncForFrame.Add(sync.Frame, (sync.SyncHash, sync.DefeatState));
		}

		public void ReceiveImmediateOrders(int clientId, OrderPacket orders)
		{
			foreach (var o in orders.GetOrders(World))
			{
				UnitOrders.ProcessOrder(this, World, clientId, o);

				// A mod switch or other event has pulled the ground from beneath us
				if (disposed)
					return;
			}
		}

		public void ReceiveOrders(int clientId, (int Frame, OrderPacket Orders) orders)
		{
			if (pendingOrders.TryGetValue(clientId, out var queue))
				queue.Enqueue((orders.Frame, orders.Orders));
			else
				throw new InvalidDataException($"Received packet from disconnected client '{clientId}'");
		}

		void ReceiveAllOrdersAndCheckSync()
		{
			Connection.Receive(this);
		}

		bool IsReadyForNextFrame => GameStarted && pendingOrders.All(p => p.Value.Count > 0);

		int SuggestedTimestep
		{
			get
			{
				if (World == null)
					return Ui.Timestep;

				if (World.IsLoadingGameSave)
					return 1;

				if (World.IsReplay)
					return World.ReplayTimestep;

				return World.Timestep;
			}
		}

		void SendOrders()
		{
			if (GameStarted && GameSaveLastFrame < NetFrameNumber && sentOrdersFrame < NetFrameNumber)
			{
				Connection.Send(NetFrameNumber, localOrders);
				localOrders.Clear();
				sentOrdersFrame = NetFrameNumber;
			}
		}

		void ProcessOrders()
		{
			var clientOrders = new List<ClientOrder>();

			foreach (var (clientId, frameOrders) in pendingOrders)
			{
				// The IsReadyForNextFrame check above guarantees that all clients have sent a packet
				var (frameNumber, orders) = frameOrders.Dequeue();

				// We expect every frame to have a queued order packet, even if it contains no orders, as this
				// controls the pacing of the game simulation.
				// Sanity check that we are processing the frame that we expect, so we can crash early instead of desyncing.
				if (frameNumber != NetFrameNumber)
					throw new InvalidDataException($"Attempted to process orders from client {clientId} for frame {frameNumber} on frame {NetFrameNumber}");

				foreach (var order in orders.GetOrders(World))
				{
					UnitOrders.ProcessOrder(this, World, clientId, order);
					clientOrders.Add(new ClientOrder { Client = clientId, Order = order });
				}
			}

			if (NetFrameNumber >= GameSaveLastSyncFrame)
			{
				var defeatState = 0UL;
				for (var i = 0; i < World.Players.Length; i++)
					if (World.Players[i].WinState == WinState.Lost)
						defeatState |= 1UL << i;

				Connection.SendSync(NetFrameNumber, World.SyncHash(), defeatState);
			}
			else
				Connection.SendSync(NetFrameNumber, 0, 0);

			if (generateSyncReport)
				using (new PerfSample("sync_report"))
					syncReport.UpdateSyncReport(clientOrders);

			++NetFrameNumber;
		}

		public void Dispose()
		{
			disposed = true;
			Connection?.Dispose();
		}

		public void TickImmediate()
		{
			SendImmediateOrders();

			ReceiveAllOrdersAndCheckSync();
		}

		public bool TryTick()
		{
			var shouldTick = true;

			if (IsNetTick)
			{
				// Check whether or not we will be ready for a tick next frame
				// We don't need to include ourselves in the equation because we can always generate orders this frame
				shouldTick = pendingOrders.All(p => p.Key == Connection.LocalClientId || p.Value.Count > 0);

				// Send orders only if we are currently ready, this prevents us sending orders too soon if we are
				// stalling
				if (shouldTick)
					SendOrders();
			}

			var willTick = shouldTick;
			if (willTick && IsNetTick)
			{
				willTick = IsReadyForNextFrame;
				if (willTick)
					ProcessOrders();
			}

			if (willTick)
				LocalFrameNumber++;

			return willTick;
		}

		bool IsNetTick => LocalFrameNumber % Game.NetTickScale == 0;
	}
}
