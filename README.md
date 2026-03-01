# AiRA — AI Bot for OpenRA Red Alert

An OpenRA mod that adds an LLM-powered AI opponent to Red Alert. The bot uses the Anthropic Messages API to make all strategic decisions: building construction, unit production, and army movement.

Every 15 seconds the bot sends a game state snapshot (buildings, units, enemy positions, resources) to the LLM and receives back a JSON with orders. The bot maintains a conversation history so it can learn and adapt within a single game session.

## Features

- Full strategic control via LLM (no hardcoded build orders)
- Multi-turn conversation history (remembers previous decisions)
- Attack detection and loss tracking
- Automatic harvester, MCV, building repair, and support power management
- File logging to `ai-strategist.log`

## Requirements

- [OpenRA](https://github.com/OpenRA/OpenRA) engine `release-20250330`
- .NET 6 SDK
- Red Alert game content (installed via OpenRA content installer)
- Anthropic API key

## Installation

```bash
# Clone the repo
git clone https://github.com/andrejbestuzhev/ai-ra-bot.git
cd ai-ra-bot

# Fetch the engine and build
# Windows:
make.cmd all
# Linux / macOS:
make all

# Set your API key (pick one method):
# Option A: environment variable
export ANTHROPIC_API_KEY="sk-ant-..."
# Option B: user.config file (gitignored)
echo 'ANTHROPIC_API_KEY="sk-ant-..."' > user.config

# Launch
# Windows:
launch-game.cmd
# Linux / macOS:
./launch-game.sh
```

## Usage

1. Start a Skirmish game
2. Add an AI opponent and select **AI Strategist** from the bot dropdown
3. The bot will start making decisions after the first 15-second interval

## Configuration

Bot parameters are in `mods/aira/rules/ai.yaml`:

| Parameter | Default | Description |
|-----------|---------|-------------|
| `ApiModel` | `claude-haiku-4-5-20251001` | LLM model identifier |
| `DecisionIntervalTicks` | `375` | Ticks between decisions (375 = 15s) |
| `MaxTokens` | `1024` | Max tokens per API response |
| `MaxHistoryTurns` | `10` | Conversation history depth |
| `EnableApi` | `true` | Enable/disable API calls |

## Project Structure

```
OpenRA.Mods.Aira/
  Traits/AIStrategistModule.cs   # Main bot module (C#)
mods/aira/
  mod.yaml                       # Mod manifest
  rules/ai.yaml                  # Bot configuration
  fluent/ai-bot.ftl              # Localization strings
```

## License

The OpenRA engine and SDK are licensed under [GPLv3](COPYING).
