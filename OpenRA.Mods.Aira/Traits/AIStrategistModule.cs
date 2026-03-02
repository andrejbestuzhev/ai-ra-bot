#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
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
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Traits;
using OpenRA.Traits;

namespace OpenRA.Mods.Aira.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("AI strategist — full control over building, unit production, and army movement via LLM API.")]
	public class AIStrategistModuleInfo : ConditionalTraitInfo
	{
		[Desc("Interval in ticks between strategy evaluations. 375 = 15 seconds at 25 TPS.")]
		public readonly int DecisionIntervalTicks = 375;

		[Desc("LLM API model identifier.")]
		public readonly string ApiModel = "";

		[Desc("Maximum tokens for LLM API response.")]
		public readonly int MaxTokens = 1024;

		[Desc("API key override. If empty, reads from user.config or ANTHROPIC_API_KEY env var.")]
		public readonly string ApiKey = "";

		[Desc("Enable sending requests to LLM API.")]
		public readonly bool EnableApi = true;

		[Desc("Maximum conversation history turns to keep (each turn = 1 user + 1 assistant message).")]
		public readonly int MaxHistoryTurns = 10;

		public override object Create(ActorInitializer init) { return new AIStrategistModule(init.Self, this); }
	}

	public class AIStrategistModule : ConditionalTrait<AIStrategistModuleInfo>,
		IBotTick, IBotEnabled, IBotRespondToAttack
	{
		const int MaxPlacementRetries = 50;

		static readonly HttpClient ApiClient = new()
		{
			Timeout = TimeSpan.FromSeconds(30)
		};

		readonly World world;
		readonly Player player;

		PlayerResources playerResources;
		PowerManager powerManager;
		int ticksSinceLastDecision;
		bool isFirstTick = true;

		// Async LLM API call
		Task<(AIDecision Decision, string RawResponse)> pendingDecision;
		string apiKey;

		// Income tracking
		int lastMoney;
		int estimatedIncomePerMin;

		// Building construction queue (populated by AI decisions)
		readonly Queue<string> buildQueue = new();
		int placementRetries;
		readonly HashSet<string> pendingPlacements = new();

		// Unit production orders (populated by AI decisions)
		readonly List<ProduceOrder> produceOrders = new();

		// Conversation history — multi-turn chat with LLM
		readonly List<ChatMessage> conversationHistory = new();

		// Attack tracking
		readonly List<AttackEvent> recentAttacks = new();

		// Loss tracking — previous cycle counts
		int prevBuildingCount;
		int prevUnitCount;
		readonly List<string> recentLosses = new();

		// Turn counter (not affected by history trimming)
		int totalTurns;
		bool logProductionDiagNextTick;

		// File logging
		string logFilePath;
		StreamWriter logWriter;

		public AIStrategistModule(Actor self, AIStrategistModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
		}

		protected override void Created(Actor self)
		{
			apiKey = !string.IsNullOrEmpty(Info.ApiKey)
				? Info.ApiKey
				: ResolveApiKey();
		}

		static string ResolveApiKey()
		{
			try
			{
				var candidates = new[]
				{
					Path.Combine(Platform.EngineDir, "..", "user.config"),
					Path.Combine(Platform.EngineDir, "user.config"),
				};

				foreach (var path in candidates)
				{
					if (!File.Exists(path))
						continue;

					foreach (var line in File.ReadAllLines(path))
					{
						var trimmed = line.Trim();
						if (!trimmed.StartsWith("ANTHROPIC_API_KEY", StringComparison.Ordinal))
							continue;

						var eqIndex = trimmed.IndexOf('=');
						if (eqIndex < 0)
							continue;

						var value = trimmed[(eqIndex + 1)..].Trim().Trim('"');
						if (!string.IsNullOrEmpty(value))
							return value;
					}
				}
			}
			catch
			{
				// Fall through to env var
			}

			return Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
		}

		void IBotEnabled.BotEnabled(IBot bot)
		{
			playerResources = player.PlayerActor.Trait<PlayerResources>();
			powerManager = player.PlayerActor.TraitOrDefault<PowerManager>();

			// Init log file — only here, because BotEnabled is only called for actual bots
			try
			{
				var logDir = Path.Combine(Platform.SupportDir, "Logs");
				Directory.CreateDirectory(logDir);
				logFilePath = Path.Combine(logDir, "ai-strategist.log");
				var fs = new FileStream(logFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
				logWriter = new StreamWriter(fs) { AutoFlush = true };
			}
			catch
			{
				// Non-critical
			}

			Log("[AI] ========================================");
			Log("[AI] AI Strategist activated for {0}", player.PlayerName);
			Log("[AI] Mode: FULL CONTROL | Model: {0} | Interval: {1}s | History: {2} turns",
				Info.ApiModel, Info.DecisionIntervalTicks / 25, Info.MaxHistoryTurns);
			Log("[AI] API: {0} | Log: {1}",
				!string.IsNullOrEmpty(apiKey) ? "configured" : "NOT configured",
				logFilePath ?? "none");
			Log("[AI] ========================================");
		}

		void IBotTick.BotTick(IBot bot)
		{
			// Every tick: place ready buildings, advance production
			TryPlaceBuildings(bot);
			TryStartBuildingProduction(bot);
			TryStartUnitProduction(bot);

			// Check if pending API call completed
			if (pendingDecision != null && pendingDecision.IsCompleted)
			{
				ProcessApiResult(bot);
				pendingDecision = null;
			}

			// Time for new decision?
			if (++ticksSinceLastDecision < Info.DecisionIntervalTicks)
				return;

			ticksSinceLastDecision = 0;

			if (pendingDecision != null)
				return;

			RequestNewDecision();
		}

		#region Attack Detection

		void IBotRespondToAttack.RespondToAttack(IBot bot, Actor self, AttackInfo e)
		{
			if (self.IsDead || self.Owner != player)
				return;

			var attackerName = e.Attacker != null && !e.Attacker.IsDead
				? e.Attacker.Info.Name : "unknown";

			recentAttacks.Add(new AttackEvent
			{
				TargetName = self.Info.Name,
				AttackerName = attackerName,
				Tick = world.WorldTick,
				IsBuilding = self.Info.HasTraitInfo<BuildingInfo>()
			});

			// Cap attack log to avoid memory bloat
			if (recentAttacks.Count > 100)
				recentAttacks.RemoveRange(0, 50);
		}

		#endregion

		#region Building Placement

		void TryPlaceBuildings(IBot bot)
		{
			var queues = AIUtils.FindQueuesByCategory(player);

			foreach (var queue in queues["Building"].Concat(queues["Defense"]))
			{
				var queueActorId = queue.Actor.ActorID;
				var pendingKey = $"{queueActorId}:{queue.Info.Type}";
				var current = queue.CurrentItem();

				if (current == null || !current.Done)
				{
					// Item gone or not done — clear pending flag
					pendingPlacements.Remove(pendingKey);
					continue;
				}

				// Already sent placement order — wait for engine to process it
				if (pendingPlacements.Contains(pendingKey))
					continue;

				var location = FindBuildingPlacement(current.Item);
				if (location.HasValue)
				{
					Log("[AI] Placing {0} at ({1},{2})",
						current.Item, location.Value.X, location.Value.Y);

					bot.QueueOrder(new Order("PlaceBuilding", player.PlayerActor,
						Target.FromCell(world, location.Value), false)
					{
						TargetString = current.Item,
						ExtraLocation = CPos.Zero,
						ExtraData = queueActorId,
						SuppressVisualFeedback = true
					});

					pendingPlacements.Add(pendingKey);
					placementRetries = 0;
				}
				else
				{
					placementRetries++;
					if (placementRetries > MaxPlacementRetries)
					{
						Log("[AI] Cannot place {0}, canceling after {1} retries",
							current.Item, MaxPlacementRetries);
						bot.QueueOrder(Order.CancelProduction(queue.Actor, current.Item, 1));
						placementRetries = 0;
					}
				}
			}
		}

		CPos? FindBuildingPlacement(string actorName)
		{
			if (!world.Map.Rules.Actors.TryGetValue(actorName, out var actorInfo))
				return null;

			var buildingInfo = actorInfo.TraitInfoOrDefault<BuildingInfo>();
			if (buildingInfo == null)
				return null;

			var baseCenter = GetBaseCenter();
			if (!baseCenter.HasValue)
				return null;

			foreach (var cell in world.Map.FindTilesInAnnulus(baseCenter.Value, 0, 25))
			{
				if (world.CanPlaceBuilding(cell, actorInfo, buildingInfo, null)
					&& buildingInfo.IsCloseEnoughToBase(world, player, actorInfo, cell))
					return cell;
			}

			return null;
		}

		CPos? GetBaseCenter()
		{
			foreach (var b in world.ActorsHavingTrait<Building>())
			{
				if (!b.IsDead && b.Owner == player && b.OccupiesSpace != null
					&& b.Info.Name == "fact")
					return b.Location;
			}

			foreach (var b in world.ActorsHavingTrait<Building>())
			{
				if (!b.IsDead && b.Owner == player && b.OccupiesSpace != null)
					return b.Location;
			}

			return null;
		}

		#endregion

		#region Building Production

		void TryStartBuildingProduction(IBot bot)
		{
			if (buildQueue.Count == 0)
				return;

			var queues = AIUtils.FindQueuesByCategory(player);

			foreach (var queue in queues["Building"].Concat(queues["Defense"]))
			{
				if (queue.AllQueued().Any())
					continue;

				var next = buildQueue.Peek();

				if (!world.Map.Rules.Actors.ContainsKey(next))
				{
					Log("[AI] Unknown building: {0}, removing", next);
					buildQueue.Dequeue();
					return;
				}

				var buildable = queue.BuildableItems()
					.FirstOrDefault(b => string.Equals(b.Name, next, StringComparison.OrdinalIgnoreCase));

				if (buildable != null)
				{
					Log("[AI] Starting construction: {0}", buildable.Name);
					bot.QueueOrder(Order.StartProduction(queue.Actor, buildable.Name, 1));
					buildQueue.Dequeue();
					return;
				}

				// Not buildable yet (missing prerequisites) — wait
			}
		}

		#endregion

		#region Unit Production

		void TryStartUnitProduction(IBot bot)
		{
			if (produceOrders.Count == 0)
				return;

			var queues = AIUtils.FindQueuesByCategory(player);

			// One-time diagnostic after each decision
			if (logProductionDiagNextTick)
			{
				logProductionDiagNextTick = false;
				var orderNames = string.Join(", ", produceOrders.Select(o => $"{o.Count}x {o.Unit}"));
				Log("[AI] ProdDiag: orders=[{0}]", orderNames);
				foreach (var category in new[] { "Infantry", "Vehicle", "Aircraft", "Ship" })
				{
					var catQueues = queues[category].ToList();
					if (catQueues.Count == 0)
					{
						Log("[AI] ProdDiag: {0}: no queues", category);
						continue;
					}

					foreach (var q in catQueues)
					{
						var busy = q.AllQueued().Any();
						var items = q.BuildableItems().Select(b => b.Name).ToArray();
						Log("[AI] ProdDiag: {0}: busy={1}, buildable=[{2}]",
							category, busy, string.Join(",", items.Take(15)));
					}
				}
			}

			foreach (var category in new[] { "Infantry", "Vehicle", "Aircraft", "Ship" })
			{
				foreach (var queue in queues[category])
				{
					if (queue.AllQueued().Any())
						continue;

					if (TryProduceFromQueue(bot, queue))
						break;
				}
			}
		}

		bool TryProduceFromQueue(IBot bot, ProductionQueue queue)
		{
			for (var i = 0; i < produceOrders.Count; i++)
			{
				var order = produceOrders[i];
				var buildable = queue.BuildableItems()
					.FirstOrDefault(b => string.Equals(b.Name, order.Unit, StringComparison.OrdinalIgnoreCase));

				if (buildable == null)
					continue;

				Log("[AI] Training: {0} ({1} remaining)", buildable.Name, order.Count - 1);
				bot.QueueOrder(Order.StartProduction(queue.Actor, buildable.Name, 1));

				order.Count--;
				if (order.Count <= 0)
					produceOrders.RemoveAt(i);

				return true;
			}

			return false;
		}

		#endregion

		#region Movement Orders

		void ApplyMovementOrders(IBot bot, List<MovementOrder> orders)
		{
			if (orders == null)
				return;

			foreach (var order in orders)
			{
				var target = new CPos(order.X, order.Y);
				var units = new List<Actor>();

				foreach (var unit in world.ActorsHavingTrait<Mobile>())
				{
					if (unit.IsDead || unit.Owner != player || unit.OccupiesSpace == null)
						continue;

					if (order.Types.Count > 0)
					{
						if (!order.Types.Any(t => string.Equals(unit.Info.Name, t, StringComparison.OrdinalIgnoreCase)))
							continue;
					}
					else
					{
						if (!unit.IsIdle)
							continue;

						var hasAttack = unit.TraitsImplementing<AttackBase>().Any(a => !a.IsTraitDisabled);
						if (!hasAttack)
							continue;
					}

					units.Add(unit);
				}

				if (units.Count == 0)
				{
					Log("[AI] No units for {0} -> ({1},{2})",
						order.Action, order.X, order.Y);
					continue;
				}

				var orderString = string.Equals(order.Action, "move", StringComparison.OrdinalIgnoreCase)
					? "Move" : "AttackMove";

				Log("[AI] {0}: {1} units -> ({2},{3})",
					orderString, units.Count, order.X, order.Y);

				bot.QueueOrder(new Order(orderString, null,
					Target.FromCell(world, target), false,
					groupedActors: units.ToArray()));
			}
		}

		#endregion

		#region API Decision Flow

		void RequestNewDecision()
		{
			var snapshot = CollectGameState();

			// Income estimate
			var currentMoney = snapshot.Money;
			if (!isFirstTick)
			{
				var intervalSeconds = Info.DecisionIntervalTicks / 25.0;
				if (intervalSeconds > 0)
					estimatedIncomePerMin = (int)((currentMoney - lastMoney) / intervalSeconds * 60);
			}

			lastMoney = currentMoney;

			// Loss tracking
			if (!isFirstTick)
			{
				var buildingDelta = prevBuildingCount - snapshot.Buildings.Count;
				var unitDelta = prevUnitCount - snapshot.Units.Count;
				if (buildingDelta > 0)
					recentLosses.Add($"Lost {buildingDelta} building(s) since last report!");
				if (unitDelta > 0)
					recentLosses.Add($"Lost {unitDelta} unit(s) since last report!");
			}

			prevBuildingCount = snapshot.Buildings.Count;
			prevUnitCount = snapshot.Units.Count;
			isFirstTick = false;

			var report = FormatGameStateReport(snapshot);

			Log("[AI] === State ===");
			var pwrExcess = snapshot.PowerProvided - snapshot.PowerDrained;
			Log("[AI] ${0} | P:{1}/{2}({3}{4}) | {5} bld | {6} units | {7} enemies | attacks: {8}",
				snapshot.Money,
				snapshot.PowerProvided, snapshot.PowerDrained,
				pwrExcess >= 0 ? "+" : "", pwrExcess,
				snapshot.Buildings.Count, snapshot.Units.Count,
				snapshot.VisibleEnemies.Count, recentAttacks.Count);

			if (Info.EnableApi && !string.IsNullOrEmpty(apiKey))
			{
				// Add game state as user message to conversation
				conversationHistory.Add(new ChatMessage { Role = "user", Content = report });

				var systemPrompt = BuildSystemPrompt();
				var messages = conversationHistory.ToList(); // snapshot for async
				pendingDecision = Task.Run(() => CallApiAsync(systemPrompt, messages));
			}

			// Clear per-cycle data
			recentAttacks.Clear();
			recentLosses.Clear();
		}

		void ProcessApiResult(IBot bot)
		{
			try
			{
				if (pendingDecision.IsFaulted)
				{
					var ex = pendingDecision.Exception?.InnerException ?? pendingDecision.Exception;
					Log("[AI] API Error: {0}", ex?.Message ?? "Unknown");

					// Remove the unanswered user message
					if (conversationHistory.Count > 0 && conversationHistory[^1].Role == "user")
						conversationHistory.RemoveAt(conversationHistory.Count - 1);

					return;
				}

				var (decision, rawResponse) = pendingDecision.Result;

				// Store assistant response in conversation history
				if (!string.IsNullOrEmpty(rawResponse))
				{
					conversationHistory.Add(new ChatMessage { Role = "assistant", Content = rawResponse });
					TrimHistory();
				}
				else if (conversationHistory.Count > 0 && conversationHistory[^1].Role == "user")
				{
					// Remove unanswered user message
					conversationHistory.RemoveAt(conversationHistory.Count - 1);
				}

				if (decision == null)
					return;

				totalTurns++;
				Log("[AI] === Decision (turn {0}) ===", totalTurns);
				Log("[AI] {0}", decision.Analysis);

				// Replace build queue
				buildQueue.Clear();
				foreach (var b in decision.Build)
					buildQueue.Enqueue(b);

				if (decision.Build.Count > 0)
					Log("[AI] Build: {0}", string.Join(", ", decision.Build));

				// Replace produce orders
				produceOrders.Clear();
				produceOrders.AddRange(decision.Produce);

				if (decision.Produce.Count > 0)
				{
					Log("[AI] Produce: {0}",
						string.Join(", ", decision.Produce.Select(p => $"{p.Count}x {p.Unit}")));
					logProductionDiagNextTick = true;
				}

				// Execute movement orders immediately
				if (decision.Orders.Count > 0)
					ApplyMovementOrders(bot, decision.Orders);
			}
			catch (Exception ex)
			{
				Log("[AI] Error: {0}", ex.Message);
			}
		}

		void TrimHistory()
		{
			// Keep at most MaxHistoryTurns * 2 messages (user + assistant pairs)
			var maxMessages = Info.MaxHistoryTurns * 2;
			while (conversationHistory.Count > maxMessages)
			{
				// Remove oldest pair (user + assistant)
				conversationHistory.RemoveAt(0);
				if (conversationHistory.Count > 0 && conversationHistory[0].Role == "assistant")
					conversationHistory.RemoveAt(0);
			}
		}

		#endregion

		#region Game State Collection

		GameStateSnapshot CollectGameState()
		{
			var snapshot = new GameStateSnapshot
			{
				GameTimeTicks = world.WorldTick,
				Money = playerResources.GetCashAndResources(),
				PowerProvided = powerManager?.PowerProvided ?? 0,
				PowerDrained = powerManager?.PowerDrained ?? 0,
				Faction = player.Faction.InternalName
			};

			foreach (var building in world.ActorsHavingTrait<Building>())
			{
				if (building.IsDead || building.Owner != player || building.OccupiesSpace == null)
					continue;

				var health = building.TraitOrDefault<IHealth>();
				snapshot.Buildings.Add(new ActorSnapshot
				{
					Name = building.Info.Name,
					Location = building.Location,
					HealthPercent = health != null && health.MaxHP > 0
						? (int)(100.0 * health.HP / health.MaxHP) : 100
				});
			}

			foreach (var unit in world.ActorsHavingTrait<Mobile>())
			{
				if (unit.IsDead || unit.Owner != player || unit.OccupiesSpace == null)
					continue;

				var health = unit.TraitOrDefault<IHealth>();
				snapshot.Units.Add(new ActorSnapshot
				{
					Name = unit.Info.Name,
					Location = unit.Location,
					HealthPercent = health != null && health.MaxHP > 0
						? (int)(100.0 * health.HP / health.MaxHP) : 100
				});
			}

			foreach (var actor in world.Actors)
			{
				if (actor.IsDead || !actor.IsInWorld || actor.Owner == player || actor.OccupiesSpace == null)
					continue;

				if (player.RelationshipWith(actor.Owner) != PlayerRelationship.Enemy)
					continue;

				if (!actor.CanBeViewedByPlayer(player))
					continue;

				var health = actor.TraitOrDefault<IHealth>();
				snapshot.VisibleEnemies.Add(new ActorSnapshot
				{
					Name = actor.Info.Name,
					Location = actor.Location,
					HealthPercent = health != null && health.MaxHP > 0
						? (int)(100.0 * health.HP / health.MaxHP) : 100,
					IsBuilding = actor.Info.HasTraitInfo<BuildingInfo>()
				});
			}

			var queues = AIUtils.FindQueuesByCategory(player);
			foreach (var category in queues)
			{
				foreach (var queue in category)
				{
					var current = queue.CurrentItem();
					if (current != null)
					{
						snapshot.ProductionQueue.Add(new ProductionSnapshot
						{
							Category = category.Key,
							ItemName = current.Item,
							Done = current.Done,
							Paused = current.Paused
						});
					}

					foreach (var item in queue.BuildableItems())
					{
						if (!snapshot.BuildableItems.Contains(item.Name))
							snapshot.BuildableItems.Add(item.Name);
					}
				}
			}

			return snapshot;
		}

		string FormatGameStateReport(GameStateSnapshot snapshot)
		{
			var sb = new StringBuilder();
			var minutes = snapshot.GameTimeTicks / 1500;
			var seconds = snapshot.GameTimeTicks % 1500 / 25;

			sb.AppendLine($"TIME: {minutes}:{seconds:D2}");
			sb.AppendLine($"FACTION: {snapshot.Faction}");
			sb.AppendLine($"MONEY: {snapshot.Money} (income ~{estimatedIncomePerMin}/min)");

			var excess = snapshot.PowerProvided - snapshot.PowerDrained;
			sb.AppendLine($"POWER: {snapshot.PowerProvided} provided / {snapshot.PowerDrained} drained (surplus: {excess})");
			if (excess < 0)
				sb.AppendLine("⚠ LOW POWER! Production is 3x SLOWER. Build powr or apwr IMMEDIATELY!");

			var baseCenter = GetBaseCenter();
			if (baseCenter.HasValue)
				sb.AppendLine($"BASE: ({baseCenter.Value.X},{baseCenter.Value.Y})");

			// ALERTS — attacks and losses
			if (recentAttacks.Count > 0)
			{
				sb.AppendLine();
				sb.AppendLine("⚠ UNDER ATTACK:");
				var attacksByTarget = recentAttacks.GroupBy(a => a.TargetName);
				foreach (var g in attacksByTarget)
				{
					var attackers = g.Select(a => a.AttackerName).Distinct();
					var type = g.First().IsBuilding ? "building" : "unit";
					sb.AppendLine($"  {g.Key} ({type}) hit {g.Count()}x by: {string.Join(", ", attackers)}");
				}
			}

			if (recentLosses.Count > 0)
			{
				sb.AppendLine();
				sb.AppendLine("⚠ LOSSES:");
				foreach (var loss in recentLosses)
					sb.AppendLine($"  {loss}");
			}

			sb.AppendLine();
			sb.AppendLine("MY BUILDINGS:");
			if (snapshot.Buildings.Count == 0)
				sb.AppendLine("  (none)");
			else
				foreach (var g in snapshot.Buildings.GroupBy(b => b.Name))
				{
					var first = g.First();
					sb.AppendLine(g.Count() > 1
						? $"  {first.Name} x{g.Count()} near ({first.Location.X},{first.Location.Y})"
						: $"  {first.Name} at ({first.Location.X},{first.Location.Y}) hp:{first.HealthPercent}%");
				}

			sb.AppendLine();
			sb.AppendLine("MY UNITS:");
			if (snapshot.Units.Count == 0)
				sb.AppendLine("  (none)");
			else
				foreach (var g in snapshot.Units.GroupBy(u => u.Name))
				{
					var avgX = (int)g.Average(u => u.Location.X);
					var avgY = (int)g.Average(u => u.Location.Y);
					sb.AppendLine($"  {g.Key} x{g.Count()} near ({avgX},{avgY})");
				}

			sb.AppendLine();
			sb.AppendLine("ENEMY:");
			if (snapshot.VisibleEnemies.Count == 0)
				sb.AppendLine("  (none spotted)");
			else
				foreach (var g in snapshot.VisibleEnemies.GroupBy(e => e.Name))
				{
					var avgX = (int)g.Average(e => e.Location.X);
					var avgY = (int)g.Average(e => e.Location.Y);
					var type = g.First().IsBuilding ? "bld" : "unit";
					sb.AppendLine($"  {g.Key} x{g.Count()} ({type}) near ({avgX},{avgY})");
				}

			sb.AppendLine();
			sb.AppendLine($"BUILDABLE: [{string.Join(", ", snapshot.BuildableItems)}]");

			sb.AppendLine("PRODUCTION:");
			if (snapshot.ProductionQueue.Count == 0)
				sb.AppendLine("  (idle)");
			else
				foreach (var p in snapshot.ProductionQueue)
				{
					var status = p.Done ? "READY" : p.Paused ? "PAUSED" : "building";
					sb.AppendLine($"  [{p.Category}] {p.ItemName} - {status}");
				}

			if (buildQueue.Count > 0)
				sb.AppendLine($"PENDING BUILD: [{string.Join(", ", buildQueue)}]");

			if (produceOrders.Count > 0)
				sb.AppendLine($"PENDING UNITS: [{string.Join(", ", produceOrders.Select(p => $"{p.Count}x {p.Unit}"))}]");

			return sb.ToString();
		}

		#endregion

		#region LLM API

		static string BuildSystemPrompt()
		{
			return @"You command a Red Alert base. FULL CONTROL: buildings, units, army orders. This is a multi-turn conversation — you remember everything. Adapt every turn.

RESPOND WITH ONLY VALID JSON. No markdown, no text outside JSON.

{
  ""analysis"": ""1-2 sentences: what changed, what's the threat, what's the plan"",
  ""build"": [""building1"", ""building2""],
  ""produce"": [{""unit"": ""name"", ""count"": N}],
  ""orders"": [{""types"": [""unit_type""], ""action"": ""attack_move"", ""x"": N, ""y"": N}]
}

RULES:
- build: queue buildings IN ORDER. Only use names from BUILDABLE list! Replaces previous queue.
- produce: queue units. CRITICAL: ONLY produce units that appear in BUILDABLE list! If a unit is NOT in BUILDABLE, you CANNOT build it.
- orders: ""types""=unit names to select (empty=all idle combat units). ""action""=""attack_move"" or ""move"". x,y=map cell coordinates.
- You can issue MULTIPLE orders per turn to different unit groups (e.g. scouts go one way, main army another).
- ALWAYS CHECK the BUILDABLE list before ordering units. If tanks (1tnk/2tnk/3tnk) are NOT listed, you need to build FIX (repair facility) first!

═══ POWER ═══
Game state shows POWER: provided/drained/surplus. You MUST keep surplus >= 0.
Low power = ALL production 3x slower (buildings AND units). This is devastating.
Build powr ($300, +100 power) early. Build apwr ($500, +200 power) when available.
Rule of thumb: 1 powr sustains 2-3 buildings. After 4+ buildings you need apwr.
If surplus goes negative: STOP all other construction, build powr/apwr FIRST.

═══ ECONOMY ═══
Buildings: powr=$300(+100pw), apwr=$500(+200pw), proc=$1400(+free harvester,-40pw), barr=$300(-20pw), tent=$300(-20pw), weap=$2000(-30pw), dome=$1000(-40pw), fix=$1200(-30pw), tech=$1500(-60pw)
Each harvester trip ≈ $700. Two proc = two harvesters = stable income.
CRITICAL: Never let money sit idle. Always be building something OR producing units.
If money > $3000 and you have weap: spam tanks. If money piles up: build 2nd weap or 2nd barr.

═══ BUILD ORDER ═══
1. powr ($300) — enables construction, provides +100 power
2. proc ($1400) — refinery + free harvester = income
3. barr ($300) — infantry for early defense/scouting
4. powr ($300) — second power plant BEFORE weap (you'll need it)
5. weap ($2000) — war factory (but tanks need FIX first!)
6. fix ($1200) — REPAIR FACILITY. CRITICAL: unlocks TANKS. Without fix, weap only builds harv/apc/ftrk!
7. proc ($1400) — second refinery = double income
8. apwr ($500) — advanced power, you need it before dome
9. dome ($1000) — radar
After dome: check power surplus! If < 50 build apwr. Then tech for advanced units, or 2nd weap.
ALWAYS CHECK POWER before building anything. Each building drains power.
NOTE: If tanks don't appear in BUILDABLE after building weap, you MUST build fix first!

═══ UNITS — COSTS AND ROLES ═══
INFANTRY (barr/tent) — cheap, slow, fragile:
  e1=$100(rifle, filler) e2=$160(grenadier, vs buildings) e3=$300(rocket, vs tanks/air)
  e4=$200(flamethrower) shok=$1200(shock trooper, devastating vs everything, requires tech)
DOG (kenn — kennel building required, $200):
  dog=$200(fastest scout, kills infantry 1-shot, CANNOT attack vehicles)
  NOTE: Dogs need a KENNEL (kenn), not barracks. Build kenn if you want dogs.

VEHICLES (weap) — your MAIN fighting force:
  WITHOUT fix: harv=$1400, apc=$600(transport), ftrk=$480(flak truck, anti-air)
  WITH fix: TANKS UNLOCKED! Check BUILDABLE list — your faction determines which tanks:
  Allied tanks: 1tnk=$700(light, fast, no fix needed) 2tnk=$800(medium, BEST VALUE, needs fix)
  Soviet tanks: 3tnk=$1150(heavy, tough, needs fix) 4tnk=$1700(mammoth, needs tech)
  Other: v2rl=$700(V2 rocket, needs dome, Soviet) arty=$600(artillery, needs dome, Allied)
  BUILD FIX ASAP after weap! Without it you have NO TANKS and will lose.

AIR (hpad/afld): heli=$1200 hind=$1300 mig=$1600 yak=$800
NAVY (spen/syrd): ss=$950 dd=$1000 ca=$2000

═══ COUNTER SYSTEM ═══
- Tanks crush infantry (always prefer tanks over infantry for combat)
- e3 rocket soldiers counter tanks (but die to other infantry)
- Dogs instantly kill any infantry (but can't attack vehicles at all)
- 4tnk mammoth has built-in AA missiles
- ftrk/agun counter air units
- arty/v2rl outrange base defenses

═══ DEFENSE ═══
gun=$600(turret, vs vehicles) pbox=$300(pillbox, vs infantry) ftur=$600(flame turret)
tsla=$1500(tesla coil, powerful) sam=$750(anti-air) agun=$600(anti-air gun)
RULE: Build 1-2 defenses near proc/harvesters BEFORE you have enough tanks to defend.
A single gun turret early can save your base from a rush.

═══ STRATEGY ═══

EARLY GAME (0:00-3:00):
- Follow build order strictly. NEVER skip proc.
- After barr: train 1-2 e1 ($100 each) for scouting. Send them to DIFFERENT map quadrants.
- If you have kenn (kennel): dogs are faster scouts. But dogs cost $200 and need a separate building.
- DO NOT send infantry to attack. They are too slow and weak. Save money for weap.
- WATCH YOUR POWER. If power surplus < 0, build powr/apwr before anything else.

MID GAME (3:00-7:00):
- After weap: build FIX immediately to unlock tanks!
- Check BUILDABLE for available tanks (faction-dependent: 2tnk for Allied, 3tnk for Soviet).
- Spam the best tank in your BUILDABLE list — best cost/power ratio.
- Build second proc for double income if not done yet.
- When you have 4-5 tanks: send them as a GROUP to attack.
- NEVER trickle units one by one — always group at least 3-5 before attacking.
- Priority targets: enemy harvesters (harv) → enemy proc → enemy weap → enemy fact
- Killing a harvester = -$1400 to enemy AND cripples income.

LATE GAME (7:00+):
- Build tech center for advanced units (mammoth tanks, shock troopers, V2 rockets).
- Consider second weap for double tank production.
- Use combined arms: tanks in front, v2rl/arty behind for siege.
- Protect your proc and harvesters — they are your lifeline.

═══ TACTICAL RULES ═══
1. NEVER send scouts with your main army. Scouts go separately to explore.
2. ALWAYS group units before attacking. 5 tanks together > 5 tanks sent one at a time.
3. If you see enemy units near your base, DEFEND FIRST before attacking.
4. Attack the ECONOMY (harvesters, refineries), not the main army.
5. If your attack fails and you lose units — DO NOT send another attack immediately. Rebuild first.
6. Keep producing tanks non-stop. Idle weap = wasted money.
7. If UNDER ATTACK: move ALL nearby combat units to intercept. Do not let them sit idle.
8. Use map coordinates from your game state to make smart orders. Don't guess coordinates.

═══ SCOUTING ═══
You can only see what your units see (fog of war). Early scouting wins games.
- Send e1 riflemen ($100 each) or dogs to the 4 corners/edges of the map to find enemy base.
- Once you find the enemy, remember their base coordinates for future attacks.
- If you haven't found the enemy, KEEP SCOUTING. You cannot attack what you cannot see.

═══ COMMON MISTAKES TO AVOID ═══
- ORDERING UNITS NOT IN BUILDABLE LIST (check BUILDABLE before every produce order!)
- NOT BUILDING FIX (without fix, weap only makes harv/apc/ftrk — NO TANKS!)
- IGNORING POWER (if surplus < 0 everything builds 3x slower — you WILL lose)
- Building dome/tech before weap+fix (you need tanks FIRST)
- Sending infantry to attack (they're too slow and weak — use tanks)
- Not building second proc (one harvester can't sustain tank production)
- Sending units without a target (don't guess — use coordinates from ENEMY section)
- Building too many defenses instead of army (defenses can't attack enemy base)
- Ignoring UNDER ATTACK warnings (your buildings die fast without defense)
- Hoarding money (if money > $2000 and nothing building — you're losing)
- Trying to build dogs from barracks (dogs need KENNEL building kenn, not barr/tent)
- Building 2tnk as Soviet or 3tnk as Allied (check your FACTION! Each has different tanks)";
		}

		async Task<(AIDecision Decision, string RawResponse)> CallApiAsync(
			string systemPrompt, List<ChatMessage> messages)
		{
			try
			{
				// Build messages array for API
				var apiMessages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray();

				var requestBody = JsonSerializer.Serialize(new
				{
					model = Info.ApiModel,
					max_tokens = Info.MaxTokens,
					system = systemPrompt,
					messages = apiMessages
				});

				var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
				{
					Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
				};

				request.Headers.Add("x-api-key", apiKey);
				request.Headers.Add("anthropic-version", "2023-06-01");

				var response = await ApiClient.SendAsync(request);
				var responseBody = await response.Content.ReadAsStringAsync();

				if (!response.IsSuccessStatusCode)
				{
					AIUtils.BotDebug("[AI] API {0}: {1}",
						(int)response.StatusCode,
						responseBody.Length > 200 ? responseBody[..200] : responseBody);
					return (null, null);
				}

				using var doc = JsonDocument.Parse(responseBody);
				var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString();

				AIUtils.BotDebug("[AI] Raw: {0}",
					text != null && text.Length > 300 ? text[..300] + "..." : text);

				return (ParseDecision(text), text);
			}
			catch (TaskCanceledException)
			{
				AIUtils.BotDebug("[AI] API timeout");
				return (null, null);
			}
			catch (HttpRequestException ex)
			{
				AIUtils.BotDebug("[AI] HTTP error: {0}", ex.Message);
				return (null, null);
			}
			catch (Exception ex)
			{
				AIUtils.BotDebug("[AI] API failed: {0}", ex.Message);
				return (null, null);
			}
		}

		static AIDecision ParseDecision(string text)
		{
			try
			{
				text = text.Trim();

				if (text.StartsWith("```", StringComparison.Ordinal))
				{
					var start = text.IndexOf('\n') + 1;
					var end = text.LastIndexOf("```", StringComparison.Ordinal);
					if (end > start)
						text = text[start..end].Trim();
				}

				using var doc = JsonDocument.Parse(text);
				var root = doc.RootElement;

				var decision = new AIDecision
				{
					Analysis = root.TryGetProperty("analysis", out var a) ? a.GetString() ?? "" : ""
				};

				if (root.TryGetProperty("build", out var buildArr) && buildArr.ValueKind == JsonValueKind.Array)
				{
					foreach (var item in buildArr.EnumerateArray())
					{
						var name = item.GetString();
						if (!string.IsNullOrEmpty(name))
							decision.Build.Add(name);
					}
				}

				if (root.TryGetProperty("produce", out var prodArr) && prodArr.ValueKind == JsonValueKind.Array)
				{
					foreach (var item in prodArr.EnumerateArray())
					{
						if (!item.TryGetProperty("unit", out var u))
							continue;

						var unitName = u.GetString();
						if (string.IsNullOrEmpty(unitName))
							continue;

						var count = item.TryGetProperty("count", out var c) ? c.GetInt32() : 1;
						decision.Produce.Add(new ProduceOrder { Unit = unitName, Count = count });
					}
				}

				if (root.TryGetProperty("orders", out var ordArr) && ordArr.ValueKind == JsonValueKind.Array)
				{
					foreach (var item in ordArr.EnumerateArray())
					{
						var order = new MovementOrder
						{
							Action = item.TryGetProperty("action", out var act) ? act.GetString() ?? "attack" : "attack",
							X = item.TryGetProperty("x", out var x) ? x.GetInt32() : 0,
							Y = item.TryGetProperty("y", out var y) ? y.GetInt32() : 0
						};

						if (item.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Array)
						{
							foreach (var t in types.EnumerateArray())
							{
								var typeName = t.GetString();
								if (!string.IsNullOrEmpty(typeName))
									order.Types.Add(typeName);
							}
						}

						if (order.X != 0 || order.Y != 0)
							decision.Orders.Add(order);
					}
				}

				return decision;
			}
			catch (Exception ex)
			{
				AIUtils.BotDebug("[AI] Parse error: {0}", ex.Message);
				return null;
			}
		}

		#endregion

		#region Logging

		void Log(string format, params object[] args)
		{
			AIUtils.BotDebug(format, args);
			LogToFile(format, args);
		}

		void LogToFile(string format, params object[] args)
		{
			if (logWriter == null)
				return;

			try
			{
				var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
				var message = args.Length > 0 ? string.Format(format, args) : format;
				logWriter.WriteLine($"[{timestamp}] {message}");
			}
			catch
			{
				// Non-critical
			}
		}

		#endregion
	}

	#region Data Classes

	sealed class GameStateSnapshot
	{
		public int GameTimeTicks;
		public int Money;
		public int PowerProvided;
		public int PowerDrained;
		public string Faction = "";
		public readonly List<ActorSnapshot> Buildings = new();
		public readonly List<ActorSnapshot> Units = new();
		public readonly List<ActorSnapshot> VisibleEnemies = new();
		public readonly List<string> BuildableItems = new();
		public readonly List<ProductionSnapshot> ProductionQueue = new();
	}

	sealed class ActorSnapshot
	{
		public string Name;
		public CPos Location;
		public int HealthPercent;
		public bool IsBuilding;
	}

	sealed class ProductionSnapshot
	{
		public string Category;
		public string ItemName;
		public bool Done;
		public bool Paused;
	}

	sealed class AIDecision
	{
		public string Analysis = "";
		public readonly List<string> Build = new();
		public readonly List<ProduceOrder> Produce = new();
		public readonly List<MovementOrder> Orders = new();
	}

	sealed class ProduceOrder
	{
		public string Unit;
		public int Count;
	}

	sealed class MovementOrder
	{
		public readonly List<string> Types = new();
		public string Action = "attack";
		public int X;
		public int Y;
	}

	sealed class ChatMessage
	{
		public string Role;
		public string Content;
	}

	sealed class AttackEvent
	{
		public string TargetName;
		public string AttackerName;
		public int Tick;
		public bool IsBuilding;
	}

	#endregion
}
