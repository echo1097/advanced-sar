# Changelog

## 0.6.1

- Added ability to preform SAR for enemy survivors. The helicopter keeps its selected survivor type when it relaunches.
- SAR helicopters pick up survivors ONLY from their assigned raft, including when multiple rafts are within pickup range.

## 0.6.0

- When another order or waypoint takes over from SAR, the helicopter reports “Rodger, aborting SAR.” in the status terminal.
- Fixed helicopters circling a rescue waypoint while trying to reach exactly 20 feet. They now check their distance from the raft separately from pickup height and speed.
- In sea states 9+ (due to pick up from helicopters being impossible as far as I can tell), the helicopter makes one low approach, reports “Too risky, unable to conduct SAR”, and returns to base.
- Helicopters report accepted SAR orders, cancellations, and skipped rafts in the status terminal. Once a helicopter starts RTB, it reports the total survivors aboard from all rafts. After landing, the home ship or airbase reports how many survivors it received.

## 0.5.3

- When a SAR relaunch finishes, replace Sea Power's automatic launch waypoints with a route toward the next reachable raft. Preserve player-issued orders and new or moved waypoints.

## 0.5.2

- The first waypoint after a SAR relaunch now points toward the selected raft. If the raft is close to the ship, the waypoint extends past it on the same heading to give the helicopter more room to depart.

## 0.5.1

- Relaunched SAR helicopters fly straight clear of the ship before turning toward the next raft.
- Helicopters now hand off to Sea Power's Return To Base order immediately when the rescue run ends. Raft-to-raft travel stays at 20 feet.

## 0.5.0

- SAR helicopters use Sea Power's normal return and landing orders when full, low on fuel, or out of reachable rafts. A confirmed recovery unloads rescued survivors without awarding rescue credit again.
- Healthy helicopters with enough fuel can skip the recovery cooldown and launch prep while still using the deck queue. They take off with their remaining fuel. Helicopters that need fuel use the normal turnaround.
- After takeoff, the recovered helicopter resumes SAR if it can reach an eligible raft. It stays aboard when no raft fits its range on a full tank.

## 0.4.2

- Helicopters with room and enough fuel fly straight to the next eligible raft at 20 feet.
- When the SAR run ends, the helicopter removes its SAR waypoint and climbs in place to the altitude it had before the first descent.

## 0.4.1

- SAR now descends short of the raft and switches to hover once close enough for pickup. If the helicopter circles without getting closer, SAR retries the approach once.

## 0.4.0

- Helicopters on SAR now properly descend to 20 feet near their raft and hold position for survivor pickup.
- SAR checks the helicopter's own transport capacity. After pickup it climbs back to its previous altitude, then looks for another reachable friendly raft if it has room and fuel to get home.
- If pickup stalls, SAR retries the approach once when fuel allows, then skips that raft for the rest of the run. During transit, route checks still run every 30 in-game seconds.

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
