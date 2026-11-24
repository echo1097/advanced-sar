# Changelog

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
