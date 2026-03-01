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
		readonly HashSet<uint> pendingPlacements = new();

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
				var queueId = queue.Actor.ActorID;
				var current = queue.CurrentItem();

				if (current == null || !current.Done)
				{
					// Item gone or not done — clear pending flag
					pendingPlacements.Remove(queueId);
					continue;
				}

				// Already sent placement order — wait for engine to process it
				if (pendingPlacements.Contains(queueId))
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
						ExtraData = queueId,
						SuppressVisualFeedback = true
					});

					pendingPlacements.Add(queueId);
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
			Log("[AI] ${0} | {1} bld | {2} units | {3} enemies | attacks: {4}",
				snapshot.Money, snapshot.Buildings.Count, snapshot.Units.Count,
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
					Log("[AI] Produce: {0}",
						string.Join(", ", decision.Produce.Select(p => $"{p.Count}x {p.Unit}")));

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
				Money = playerResources.GetCashAndResources()
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
			sb.AppendLine($"MONEY: {snapshot.Money} (income ~{estimatedIncomePerMin}/min)");

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
			return @"You are a Red Alert RTS commander with FULL CONTROL over building, production, and army movement.
This is a CONVERSATION — you remember all previous game states and your decisions. Learn from what worked and what failed.

Respond with ONLY valid JSON. No markdown fences, no text outside JSON.

FORMAT:
{
  ""analysis"": ""1-2 sentence assessment referencing what changed since last turn"",
  ""build"": [""building1"", ""building2""],
  ""produce"": [{""unit"": ""name"", ""count"": N}],
  ""orders"": [{""types"": [""unit_type""], ""action"": ""attack"", ""x"": N, ""y"": N}]
}

FIELDS:
- build: Buildings to construct IN ORDER. Names from BUILDABLE list. Replaces previous queue.
- produce: Units to train. Replaces previous orders.
- orders: Movement commands. ""types""=unit names to select (empty=all idle combat). ""action""=""attack"" or ""move"". x,y=map coordinates.

BUILD ORDER:
powr(power) → proc(refinery,$income) → barr/tent(infantry) → weap(war factory) → dome(radar) → tech
apwr=advanced power. Each proc gives free harvester. Keep power positive.
Defenses: pbox, gun, ftur, tsla, agun, sam

UNITS:
Infantry(barr/tent): e1(rifle) e2(grenadier) e3(rocket) e4(flame) dog(scout) shok(shock trooper)
Vehicle(weap): 1tnk(light) 2tnk(medium) 3tnk(heavy) 4tnk(mammoth) v2rl(rocket) arty(artillery) apc ftrk(flak) stnk(stealth)
Air(hpad/afld): heli hind mh60 mig yak
Navy(spen/syrd): ss msub dd ca pt

STRATEGY:
- Early: powr → proc → barr → weap → dome, then expand production
- Tanks beat infantry. Build mostly 2tnk/3tnk.
- Attack enemy harvesters(harv) and refineries(proc) to cripple economy
- Scout with dogs before committing army
- If UNDER ATTACK warning: immediately build defenses, pull units back to defend
- LEARN: if you sent units and lost them, try a different approach next time
- Don't repeat failed strategies — adapt!";
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
