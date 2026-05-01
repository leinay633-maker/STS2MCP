Play Slay the Spire 2 using the MCP tools (`mcp__sts2__*`). Your goal is to play as well as possible and win the run.

## Setup
1. Read `AGENTS.md` for general strategy and MCP calling tips.
2. Read `GUIDE.md` and the relevant files under `knowledge/` for gameplay knowledge.
3. Call `get_game_state(format="markdown")` to see the current state and begin playing.
4. The user starts each run manually. Do not call `auto_slay_start_loop`, do not start background loops, and do not automatically start the next run after a win/loss.
5. Do not equate MCP `run.act` with the user's A-number. In this run, MCP `run.act=2` is `A1 第二幕`, not `A2`.

## Gameplay Loop
- **Map**: Evaluate paths. Prefer elites when healthy, rest sites before bosses.
- **Combat**: Read intents. Sequence cards optimally (setup → skills → attacks). Play from right-to-left indices when possible to avoid index shift bugs.
- **Events**: Evaluate options based on current HP, gold, and deck needs.
- **Rewards**: Skip cards that don't synergize. Claim gold and relics. Potions if slots open.
- **Rest Sites**: Heal if below 80% HP before boss. Otherwise upgrade or train (Girya).
- **Shop**: Buy if 100+ gold and something useful is available.

## Important Rules
- Always re-check `get_game_state` after playing cards — indices shift.
- Use `format: "json"` in combat for precise data, `format: "markdown"` for overview screens.
- Use potions BEFORE playing cards when they grant buffs (e.g. Flex Potion).
- Focus fire on bosses — minions flee when the leader dies.
- Card reward screens use `select_card_reward` with `card_index`; generic selection screens such as `Headbutt` discard-pile selection use `select_card` with `index`.

## Learning & Updating
- After each act boss is defeated and the next act begins, pause briefly and update the knowledge base before continuing.
- On death, save and quit, or user stop, update the knowledge base with failure lessons before ending.
- Record both successful and failed choices. Keep raw run notes under `knowledge/runs/`, then extract reusable lessons into the relevant hero file and `knowledge/lessons.md`.
- Keep `GUIDE.md` as the high-priority summary/index. Put detailed hero strategy in `knowledge/heroes/<hero>.md`.
- If playing a hero not yet covered, create a new hero file using the structure in `knowledge/README.md`.
