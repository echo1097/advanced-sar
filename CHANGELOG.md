# Changelog

## 0.3.2

- Begin SAR checks whether the helicopter has enough fuel to reach the raft and return to its home ship or airbase. It skips rafts outside that range.
- Travel time is estimated at the helicopter's top speed. The fuel check uses the higher of its hover and full-speed burn rates, including loadout weight.
- The estimate allows 4 minutes for pickup and 2 minutes for landing. The fuel reserve is 20% of the estimated trip or 5 minutes, whichever is greater.
- Active SAR routes check their raft every 30 in-game seconds. The waypoint follows drift of at least 0.1 nautical miles. If the raft disappears or is emptied, SAR switches to a reachable, unclaimed raft when one is available.

## 0.3.1

- Added **Cancel SAR** to remove the helicopter's SAR waypoint and free the raft for another helicopter.
- Begin SAR skips rafts already assigned to other helicopters. A raft becomes available again when its SAR waypoint is completed or removed, or the assigned helicopter is lost.
- SAR prioritizes friendly rafts with fewer survivors and uses distance to break ties.

## 0.3.0

- Added **Search & Rescue → Begin SAR** to the helicopter Orders menu.
- Begin SAR sends the helicopter to the nearest friendly life raft carrying survivors while keeping its assigned altitude.
- Helicopters with no available survivor space respond with “Unable to comply.” If no friendly raft with survivors is found, the game displays a message and keeps the current route.

## 0.2.1

- Color-coded rescue beacons by allegiance and added an option to hide enemy beacons.
- Made map markers smaller and moved survivor counts above the markers.

## 0.2.0

- Added rescue beacon map icons and survivor count tooltips.
