# RandomVisionSuperCharged

Version 3.0 Slay the Spire 2 mod for transparent previews of deterministic event outcomes, map encounter queues, Crystal Sphere rolls, and draw pile order.

## Features

- Event previews use a structured overlay with coverage labels for each option.
- Supported hidden random outcomes are filled in by event-specific adapters when they can be determined safely.
- Encounter, event, and ancient previews expose seeded map-room outcomes before entry.
- Draw pile order peeking is included.
- Card, relic, potion, upgrade, downgrade, and event-order previews cover Neow's Bones, Aroma of Chaos, This or That, Potion Courier, Phial Holster, Hefty Tablet, New Leaf, Battleworn Dummy, Reflections, Symbiote, Pandora's Box, Calling Bell, Sere Talon, Hungry for Mushrooms, Round Tea Party, Glass Eye, Alchemical Coffer, Toy Box, Sea Glass, Sand Castle, Astrolabe, Trail, Tinker Time, Slippery Bridge, Endless Conveyor, Tablet of Truth, Trash Heap, Welcome to Wongos, Whispering Hollow, Luminous Choir, WarHistorian Repy, Doors of Light and Dark, and Morphic Grove.

## Release Package

The GitHub release package is `RandomVisionSuperChargedV30.zip`. It contains the built mod files and does not include local package caches.

## Build

```sh
dotnet build RandomVisionSuperCharged.csproj -c Release
```

Use `ModOutputDir` to choose where the built mod files are copied:

```sh
dotnet publish RandomVisionSuperCharged.csproj -c Release -p:ModOutputDir=/path/to/output
```

## TODO

- If an event happened in prior act, then it won't trigger again.
- In multiplayer game, prediction refresh will prevent player from scrolling event prediction list down.
- Neow's Bone (and others): Upon pick up/selection, RNG is consumed and due to prediction auto refresh mech, it will refresh to the next RNG prediction (instead of staying the same)
