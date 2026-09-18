# Nuclear Option performance research: is long-match slowdown the base game?

Research date: 2026-09-17. Web sources only. No code was read and nothing was built.

A note on dates. Steam omits the year on posts made in the current year, so a post dated
"8 May" was made on 8 May 2026. Dates carrying an explicit year are quoted as shown.

## Short answer

Yes. Frame-rate decline as a Nuclear Option match fills up is a long-standing, widely
reported problem in the unmodified game. It has been raised repeatedly on the Steam
discussion forum since at least December 2024 and was still being raised in May 2026.
The developer has never, in anything I could find publicly, posted a direct explanation,
but the developer's own patch notes show repeated work aimed squarely at this problem.

The strongest single piece of evidence is not a forum post but a patch note. In update
0.29.3 (21 January 2025) the developer added settings to cap the number of wrecks kept on
the map and to make wrecks disappear after a set time. Before that update wrecks were kept
for the whole match. A developer does not add a "remove the oldest wrecks first" control
unless keeping them all was hurting something.

## 1. Is it a known problem?

It is, and the reports are independent of each other and span nearly two years.

A player reported in December 2024 that late-stage Escalation missions lagged so badly the
aircraft became uncontrollable, and that lowering graphics settings changed nothing. Another
player in the same thread said recent updates had made the game much harder on the processor
since version 0.29, and a third pushed back on the "buy a better computer" answer with
"still runs bad on better cpus, it's a game issue".

In March 2025 a player reported roughly 30 frames per second on low settings in dense
missions while a solo test flight ran smoothly. A reply put it plainly: "The more units that
are on the map the more it lags", and said they had found a threshold while building missions
beyond which the game struggles.

The clearest match for the symptom described here comes from January 2026. A player with a
high-end graphics card, 64 GB of memory and a solid-state drive reported dropping to about
20 frames per second in Escalation after ten minutes. That is decay over match time on
hardware that is not the limiting factor.

Reports continue into May 2026. One player with a fast machine reported 40 to 60 frames per
second at only half utilisation of processor, graphics card and memory. Another in the same
thread suspected things got worse after version 0.33.

I found no bug tracker, no public Discord archive and no Reddit discussion of substance. The
whole public conversation is the Steam forum plus the community wiki. That is thinner than it
sounds, but the same few claims recur from people who clearly are not talking to each other.

## 2. What is the reported cause?

Confirmed by the developer, through patch notes:

- Processor multithreading was added for aerodynamics, ship water physics and line-of-sight
  checks in update 0.29.1 (21 January 2025), and many particle effects were optimised in the
  same update. That tells you those three were hot enough to be worth the effort.
- Wreck removal settings arrived in update 0.29.3 (21 January 2025).
- An in-game vehicle whose job was to drive around collecting wrecks off the battlefield was
  added in 0.30.9 (28 May 2025) and pulled again in 0.30.91 as unfinished.
- Visual effects got a performance pass in hotfix 0.33.3 (30 April 2026), and update 0.34
  (26 July 2026) reduced the cost of loading and unloading particle systems and fixed what
  the notes call "an unintended performance issue with Bullet Simulation System".

Reported by several players independently: the game is limited by the processor rather than
the graphics card, graphics settings barely move the frame rate, and the cost tracks the
number of live things in the world, specifically players, missiles, bombs and vehicles.

Said once by one person, and worth treating as informed guesswork only: that the reason the
game cannot spread the load further is that Unity's physics must run on one thread or the
simulation misbehaves. The person who said it is a mod author for the game, not the developer.

On the specific suspects in the brief, I found nothing public either way about accumulating
audio sources, shell casings, the game's own contact tracking growing without limit, or save
and replay recording. Absence of discussion is not evidence of innocence here. The only
accumulation mechanism with hard public backing is wrecks, and the backing is the developer's
own decision to add controls for it.

One structural fact worth holding on to: each aircraft is made of up to 50 individually
simulated physics parts, per the game's own Steam store description. Physics cost in this
game is not one body per aircraft.

## 3. Settings that mitigate it

Two settings exist, both in mission settings and in the customise options when starting a
mission. One caps the number of wrecks and throws away the oldest when the cap is hit. The
other makes wrecks disappear after a set time. Community documentation states the despawn
time defaults to disabled, meaning wrecks stay for the whole match unless you change it, and
advises setting the cap low if performance is poor.

Using a fast wreck despawn time is therefore the right move and is exactly what the
developer intended, but it only covers wrecks. It does nothing about live units.

The other lever is the number of aircraft the computer-controlled side keeps in the air.
Mission settings expose an active aircraft count per side, and community mission-building
advice is to keep the default low because physics is the bottleneck, and to avoid ships,
convoys and ballistic missiles all happening at once.

Outside the game: several players report that turning vertical sync off helped, one
substantially. Others suggest raising the game's process priority or restricting which
processor cores it uses. Treat these as folk remedies with mixed results, not fixes.

## 4. A unit-count ceiling

There is no published number. Players say confidently that a ceiling exists and that they
have run into it while building missions, but nobody states where it is, and it will move
with the machine since the limit is single-thread processor speed. One player noted the game
was designed around 16 players and that community servers exceed that. For ground vehicles
and aircraft I found no figure at all. This is a genuine gap.

## 5. Engine and version

The game is built in Unity. I could not confirm the Unity version from any public source;
the database page that would show it refused the request. The game supports DirectX 12 and
Vulkan as of update 0.32 (15 December 2025). No publicly known Unity defect specific to this
game's engine version turned up, because the version itself is not public.

You can settle this locally without guessing. Right-click `UnityPlayer.dll` in the game
folder and read the file version, or open `Nuclear Option_Data\boot.config`. That is a
two-minute check and beats any inference.

## 6. Telling the game apart from the mod, in one evening

The game has a built-in frame counter. Press F3 to toggle a display showing frame rate and
processor and graphics timings. It lives under the debug section of the keybinds, so if F3
does nothing, rebind it there. Use timings, not the frame rate, because frame time is linear
and frames per second is not.

Run four sessions of the same mission, same settings, same seat, roughly 45 minutes each,
writing down the processor frame time at 0, 10, 20, 30 and 45 minutes. Do nothing but fly a
holding pattern, so your own actions are not a variable.

1. Mod removed entirely, wrecks left at the game's default of never disappearing.
2. Mod removed entirely, wreck cap low and wreck despawn time short.
3. Mod installed, wrecks at default.
4. Mod installed, wreck cap low and despawn short.

Read it like this. If run 1 decays and run 2 mostly does not, the decay is the base game
hoarding objects, and it is yours to work around rather than fix. If runs 1 and 3 decay by
about the same amount, the mod is not the cause; a mod that costs processor time shows up as
a constant gap between the two lines from the first minute, not as a steeper slope. A
steeper slope with the mod installed is the one result that convicts the mod.

Two refinements worth the small extra effort. First, pause the game and watch the frame time.
The mod's periodic work runs on scaled time and stops when the game pauses, so if frame time
recovers while paused the cost is in per-tick logic; if it stays bad, the cost is in the
number of objects being drawn and simulated. Second, and this is the trap: the mod buys and
spawns units, so it raises the unit count and therefore the wreck count. That is mod-caused
decay arriving through the base game's known weakness. To separate the two, the no-mod runs
must reach a comparable unit count, which means raising the computer-controlled aircraft and
ground unit settings in the plain game rather than comparing a busy modded match against a
quiet unmodded one.

Removing the on-screen overlay proving nothing is consistent with everything above. The
problem is reported as processor-bound simulation, not drawing.

## Profiling

Nobody has publicly profiled this game. No hardware site benchmark, no frame-time analysis,
no developer profiling post. The F3 counter is the only instrument the game offers, and it
is enough to measure a slope. Anything finer would mean attaching an external profiler to a
Unity build, which nothing public describes anyone doing for this title.

## Sources

- Poor Game Performance / Optimization, Steam forum, May 2026 — https://steamcommunity.com/app/2168680/discussions/0/844005426527771929/
- Performance Issues, Steam forum, 29 March 2025 onward — https://steamcommunity.com/app/2168680/discussions/0/595144890359226162/
- Poor performance during late game in Escalation, Steam forum, 29 December 2024 — https://steamcommunity.com/app/2168680/discussions/0/595136643598451104/
- Performance issues, Steam forum, 2 October 2024 — https://steamcommunity.com/app/2168680/discussions/0/4849904427677032122/
- Multiplayer performance issues on larger missions, Steam forum, 8 December 2024 — https://steamcommunity.com/app/2168680/discussions/0/604141990686232884/
- Is this game single-threaded?, Steam forum, 5 January 2026 — https://steamcommunity.com/app/2168680/discussions/0/691997670669212607/
- For anyone with performance issues in MP, Steam forum, 27 January 2026 — https://steamcommunity.com/app/2168680/discussions/0/690873811210402445/
- FPS, GPU, and CPU Counter, Steam forum, 23 August 2025 — https://steamcommunity.com/app/2168680/discussions/0/595157852294008574/
- Graphics performance bottleneck?, Steam forum, 13 December 2024 — https://steamcommunity.com/app/2168680/discussions/0/537714352002731923/
- Development (full update history), Nuclear Option Wiki, read 17 September 2026 — https://nuclearoption.wiki.gg/wiki/Development
- HLT wreck removal, Nuclear Option Wiki, read 17 September 2026 — https://nuclearoption.wiki.gg/wiki/HLT_wreck_removal
- Update 0.34 patch notes, 27 July 2026 — https://changelog.gg/games/nuclear-option-2168680/updates/2026-07-27-update-0-34-bbd0e223afeaf8de
- Nuclear Option store page, Steam, read 17 September 2026 — https://store.steampowered.com/app/2168680/Nuclear_Option/
- Mission Editor Guides, Resources and other related things, Steam guide — https://steamcommunity.com/sharedfiles/filedetails/?id=3765197239 (reached through search result extracts only; Steam rate-limited the direct fetch on 17 September 2026)
